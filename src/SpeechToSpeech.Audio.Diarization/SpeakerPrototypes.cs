namespace SpeechToSpeech.Audio.Diarization;

/// <summary>
/// 会话的说话人身份表——把逐轮声纹变成稳定 speaker 编号的那层状态。每个说话人一个原型
/// （L2 归一化质心 + 建起它的证据量）；轮次按余弦距离比对，比中的折回该原型，
/// 于是同一个声音跨很多窗口也只占一个 id，全程不需要重新聚类整场。
///
/// <para><b>id 稳定性。</b>id 按创建顺序发放，永不重编号、永不复用。id 唯一会消失的方式是合并
/// （两个原型被证明是同一个人），而且连那时幸存的也是更旧的那个——消费方的历史因此保持原义。</para>
///
/// <para><b>短语音与低置信度。</b>一个轮次要够 <see cref="StreamingDiarizerConfig.MinNewSpeakerEvidence"/>
/// 秒语音<b>且</b>平均活动度不低于 <see cref="StreamingDiarizerConfig.MinTurnConfidence"/> 才有资格新建说话人。
/// 其余的都靠<b>借用</b>：轮次紧跟前一位（在 <see cref="StreamingDiarizerConfig.ContinuityWindow"/> 内）
/// 就沿用他，否则挂最近的说话人——这种段发布为未定，因为证据本来就薄。
/// 唯一不猜的情形是「没有原型靠得近、轮次又太薄」：那是 <see cref="SpeakerSegmentUpdate.UnknownSpeaker"/>，
/// 而不是凭空造一个说话人。</para>
/// </summary>
internal sealed class SpeakerPrototypes
{
    private sealed class Prototype
    {
        public required int Id { get; init; }
        public required double[] Centroid { get; set; } // L2 归一化
        public double Weight { get; set; }              // 语音秒数，有上限（约束漂移）
        public double TotalEvidence { get; set; }       // 语音秒数，无上限（可合并性判据）
    }

    private readonly StreamingDiarizerConfig _config;
    private readonly List<Prototype> _prototypes = [];
    private int _nextId;

    public SpeakerPrototypes(StreamingDiarizerConfig config)
        => _config = config ?? throw new ArgumentNullException(nameof(config));

    /// <summary>按创建顺序排列的说话人 id。</summary>
    public IReadOnlyList<int> SpeakerIds
    {
        get
        {
            var ids = new int[_prototypes.Count];
            for (int i = 0; i < _prototypes.Count; i++) ids[i] = _prototypes[i].Id;
            return ids;
        }
    }

