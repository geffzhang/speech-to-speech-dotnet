using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Runtime;

namespace SpeechToSpeech.Core.Backends;

/// <summary>后端能力标记，对应上游 <c>BackendCapabilities</c>。</summary>
public sealed record BackendCapabilities
{
    /// <summary>该 STT 后端直接产出 <see cref="AudioInputCompletedEvent"/>，绕过 TranscriptionNotifier。</summary>
    public bool BypassesTranscriptionNotifier { get; init; }

    /// <summary>该 STT 后端消费原始音频块（而非整段），支持有状态流式转写。</summary>
    public bool StreamsAudioChunks { get; init; }

    /// <summary>该 LLM 后端支持 <c>/v1/realtime</c> 之外的直连音频输入。</summary>
    public bool SupportsAudioInput { get; init; }

    /// <summary>该 LLM 后端支持把 realtime 请求代理到上游。</summary>
    public bool SupportsLlmProxy { get; init; }

    /// <summary>该 TTS 后端需要感知助手语言以选择发音。</summary>
    public bool PassesAssistantLanguageFlag { get; init; }
}

/// <summary>VAD 参数。</summary>
public sealed class VadOptions
{
    public string? ModelPath { get; init; }
    public string? ConfigPath { get; init; }

    /// <summary>语音概率阈值；0 表示用模型自带阈值。</summary>
    public float Threshold { get; init; }

    public int SampleRate { get; init; } = 16_000;
    public int ThreadsNum { get; init; } = 1;

    /// <summary>判定语音结束所需的最短静音时长。</summary>
    public int MinSilenceMs { get; init; } = 500;

    /// <summary>最短语音时长，短于此的段被丢弃。</summary>
    public int MinSpeechMs { get; init; } = 250;

    /// <summary>切段时向前补的音频时长。</summary>
    public int PrefixPaddingMs { get; init; } = 300;

    public bool IsDebug { get; init; }
}

/// <summary>STT 参数。</summary>
public sealed class SttOptions
{
    public string? ModelDir { get; init; }
    public string? Language { get; init; }
    public int ThreadsNum { get; init; } = 2;
    public int SampleRate { get; init; } = 16_000;
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? ModelName { get; init; }

    /// <summary>是否输出流式增量结果。</summary>
    public bool EmitPartials { get; init; } = true;

    /// <summary>
    /// 流式转写后端（openai-realtime / vllm-realtime）的会话模型 id。
    /// 注意 <see cref="ModelName"/> 在 CLI 组合根里承载的是<b>后端名</b>，转写模型另存这里。
    /// </summary>
    public string? StreamingModel { get; init; }

    /// <summary>WebSocket 连接与会话建立的超时（上游 openai/vllm_realtime_stt_connect_timeout，默认 10s）。</summary>
    public double ConnectTimeoutSeconds { get; init; } = 10.0;

    /// <summary>定稿转写的最长等待（上游 *_final_timeout，默认 60s）。</summary>
    public double FinalTimeoutSeconds { get; init; } = 60.0;
}

/// <summary>LLM 参数。</summary>
public sealed class LlmOptions
{
    public string? ModelPath { get; init; }
    public string? MmProjPath { get; init; }
    public string? Backend { get; init; }
    public int MaxNewTokens { get; init; } = 512;
    public double Temperature { get; init; } = 0.6;
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? ModelName { get; init; }
}

/// <summary>TTS 参数。</summary>
public sealed class TtsOptions
{
    public string? ModelDir { get; init; }

    /// <summary>音色。Qwen3-TTS 使用 <c>ryan</c>/<c>serena</c> 等预设名。</summary>
    public string? Speaker { get; init; }

    public string? Language { get; init; }
    public string? Instruct { get; init; }
    public int SampleRate { get; init; } = 24_000;
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? ModelName { get; init; }
    public string? Voice { get; init; }
    public int MaxConcurrency { get; init; } = 1;
}

/// <summary>VAD 帧类型。</summary>
public enum VadFrameKind
{
    /// <summary>检测到用户开口。</summary>
    SpeechStarted,

    /// <summary>语音段结束（此时 <see cref="VadFrame.Audio"/> 携带整段音频）。</summary>
    SpeechStopped,

    /// <summary>没有状态变化，仅推进内部窗口。</summary>
    None,
}

/// <summary>VAD 处理一块音频后的产出。</summary>
/// <param name="SpeechEndSample">
/// 这一段里<b>最后一个有声窗</b>的结束样本位（相对本次会话的累计样本流，不是段内偏移）。
/// 用于算 <c>vad_decision_s</c>：从用户真的闭嘴到我们下结论，中间隔着判定器的静音确认窗口。
///
/// 与 <paramref name="AudioEndMs"/> 是两个量：段尾包含用来闭合这段的尾部静音，
/// 拿它当「语音结束」会把静音长度从延迟里抹掉。后端给不出就留 null——
/// 缺口写成 null 比写一个偏小的数字诚实。
/// </param>
public sealed record VadFrame(
    VadFrameKind Kind,
    float[]? Audio = null,
    int AudioStartMs = 0,
    int AudioEndMs = 0,
    double DurationSeconds = 0,
    int? SpeechEndSample = null);

