namespace SpeechToSpeech.Audio.Diarization;

/// <summary>
/// <b>帧级</b>说话人分割接缝：输入一个定长窗口，输出逐帧、逐本地说话人的活动概率。
///
/// 与「语音/静音」二分类接缝的区别正在这里：powerset 掩码不被压平，于是同一帧上可以有两个人同时为真
/// （重叠发言），换人表现为两条轨道各自起落而不是一条开关沿。流式分离要用它做三件事：按换人切轮、
/// 保留重叠、以及挑出该做声纹的那段音频。
///
/// <para>引擎刻意做成<b>无状态、单窗口</b>：窗口调度、滑窗重叠、绝对时间轴、全部会话状态都在
/// <see cref="StreamingDiarizer"/>——模型侧因此是一个纯函数，测试里可以用两个音调伪造。</para>
///
/// <para>这里的说话人编号<b>只在窗口内有意义</b>（powerset 头的通道顺序），跨窗口不带身份。
/// 让它变成会话级 id 的是 <see cref="StreamingDiarizer"/>：每个本地轮次取一次声纹，与会话原型比对。</para>
/// </summary>
public interface ISpeakerMaskSegmentation
{
    /// <summary>引擎要求的采样率；用别的采样率喂窗口是编程错误。</summary>
    int SampleRate { get; }

    /// <summary><see cref="Analyze"/> 期望的精确样本数（模型窗口几何）。</summary>
    int WindowSamples { get; }

    /// <summary>相邻输出帧之间的步长，单位样本（pyannote-3.0 为 10 ms）。</summary>
    int FrameShiftSamples { get; }

    /// <summary>
    /// 帧网格位置到该帧所描述时刻的偏移（感受野的一半——模型把一帧居中在它概括的音频上）。
    /// 对外报出的段边界要加上它。
    /// </summary>
    int FrameOffsetSamples { get; }

    /// <summary>每帧概率向量的宽度（模型的本地说话人通道数）。</summary>
    int MaxLocalSpeakers { get; }

    /// <summary>
    /// 给一个窗口打分。<paramref name="window"/> 必须正好 <see cref="WindowSamples"/> 长。
    /// 返回的每帧概率在 [0, 1]：静音是全零，重叠是同一帧上两个（或更多）非零项。
    /// </summary>
    SpeakerMaskWindow Analyze(ReadOnlySpan<float> window);
}

/// <summary>
/// 一个窗口的说话人掩码：<c>FrameProbabilities[f][s]</c> 是本地说话人 <c>s</c> 在第 <c>f</c> 帧上活跃的概率。
/// 行是定宽的（<see cref="ISpeakerMaskSegmentation.MaxLocalSpeakers"/>）。
/// </summary>
public sealed record SpeakerMaskWindow(IReadOnlyList<float[]> FrameProbabilities)
{
    /// <summary>窗口内的帧数。</summary>
    public int FrameCount => FrameProbabilities.Count;

    /// <summary>空窗口（没有帧）——零长输入解码出来的就是这个。</summary>
    public static SpeakerMaskWindow Empty { get; } = new([]);
}

/// <summary>
/// 一段音频的定维说话人声纹。模型侧实现（如 WeSpeaker），<see cref="StreamingDiarizer"/> 每个本地轮次调一次，
/// 用余弦距离与会话原型比对。向量不必是单位长度——归一化在比对处做。
/// </summary>
public interface ISpeakerEmbedding
{
    /// <summary>该模型每次产出的固定宽度。</summary>
    int EmbeddingDim { get; }

    /// <summary>把 <paramref name="sampleRate"/> 采样率的单声道 float 音频压成一个说话人向量。</summary>
    float[] Embed(ReadOnlySpan<float> audio, int sampleRate);
}