    /// <summary>
    /// 把一个轮次判给某个说话人，并按证据决定是否新建/更新原型。证据薄<b>不抛异常</b>——
    /// 它回答 <see cref="SpeakerSegmentUpdate.UnknownSpeaker"/>，于是一次噪声抖动造不出一个身份。
    /// </summary>
    /// <param name="embedding">该轮次的声纹（不必单位长度）。</param>
    /// <param name="durationSeconds">该轮次的语音秒数。</param>
    /// <param name="confidence">该轮次自己那条轨道在窗口内的平均活动度，[0, 1]。</param>
    /// <param name="previousSpeaker">最近一条已提交段的说话人，没有则 -1。</param>
    /// <param name="gapSeconds">那条段结束到本轮回开始的静音（没有则 ∞）。</param>
    /// <param name="enrol">
    /// <c>false</c> 表示除「新建说话人」之外原型只读：匹配仍然返回 id，但不移动该原型的质心。
    /// 用于仍在生长的轮次的未定标签——片段不能把已经站稳的声音往自己这边拖
    /// （否则进行中的重叠会把别人的声音教进这个说话人）。真正入册的是轮次的收尾判定。
    /// </param>
    public SpeakerDecision Decide(
        float[] embedding,
        double durationSeconds,
        double confidence,
        int previousSpeaker,
        double gapSeconds,
        bool enrol = true)
    {
        ArgumentNullException.ThrowIfNull(embedding);

        bool evidenced = durationSeconds >= _config.MinNewSpeakerEvidence
                         && confidence >= _config.MinTurnConfidence;

        if (_prototypes.Count == 0)
        {
            if (!evidenced)
                return new SpeakerDecision(SpeakerSegmentUpdate.UnknownSpeaker, double.MaxValue, Confident: false, Created: false);
            var created = Create(embedding, durationSeconds);
            return new SpeakerDecision(created, 0, Confident: true, Created: true);
        }

        var (best, distance) = Nearest(embedding);

        if (distance <= _config.MatchThreshold)
        {
            if (enrol) Update(best, embedding, durationSeconds);
            return new SpeakerDecision(best.Id, distance, Confident: true, Created: false);
        }

        if (evidenced && distance > _config.NewSpeakerThreshold && _prototypes.Count < _config.MaxSpeakers)
        {
            var created = Create(embedding, durationSeconds);
            return new SpeakerDecision(created, distance, Confident: true, Created: true);
        }

        // 证据薄（太短或太轻）：优先判给刚刚在说话的那个人。这就是「嗯」「对」这类附和声——
        // 对话里 300 ms 的一下，几乎总是同一个人。
        if (!evidenced && previousSpeaker >= 0 && gapSeconds <= _config.ContinuityWindow && Find(previousSpeaker) is { } previous)
        {
            if (enrol) Update(previous, embedding, durationSeconds);
            return new SpeakerDecision(previous.Id, distance, Confident: false, Created: false);
        }

        // 模糊带（证据够但分不开）或说话人额度已满：挂到最近的原型，标签可信度如实说明这一点。
        if (enrol) Update(best, embedding, durationSeconds);
        return new SpeakerDecision(best.Id, distance, Confident: false, Created: false);
    }

    /// <summary>
    /// 把被证明是同一个人的原型并进它更年长的兄弟。一个说话人只有在证据低于
    /// <see cref="StreamingDiarizerConfig.MergeEvidence"/> 时才可被合并——扛过很长一段对话的声音
    /// 永远不会被一个新人挤掉。幸存的是较小（更旧）的 id；退位的 id 上报出去，
    /// 便于消费方重映射修订窗口之外的历史。
    /// </summary>
    public IReadOnlyList<SpeakerMergeInfo> Merge(double nowSeconds)
    {
        List<SpeakerMergeInfo>? merges = null;

        bool merged = true;
        while (merged && _prototypes.Count > 1)
        {
            merged = false;
            var (a, b) = ClosestPair();
            if (a is null || b is null) break;
            if (Distance(a.Centroid, b.Centroid) >= _config.MergeThreshold) break;

            // 退位的是更年轻的说话人；而且它必须仍是个新人。
            var (retired, surviving) = a.Id > b.Id ? (a, b) : (b, a);
            if (retired.TotalEvidence >= _config.MergeEvidence) break;

            retired.TotalEvidence += surviving.TotalEvidence;
            surviving.Weight = Math.Min(_config.PrototypeWeightCap, surviving.Weight + retired.Weight);
            _prototypes.Remove(retired);
            (merges ??= []).Add(new SpeakerMergeInfo(retired.Id, surviving.Id, nowSeconds));
            merged = true;
        }

        return (IReadOnlyList<SpeakerMergeInfo>?)merges ?? [];
    }

    private Prototype? Find(int id)
    {
        foreach (var p in _prototypes) if (p.Id == id) return p;
        return null;
    }

    private int Create(float[] embedding, double durationSeconds)
    {
        var prototype = new Prototype
        {
            Id = _nextId++,
            Centroid = Normalize(embedding),
            Weight = Math.Min(durationSeconds, _config.PrototypeWeightCap),
            TotalEvidence = durationSeconds,
        };
        _prototypes.Add(prototype);
        return prototype.Id;
    }

