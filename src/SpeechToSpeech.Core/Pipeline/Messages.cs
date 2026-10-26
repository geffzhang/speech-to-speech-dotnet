using System.Diagnostics;
using SpeechToSpeech.Core.Runtime;

namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// 所有类型化管线消息的基类。
///
/// 对应 Python 侧 <c>speech_to_speech/pipeline/messages.py</c> 的 <see cref="PipelineMessage"/>。
/// Python 用 Pydantic 的 <c>tag</c> 做联合类型判别；C# 侧用 CLR 类型本身判别，
/// <see cref="Tag"/> 仅用于日志与差分语料对齐，不参与分派。
/// </summary>
public abstract class PipelineMessage
{
    /// <summary>与 Python 侧 <c>tag</c> 字面值一致，用于日志与语料比对。</summary>
    public abstract string Tag { get; }
}

// ── VAD → STT ─────────────────────────────────────────────────────────

/// <summary>
/// VAD 切出的语音段，可携带 realtime 转写模式。
/// 对应 <c>VADAudio</c>。
/// </summary>
public sealed class VadAudio : PipelineMessage
{
    public override string Tag => "vad_audio";

    /// <summary>单声道 float32 PCM，取值域 [-1, 1]，采样率见 <see cref="SampleRate"/>。</summary>
    public required float[] Audio { get; init; }

    public int SampleRate { get; init; } = 16_000;

    public SpeakerAttribution? SpeakerAttribution { get; init; }

    public PendingSpeakerAttribution? SpeakerPending { get; init; }

    public RuntimeConfig? RuntimeConfig { get; init; }

    /// <summary><c>"progressive"</c> 或 <c>"final"</c>；null 表示普通整段。</summary>
    public string? Mode { get; init; }

    public string? TurnId { get; init; }

    public int? TurnRevision { get; init; }

    public double ProcessingDelaySeconds { get; init; }

    public double CreatedAtSeconds { get; init; } = Monotonic.NowSeconds();

    /// <summary>服务端单调时钟上估计的语音结束时刻，与 gate 年龄分开。</summary>
    public double? SpeechEndAtSeconds { get; init; }

    public bool IsProgressive => Mode == "progressive";
    public bool IsFinal => Mode == "final";
}

// ── STT → TranscriptionNotifier → LLM ─────────────────────────────────

/// <summary>实时增量转写（由 TranscriptionNotifier 消费，不转发给 LLM）。</summary>
public sealed class PartialTranscription : PipelineMessage
{
    public override string Tag => "partial_transcription";

    public required string Text { get; init; }
    public string? TurnId { get; init; }
    public int? TurnRevision { get; init; }
}

/// <summary>最终转写结果。</summary>
public sealed class Transcription : PipelineMessage
{
    public override string Tag => "transcription";

    public required string Text { get; init; }
    public SpeakerAttribution? SpeakerAttribution { get; init; }
    public string? LanguageCode { get; init; }
    public string? TurnId { get; init; }
    public int? TurnRevision { get; init; }
    public double? SpeechStoppedAtSeconds { get; init; }
}

/// <summary>最终 STT 失败且未产生 LLM 输入。</summary>
public sealed class TranscriptionFailure : PipelineMessage
{
    public override string Tag => "transcription_failure";

    public required string Message { get; init; }
    public string? TurnId { get; init; }
    public int? TurnRevision { get; init; }
    public double? SpeechStoppedAtSeconds { get; init; }
}

// ── LLM → LMOutputProcessor ───────────────────────────────────────────

/// <summary>助手指令性输出的有序分片基类。</summary>
public abstract class AssistantOutputPart
{
    public abstract string Type { get; }
}

/// <summary>一段助手文本。</summary>
public sealed class AssistantTextPart : AssistantOutputPart
{
    public override string Type => "text";
    public required string Text { get; init; }
}

/// <summary>一次助手函数调用。</summary>
public sealed class AssistantToolCallPart : AssistantOutputPart
{
    public override string Type => "tool_call";
    public required ToolCall Tool { get; init; }
}

/// <summary>
/// 一次有序的助手输出分组。
///
/// <see cref="Text"/> 与 <see cref="Tools"/> 是 <see cref="Parts"/> 的兼容视图，
/// 由 <see cref="Normalize"/> 保证二者一致——对应上游 <c>_normalize_assistant_output_fields</c>。
/// </summary>
public sealed class LlmResponseChunk : PipelineMessage
{
    public override string Tag => "llm_response_chunk";

