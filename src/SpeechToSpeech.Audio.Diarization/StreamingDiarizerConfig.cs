namespace SpeechToSpeech.Audio.Diarization;

/// <summary>
/// 流式分离的可调项。其中两项决定了这项能力「拿多少时延换多少准确度」，所以显式命名而不是藏在默认值里：
/// <see cref="WindowSamples"/> / <see cref="HopSamples"/> 决定前瞻（<c>(window − hop) / 2</c>），
/// <see cref="ContinuityWindow"/> 决定一个短/低置信轮次可以沿用前一位说话人多久而不新建。
/// </summary>
public sealed record StreamingDiarizerConfig
{
    /// <summary>推入分离器的音频采样率（必须与分割引擎一致）。</summary>
    public int SampleRate { get; init; } = 16_000;

    /// <summary>
    /// 模型窗口长度，单位样本。<c>0</c> = 用引擎自己的 <see cref="ISpeakerMaskSegmentation.WindowSamples"/>。
    /// 窗口越大越准、前瞻越长：一条段要等「拥有它中心」的那个窗口跑完才能提交，而那离「现在」还有
    /// <c>(window − hop) / 2</c>。
    /// </summary>
    public int WindowSamples { get; init; }

    /// <summary>
    /// 相邻窗口之间的推进量。<c>0</c> = <c>WindowSamples / 10</c>（参照实现的 10% 跳长）。
    /// 不得超过窗口的三分之一，否则一个窗口看不清它该负责决定的那一段。
    /// </summary>
    public int HopSamples { get; init; }

    /// <summary>本地说话人轨道开启的概率阈值；也是判定「凹陷」的界线。</summary>
    public double Onset { get; init; } = 0.5;

    /// <summary>
    /// 概率掉到 <see cref="Onset"/> 之下持续多久之后关闭该轮。
    ///
    /// <para>这里曾经有一个更低的 offset（onset 的 0.7 倍）作为关断门槛，参照实现就是这么写的。
    /// 真实权重上它不成立：模型对两段语音之间的短停顿给出约 0.40 的持续弱响应，既低于 onset
    /// 又远高于 0.35，于是凹陷永远不成立、两个人的话被缝成一段，声纹取到混合声后整场只剩一个
    /// 说话人。凹陷只按 onset 判定——多切几段无害（同一人的相邻段共享标签），缝在一起就没救了。</para>
    /// </summary>
    public double MinSilenceDuration { get; init; } = 0.2;

    /// <summary>短于此值的轮次不单独判定，交给连续性策略。</summary>
    public double MinSpeechDuration { get; init; } = 0.25;

    /// <summary>短于此跨度的音频从不取声纹（声纹会描述得更多是静音而不是说话）。</summary>
    public double MinEmbeddingDuration { get; init; } = 0.4;

    /// <summary>「这个轮次是已知说话人」的余弦距离上限。</summary>
    public double MatchThreshold { get; init; } = 0.45;

    /// <summary>
    /// 余弦距离超过它、且轮次<b>证据充分</b>时才新建说话人。介于它与 <see cref="MatchThreshold"/> 之间
    /// 是模糊带：挂到最近的说话人，段标为未定。
    /// </summary>
    public double NewSpeakerThreshold { get; init; } = 0.62;

    /// <summary>单个轮次至少贡献多少秒语音才有资格新建说话人。</summary>
    public double MinNewSpeakerEvidence { get; init; } = 1.0;

    /// <summary>轮次平均逐帧活动度要达到多少才有资格新建说话人。</summary>
    public double MinTurnConfidence { get; init; } = 0.5;

    /// <summary>上一轮结束之后多久之内，短/低置信轮次可以沿用那位说话人。</summary>
    public double ContinuityWindow { get; init; } = 1.5;

    /// <summary>同一说话人、间隔不超过此值的两段合并成一段。</summary>
    public double MaxMergeGap { get; init; } = 0.6;

    /// <summary>说话人上限。到顶也不重编号——新轮次挂到最近的说话人。</summary>
    public int MaxSpeakers { get; init; } = 10;

    /// <summary>累计语音秒数超过它之后原型不再移动（约束长会话漂移）。</summary>
    public double PrototypeWeightCap { get; init; } = 20.0;

    /// <summary>一次说话人合并能回溯改写多早之前发布过的段。更早的由消费方按合并通知重映射。</summary>
    public double RevisionWindow { get; init; } = 30.0;

    /// <summary>两个原型近于此值就是同一个人；较新的 id 退位到较旧的那个。</summary>
    public double MergeThreshold { get; init; } = 0.3;

    /// <summary>说话人只有在累计证据低于此值时才可以被合并（已经站稳的说话人不会消失）。</summary>
    public double MergeEvidence { get; init; } = 3.0;

    /// <summary>保留音频的上限，单位秒——环形缓冲的容量（不随会话增长）。</summary>
    public double MaxBufferedSeconds { get; init; } = 60;

    /// <summary>把生效的窗口长度对引擎几何解出来。</summary>
    /// <summary>
    /// 解出生效的窗口与跳长。两件事分开对待：
    ///
    /// <para><b>模型声明的窗口</b>由本方法取整到帧网格。下游全部时间映射都假设窗口和跳长是整数个帧
    /// （<c>windowStart / frameShift</c> 取整才等于帧序号），而模型的 window_size 未必是帧步长的整数倍。
    /// sherpa 的 pyannote segmentation-3.0 导出正是这样——<c>window_size=160000</c> 对
    /// <c>receptive_field_shift=270</c>，160000/270 = 592.6。向下取整成 592 帧 = 159840 样本（9.99 秒）
    /// 才是「10 秒窗口」这个意图在网格上的表示；照抄原值会被几何校验拒掉。</para>
    ///
    /// <para><b>调用方显式给的窗口/跳长</b>原样返回，不进网格也不钳制——给错了就该在构造期被拒
    /// （见 <see cref="StreamingDiarizer"/> 的几何校验），静默改写只会把配置错误藏起来。</para>
    ///
    /// <para>只有<b>默认跳长</b>会自己凑齐奇偶：own 区是窗口中心一个跳长宽，起点
    /// <c>(window − hop) / 2</c> 也要落在帧网格上，即窗口与跳长的帧数必须同奇偶。592 帧的窗口配 59 帧
    /// （10%）会偏半帧，退一帧到 58 才是合法的默认值——这就是它看起来不是正 10% 的原因。</para>
    /// </summary>
    public (int WindowSamples, int HopSamples) ResolveGeometry(ISpeakerMaskSegmentation segmentation)
    {
        ArgumentNullException.ThrowIfNull(segmentation);
        int shift = Math.Max(1, segmentation.FrameShiftSamples);

        int window = WindowSamples > 0
            ? WindowSamples
            : Math.Max(shift, segmentation.WindowSamples / shift * shift);

        if (HopSamples > 0) return (window, HopSamples);

        int frames = window / shift;
        int hopFrames = Math.Clamp(window / 10 / shift, 1, Math.Max(1, frames / 3));
        if ((frames - hopFrames) % 2 != 0) hopFrames = hopFrames > 1 ? hopFrames - 1 : hopFrames + 1;
        return (window, hopFrames * shift);
    }
}