    // 加权质心更新，一旦原型吃满 PrototypeWeightCap 秒就冻结：可加权重缩到 0，
    // 于是长会话不会把一个已经站稳的声音慢慢拖走。
    private void Update(Prototype prototype, float[] embedding, double durationSeconds)
    {
        prototype.TotalEvidence += durationSeconds;
        double headroom = _config.PrototypeWeightCap - prototype.Weight;
        if (headroom <= 0 || durationSeconds <= 0) return;

        double add = Math.Min(durationSeconds, headroom);
        double total = prototype.Weight + add;
        var unit = Normalize(embedding);
        for (int i = 0; i < prototype.Centroid.Length; i++)
            prototype.Centroid[i] = (prototype.Centroid[i] * prototype.Weight + unit[i] * add) / total;

        NormalizeInPlace(prototype.Centroid);
        prototype.Weight = total;
    }

    private (Prototype Best, double Distance) Nearest(float[] embedding)
    {
        Prototype? best = null;
        double bestDistance = double.MaxValue;
        foreach (var candidate in _prototypes)
        {
            double distance = Distance(candidate.Centroid, embedding);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return (best!, bestDistance);
    }

    // 最近的一对原型；并列时取最早插入的那对（合并因此是确定性的）。
    private (Prototype? A, Prototype? B) ClosestPair()
    {
        Prototype? bestA = null, bestB = null;
        double best = double.MaxValue;
        for (int a = 0; a < _prototypes.Count; a++)
        {
            for (int b = a + 1; b < _prototypes.Count; b++)
            {
                double distance = Distance(_prototypes[a].Centroid, _prototypes[b].Centroid);
                if (distance < best)
                {
                    best = distance;
                    bestA = _prototypes[a];
                    bestB = _prototypes[b];
                }
            }
        }
        return (bestA, bestB);
    }

    // 双精度质心与 float 声纹（接缝的向量类型）之间的余弦距离。与批量聚合同一定义；
    // 零向量判为最不相似（2.0），绝不出 NaN。
    private static double Distance(double[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += (double)b[i] * b[i];
        }
        if (normA <= 0 || normB <= 0) return 2.0;
        return 1.0 - Math.Clamp(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)), -1.0, 1.0);
    }

    private static double Distance(double[] a, double[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA <= 0 || normB <= 0) return 2.0;
        return 1.0 - Math.Clamp(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)), -1.0, 1.0);
    }

    private static double[] Normalize(float[] embedding)
    {
        var unit = new double[embedding.Length];
        for (int i = 0; i < embedding.Length; i++) unit[i] = embedding[i];
        NormalizeInPlace(unit);
        return unit;
    }

    // 零向量没有方向，就让它保持零（与任何东西的余弦距离都是 2，永远合并不上）：
    // 退化的声纹宁可匹配不上，也不能把说话人悄悄并到一起。
    private static void NormalizeInPlace(double[] vector)
    {
        double norm = 0;
        for (int i = 0; i < vector.Length; i++) norm += vector[i] * vector[i];
        if (norm <= 0) return;
        double scale = 1.0 / Math.Sqrt(norm);
        for (int i = 0; i < vector.Length; i++) vector[i] *= scale;
    }
}

/// <summary>一个轮次与会话说话人比对的结果。</summary>
/// <param name="Speaker">判给的说话人 id，或 <see cref="SpeakerSegmentUpdate.UnknownSpeaker"/>。</param>
/// <param name="Distance">与最近原型的余弦距离（没有可比对象时是 ∞）。</param>
/// <param name="Confident">判给是否建立在证据上；沿用/挂最近为 false。</param>
/// <param name="Created">这一轮是否创建了它被判给的说话人。</param>
internal readonly record struct SpeakerDecision(int Speaker, double Distance, bool Confident, bool Created)
{
    /// <summary>
    /// 标签的可信度：本论创建说话人 1.0；匹配到 1 − 距离；借用（沿用或挂最近）再减半；
    /// <see cref="SpeakerSegmentUpdate.UnknownSpeaker"/> 为 0。
    /// </summary>
    public double LabelConfidence
    {
        get
        {
            if (Speaker == SpeakerSegmentUpdate.UnknownSpeaker) return 0;
            if (Created) return 1.0;
            double similarity = Math.Clamp(1 - Distance, 0, 1);
            return Confident ? similarity : similarity * 0.5;
        }
    }
}