    public ResponseHistory? History { get; init; }
    public List<AssistantOutputPart> Parts { get; init; } = [];
    public string Text { get; init; } = "";
    public string? LanguageCode { get; init; }
    public string? SelectedLanguage { get; init; }
    public List<ToolCall> Tools { get; init; } = [];
    public RuntimeConfig? RuntimeConfig { get; init; }
    public RealtimeResponseCreateParams? Response { get; init; }
    public string? TurnId { get; init; }
    public int? TurnRevision { get; init; }
    public double? SpeechStoppedAtSeconds { get; init; }
    public int? CancelGeneration { get; init; }
    public string? ResponseKey { get; init; }
    public ResponsePrefetchTransaction? PrefetchTransaction { get; init; }

    /// <summary>
    /// 把 <see cref="Parts"/> 与 <see cref="Text"/> / <see cref="Tools"/> 投影成一致的视图。
    ///
    /// 两条来源都要认：新后端只填 <see cref="Parts"/>，老后端只填 <see cref="Text"/>。
    /// 上游在两者冲突时抛 <c>ValueError</c>；这里取"显式 Parts 优先"，
    /// 由差异账 C-08 记录这处容错差异。
    /// </summary>
    public LlmResponseChunk Normalize()
    {
        var parts = Parts.Count > 0
            ? Parts
            : Text.Length > 0
                ? [new AssistantTextPart { Text = Text }]
                : [];

        return new LlmResponseChunk
        {
            History = History,
            Parts = parts,
            Text = string.Concat(parts.OfType<AssistantTextPart>().Select(p => p.Text)),
            LanguageCode = LanguageCode,
            SelectedLanguage = SelectedLanguage,
            Tools = parts.OfType<AssistantToolCallPart>().Select(p => p.Tool).ToList(),
            RuntimeConfig = RuntimeConfig,
            Response = Response,
            TurnId = TurnId,
            TurnRevision = TurnRevision,
            SpeechStoppedAtSeconds = SpeechStoppedAtSeconds,
            CancelGeneration = CancelGeneration,
            ResponseKey = ResponseKey,
            PrefetchTransaction = PrefetchTransaction,
        };
    }
}

/// <summary>Token 用量（侧信道，不转发给 TTS）。</summary>
public sealed class TokenUsage : PipelineMessage
{
    public override string Tag => "token_usage";

    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public string? TurnId { get; init; }
    public int? TurnRevision { get; init; }
    public int? CancelGeneration { get; init; }
    public string? ResponseKey { get; init; }
}

/// <summary>响应未完成的提供方侧原因。</summary>
public enum ResponseIncompleteReason
{
    None,
    MaxOutputTokens,
    ContentFilter,
}

/// <summary>响应终止状态。</summary>
public enum ResponseStatus
{
    Completed,
    Incomplete,
}

/// <summary>
/// 标记一次响应结束。
///
/// <see cref="Error"/> 非空表示生成失败，输出处理器会转成 <c>response.done(status="failed")</c>。
/// </summary>
public sealed class EndOfResponse : PipelineMessage
{
    public override string Tag => "end_of_response";

    public ResponseHistory? History { get; init; }
    public string? TurnId { get; init; }
    public int? TurnRevision { get; init; }
    public int? CancelGeneration { get; init; }
    public string? ResponseKey { get; init; }
    public string? Error { get; init; }
    public ResponseStatus Status { get; init; } = ResponseStatus.Completed;
    public ResponseIncompleteReason Reason { get; init; } = ResponseIncompleteReason.None;
    public bool CleanupOnly { get; init; }
    public List<string> InputToolCallIds { get; init; } = [];
}

// ── LMOutputProcessor → TTS ──────────────────────────────────────────

/// <summary>待合成文本，带每次响应的上下文。</summary>
public sealed class TtsInput : PipelineMessage
{
    public override string Tag => "tts_input";

    public required string Text { get; init; }
    public string? LanguageCode { get; init; }
    public string? SelectedLanguage { get; init; }

    /// <summary>
    /// TTS 语言码快照。
    ///
    /// 上游 <c>snapshot_language</c> 的判定顺序：<c>auto</c> → null（交给模型自动判语言）；
    /// 显式选择 → 用选择值；否则回落到 <see cref="LanguageCode"/>。
    /// </summary>
    public string? TtsLanguageCode { get; init; }

