namespace SpeechToSpeech.Audio.Diarization;

/// <summary>
/// 流式（在线）说话人分离：音频一边到达一边回答「谁在什么时候说话」，而不是等录音结束再重跑一遍。
/// 它只维持三种状态，没有第四种：
///
/// <list type="number">
/// <item><b>增量音频</b>——按绝对样本索引寻址的定容 <see cref="AudioRingBuffer"/>，加上下一个要跑的窗口和
/// 提交前沿。音频从不攒满整场，也从不重算。</item>
/// <item><b>窗口重叠</b>——一个窗口只为它中心那一片负责（「own 区」：它和邻居都看到过的部分），
/// 它在这个区域之外报的轮次按未定发布。前瞻就来自这里：落后最新音频 <c>(window − hop) / 2</c> 秒。</item>
/// <item><b>会话说话人</b>——<see cref="SpeakerPrototypes"/>，把每个轮次的声纹匹配到稳定 id；
/// 加上仍在进行中的轮次。</item>
/// </list>
///
/// <para><b>为什么是本地轮次而不是帧。</b>分割模型的说话人通道是<b>窗口内</b>的（这个窗口的 0 号通道
/// 不是下个窗口的 0 号通道），所以帧不能按通道跨窗口累积。同一本地通道上连续的一段帧是一个
/// <i>本地轮次</i>：它覆盖一段确定的音频，这段音频取一次声纹，跨窗口的身份由声纹携带。
/// 重叠能保留，是因为两个本地通道可以覆盖同一批帧，两个轮次都被发布。</para>
///
/// <para><b>一条段先发布、再延长。</b>第一个报出某个轮次的窗口创建它的段——未定，若还不够长就按连续性
/// 借用标签——之后每个窗口延长同一条段（同一个 id），直到轮次的结尾落进 own 区，此时段定稿。
/// 说话人合并是另一个修订源，受修订窗口约束。消费方按 <see cref="SpeakerSegmentUpdate.Id"/> 建键，
/// 把更高的 revision 当成替换。</para>
///
/// <para>非线程安全：一条会话流一个实例，由单个消费者驱动（管线里的分离 worker）。它不含模型运行时——
/// 分割器与声纹器都是接缝。</para>
/// </summary>
public sealed class StreamingDiarizer
{
    private readonly ISpeakerMaskSegmentation _segmentation;
    private readonly ISpeakerEmbedding _embedding;
    private readonly StreamingDiarizerConfig _config;

    private readonly int _windowSamples;
    private readonly int _hopSamples;
    private readonly int _frameShift;
    private readonly int _frameOffset;
    private readonly int _ownedOffsetSamples; // 窗口起点到它负责决定的区域的距离

    private readonly AudioRingBuffer _audio;
    private readonly SpeakerPrototypes _prototypes;
    private readonly float[] _windowScratch;
    private float[] _embeddingScratch = []; // 复用：一次判定不该为一段音频分配

    private readonly Dictionary<string, Segment> _history = []; // 已发布的段，按创建顺序
    private readonly Dictionary<int, string> _channelMap = [];  // 本地通道 -> 上一窗它在续接的段
    private readonly List<Segment> _open = [];                  // 仍在进行中的轮次
    private readonly HashSet<string> _touched = [];              // 本窗报出过的段

    private readonly List<SpeakerSegmentUpdate> _updates = [];
    private readonly List<SpeakerMergeInfo> _merges = [];

    private long _nextWindowStart;
    private long _frontier;
    private int _segmentCounter;

