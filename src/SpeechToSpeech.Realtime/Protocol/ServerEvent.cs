using System.Text.Json;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Json;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Realtime.Protocol;

/// <summary>事件名常量。与上游线上协议逐字一致，改动会直接破坏客户端兼容。</summary>
public static class EventNames
{
    // 客户端 → 服务端
    public const string InputAudioBufferAppend = "input_audio_buffer.append";
    public const string InputAudioBufferCommit = "input_audio_buffer.commit";
    public const string InputAudioBufferClear = "input_audio_buffer.clear";
    public const string OutputAudioBufferClear = "output_audio_buffer.clear";
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
    public const string InvalidInputItem = "invalid_input_item";
    public const string FunctionCallOutputPending = "function_call_output_pending";
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
    /// <summary>
    /// 线路序列化选项。编码器与探针共用 <see cref="WireJson"/>——上游走 Starlette 的
    /// <c>send_json</c>（<c>ensure_ascii=False</c>），中文、base64 的 <c>+</c>、BMP 外的字符
    /// 都原样输出；用默认编码器会逐字节偏掉（C012 / C017）。
    /// </summary>
    private static readonly JsonSerializerOptions Options = WireJson.Compact;

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

    /// <summary>
    /// 上游 <c>make_error</c> 形状的错误事件：<c>error.type</c> 直接放错误类型
    /// （如 <c>invalid_input_item</c>），没有 <c>code</c> 字段。
    ///
    /// 与 <see cref="Error"/>（官方 schema 校验形状：<c>type=invalid_request_error</c>
    /// + <c>code</c> + 可选 <c>param</c>）是两条不同的构造路径，上游就是两个形状，
    /// 不能统一成一个工厂——D027 负责把剩余调用点按上游归位。
    /// </summary>
    public static JsonObject TypeError(string errorType, string message, string? eventId = null)
    {
        var error = new JsonObject
        {
            ["message"] = message,
            ["type"] = errorType,
        };

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

    /// <summary>
    /// <c>response.created</c>。载荷与 <c>response.done</c> 同构，差别只在 status 与当时的 output。
    ///
    /// 客户端会拿这份载荷建立「当前响应」，之后再拿 done 的载荷替换它——两次字段集不一致，
    /// 严格解析器会在其中一次失败。
    /// </summary>
    public static JsonObject ResponseCreated(ResponseSnapshot snapshot)
        => new()
        {
            ["type"] = EventNames.ResponseCreated,
            ["event_id"] = NewEventId(),
            ["response"] = BuildResponse(snapshot),
        };

    /// <summary>
    /// 组装 <c>response</c> 对象。上游 <c>_build_response</c> 的等价物，是这一层唯一的产地。
    /// </summary>
    public static JsonObject BuildResponse(ResponseSnapshot snapshot)
    {
        var response = new JsonObject
        {
            ["id"] = snapshot.Id,
            ["object"] = "realtime.response",
            ["status"] = snapshot.Status,
        };

        // 上游对任何终态都发 status_details，completed 也发（type 与 status 同名）。
        // 漏掉这条时客户端拿不到「正常结束」的确认，只能靠 status 字段反推。
        if (snapshot.Status != "in_progress")
        {
            var details = new JsonObject { ["type"] = snapshot.Status };
            if (snapshot.Reason is not null)
            {
                details["reason"] = snapshot.Reason;
            }

            if (snapshot.Status == "failed")
            {
                // 上游只带 error.type（恒 "response_failed"）；失败详情走独立的 error 事件，
                // 不塞进这里——两处都有 message 时客户端会挑一处显示，另一处就永久沉默。
                details["error"] = new JsonObject { ["type"] = snapshot.ErrorType ?? "response_failed" };
            }

            response["status_details"] = details;
        }

        response["audio"] = new JsonObject
        {
            ["output"] = snapshot.Voice is null
                ? new JsonObject()
                : new JsonObject { ["voice"] = snapshot.Voice },
        };

        if (snapshot.ConversationId is not null)
        {
            response["conversation_id"] = snapshot.ConversationId;
        }

        if (snapshot.Metadata is not null)
        {
            // DeepClone：JsonNode 只能有一个父节点，直接挂会把调用方手里的对象偷走。
            response["metadata"] = snapshot.Metadata.DeepClone();
        }

        response["output_modalities"] = new JsonArray(
            [.. snapshot.OutputModalities.Select(m => (JsonNode) JsonValue.Create(m)!)]);
        // DeepClone 而不是直接挂：JsonNode 只能有一个父节点，挂上去会把调用方手里的对象偷走。
        response["output"] = new JsonArray([.. snapshot.Output.Select(item => (JsonNode) item.DeepClone())]);
        response["usage"] = new JsonObject
        {
            ["input_token_details"] = new JsonObject
            {
                ["audio_tokens"] = 0,
                ["cached_tokens"] = 0,
                ["image_tokens"] = 0,
                ["text_tokens"] = snapshot.Usage.InputTokens,
            },
            ["input_tokens"] = snapshot.Usage.InputTokens,
            ["output_token_details"] = new JsonObject
            {
                ["audio_tokens"] = 0,
                ["text_tokens"] = snapshot.Usage.OutputTokens,
            },
            ["output_tokens"] = snapshot.Usage.OutputTokens,
            ["total_tokens"] = snapshot.Usage.InputTokens + snapshot.Usage.OutputTokens,
        };

        return response;
    }

    /// <summary>
    /// 助手消息项。三处共用：<c>output_item.added</c>（content 为空）、
    /// <c>output_item.done</c>、以及 <c>response.output</c> 数组里的同一项。
    ///
    /// 上游这三处都走 <c>_build_message_item</c>，因此终态消息项与 done 事件里的那份逐字段相同。
    /// 分开写会让客户端拿到两个 id 相同、内容不同的 item。
    /// </summary>
    public static JsonObject AssistantMessageItem(string itemId, string status, bool wantsAudio, string text)
    {
        var content = wantsAudio
            ? new JsonObject { ["type"] = "output_audio", ["transcript"] = text }
            : new JsonObject { ["type"] = "output_text", ["text"] = text };

        return new JsonObject
        {
            ["id"] = itemId,
            ["object"] = "realtime.item",
            ["type"] = "message",
            ["role"] = "assistant",
            ["status"] = status,
            ["content"] = new JsonArray(content),
        };
    }

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
        => new()
        {
            ["type"] = EventNames.OutputItemDone,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["output_index"] = outputIndex,
            // 与 response.output 里的终态项走同一个构造，保证两份逐字段相同。
            ["item"] = AssistantMessageItem(itemId, completed ? "completed" : "incomplete", wantsAudio, text),
        };

    /// <summary>内容片骨架。音频模态用 <c>audio</c>/<c>transcript</c>，纯文本模态用 <c>text</c>。</summary>
    private static JsonObject EmptyPart(bool wantsAudio)
        => wantsAudio
            ? new JsonObject { ["type"] = "audio", ["audio"] = "", ["transcript"] = "" }
            : new JsonObject { ["type"] = "text", ["text"] = "" };

    public static JsonObject OutputAudioDelta(string responseId, string itemId, string audioBase64,
        int outputIndex = 0)
        => new()
        {
            ["type"] = EventNames.OutputAudioDelta,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = outputIndex,
            ["content_index"] = 0,
            ["delta"] = audioBase64,
        };

    /// <summary>
    /// <c>speech_to_speech.output_audio.visemes</c>：口型扩展事件。
    ///
    /// 上游在与音频同一 <c>item_id/output_index/content_index</c> 上先发这条、再发音频块；
    /// 标准音频事件完全不变，不认识该事件的客户端会忽略它。
    /// 区间是响应音频时间线上的绝对秒（STV 级已经加好窗口偏移）。
    /// </summary>
    public static JsonObject OutputAudioVisemes(string responseId, string itemId,
        IReadOnlyList<Viseme> visemes, int outputIndex = 0)
    {
        var payload = new JsonArray();
        foreach (var viseme in visemes)
        {
            payload.Add(new JsonObject
            {
                ["viseme"] = viseme.VisemeId,
                ["start_s"] = viseme.StartSeconds,
                ["end_s"] = viseme.EndSeconds,
            });
        }

        return new JsonObject
        {
            ["type"] = EventNames.OutputAudioVisemes,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = outputIndex,
            ["content_index"] = 0,
            ["visemes"] = payload,
        };
    }

    public static JsonObject OutputAudioDone(string responseId, string itemId, int outputIndex = 0)
        => new()
        {
            ["type"] = EventNames.OutputAudioDone,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = outputIndex,
            ["content_index"] = 0,
        };

    public static JsonObject OutputAudioTranscriptDelta(string responseId, string itemId, string delta,
        int outputIndex = 0)
        => new()
        {
            ["type"] = EventNames.OutputAudioTranscriptDelta,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = outputIndex,
            ["content_index"] = 0,
            ["delta"] = delta,
        };

    public static JsonObject OutputAudioTranscriptDone(string responseId, string itemId, string transcript,
        int outputIndex = 0)
        => new()
        {
            ["type"] = EventNames.OutputAudioTranscriptDone,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = outputIndex,
            ["content_index"] = 0,
            ["transcript"] = transcript,
        };

    /// <summary>
    /// 函数调用项。三处共用：<c>output_item.added</c>（arguments 空格、in_progress）、
    /// <c>output_item.done</c>、以及 <c>response.output</c> 数组里的同一项——
    /// 上游同一个 <c>RealtimeConversationItemFunctionCall</c> 实例三处复用，逐字段相同。
    /// </summary>
    public static JsonObject FunctionCallItem(string itemId, string callId, string name, string arguments,
        string status)
        => new()
        {
            ["id"] = itemId,
            ["object"] = "realtime.item",
            ["type"] = "function_call",
            ["call_id"] = callId,
            ["name"] = name,
            ["arguments"] = arguments,
            ["status"] = status,
        };

    /// <summary><c>response.output_item.added</c>：函数调用项登场，arguments 为空、状态 in_progress。</summary>
    public static JsonObject FunctionCallOutputItemAdded(string responseId, string itemId, int outputIndex,
        string callId, string name)
        => new()
        {
            ["type"] = EventNames.OutputItemAdded,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["output_index"] = outputIndex,
            ["item"] = FunctionCallItem(itemId, callId, name, arguments: "", status: "in_progress"),
        };

    /// <summary><c>response.output_item.done</c>：函数调用项收口，item 与 <c>response.output</c> 同源。</summary>
    public static JsonObject FunctionCallOutputItemDone(string responseId, JsonObject item, int outputIndex)
        => new()
        {
            ["type"] = EventNames.OutputItemDone,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["output_index"] = outputIndex,
            ["item"] = item.DeepClone(),
        };

    /// <summary><c>response.function_call_arguments.done</c>：完整参数到位，与 added 同 item_id 便于关联。</summary>
    public static JsonObject FunctionCallArgumentsDone(string responseId, string itemId, string callId, string name,
        string arguments, int outputIndex = 0)
        => new()
        {
            ["type"] = EventNames.FunctionCallArgumentsDone,
            ["event_id"] = NewEventId(),
            ["response_id"] = responseId,
            ["item_id"] = itemId,
            ["output_index"] = outputIndex,
            ["call_id"] = callId,
            ["name"] = name,
            ["arguments"] = arguments,
        };

    /// <summary>
    /// <c>response.done</c>。载荷与 <c>response.created</c> 同构（同一个 <see cref="BuildResponse"/>），
    /// 差别只在 status 与那一刻的 output。
    /// </summary>
    public static JsonObject ResponseDone(ResponseSnapshot snapshot)
        => new()
        {
            ["type"] = EventNames.ResponseDone,
            ["event_id"] = NewEventId(),
            ["response"] = BuildResponse(snapshot),
        };

    public static string Serialize(JsonObject payload) => payload.ToJsonString(Options);

    public static string NewEventId() => "event_" + Guid.NewGuid().ToString("N")[..12];
}