    public string? ResponseAssistantLanguageCode { get; init; }
    public RuntimeConfig? RuntimeConfig { get; init; }
    public RealtimeResponseCreateParams? Response { get; init; }
    public string? TurnId { get; init; }
    public int? TurnRevision { get; init; }
    public double? SpeechStoppedAtSeconds { get; init; }
    public int? CancelGeneration { get; init; }
    public string? ResponseKey { get; init; }
    public ResponsePrefetchTransaction? PrefetchTransaction { get; init; }

    /// <summary>复现上游 <c>TTSInput.snapshot_language</c> 的取值规则。</summary>
    public static TtsInput Create(
        string text,
        RuntimeConfig? runtimeConfig = null,
        string? languageCode = null,
        string? selectedLanguage = null,
        string? responseKey = null,
        int? cancelGeneration = null,
        ResponsePrefetchTransaction? prefetchTransaction = null,
        string? turnId = null,
        int? turnRevision = null)
    {
        var resolvedSelected = selectedLanguage ?? runtimeConfig?.SelectedLanguage;
        var ttsLanguage = resolvedSelected switch
        {
            null => languageCode,
            "auto" => null,
            _ => resolvedSelected,
        };
        return new TtsInput
        {
            Text = text,
            RuntimeConfig = runtimeConfig,
            LanguageCode = languageCode,
            SelectedLanguage = resolvedSelected,
            TtsLanguageCode = ttsLanguage,
            ResponseKey = responseKey,
            CancelGeneration = cancelGeneration,
            PrefetchTransaction = prefetchTransaction,
            TurnId = turnId,
            TurnRevision = turnRevision,
        };
    }
}

/// <summary>
/// 音频队列项：带上产生它的响应代际标记。
///
/// <see cref="Audio"/> 为 int16 交错 PCM 字节（下行用），与上游 <c>bytes | np.ndarray</c> 等价。
/// </summary>
public sealed class AudioOutput : PipelineMessage
{
    public override string Tag => "audio_output";

    public required byte[] Audio { get; init; }
    public List<Viseme> Visemes { get; init; } = [];
    public int? CancelGeneration { get; init; }
    public string? ResponseKey { get; init; }
    public bool CleanupOnly { get; init; }
}

// ── Realtime service → LLM ────────────────────────────────────────────

/// <summary>
/// 只有在 prefetch 被 <c>response.create</c> 认领后才提交不可回滚的 chat 清理。
///
/// 忠实移植：被丢弃的 prefetch 永远不写历史；<see cref="Claim"/> 之后 <see cref="Complete"/>
/// 立即执行清理；清理抛异常时把事务标记为失败并唤醒等待者。
/// </summary>
public sealed class ResponsePrefetchTransaction
{
    private readonly Lock _lock = new();
    private readonly List<Action> _abortCallbacks = [];
    private readonly ManualResetEventSlim _resolved = new(false);
    private Action? _cleanup;
    private bool _claimed;
    private bool _discarded;

    public bool Discarded
    {
        get { lock (_lock) { return _discarded; } }
    }

    public bool Claimed
    {
        get { lock (_lock) { return _claimed; } }
    }

    /// <summary>等待公开认领或内部丢弃做出裁决。</summary>
    public bool WaitUntilResolved(TimeSpan timeout) => _resolved.Wait(timeout);

    /// <summary>已丢弃则立即中止，否则挂上待中止回调。</summary>
    public void RegisterAbort(Action abort)
    {
        bool runNow;
        lock (_lock)
        {
            runNow = _discarded;
            if (!runNow && !_claimed)
            {
                _abortCallbacks.Add(abort);
            }
        }

        if (runNow)
        {
            RunAbort(abort);
        }
    }

    private static void RunAbort(Action abort)
    {
        try
        {
            abort();
        }
        catch (Exception ex)
        {
            // 取消必须仍能到达 response 回滚与墓碑化，即使后端在释放流时抛错。
            TranscriptLog.Error("Failed to abort discarded response prefetch", ex);
        }
    }

    /// <summary>存下清理动作；若已被认领则立即执行。</summary>
    public void Complete(Action cleanup)
    {
        bool runNow = false;
        lock (_lock)
        {
            if (_discarded)
            {
                return;
            }

            _abortCallbacks.Clear();
            if (_claimed)
            {
                runNow = true;
            }
            else
            {
                _cleanup = cleanup;
            }
        }

        if (runNow)
        {
            cleanup();
        }
    }

