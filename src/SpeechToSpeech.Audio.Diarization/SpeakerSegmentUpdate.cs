namespace SpeechToSpeech.Audio.Diarization;

/// <summary>
/// 一条段的发布步骤。流式分离是<b>可修订</b>的，不是只追加：模型一次只看一个窗口，
/// 所以一段的结尾（有时还有它的说话人）要等后一个窗口覆盖过它才知道。
/// 消费方因此把 <see cref="SpeakerSegmentUpdate"/> 当成按 <see cref="Id"/> 的 upsert：
///
/// <list type="bullet">
/// <item><see cref="Revision"/> = 0 是这条段的第一次发布；&gt; 0 替换同一 <see cref="Id"/> 之前发布的状态
/// （end 变长、合并后换了 speaker、或者终审结论）。</item>
/// <item><see cref="IsFinal"/> = <c>true</c> 表示几何已定——它的 end 不会再动。这是<b>提交</b>信号：
/// 未定的段仍够得着窗口的提交前沿，随时可能被延长。（说话人合并仍然可以修订一条已 final 的段，
/// 那由 <see cref="SpeakerMergeInfo"/> 通知，并受修订窗口约束——它动的是标签，不是边界。）</item>
/// </list>
///
/// <para><see cref="Speaker"/> 是会话内稳定的 0 基 id（按创建顺序）；<see cref="UnknownSpeaker"/> 标记
/// 「是语音，但归不了人」（太短、太轻、或说话人上限已满）。时间是会话音频时间轴上的绝对秒——
/// 与转写时间戳同一根时钟。</para>
///
/// <para><see cref="Confidence"/> 是<b>标签</b>的可信度，不是语音检测的可信度：新建的说话人为 1.0，
/// 匹配上的是 1 − 余弦距离，借用来的大约再减半（短轮次沿用前一位，或含糊不清挂到最近的原型），
/// <see cref="UnknownSpeaker"/> 为 0。它不参与 <see cref="IsFinal"/> 的判定——想要更严的标签，
/// 按它过滤即可，不必和修订协议较劲。</para>
/// </summary>
public sealed record SpeakerSegmentUpdate(
    string Id,
    int Speaker,
    double Start,
    double End,
    bool IsFinal,
    int Revision,
    double Confidence)
{
    /// <summary>是语音但归不了人时用的说话人 id。</summary>
    public const int UnknownSpeaker = -1;
}

/// <summary>
/// 说话人合并通知：两个原型被证明是同一个人，<see cref="RetiredSpeaker"/> 退位到 <see cref="SurvivingSpeaker"/>。
/// 修订窗口之内的段会用幸存 id 重新发布；更早的不再重写，持有长历史的消费方用这条记录自行重映射。
/// id 永不复用——退位的 id 在本次会话里一直是退位状态。
/// </summary>
public sealed record SpeakerMergeInfo(int RetiredSpeaker, int SurvivingSpeaker, double AtSeconds);

/// <summary>
/// 一次分离步骤的全部产出：按时间顺序的段 upsert，加上可能发生的合并。
/// 什么都没提交的步骤（大多数）返回 <see cref="Empty"/>。
/// </summary>
public sealed record DiarizerUpdate(
    IReadOnlyList<SpeakerSegmentUpdate> Segments,
    IReadOnlyList<SpeakerMergeInfo> Merges)
{
    /// <summary>什么都没产出的一个步骤。</summary>
    public static DiarizerUpdate Empty { get; } = new([], []);
}