    public StreamingDiarizer(
        ISpeakerMaskSegmentation segmentation,
        ISpeakerEmbedding embedding,
        StreamingDiarizerConfig? config = null)
    {
        _segmentation = segmentation ?? throw new ArgumentNullException(nameof(segmentation));
        _embedding = embedding ?? throw new ArgumentNullException(nameof(embedding));
        _config = config ?? new StreamingDiarizerConfig();

        if (_config.SampleRate != segmentation.SampleRate)
            throw new ArgumentException(
                $"分离器采样率 {_config.SampleRate} Hz 与分割引擎的 {segmentation.SampleRate} Hz 不一致；请在上游重采样。",
                nameof(config));
        if (embedding.EmbeddingDim <= 0)
            throw new ArgumentException("声纹引擎必须声明正的 EmbeddingDim。", nameof(embedding));

        (_windowSamples, _hopSamples) = _config.ResolveGeometry(segmentation);
        _frameShift = segmentation.FrameShiftSamples;
        _frameOffset = segmentation.FrameOffsetSamples;

        ValidateGeometry();

        // own 区是窗口中心：(window - hop) / 2 处进，宽一个 hop。
        _ownedOffsetSamples = (_windowSamples - _hopSamples) / 2;

        int capacity = Math.Max(_windowSamples + _hopSamples, (int)(_config.MaxBufferedSeconds * _config.SampleRate));
        _audio = new AudioRingBuffer(capacity);
        _windowScratch = new float[_windowSamples];
        _prototypes = new SpeakerPrototypes(_config);
    }

    /// <summary>推入音频的采样率（与分割引擎相同）。</summary>
    public int SampleRate => _config.SampleRate;

    /// <summary>模型窗口长度，单位样本（已把配置对引擎几何解算过）。</summary>
    public int WindowSamples => _windowSamples;

    /// <summary>窗口之间的推进量，单位样本。</summary>
    public int HopSamples => _hopSamples;

    /// <summary>
    /// 到此处为止所有段都已判定，单位绝对秒。比它更新的音频至多只有<b>未定</b>标签；
    /// 它与 <see cref="SamplesSeen"/> 的距离就是这项能力付出的前瞻。
    /// </summary>
    public double CommitFrontierSeconds => _frontier / (double)SampleRate;

    /// <summary>至今摄入的绝对样本数，含通过 <see cref="NotifyGap"/> 上报的那些。</summary>
    public long SamplesSeen => _audio.End;

    /// <summary>有界音频缓冲当前保留的样本数。</summary>
    public long BufferedSamples => _audio.Count;

    /// <summary>本次会话活着的说话人 id，按创建顺序（合并时更早的 id 幸存）。</summary>
    public IReadOnlyList<int> SpeakerIds => _prototypes.SpeakerIds;

    /// <summary>
    /// 喂入 <see cref="SampleRate"/> 的音频。返回这次推入跑完的窗口发布的一切；
    /// 大多数推入什么也不发布（窗口还在填）。
    /// </summary>
    public DiarizerUpdate Push(ReadOnlySpan<float> audio)
    {
        _updates.Clear();
        _merges.Clear();

        if (audio.Length > 0) _audio.Write(audio);

        while (SamplesSeen >= _nextWindowStart + _windowSamples)
        {
            RunWindow(_nextWindowStart, flush: false);
            _nextWindowStart += _hopSamples;
            RetainAudio();
        }

        RetainAudio();

        return Result();
    }

    // 缓冲有两个读者：窗口调度从 _nextWindowStart 开始读，轮次的收尾声纹要回溯到该轮次的起点。
    // 只有两者都用不上时才丢音频——否则一个比窗口更长的轮次，收尾时能读到的就只剩尾巴，
    // 声纹描述的会是别人的声音。
    private void RetainAudio()
    {
        long horizon = SamplesSeen - (long)(_config.MaxBufferedSeconds * SampleRate);
        _audio.DiscardBefore(Math.Min(_nextWindowStart, horizon));
    }