    /// <summary>提交清理；投机工作已失败时返回 false。</summary>
    public bool Claim()
    {
        Action? cleanup;
        lock (_lock)
        {
            if (_discarded)
            {
                return false;
            }

            if (_claimed)
            {
                return true;
            }

            _claimed = true;
            _abortCallbacks.Clear();
            cleanup = _cleanup;
            _cleanup = null;
        }

        if (cleanup is not null)
        {
            try
            {
                cleanup();
            }
            catch
            {
                lock (_lock)
                {
                    _claimed = false;
                    _discarded = true;
                }

                _resolved.Set();
                throw;
            }
        }

        _resolved.Set();
        return true;
    }

    /// <summary>投机被作废后禁止清理再改动 chat。</summary>
    public void Discard()
    {
        Action[] aborts;
        lock (_lock)
        {
            if (_discarded || _claimed)
            {
                return;
            }

            _discarded = true;
            _cleanup = null;
            aborts = [.. _abortCallbacks];
            _abortCallbacks.Clear();
            _resolved.Set();
        }

        foreach (var abort in aborts)
        {
            RunAbort(abort);
        }
    }
}

/// <summary>
/// 触发某个 realtime 会话的 LLM 生成。
///
/// 携带 LM handler 需要的全部信息，使它不必回读共享对象。
/// </summary>
public sealed class GenerateResponseRequest : PipelineMessage
{
    public override string Tag => "generate_response";

    public string ResponseKey { get; init; } = Guid.NewGuid().ToString("N");
    public required RuntimeConfig RuntimeConfig { get; init; }
    public string? SelectedLanguage { get; init; }
    public RealtimeResponseCreateParams? Response { get; init; }

    /// <summary>直接音频输入（无 STT）时的 16 kHz 单声道 float PCM。</summary>
    public float[]? Audio { get; init; }

    public int AudioSampleRate { get; init; } = 16_000;
    public string? InputItemId { get; init; }
    public string? LanguageCode { get; init; }
    public string? TurnId { get; init; }
    public int? TurnRevision { get; init; }
    public double? SpeechStoppedAtSeconds { get; init; }
    public ResponsePrefetchTransaction? PrefetchTransaction { get; init; }

    /// <summary>复现上游 <c>snapshot_selected_language</c>：未显式给出时取会话快照。</summary>
    public static GenerateResponseRequest Create(
        RuntimeConfig runtimeConfig,
        RealtimeResponseCreateParams? response = null,
        string? selectedLanguage = null,
        float[]? audio = null,
        int audioSampleRate = 16_000,
        string? inputItemId = null,
        string? languageCode = null,
        string? turnId = null,
        int? turnRevision = null,
        ResponsePrefetchTransaction? prefetchTransaction = null,
        string? responseKey = null)
        => new()
        {
            ResponseKey = responseKey ?? Guid.NewGuid().ToString("N"),
            RuntimeConfig = runtimeConfig,
            SelectedLanguage = selectedLanguage ?? runtimeConfig.SelectedLanguage,
            Response = response,
            Audio = audio,
            AudioSampleRate = audioSampleRate,
            InputItemId = inputItemId,
            LanguageCode = languageCode,
            TurnId = turnId,
            TurnRevision = turnRevision,
            PrefetchTransaction = prefetchTransaction,
        };
}

/// <summary>管线二进制哨兵的具名包装（见差异账 C-03）。</summary>
public static class PipelineSentinels
{
    /// <summary>对应上游 <c>b"END"</c>：放入队列以避免阻塞死锁。</summary>
    public static readonly PipelineEndMarker PipelineEnd = new();

    /// <summary>对应上游 <c>b"__RESPONSE_DONE__"</c>。</summary>
    public static readonly ResponseDoneMarker AudioResponseDone = new();
}

/// <summary>管线终止哨兵。</summary>
public sealed class PipelineEndMarker
{
    internal PipelineEndMarker()
    {
    }

    public override string ToString() => "__PIPELINE_END__";
}

/// <summary>一段响应音频结束哨兵。</summary>
public sealed class ResponseDoneMarker
{
    internal ResponseDoneMarker()
    {
    }

    public override string ToString() => "__RESPONSE_DONE__";
}

/// <summary>单调时钟辅助，等价 Python 的 <c>time.perf_counter()</c>。</summary>
public static class Monotonic
{
    private static readonly double Origin = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public static double NowSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - Origin;
}
