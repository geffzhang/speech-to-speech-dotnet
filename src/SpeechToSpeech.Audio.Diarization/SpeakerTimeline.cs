namespace SpeechToSpeech.Audio.Diarization;

/// <summary>
/// 归因策略：一段转写被算到谁头上。
/// 默认是「重叠最多者胜，其次是连续性」——跨说话人变化的转写算给覆盖它最多的那个人；
/// 而分离器还没提交到的那段转写（它落后一个前瞻），借用紧邻它之前正在说话的那个人。
/// </summary>
public sealed record SpeakerTimelineOptions
{
    /// <summary>
    /// 一条已定的段可以停在转写起点之前多远还仍然借出它的说话人。正好覆盖「转写先到、分离器还在追」的情形——
    /// 这个标签随后会被段流修正。
    /// </summary>
    public double ContinuityWindow { get; init; } = 1.5;

    /// <summary>用于查找的段数上限（最旧的被丢掉，长通话不会把它撑大）。</summary>
    public int MaxTrackedSegments { get; init; } = 512;

    /// <summary>说话人 id 在标签里的拼法，例如 <c>speaker_1</c>（与上游 <c>speaker_{n}</c> 同形）。</summary>
    public string SpeakerIdPrefix { get; init; } = "speaker_";
}

/// <summary>一次归因的结果：判给的说话人与该标签的可信度。</summary>
/// <param name="Speaker">说话人 id，或 <see cref="SpeakerSegmentUpdate.UnknownSpeaker"/>。</param>
/// <param name="Confidence">所依据段的标签可信度（借用时就是借用那一段的可信度）。</param>
public readonly record struct SpeakerAttributionResult(int Speaker, double Confidence)
{
    /// <summary>什么都没判出来。</summary>
    public static SpeakerAttributionResult Unknown { get; } =
        new(SpeakerSegmentUpdate.UnknownSpeaker, 0);
}

/// <summary>
/// 归因侧看到的「谁在什么时候说话」：按段 id 建键，所以一次修订替换它取代的状态，而不是追加一条重复的。
/// 有界：到 <see cref="SpeakerTimelineOptions.MaxTrackedSegments"/> 之后丢最旧的段——查找永远只针对最近的过去。
///
/// <para><b>线程。</b>写入方是分离 worker 线程，读取方是 STT 线程，所以这里加了锁。这与
/// <see cref="StreamingDiarizer"/> 的「单消费者、非线程安全」并不矛盾：状态机只被一个线程碰，
/// 而这张表被跨线程查询。</para>
/// </summary>
public sealed class SpeakerTimeline
{
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, int> _indexById = [];
    private readonly SpeakerTimelineOptions _options;
    private readonly object _gate = new();

    public SpeakerTimeline(SpeakerTimelineOptions? options = null)
        => _options = options ?? new SpeakerTimelineOptions();

    /// <summary>当前跟踪的段数（有界性的测试缝）。</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>应用一次分离步骤：先按 id upsert 段，再应用合并。</summary>
    public void Apply(DiarizerUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Segments.Count == 0 && update.Merges.Count == 0) return;