/// <summary>
/// VAD 后端接缝。
///
/// 唯一职责：吃 PCM 块，吐开口/闭嘴判定。切出的语音段由 handler 包成 <see cref="VadAudio"/> 送入 STT。
/// </summary>
public interface IVadBackend : IDisposable
{
    string Name { get; }

    /// <summary>
    /// 推入一块 16 kHz 单声道 float PCM，返回本次产生的帧（可能为空）。
    /// <paramref name="runtimeConfig"/> 提供每会话的阈值覆盖。
    /// </summary>
    IEnumerable<VadFrame> Push(float[] samples, RuntimeConfig runtimeConfig);

    /// <summary>会话结束：丢弃内部状态，不销毁模型。</summary>
    void ResetSession();
}

/// <summary>
/// STT 后端接缝。
///
/// 产出 <see cref="PartialTranscription"/>（可选）与 <see cref="Transcription"/> / <see cref="TranscriptionFailure"/>。
/// </summary>
public interface ISttBackend : IDisposable
{
    string Name { get; }

    IEnumerable<PipelineMessage> Transcribe(VadAudio audio, CancellationToken cancellationToken);
}

/// <summary>
/// 流式 STT 的 VAD 侧入口（上游 <c>StatefulStreamingSTTHandler</c> 的流式面：
/// append_audio / start_turn / commit_boundary / discard_utterance / cancel_session）。
///
/// VAD 在说话过程中直接推音频，转写 partial 与普通后端一样走 STT 输出队列——
/// <see cref="Pipeline.Handlers.TranscriptionNotifier"/> 已有 partial 消费路径。
/// </summary>
public interface IStreamingSttSink
{
    /// <summary>开口/复开：标记流式会话的当前轮次。</summary>
    void StartTurn(string? turnId, int? turnRevision);

    /// <summary>推入一段 16 kHz 单声道 float PCM（管线率，内部按需重采样）。</summary>
    void AppendAudio(float[] samples);

    /// <summary>段定稿：冲掉重采样尾样本并入队提交票据。</summary>
    void CommitBoundary(string? turnId, int? turnRevision);

    /// <summary>丢弃本轮未定稿的音频（barge-in / 空段）。</summary>
    void DiscardUtterance();

    /// <summary>会话结束：推进代际并断开远端连接。</summary>
    void CancelSession();
}

/// <summary>
/// 需要按连接挂载运行时资源（输出队列 / 投机轮次 tracker / 取消令牌）的流式 STT 后端。
/// <see cref="Attach"/> 在管线装配期调用，返回的 sink 交给 <see cref="Pipeline.Handlers.VadHandler"/>。
/// </summary>
public interface IStreamAwareSttBackend : ISttBackend
{
    IStreamingSttSink? Attach(
        System.Collections.Concurrent.BlockingCollection<object> queueOut,
        Pipeline.SpeculativeTurnTracker speculativeTurns,
        CancellationToken stopped);
}

/// <summary>一次 LLM 生成的输入快照。</summary>
public sealed class LlmRequest
{
    public required IReadOnlyList<ChatMessage> History { get; init; }
    public string? Instructions { get; init; }
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
    public int? MaxOutputTokens { get; init; }
    public double? Temperature { get; init; }

    /// <summary>直接音频输入（无 STT 路径）。</summary>
    public float[]? Audio { get; init; }

    public int AudioSampleRate { get; init; } = 16_000;
}

/// <summary>
/// LLM 后端接缝。
///
/// 流式产出有序的助手输出分片。失败必须编码为 <see cref="EndOfResponse.Error"/> 或抛可诊断异常，
/// 不允许把半截输出当成成功（见 <c>porting/02-differences.md</c> 实现纪律一节）。
/// </summary>
public interface ILanguageModelBackend : IDisposable
{
    string Name { get; }

    IAsyncEnumerable<LlmResponseChunk> GenerateAsync(LlmRequest request, CancellationToken cancellationToken);
}

/// <summary>一次合成请求。</summary>
public sealed class TtsRequest
{
    public required string Text { get; init; }

    /// <summary>已按 TTS 规则快照过的语言码；null 表示交给模型自动判定。</summary>
    public string? Language { get; init; }

    public string? Speaker { get; init; }
    public string? Instruct { get; init; }
}

/// <summary>
/// TTS 后端接缝。
///
/// 产出的每个 <c>byte[]</c> 都是 int16 小端、单声道、<see cref="SampleRate"/> 采样率的 PCM 块。
/// 句级切分由 handler 负责，后端只保证"给一段文本，吐连续 PCM"。
/// </summary>
public interface ITtsBackend : IDisposable
{
    string Name { get; }

    int SampleRate { get; }

    IEnumerable<byte[]> Synthesize(TtsRequest request, CancellationToken cancellationToken);
}

/// <summary>后端无法在当前构建中实例化时抛出。</summary>
public sealed class BackendUnavailableException : Exception
{
    public BackendUnavailableException(string backendName, string reason)
        : base($"后端 '{backendName}' 在当前 C# 构建中不可用：{reason}")
    {
        BackendName = backendName;
        Reason = reason;
    }

    public string BackendName { get; }

    public string Reason { get; }
}
