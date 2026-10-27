using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Core.Backends;

/// <summary>
/// 口型提取后端：把一段音频转成口型区间。
///
/// 对应上游 <c>STV/w2v_stv_handler.py</c> 里 <c>speech_to_visemes()</c> 这一层
/// （上游是 wav2vec2 音素 ASR + 音素→口型映射，本构建是 OpenLipSync TCN，
/// 引擎差异见差异账 D031 / C033）。管线级的缓冲、偏移累加与分块重排由
/// <see cref="Pipeline.Handlers.StvHandler"/> 负责，与后端无关。
///
/// 调用约定：一次 <see cref="Extract"/> 对应上游的一次「0.5 秒窗口推理」——
/// 每次调用都是独立上下文，不跨窗口携带声学状态（上游每个窗口也是一次全新 ASR 调用）。
/// </summary>
public interface IVisemeExtractor : IDisposable
{
    /// <summary>引擎期望的输入采样率（单声道 PCM16）。</summary>
    int SampleRate { get; }

    /// <summary>
    /// 把一段单声道小端 PCM16 转成口型区间。
    ///
    /// 返回的 <c>StartSeconds/EndSeconds</c> 相对**本段音频起点**（管线侧再加窗口偏移）；
    /// 提取失败返回空列表——口型是附加信息，不能让助手没声音。
    /// </summary>
    IReadOnlyList<Viseme> Extract(ReadOnlySpan<byte> pcm16);
}
