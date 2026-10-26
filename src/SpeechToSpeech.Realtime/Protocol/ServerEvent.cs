using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpeechToSpeech.Realtime.Protocol;

/// <summary>事件名常量。与上游线上协议逐字一致，改动会直接破坏客户端兼容。</summary>
public static class EventNames
{
    // 客户端 → 服务端
    public const string InputAudioBufferAppend = "input_audio_buffer.append";
    public const string InputAudioBufferCommit = "input_audio_buffer.commit";
    public const string InputAudioBufferClear = "input_audio_buffer.clear";
    public const string SessionUpdate = "session.update";
    public const string ConversationItemCreate = "conversation.item.create";
    public const string ConversationItemDelete = "conversation.item.delete";
    public const string ConversationItemTruncate = "conversation.item.truncate";
    public const string ResponseCreate = "response.create";
    public const string ResponseCancel = "response.cancel";

    // 服务端 → 客户端
    public const string SessionCreated = "session.created";
    public const string SessionUpdated = "session.updated";
    public const string Error = "error";
    public const string SpeechStarted = "input_audio_buffer.speech_started";
    public const string SpeechStopped = "input_audio_buffer.speech_stopped";
    public const string InputAudioBufferCommitted = "input_audio_buffer.committed";
    public const string ConversationItemCreated = "conversation.item.created";
    public const string ConversationItemDeleted = "conversation.item.deleted";
    public const string TranscriptionDelta = "conversation.item.input_audio_transcription.delta";
    public const string TranscriptionCompleted = "conversation.item.input_audio_transcription.completed";
    public const string TranscriptionFailed = "conversation.item.input_audio_transcription.failed";
    public const string TranscriptionSnapshot = "speech_to_speech.input_audio_transcription.snapshot";
    public const string ResponseCreated = "response.created";
    public const string OutputItemAdded = "response.output_item.added";
    public const string OutputItemDone = "response.output_item.done";
    public const string ContentPartAdded = "response.content_part.added";
    public const string ContentPartDone = "response.content_part.done";
    public const string OutputTextDelta = "response.output_text.delta";
    public const string OutputTextDone = "response.output_text.done";
    public const string OutputAudioDelta = "response.output_audio.delta";
    public const string OutputAudioDone = "response.output_audio.done";
    public const string OutputAudioTranscriptDelta = "response.output_audio_transcript.delta";
    public const string OutputAudioTranscriptDone = "response.output_audio_transcript.done";
    public const string FunctionCallArgumentsDone = "response.function_call_arguments.done";
    public const string ResponseDone = "response.done";
    public const string OutputAudioVisemes = "speech_to_speech.output_audio.visemes";

    // 扩展能力
    public const string SnapshotExtension = "speech_to_speech.input_audio_transcription.snapshot";
}

/// <summary>协议错误码。字符串与上游一致，客户端会按它分支。</summary>
public static class ErrorCodes
{
    public const string UnknownOrInvalidEvent = "unknown_or_invalid_event";
    public const string InvalidSessionType = "invalid_session_type";
    public const string ConversationAlreadyHasActiveResponse = "conversation_already_has_active_response";
    public const string SessionLimitReached = "session_limit_reached";
    public const string InvalidValue = "invalid_value";
    public const string ConversationItemNotFound = "conversation_item_not_found";
    public const string InternalError = "internal_error";
}

