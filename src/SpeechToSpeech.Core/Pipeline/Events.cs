namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// 从 pipeline handler 流向 realtime router 的类型化事件。
///
/// VAD 与转写事件走 <c>text_output_queue</c>；依赖响应的助手事件与 token 用量事件
/// 与对应音频共用 TTS 输出队列，以保证模型原始的文本/工具/音频顺序。
/// <see cref="EventType"/> 与协议事件名不同：协议名由 realtime 层决定（见差异账 C-07）。
/// </summary>
public abstract class PipelineEvent
{
    public abstract string EventType { get; }

    public string? TurnId { get; init; }

    public int? TurnRevision { get; init; }
}

// ── VAD 事件 ──────────────────────────────────────────────────────────

/// <summary>检测到用户开口。</summary>
public sealed class SpeechStartedEvent : PipelineEvent
{
    public override string EventType => "speech_started";

    public int AudioStartMs { get; init; }
    public bool Reopened { get; init; }

    /// <summary>服务端 VAD 打断：为 true 时会以 <c>turn_detected</c> 取消在途响应。</summary>
    public bool InterruptResponse { get; init; } = true;
}

/// <summary>用户语音段结束。</summary>
public sealed class SpeechStoppedEvent : PipelineEvent
{
    public override string EventType => "speech_stopped";

    public double DurationSeconds { get; init; }
    public int AudioEndMs { get; init; }
}

// ── 转写事件（TranscriptionNotifier）─────────────────────────────────

/// <summary>
/// 单个输入音频项的累计 STT 假设。
///
/// <see cref="Delta"/> 保留是为公开 API 兼容；其值是累计假设，不是线上的增量 delta。
/// </summary>
public sealed class PartialTranscriptionEvent : PipelineEvent
{
    public override string EventType => "partial_transcription";

    public required string Delta { get; init; }
}

/// <summary>最终转写完成。</summary>
public sealed class TranscriptionCompletedEvent : PipelineEvent
{
    public override string EventType => "transcription_completed";

    public required string Transcript { get; init; }
    public SpeakerAttribution? SpeakerAttribution { get; init; }
    public string? LanguageCode { get; init; }
    public double? SpeechStoppedAtSeconds { get; init; }
}

/// <summary>转写失败。</summary>
public sealed class TranscriptionFailedEvent : PipelineEvent
{
    public override string EventType => "transcription_failed";

    public required string Message { get; init; }
}

// ── 直接音频事件（AudioInputNotifier）────────────────────────────────

/// <summary>等待 realtime 响应记账的最终 VAD 音频。</summary>
public sealed class AudioInputCompletedEvent : PipelineEvent
{
    public override string EventType => "audio_input_completed";

    public required float[] Audio { get; init; }
    public int AudioSampleRate { get; init; } = 16_000;
    public double AudioDurationSeconds { get; init; }
    public double? SpeechStoppedAtSeconds { get; init; }
}

// ── LLM 输出事件（LMOutputProcessor）─────────────────────────────────

/// <summary>等待 API 侧序列化的有序助手输出。</summary>
public sealed class AssistantOutputEvent : PipelineEvent
{
    public override string EventType => "assistant_text";

    public ResponseHistory? History { get; init; }
    public List<AssistantOutputPart> Parts { get; init; } = [];
    public string Text { get; init; } = "";
    public List<ToolCall> Tools { get; init; } = [];
    public int? CancelGeneration { get; init; }
    public string? ResponseKey { get; init; }

    /// <summary>LMOutputProcessor 分配的单调分片序号。</summary>
    public int? OutputSequence { get; init; }

    /// <summary>把 <see cref="Parts"/> 与兼容视图投影成一致结果。</summary>
    public AssistantOutputEvent Normalize()
    {
        var parts = Parts.Count > 0
            ? Parts
            : Text.Length > 0
                ? [new AssistantTextPart { Text = Text }]
                : [];

        return new AssistantOutputEvent
        {
            History = History,
            Parts = parts,
            Text = string.Concat(parts.OfType<AssistantTextPart>().Select(p => p.Text)),
            Tools = parts.OfType<AssistantToolCallPart>().Select(p => p.Tool).ToList(),
            CancelGeneration = CancelGeneration,
            ResponseKey = ResponseKey,
            OutputSequence = OutputSequence,
            TurnId = TurnId,
            TurnRevision = TurnRevision,
        };
    }
}

/// <summary>
/// 不必等前置 TTS 即可下发的工具调用。
///
/// 对应的 <see cref="AssistantOutputEvent"/> 仍走 TTS 队列并负责音频收尾顺序；
/// 该侧信道事件只让 function-call 参数事件尽早到达客户端，使其在合成继续时就能执行工具。
/// </summary>
public sealed class AssistantToolCallReadyEvent : PipelineEvent
{
    public override string EventType => "assistant_tool_call_ready";

    public ResponseHistory? History { get; init; }
    public required AssistantToolCallPart Part { get; init; }
    public required int OutputSequence { get; init; }
    public int? CancelGeneration { get; init; }
    public string? ResponseKey { get; init; }
}

/// <summary>Token 用量事件。</summary>
public sealed class TokenUsageEvent : PipelineEvent
{
    public override string EventType => "token_usage";

    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int? CancelGeneration { get; init; }
    public string? ResponseKey { get; init; }
}

/// <summary>标记有序输出完成，并携带本次响应消费的 tool 结果 id。</summary>
public sealed class AssistantResponseDoneEvent : PipelineEvent
{
    public override string EventType => "assistant_response_done";

    public ResponseHistory? History { get; init; }
    public ResponseStatus Status { get; init; } = ResponseStatus.Completed;
    public ResponseIncompleteReason Reason { get; init; } = ResponseIncompleteReason.None;
    public string? ResponseKey { get; init; }
    public List<string> InputToolCallIds { get; init; } = [];
    public int? CancelGeneration { get; init; }
}

/// <summary>
/// 内部信号：LM 生成已结束但下游 TTS 尚未排空。永不序列化到客户端。
/// </summary>
public sealed class ResponseGenerationDoneEvent : PipelineEvent
{
    public override string EventType => "response_generation_done";

    public ResponseHistory? History { get; init; }
    public string? ResponseKey { get; init; }
    public List<string> CallIds { get; init; } = [];
    public bool Succeeded { get; init; } = true;
    public int? CancelGeneration { get; init; }
}

/// <summary>响应无法生成（例如无带外输入），服务应以 <c>status="failed"</c> 关闭。</summary>
public sealed class ResponseFailedEvent : PipelineEvent
{
    public override string EventType => "response_failed";

    public ResponseHistory? History { get; init; }
    public string Message { get; init; } = "";
    public int? CancelGeneration { get; init; }
    public string? ResponseKey { get; init; }
}