        lock (_gate)
        {
            foreach (var segment in update.Segments) Upsert(segment);
            foreach (var merge in update.Merges) Merge(merge.RetiredSpeaker, merge.SurvivingSpeaker);
        }
    }

    /// <summary>清空（会话结束，说话人 id 的语义随新会话重来）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _indexById.Clear();
        }
    }

    // 晚到的旧修订被忽略：流本来有序，但输掉竞争的那次修订不能把标签回滚。
    private void Upsert(SpeakerSegmentUpdate segment)
    {
        if (_indexById.TryGetValue(segment.Id, out int index))
        {
            var existing = _entries[index];
            if (segment.Revision < existing.Revision) return;
            _entries[index] = new Entry(segment.Id, segment.Speaker, segment.Start, segment.End, segment.Revision, segment.Confidence);
            return;
        }

        _entries.Add(new Entry(segment.Id, segment.Speaker, segment.Start, segment.End, segment.Revision, segment.Confidence));
        _indexById[segment.Id] = _entries.Count - 1;
        Trim();
    }

    // 被退位的说话人的每个仍在跟踪的段改写成幸存的 id（id 永不复用）。
    private void Merge(int retired, int surviving)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Speaker == retired) _entries[i] = _entries[i] with { Speaker = surviving };
    }

    /// <summary>
    /// 把一段区间判给某个说话人：累计重叠最多者胜；分离器还没覆盖到的区间借用紧挨它之前结束的那位
    /// （在连续性窗口内）。无话可说时返回 <see cref="SpeakerSegmentUpdate.UnknownSpeaker"/>。
    /// </summary>
    /// <remarks>
    /// 重叠是<b>按说话人累加</b>的，所以一个人被切成好几段（一次修订、或并进来的邻居）不会因此在
    /// 与另一条完整段竞争时吃亏。并列取起点更早的那一段，于是横跨换人的一句话归给先开口的人——
    /// 确定性，而且这正是「文本覆盖两个说话人」时该有的规则。
    /// </remarks>
    public SpeakerAttributionResult Attribute(double start, double end)
    {
        if (end < start) (start, end) = (end, start);

        lock (_gate)
        {
            if (_entries.Count == 0) return SpeakerAttributionResult.Unknown;

            Dictionary<int, double>? overlap = null;
            double bestOverlap = 0;
            int bestIndex = -1;

            foreach (var entry in _entries)
            {
                double shared = Math.Min(end, entry.End) - Math.Max(start, entry.Start);
                if (shared <= 0) continue;

                overlap ??= [];
                overlap.TryGetValue(entry.Speaker, out double total);
                overlap[entry.Speaker] = total + shared;
            }

            if (overlap is not null)
            {
                double bestStart = double.MaxValue;
                for (int i = 0; i < _entries.Count; i++)
                {
                    var entry = _entries[i];
                    double shared = Math.Min(end, entry.End) - Math.Max(start, entry.Start);
                    if (shared <= 0) continue;
                    double total = overlap[entry.Speaker];
                    if (total > bestOverlap + 1e-9 || (Math.Abs(total - bestOverlap) <= 1e-9 && entry.Start < bestStart))
                    {
                        bestOverlap = total;
                        bestIndex = i;
                        bestStart = entry.Start;
                    }
                }

                if (bestIndex >= 0 && _entries[bestIndex].Speaker != SpeakerSegmentUpdate.UnknownSpeaker)
                    return new SpeakerAttributionResult(_entries[bestIndex].Speaker, _entries[bestIndex].Confidence);
            }

            // 还没有任何段覆盖它：分离器在音频之后提交，所以先到的转写借用紧挨它之前说话的那个人。
            int previous = SpeakerSegmentUpdate.UnknownSpeaker;
            double previousEnd = double.MinValue;
            double previousConfidence = 0;
            foreach (var entry in _entries)
            {
                if (entry.End > start + 1e-9 || entry.End < previousEnd) continue;
                previousEnd = entry.End;
                previous = entry.Speaker;
                previousConfidence = entry.Confidence;
            }

            if (previous == SpeakerSegmentUpdate.UnknownSpeaker) return SpeakerAttributionResult.Unknown;
            return start - previousEnd <= _options.ContinuityWindow
                ? new SpeakerAttributionResult(previous, previousConfidence)
                : SpeakerAttributionResult.Unknown;
        }
    }

    private void Trim()
    {
        int excess = _entries.Count - Math.Max(1, _options.MaxTrackedSegments);
        if (excess <= 0) return;

        _entries.RemoveRange(0, excess);
        _indexById.Clear();
        for (int i = 0; i < _entries.Count; i++) _indexById[_entries[i].Id] = i;
    }

    private readonly record struct Entry(string Id, int Speaker, double Start, double End, int Revision, double Confidence);
}