/// <summary>
/// 协议事件构造。
///
/// 所有服务端事件都经这里产出，避免字段名漂移——线路形状测试锁的就是这一层。
/// </summary>
public static class ServerEvent
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        // 上游走 Starlette 的 send_json，它用 json.dumps(separators=(",", ":"), ensure_ascii=False)：
        // 中文原样输出、base64 里的 '+' 不转义。默认的 JavaScriptEncoder 会把非 ASCII 转成
        // \uXXXX（汉字 3 字节变 6 字节），并把 '+' 转成 \u002B——音频与转写都在热路径上，
        // 这里必须与上游一致。Unsafe 指 HTML 内联风险，本协议不经 HTML，不适用。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonObject Create(string type) => new() { ["type"] = type };

    public static JsonObject Error(string code, string message, string? param = null, string? eventId = null)
    {
        var error = new JsonObject
        {
            ["type"] = "invalid_request_error",
            ["code"] = code,
            ["message"] = message,
        };

        if (param is not null)
        {
            error["param"] = param;
        }

        if (eventId is not null)
        {
            error["event_id"] = eventId;
        }

        return new JsonObject
        {
            ["type"] = EventNames.Error,
            ["event_id"] = NewEventId(),
            ["error"] = error,
        };
    }

    public static JsonObject SessionCreated(string sessionId, JsonObject session)
        => new()
        {
            ["type"] = EventNames.SessionCreated,
            ["event_id"] = NewEventId(),
            ["session"] = session,
        };

    public static JsonObject SessionUpdated(JsonObject session)
        => new()
        {
            ["type"] = EventNames.SessionUpdated,
            ["event_id"] = NewEventId(),
            ["session"] = session,
        };

    public static JsonObject SpeechStarted(string itemId, int audioStartMs)
        => new()
        {
            ["type"] = EventNames.SpeechStarted,
            ["event_id"] = NewEventId(),
            ["audio_start_ms"] = audioStartMs,
            ["item_id"] = itemId,
        };

    public static JsonObject SpeechStopped(string itemId, int audioEndMs)
        => new()
        {
            ["type"] = EventNames.SpeechStopped,
            ["event_id"] = NewEventId(),
            ["audio_end_ms"] = audioEndMs,
            ["item_id"] = itemId,
        };

    /// <summary>
    /// <c>input_audio_buffer.committed</c>。
    ///
    /// 紧跟在 <c>speech_stopped</c> 之后发出：此刻这一段音频已经定稿、不会再被追加。
    /// <paramref name="previousItemId"/> 串起会话项顺序链，客户端断线重连后靠它按序补齐。
    /// </summary>
    public static JsonObject InputAudioBufferCommitted(string itemId, string? previousItemId)
    {
        var payload = new JsonObject
        {
            ["type"] = EventNames.InputAudioBufferCommitted,
            ["event_id"] = NewEventId(),
            ["item_id"] = itemId,
        };

        if (previousItemId is not null)
        {
            payload["previous_item_id"] = previousItemId;
        }

        return payload;
    }

    public static JsonObject TranscriptionDelta(string itemId, string delta, int? contentIndex = 0)
        => new()
        {
            ["type"] = EventNames.TranscriptionDelta,
            ["event_id"] = NewEventId(),
            ["item_id"] = itemId,
            ["content_index"] = contentIndex,
            ["delta"] = delta,
        };

    /// <summary>
    /// <c>conversation.item.input_audio_transcription.completed</c>。
    ///
    /// <paramref name="audioSeconds"/> 是该输入项的音频时长，落在官方 schema 的
    /// <c>usage: {type: "duration", seconds}</c>。这个字段在官方 schema 里是**必填**，
    /// 缺了严格客户端（按 Pydantic 模型校验的那种）会直接拒收整条事件——不是「少个统计」而已。
    /// 上游按 <c>duration</c> 形态上报，不发 token 形态（那要 STT 后端支持计费口径）。
    /// </summary>
    public static JsonObject TranscriptionCompleted(
        string itemId,
        string transcript,
        int? contentIndex = 0,
        double audioSeconds = 0)
        => new()
        {
            ["type"] = EventNames.TranscriptionCompleted,
            ["event_id"] = NewEventId(),
            ["item_id"] = itemId,
            ["content_index"] = contentIndex,
            ["transcript"] = transcript,
            ["usage"] = new JsonObject
            {
                ["type"] = "duration",
                ["seconds"] = audioSeconds,
            },
        };

    public static JsonObject TranscriptionSnapshot(string itemId, string snapshot)
        => new()
        {
            ["type"] = EventNames.TranscriptionSnapshot,
            ["event_id"] = NewEventId(),
            ["item_id"] = itemId,
            ["snapshot"] = snapshot,
        };

    /// <summary>
    /// <c>conversation.item.created</c>。
    ///
    /// <paramref name="content"/> 必须是完整的内容数组——客户端的渲染器读它取消息体，
    /// 缺了会渲染成空消息。<paramref name="previousItemId"/> 是顺序链的前驱（首个项为 null），
    /// 客户端断线重连后靠它把对话补齐。
    /// </summary>
    public static JsonObject ConversationItemCreated(
        string itemId,
        string role,
        JsonArray? content = null,
        string? previousItemId = null)
    {
        var item = new JsonObject
        {
            ["id"] = itemId,
            ["object"] = "realtime.item",
            ["type"] = "message",
            ["role"] = role,
            ["status"] = "completed",
            ["content"] = content ?? new JsonArray(),
        };

        var payload = new JsonObject
        {
            ["type"] = EventNames.ConversationItemCreated,
            ["event_id"] = NewEventId(),
            ["item"] = item,
        };

        if (previousItemId is not null)
        {
            payload["previous_item_id"] = previousItemId;
        }

        return payload;
    }

    /// <summary><c>conversation.item.deleted</c>：客户端删除会话项后的确认。</summary>
    public static JsonObject ConversationItemDeleted(string itemId)
        => new()
        {
            ["type"] = EventNames.ConversationItemDeleted,
            ["event_id"] = NewEventId(),
            ["item_id"] = itemId,
        };

    /// <summary>
    /// <c>conversation.item.input_audio_transcription.failed</c>。
    ///
    /// 这是 item 作用域的失败通告，不是 <c>error</c>：客户端据此只把该条输入标记为转写失败，
    /// 而不是把整条连接当成出错。用 <c>error</c> 代替会让客户端误判。
    /// </summary>
    public static JsonObject TranscriptionFailed(string itemId, string message, int contentIndex = 0)
        => new()
        {
            ["type"] = EventNames.TranscriptionFailed,
            ["event_id"] = NewEventId(),
            ["content_index"] = contentIndex,
            ["item_id"] = itemId,
            ["error"] = new JsonObject
            {
                ["type"] = "transcription_error",
                ["code"] = "transcription_failed",
                ["message"] = message,
            },
        };

    public static JsonObject ResponseCreated(string responseId)
        => new()
        {
            ["type"] = EventNames.ResponseCreated,
            ["event_id"] = NewEventId(),
            ["response"] = new JsonObject
            {
                ["id"] = responseId,
                ["object"] = "realtime.response",
                ["status"] = "in_progress",
            },
        };

    // ── 助手消息项的生命周期 ──────────────────────────────────────────
    //
    // 这四个事件的顺序是 OpenAI 客户端的渲染契约：
    //   output_item.added → content_part.added → (delta…) → content_part.done → output_item.done
    // 官方客户端靠 added 建立消息项与内容片，靠 done 收口。缺了 added，整段回复不显示；
    // 缺了 done，会留下永远处于 streaming 状态的气泡。

    /// <summary><c>response.output_item.added</c>：开启一个助手消息项，初始 content 为空。</summary>
    public static JsonObject OutputItemAdded(string responseId, string itemId, int outputIndex)
        => new()
        {
            ["type"] = EventNames.OutputItemAdded,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["output_index"] = outputIndex,
            ["item"] = new JsonObject
            {
                ["id"] = itemId,
                ["object"] = "realtime.item",
                ["type"] = "message",
                ["role"] = "assistant",
                ["status"] = "in_progress",
                ["content"] = new JsonArray(),
            },
        };

    /// <summary><c>response.content_part.added</c>：开启消息项下唯一的内容片。</summary>
    public static JsonObject ContentPartAdded(string responseId, string itemId, int outputIndex, int contentIndex,
        bool wantsAudio)
        => new()
        {
            ["type"] = EventNames.ContentPartAdded,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = outputIndex,
            ["content_index"] = contentIndex,
            ["part"] = EmptyPart(wantsAudio),
        };

    /// <summary><c>response.output_text.delta</c>：纯文本模态的增量。</summary>
    public static JsonObject OutputTextDelta(string responseId, string itemId, string delta,
        int outputIndex = 0, int contentIndex = 0)
        => new()
        {
            ["type"] = EventNames.OutputTextDelta,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = outputIndex,
            ["content_index"] = contentIndex,
            ["delta"] = delta,
        };

    /// <summary><c>response.output_text.done</c>：纯文本模态收口，text 是整段而非增量。</summary>
    public static JsonObject OutputTextDone(string responseId, string itemId, string text,
        int outputIndex = 0, int contentIndex = 0)
        => new()
        {
            ["type"] = EventNames.OutputTextDone,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = outputIndex,
            ["content_index"] = contentIndex,
            ["text"] = text,
        };

    /// <summary><c>response.content_part.done</c>：内容片收口，part 里带上完整文本。</summary>
    public static JsonObject ContentPartDone(string responseId, string itemId, string text,
        int outputIndex = 0, int contentIndex = 0, bool wantsAudio = true)
    {
        var part = EmptyPart(wantsAudio);
        if (wantsAudio)
        {
            part["transcript"] = text;
        }
        else
        {
            part["text"] = text;
        }

        return new JsonObject
        {
            ["type"] = EventNames.ContentPartDone,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = outputIndex,
            ["content_index"] = contentIndex,
            ["part"] = part,
        };
    }

    /// <summary>
    /// <c>response.output_item.done</c>：消息项收口。
    ///
    /// <paramref name="completed"/> 为 false 时是 <c>incomplete</c>（被打断或超长），
    /// 客户端据此知道这条消息没说完。
    /// </summary>
    public static JsonObject OutputItemDone(string responseId, string itemId, string text, int outputIndex = 0,
        bool wantsAudio = true, bool completed = true)
    {
        var content = wantsAudio
            ? new JsonObject { ["type"] = "output_audio", ["transcript"] = text }
            : new JsonObject { ["type"] = "output_text", ["text"] = text };

        return new JsonObject
        {
            ["type"] = EventNames.OutputItemDone,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["output_index"] = outputIndex,
            ["item"] = new JsonObject
            {
                ["id"] = itemId,
                ["object"] = "realtime.item",
                ["type"] = "message",
                ["role"] = "assistant",
                ["status"] = completed ? "completed" : "incomplete",
                ["content"] = new JsonArray(content),
            },
        };
    }

    /// <summary>内容片骨架。音频模态用 <c>audio</c>/<c>transcript</c>，纯文本模态用 <c>text</c>。</summary>
    private static JsonObject EmptyPart(bool wantsAudio)
        => wantsAudio
            ? new JsonObject { ["type"] = "audio", ["audio"] = "", ["transcript"] = "" }
            : new JsonObject { ["type"] = "text", ["text"] = "" };

    public static JsonObject OutputAudioDelta(string responseId, string itemId, string audioBase64)
        => new()
        {
            ["type"] = EventNames.OutputAudioDelta,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["delta"] = audioBase64,
        };

    public static JsonObject OutputAudioDone(string responseId, string itemId)
        => new()
        {
            ["type"] = EventNames.OutputAudioDone,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = 0,
            ["content_index"] = 0,
        };

    public static JsonObject OutputAudioTranscriptDelta(string responseId, string itemId, string delta)
        => new()
        {
            ["type"] = EventNames.OutputAudioTranscriptDelta,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["delta"] = delta,
        };

    public static JsonObject OutputAudioTranscriptDone(string responseId, string itemId, string transcript)
        => new()
        {
            ["type"] = EventNames.OutputAudioTranscriptDone,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["transcript"] = transcript,
        };

    public static JsonObject FunctionCallArgumentsDone(string responseId, string itemId, string callId, string name,
        string arguments)
        => new()
        {
            ["type"] = EventNames.FunctionCallArgumentsDone,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = 0,
            ["call_id"] = callId,
            ["name"] = name,
            ["arguments"] = arguments,
        };

    /// <summary>
    /// <c>response.done</c>。
    ///
    /// 终态判定顺序有意为之：错误优先于"不完整"，"不完整"优先于 completed——
    /// 客户端据此决定是否回滚历史，顺序错了会留下半截对话。
    /// </summary>
    public static JsonObject ResponseDone(
        string responseId,
        string status,
        string? reason = null,
        string? errorMessage = null)
    {
        var response = new JsonObject
        {
            ["id"] = responseId,
            ["object"] = "realtime.response",
            ["status"] = status,
            ["output"] = new JsonArray(),
        };

        // status_details.type 与 status 同域（completed / cancelled / incomplete / failed）。
        // 原先恒写 "incomplete"，于是取消时会产出「type=incomplete + reason=client_cancelled」——
        // 官方 schema 里不存在这个组合，严格客户端解析会失败。
        if (status is "incomplete" or "cancelled" or "failed")
        {
            var details = new JsonObject { ["type"] = status };
            if (reason is not null)
            {
                details["reason"] = reason;
            }

            response["status_details"] = details;
        }

        var envelope = new JsonObject
        {
            ["type"] = EventNames.ResponseDone,
            ["event_id"] = NewEventId(),
            ["response"] = response,
        };

        if (errorMessage is not null)
        {
            response["status_details"] = new JsonObject
            {
                ["type"] = "failed",
                ["error"] = new JsonObject
                {
                    ["code"] = "server_error",
                    ["message"] = errorMessage,
                },
            };
        }

        return envelope;
    }

    public static string Serialize(JsonObject payload) => payload.ToJsonString(Options);

    public static string NewEventId() => "event_" + Guid.NewGuid().ToString("N")[..12];
}