    /// <summary>
    /// 上报上游丢了 <paramref name="droppedSamples"/> 个样本（调用方的有界队列溢出）。
    /// 时间轴照旧前进——时间戳仍然意味着原来的时刻——但窗口调度在空洞之后重启，所有进行中的轮次收尾，
    /// 因为丢掉的那段里接着说的话已经无法恢复。缺口是不连续，绝不悄悄缝合。
    /// </summary>
    public DiarizerUpdate NotifyGap(int droppedSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(droppedSamples);
        _updates.Clear();
        _merges.Clear();
        if (droppedSamples == 0) return Result();

        foreach (var segment in _open.ToArray()) Finalize(segment);
        _open.Clear();

        // 用静音推进时间轴：分块写，长缺口也不会大分配。
        var silence = new float[Math.Min(droppedSamples, Math.Max(1, SampleRate))];
        int remaining = droppedSamples;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, silence.Length);
            _audio.Write(silence.AsSpan(0, chunk));
            remaining -= chunk;
        }

        _nextWindowStart = SamplesSeen;
        _frontier = SamplesSeen;
        _audio.DiscardBefore(SamplesSeen);
        return Result();
    }

    /// <summary>
    /// 流结束：把剩下的窗口跑完（尾部补零，与批量解码的最后一块同理）、发布尾部、并把每个进行中的轮次提交。
    /// 调用一次即可；之后再 <see cref="Push"/> 会从新的调度开始。
    /// </summary>
    public DiarizerUpdate Flush()
    {
        _updates.Clear();
        _merges.Clear();

        // 每次迭代正好拥有下一个 hop，调度就从流停下的地方接着走：窗口绝不回头重跑已经跑过的音频。
        // 窗口本身会越过音频末尾，那段由环形缓冲补零——这是补出来的尾巴，不是引擎推断出的静音。
        while (_frontier < SamplesSeen)
        {
            long start = _frontier - _ownedOffsetSamples;
            if (start < _audio.Oldest) start = _audio.Oldest;
            if (start < 0) start = 0;

            long previous = _frontier;
            RunWindow(start, flush: true);
            if (_frontier <= previous) _frontier = SamplesSeen; // 兜底：循环必须终止
        }

        foreach (var segment in _open.ToArray()) Finalize(segment);
        _open.Clear();

        _nextWindowStart = Math.Max(_nextWindowStart, SamplesSeen);
        return Result();
    }

    // ---------------------------------------------------------------- 窗口处理

    private void RunWindow(long windowStart, bool flush)
    {
        _audio.CopyTo(windowStart, _windowScratch);
        var masks = _segmentation.Analyze(_windowScratch);
        if (masks.FrameCount == 0) return;

        long ownedStart = windowStart + _ownedOffsetSamples;
        long ownedEnd = Math.Min(ownedStart + _hopSamples, SamplesSeen);
        if (ownedEnd <= ownedStart && !flush) return;

        _touched.Clear();
        long firstFrame = windowStart / _frameShift;
        var channelMap = new Dictionary<int, string>(_channelMap);

        foreach (var turn in ExtractTurns(masks))
        {
            long turnStartFrame = firstFrame + turn.StartFrame;
            long turnEndFrame = firstFrame + turn.EndFrame;
            long turnStart = turnStartFrame * _frameShift + _frameOffset;
            long turnEnd = Math.Min(turnEndFrame * _frameShift + _frameOffset, SamplesSeen);
            if (turnEnd <= turnStart) continue;

            // 轮次在提交前沿之前就结束了，判定它的音频已经过去：这个窗口说什么都改不了。
            // 收尾窗口会与流已经跑过的调度重叠，这条判断就是它不会把已完成的轮次当成新轮次再报一次的原因。
            if (turnEnd <= _frontier) continue;

            var segment = Associate(turn, firstFrame, turnStartFrame, turnEnd, out bool alreadyCommitted);
            if (alreadyCommitted) continue;

            if (segment is null)
            {
                segment = CreateSegment(turnStart, turnEnd, turnStartFrame, turnEndFrame);
            }
            else
            {
                segment.EndSample = Math.Max(segment.EndSample, turnEnd);
                segment.LastFrame = Math.Max(segment.LastFrame, turnEndFrame);
            }

            // 这条轨道在哪个窗口里最清楚：只逮住闭合帧（或尾部静音）的窗口会低估一个刚才还很明显的
            // 声音，而可信度是新建说话人的门槛。
            segment.Activity = Math.Max(segment.Activity, turn.Confidence);

            channelMap[turn.Speaker] = segment.Id;
            _touched.Add(segment.Id);

            // 轮次的结尾落在本窗负责的区域内：这就是它几何的最终结论。
            bool ending = flush || turnEnd <= ownedEnd;

            // 够长就尽早描述（这正是「流式」的意义），结束时再用完整的轮次描述一次——
            // 那是最准的一次。只有收尾那次入册声纹：未定的匹配不能把原型往一个片段拖，
            // 否则进行中的重叠会把别人的声音教给已经站稳的说话人。
            if (ShouldDecide(segment, ending)) DecideSpeaker(segment, enrol: ending);

            if (ending) Finalize(segment);
            else Publish(segment);
        }

        // 一个完整 hop 里没有任何窗口报出的轮次已经结束，即使它的闭合帧正好落在窗口边界之外。
        foreach (var segment in _open.ToArray())
        {
            if (segment.EndSample <= _frontier && !_touched.Contains(segment.Id)) Finalize(segment);
        }

        _channelMap.Clear();
        foreach (var (channel, id) in channelMap) _channelMap[channel] = id;

        _frontier = Math.Max(_frontier, flush ? Math.Min(ownedEnd, SamplesSeen) : ownedEnd);

        if (_prototypes.Merge(_frontier / (double)SampleRate) is { Count: > 0 } merges) ApplyMerges(merges);

        PruneHistory();
    }

    private Segment CreateSegment(long startSample, long endSample, long firstFrame, long lastFrame)
    {
        var (previousSpeaker, gap) = FindPrevious(startSample);
        var segment = new Segment
        {
            Id = $"seg-{++_segmentCounter}",
            StartSample = startSample,
            EndSample = endSample,
            FirstFrame = firstFrame,
            LastFrame = lastFrame,
            PreviousSpeaker = previousSpeaker,
            PreviousGap = gap,
            // 还没有证据的轮次借用刚在说话的那个人——附和声，或者被窗口边界切开的续说。
            // 在它长到足以描述之前一直是未定（这个标签就是消费方此刻能看到的）。
            Speaker = previousSpeaker >= 0 && gap <= _config.ContinuityWindow
                ? previousSpeaker
                : SpeakerSegmentUpdate.UnknownSpeaker,
        };
        segment.Confidence = segment.Speaker >= 0 ? 0.25 : 0;
        _history[segment.Id] = segment;
        _open.Add(segment);
        Publish(segment);
        return segment;
    }

    // 音频够不够描述一个声音：声纹下限与新建说话人的证据下限两者取大。判早了就是从片段下一锤定音的结论，
    // 之后再也没有机会翻案。
    private bool HasEvidence(Segment segment)
    {
        double required = Math.Max(_config.MinEmbeddingDuration, _config.MinNewSpeakerEvidence);
        return segment.EndSample - segment.StartSample >= GapSamples(required);
    }

    // 一条段只描述两次：第一次有足够音频时（未定），以及结束时。中途不重复描述——
    // 一次声纹是一次模型调用，而从不断变长的片段上取的标签只会来回跳。
    private bool ShouldDecide(Segment segment, bool ending)
    {
        if (ending) return !segment.Decided || segment.DecidedSample != segment.EndSample;
        return !segment.Decided && HasEvidence(segment);
    }

    private void DecideSpeaker(Segment segment, bool enrol)
    {
        double duration = (segment.EndSample - segment.StartSample) / (double)SampleRate;
        if (duration >= _config.MinEmbeddingDuration && EmbedSpan(segment.StartSample, segment.EndSample) is { } embedding)
        {
            var decision = _prototypes.Decide(
                embedding, duration, segment.Activity, segment.PreviousSpeaker, segment.PreviousGap, enrol);
            segment.Speaker = decision.Speaker;
            segment.Confidence = decision.LabelConfidence;
        }
        // 太短，描述不出声音：保留创建时借来的连续性标签。
        segment.Decided = true;
        segment.DecidedSample = segment.EndSample;
    }

    private void Finalize(Segment segment)
    {
        segment.Extendable = false;
        _open.Remove(segment);
        Publish(segment);
    }

    // 每次发布都是同一个 id 的新 revision：第一次 0，然后 1、2……消费方按 id upsert，
    // 编号就是「晚到或重复的投递能被认成陈旧」的依据。
    private void Publish(Segment segment)
    {
        _updates.Add(new SpeakerSegmentUpdate(
            segment.Id,
            segment.Speaker,
            Time(segment.StartSample),
            Time(segment.EndSample),
            IsFinal: !segment.Extendable,
            Revision: segment.Revision++,
            segment.Confidence));
    }

    private void ApplyMerges(IReadOnlyList<SpeakerMergeInfo> merges)
    {
        foreach (var merge in merges)
        {
            _merges.Add(merge);

            foreach (var segment in _history.Values)
            {
                if (segment.Speaker != merge.RetiredSpeaker) continue;
                segment.Speaker = merge.SurvivingSpeaker;
                Publish(segment); // 只在修订窗口内——PruneHistory 让这个列表有界
            }
        }
    }

    // 约束段历史：早于修订窗口的都可以走，进行中的轮次除外（合并改不了已经不在保留范围内的东西——
    // 那正是合并通知存在的理由）。
    private void PruneHistory()
    {
        const int minRetained = 128;
        if (_history.Count <= minRetained) return;

        double horizon = _frontier / (double)SampleRate - Math.Max(_config.RevisionWindow, 0);
        foreach (var id in _history.Keys.ToArray())
        {
            if (_history.Count <= minRetained) break;
            var segment = _history[id];
            if (segment.Extendable || Time(segment.EndSample) >= horizon) continue;
            _history.Remove(id);
        }
    }

    // ---------------------------------------------------------------- 辅助

    // 这一批帧续接哪条进行中的段；没有能认领的返回 null。
    //
    // 从窗口左沿开始的 run 在窗口打开时就已经在说话了：模型把它截断了，所以 run 的起点说明不了轮次从哪开始，
    // 只有逐通道的映射能说它续接谁（重叠时两个通道覆盖同一批帧，光看帧分不出谁续谁）。
    // 起点落在窗口内部的 run 本身就是一次独立的目击，用它自己的绝对起始帧做身份——该帧在后续窗口重报时不变。
    private Segment? Associate(LocalTurn turn, long firstFrame, long turnStartFrame, long turnEnd, out bool alreadyCommitted)
    {
        alreadyCommitted = false;

        // 先按「这个通道上一窗口在续接哪个段」认亲，而且**不论这个轮次是不是从窗口开头开始的**。
        // 窗口约 10 秒、跳长不到 1 秒，所以一段 3 秒的话会被十来个窗口看到；后续窗口里它的起点
        // 落在 own 区中间（StartFrame > 0），如果只在 StartFrame == 0 时才查通道映射，
        // 这些窗口就只能靠 FindOpen 在**尚未结束**的段里找——而它早被 Finalize 移出 _open 了。
        // 于是每个窗口都给它发一个新 id：时间线上是一堆互相咬住的段，总时长能超过音频本身。
        Segment? tracked = null;
        if (_channelMap.TryGetValue(turn.Speaker, out string? trackedId))
            _history.TryGetValue(trackedId, out tracked);

        if (tracked is not null)
        {
            // 上一段的帧区间盖住了本窗口看到的这一帧：同一个轮次又出现了一次。
            if (turnStartFrame >= tracked.FirstFrame - 1 && turnStartFrame <= tracked.LastFrame + 1)
            {
                if (tracked.Extendable) return tracked;

                // 已提交：本窗口这次观测能提供的信息它都有了。只有尾巴伸得更远才算新消息。
                alreadyCommitted = turnEnd <= tracked.EndSample;
                return null;
            }

            // 段还在长、而且一直长到本窗口开头：通道中间没有断过，继续用它。
            if (turn.StartFrame == 0 && tracked.Extendable && tracked.LastFrame >= firstFrame - 1)
                return tracked;

            // 段已提交，而本窗口的轮次也结束在它结束的地方：这是后一个窗口在重报那个已完成的轮次
            // （收尾窗口与它的前驱重叠），不是又发现了一个。
            if (turn.StartFrame == 0 && !tracked.Extendable && turnEnd <= tracked.EndSample)
            {
                alreadyCommitted = true;
                return null;
            }
        }

        // 通道上没有人在续接，而这个轮次也不是从窗口开头开始的：它必是别的窗口已经开过的段。
        if (turn.StartFrame > 0) return FindOpen(turnStartFrame);

        return null; // 这个通道上没有人在说话：轮次确实从这里开始
    }

    // 后一个窗口重报的同一个轮次：模型从 run 自己的起点开始报，所以起点帧就是它还在窗口里时的身份。
    // 两个重叠的说话人起点不同（万一撞上，先来的那个本窗已经认领了自己的段），
    // 所以光靠位置不会把两个人粘成一段。
    private Segment? FindOpen(long startFrame)
    {
        foreach (var segment in _open)
        {
            if (_touched.Contains(segment.Id)) continue;
            if (Math.Abs(segment.FirstFrame - startFrame) <= 1) return segment;
        }
        return null;
    }

    // 在 beforeSample 及之前结束的、最近的一条已发布段——短轮次或附和声最可能在续接的那位说话人。
    private (int Speaker, double Gap) FindPrevious(long beforeSample)
    {
        int speaker = SpeakerSegmentUpdate.UnknownSpeaker;
        long best = long.MinValue;
        foreach (var segment in _history.Values)
        {
            if (segment.EndSample > beforeSample || segment.EndSample < best) continue;
            best = segment.EndSample;
            speaker = segment.Speaker;
        }
        double gap = speaker < 0 ? double.MaxValue : (beforeSample - best) / (double)SampleRate;
        return (speaker, gap);
    }

    // 从缓冲里回读该轮次的音频。比保留地平线更长的轮次只能用还在的部分描述——
    // 有界内存意味着无界的轮次没法整段取声纹，而把截断报出去只会请调用方去处理一个他控制不了的数字。
    private float[]? EmbedSpan(long startSample, long endSample)
    {
        long from = Math.Max(startSample, _audio.Oldest);
        int length = (int)Math.Min(endSample - from, int.MaxValue);
        if (length <= 0) return null;

        if (_embeddingScratch.Length < length) _embeddingScratch = new float[length];

        // 必须把**本段的长度**传给 CopyTo：它按目标 span 的长度复制，而 scratch 只增不减
        // （复用上次的分配）。整块传进去的话，一段 2 秒的话会连着它后面 4 秒的音频一起被算进声纹，
        // 于是所有段的向量都朝「整条音轨的平均」收敛，谁跟谁都像——表现就是永远只有 1 个说话人。
        int copied = _audio.CopyTo(from, _embeddingScratch.AsSpan(0, length));
        return copied == 0 ? null : _embedding.Embed(_embeddingScratch.AsSpan(0, copied), SampleRate);
    }

    private double Time(long sample) => sample / (double)SampleRate;

    private long GapSamples(double seconds) => (long)(seconds * SampleRate);

    private DiarizerUpdate Result()
        => _updates.Count == 0 && _merges.Count == 0
            ? DiarizerUpdate.Empty
            : new DiarizerUpdate(_updates.ToArray(), _merges.ToArray());

    /// <summary>窗口内某个本地说话人通道上的一段连续帧。</summary>
    private readonly record struct LocalTurn(int Speaker, int StartFrame, int EndFrame, double Confidence);

    // 每个本地通道一段活动轨道的 onset / 静音关断。
    //
    // 「关」的判据是概率掉到 Onset 之下并持续 MinSilenceDuration——不是掉到某个更低的 offset 之下。
    // 参照实现用的是「低于 offset(0.7×onset) 才算静音」，可真实模型的概率经常在 0.4 附近徘徊：
    // 两个说话人之间那段短停顿、拼接处的数字静音，模型都给出 0.40 左右的持续弱响应。
    // 它既明显低于 onset，又远高于 0.35，于是凹陷永远不成立，两个人的语音被连成一个轮次；
    // 声纹取到的是两个人的混合声，之后所有段都跟这个「半人半鬼」的原型比、谁跟谁都像，
    // 最后整场只得出一个说话人。宁可多切几段（同一说话人的相邻段共享标签，无害），
    // 也不能把两个人缝进一段——缝进去就没有任何下游手段能拆开。
    private List<LocalTurn> ExtractTurns(SpeakerMaskWindow masks)
    {
        var turns = new List<LocalTurn>();
        int frames = masks.FrameCount;
        int speakers = _segmentation.MaxLocalSpeakers;
        int closeFrames = Math.Max(1, (int)Math.Round(_config.MinSilenceDuration * SampleRate / _frameShift));

        for (int s = 0; s < speakers; s++)
        {
            bool active = false;
            int startFrame = 0, silent = 0;
            double sum = 0;
            int counted = 0;

            for (int f = 0; f < frames; f++)
            {
                var row = masks.FrameProbabilities[f];
                float probability = s < row.Length ? row[s] : 0f;

                if (!active)
                {
                    if (probability >= _config.Onset)
                    {
                        active = true;
                        startFrame = f;
                        silent = 0;
                        sum = probability;
                        counted = 1;
                    }
                    continue;
                }

                sum += probability;
                counted++;
                if (probability >= _config.Onset)
                {
                    silent = 0;
                }
                else if (++silent >= closeFrames)
                {
                    turns.Add(new LocalTurn(s, startFrame, f - silent + 1, sum / counted));
                    active = false;
                }
            }

            if (active) turns.Add(new LocalTurn(s, startFrame, frames, sum / counted));
        }

        turns.Sort((x, y) => x.StartFrame.CompareTo(y.StartFrame));
        return turns;
    }

    private void ValidateGeometry()
    {
        if (_windowSamples <= 0 || _hopSamples <= 0 || _frameShift <= 0)
            throw new ArgumentException(
                $"分离几何非法：window={_windowSamples}，hop={_hopSamples}，frameShift={_frameShift}。");

        if (_hopSamples > _windowSamples / 3)
            throw new ArgumentException(
                $"跳长 {_hopSamples} 不得超过窗口 {_windowSamples} 的三分之一：窗口必须看得清它负责决定的那一段。");

        if (_windowSamples % _frameShift != 0 || _hopSamples % _frameShift != 0
            || (_windowSamples - _hopSamples) / 2 % _frameShift != 0)
            throw new ArgumentException(
                $"窗口 {_windowSamples} 与跳长 {_hopSamples} 都必须是帧步长 {_frameShift} 的整数倍，" +
                "且 (window - hop) / 2 必须落在帧网格上。");
    }

    /// <summary>一条已发布段的可变状态（按 <see cref="Id"/> upsert）。</summary>
    private sealed class Segment
    {
        public required string Id { get; init; }
        public int Speaker { get; set; }
        public long StartSample { get; init; }
        public long EndSample { get; set; }
        public long FirstFrame { get; init; }
        public long LastFrame { get; set; }
        public int PreviousSpeaker { get; init; }
        public double PreviousGap { get; init; }
        public double Activity { get; set; } // 这条轨道在窗口内达到过的最强活动度
        public int Revision { get; set; }
        public bool Extendable { get; set; } = true;
        public bool Decided { get; set; }
        public long DecidedSample { get; set; } // 上一次判定所依据的结尾
        public double Confidence { get; set; }
    }
}
