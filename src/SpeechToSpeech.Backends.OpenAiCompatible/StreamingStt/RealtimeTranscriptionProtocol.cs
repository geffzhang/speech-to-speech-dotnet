using System.Text.Json.Nodes;

namespace SpeechToSpeech.Backends.OpenAiCompatible.StreamingStt;

/// <summary>协议事件的分类（上游 <c>_ProtocolEvent.kind</c>）。</summary>
public enum ProtocolEventKind
{
    Ignore,
    Committed,
    Delta,
    Completed,
    Failed,
    Error,
}

/// <summary>协议解析出的单一事件（上游 <c>_ProtocolEvent</c>）。</summary>
public sealed record ProtocolEvent(
    ProtocolEventKind Kind,
    string Text = "",
    string? Language = null,
    string Message = "",
    string? ItemId = null,
    int? ContentIndex = null);

/// <summary>
/// 流式转写的线上协议方言（上游 <c>StreamingSTTProtocol</c>）。
///
/// 六个方法覆盖一次会话的完整消息面：握手配置、开话、喂音频、收口、丢弃、以及把远端
/// 事件解析成统一分类。实现必须是无状态纯函数（frozen dataclass 的对应物）——
/// 重连、代际、归属判定都住在 <see cref="StreamingSttSession"/> 里，不在这里。
/// </summary>
public interface IRealtimeTranscriptionProtocol
{
    /// <summary>线上方言名（日志与 setup 日志用）。</summary>
    string Name { get; }

    /// <summary>握手时是否要等 <c>session.updated</c> 回声。</summary>
    bool RequiresSessionUpdated { get; }

    JsonObject SessionUpdate();

    JsonObject? StartUtterance();

    JsonObject AppendAudio(ReadOnlySpan<byte> audio);

    JsonObject FinishUtterance();

    /// <summary>返回 null 表示该方言没有逐缓冲清除事件（vLLM）——调用方只能断连兜底。</summary>
    JsonObject? DiscardUtterance();

    ProtocolEvent ParseEvent(JsonObject raw);
}

/// <summary>
/// OpenAI Realtime transcription 会话方言（上游 <c>OpenAIRealtimeProtocol</c>）。
///
/// 音频走 <c>input_audio_buffer.append/commit/clear</c>，转写事件挂在
/// <c>conversation.item.input_audio_transcription.*</c> 下，按 <c>item_id</c> +
/// <c>content_index</c> 归属判定。
/// </summary>
public sealed record OpenAiRealtimeProtocol(
    string Model,
    string? Language,
    int AudioSampleRate) : IRealtimeTranscriptionProtocol
{
    public const string DefaultBaseUrl = "wss://api.openai.com/v1";

    public string Name => "openai-realtime";

    public bool RequiresSessionUpdated => true;

    public JsonObject SessionUpdate()
    {
        var transcription = new JsonObject { ["model"] = Model };
        if (!string.IsNullOrEmpty(Language))
        {
            // gpt-live-transcribe / gpt-transcribe 用复数键的数组形态，其余用单数键。
            if (Model is "gpt-live-transcribe" or "gpt-transcribe")
            {
                transcription["languages"] = new JsonArray(Language);
            }
            else
            {
                transcription["language"] = Language;
            }
        }

        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "transcription",
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = AudioSampleRate },
                        ["transcription"] = transcription,
                        ["turn_detection"] = null,
                    },
                },
            },
        };
    }

    public JsonObject? StartUtterance() => null;

    public JsonObject AppendAudio(ReadOnlySpan<byte> audio) => new()
    {
        ["type"] = "input_audio_buffer.append",
        ["audio"] = Convert.ToBase64String(audio),
    };

    public JsonObject FinishUtterance() => new() { ["type"] = "input_audio_buffer.commit" };

    public JsonObject? DiscardUtterance() => new() { ["type"] = "input_audio_buffer.clear" };

    public ProtocolEvent ParseEvent(JsonObject raw)
    {
        var eventType = raw.TryGetPropertyValue("type", out var typeNode) ? typeNode?.ToString() : null;
        var itemId = RawItemId(raw);
        if (eventType == "input_audio_buffer.committed")
        {
            if (itemId is null)
            {
                return new ProtocolEvent(ProtocolEventKind.Ignore);
            }

            return new ProtocolEvent(ProtocolEventKind.Committed, ItemId: itemId);
        }

        var hasContentIndex = raw.ContainsKey("content_index");
        var contentIndex = ParseContentIndex(raw);
        if (eventType == "conversation.item.input_audio_transcription.delta" && !hasContentIndex)
        {
            // delta 可能省略 index：本适配器只送一个音频 part，默认 0。
            contentIndex = 0;
        }

        if (eventType is "conversation.item.input_audio_transcription.delta"
            or "conversation.item.input_audio_transcription.completed"
            or "conversation.item.input_audio_transcription.failed"
            && (itemId is null
                || contentIndex is null
                || contentIndex < 0))
        {
            return new ProtocolEvent(ProtocolEventKind.Ignore);
        }

        switch (eventType)
        {
            case "conversation.item.input_audio_transcription.delta":
            {
                var delta = raw.TryGetPropertyValue("delta", out var deltaNode) ? deltaNode?.ToString() : null;
                return new ProtocolEvent(ProtocolEventKind.Delta, Text: delta ?? "", ItemId: itemId,
                    ContentIndex: contentIndex);
            }

            case "conversation.item.input_audio_transcription.completed":
            {
                var transcript = raw.TryGetPropertyValue("transcript", out var tNode) ? tNode?.ToString() : null;
                var language = StreamingSttText.FirstLanguageCode(raw);
                if (language is null
                    && raw.TryGetPropertyValue("language", out var langNode)
                    && langNode is JsonValue { } value
                    && value.TryGetValue<string>(out var single))
                {
                    language = single;
                }

                return new ProtocolEvent(ProtocolEventKind.Completed, Text: transcript ?? "", Language: language,
                    ItemId: itemId, ContentIndex: contentIndex);
            }

            case "conversation.item.input_audio_transcription.failed":
                return new ProtocolEvent(ProtocolEventKind.Failed, Message: StreamingSttText.RemoteErrorMessage(raw),
                    ItemId: itemId, ContentIndex: contentIndex);

            case "error":
                return new ProtocolEvent(ProtocolEventKind.Error, Message: StreamingSttText.RemoteErrorMessage(raw));

            default:
                return new ProtocolEvent(ProtocolEventKind.Ignore);
        }
    }

    private static string? RawItemId(JsonObject raw)
    {
        if (!raw.TryGetPropertyValue("item_id", out var node) || node is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    }

    private static int? ParseContentIndex(JsonObject raw)
    {
        if (!raw.TryGetPropertyValue("content_index", out var node) || node is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<int>(out var i) ? i : null;
    }
}

/// <summary>
/// vLLM 实验性 Realtime 方言（上游 <c>VLLMRealtimeProtocol</c>）。
///
/// 与 OpenAI 方言的差异：开话要发非 final 的 <c>input_audio_buffer.commit</c>、
/// 收口发 final commit、事件名是 <c>transcription.delta/done</code>、没有 clear——
/// 丢弃只能靠断连兜底（拒收的 PCM 不可能再流向下一轮）。
/// </summary>
public sealed record VllmRealtimeProtocol(
    string Model,
    string? Language,
    int AudioSampleRate) : IRealtimeTranscriptionProtocol
{
    public string Name => "vllm-realtime";

    // vLLM 不回 session.updated 回声，握手只发 session.update 不等确认。
    public bool RequiresSessionUpdated => false;

    public JsonObject SessionUpdate() => new() { ["type"] = "session.update", ["model"] = Model };

    public JsonObject? StartUtterance() => new() { ["type"] = "input_audio_buffer.commit", ["final"] = false };

    public JsonObject AppendAudio(ReadOnlySpan<byte> audio) => new()
    {
        ["type"] = "input_audio_buffer.append",
        ["audio"] = Convert.ToBase64String(audio),
    };

    public JsonObject FinishUtterance() => new() { ["type"] = "input_audio_buffer.commit", ["final"] = true };

    public JsonObject? DiscardUtterance() => null;

    public ProtocolEvent ParseEvent(JsonObject raw)
    {
        var eventType = raw.TryGetPropertyValue("type", out var typeNode) ? typeNode?.ToString() : null;
        switch (eventType)
        {
            case "transcription.delta":
            {
                var delta = raw.TryGetPropertyValue("delta", out var deltaNode) ? deltaNode?.ToString() : null;
                return new ProtocolEvent(ProtocolEventKind.Delta, Text: delta ?? "");
            }

            case "transcription.done":
            {
                var text = raw.TryGetPropertyValue("text", out var textNode) ? textNode?.ToString() : null;
                return new ProtocolEvent(ProtocolEventKind.Completed, Text: text ?? "");
            }

            case "error":
                return new ProtocolEvent(ProtocolEventKind.Error, Message: StreamingSttText.RemoteErrorMessage(raw));

            default:
                return new ProtocolEvent(ProtocolEventKind.Ignore);
        }
    }
}

/// <summary>上游模块级工具函数的对应物。</summary>
public static class StreamingSttText
{
    private const string FallbackRemoteError = "remote transcription error";

    /// <summary><c>_first_language_code</c>：数组取第一个字符串或 {code: string}。</summary>
    public static string? FirstLanguageCode(JsonObject raw)
    {
        if (!raw.TryGetPropertyValue("languages", out var node) || node is not JsonArray array)
        {
            return null;
        }

        foreach (var item in array)
        {
            if (item is JsonValue scalar && scalar.TryGetValue<string>(out var language))
            {
                return language;
            }

            if (item is JsonObject obj
                && obj.TryGetPropertyValue("code", out var codeNode)
                && codeNode is JsonValue codeValue
                && codeValue.TryGetValue<string>(out var code))
            {
                return code;
            }
        }

        return null;
    }

    /// <summary><c>_remote_error_message</c>：error.message → error → 固定兜底。</summary>
    public static string RemoteErrorMessage(JsonObject raw)
    {
        if (!raw.TryGetPropertyValue("error", out var errorNode))
        {
            return FallbackRemoteError;
        }

        if (errorNode is JsonObject obj
            && obj.TryGetPropertyValue("message", out var messageNode)
            && messageNode is JsonValue messageValue
            && messageValue.TryGetValue<string>(out var message))
        {
            return message;
        }

        if (errorNode is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            return text;
        }

        return FallbackRemoteError;
    }

    /// <summary><c>_join_transcripts</c>：两侧 strip 后空串短路，否则单空格拼接。</summary>
    public static string JoinTranscripts(string prefix, string text)
    {
        prefix = prefix.Trim();
        text = text.Trim();
        if (prefix.Length == 0)
        {
            return text;
        }

        if (text.Length == 0)
        {
            return prefix;
        }

        return $"{prefix} {text}";
    }
}

/// <summary>
/// <c>_endpoint_url</c>：base_url → WS(S) realtime 端点。
///
/// http(s) 映射 ws(s)；路径不以 <c>/realtime</c> 结尾就补上；hosted OpenAI 强制
/// <c>intent=transcription</c> 并剔除 query 里的 model（转写模型在 session.update 里选，
/// 放 query 会被拒），自建端点按 <paramref name="includeModelQuery"/> 决定是否带 model。
/// </summary>
public static class RealtimeEndpointUrl
{
    public static string Build(string baseUrl, string model, bool includeModelQuery)
    {
        Uri uri;
        try
        {
            uri = new Uri(baseUrl.Trim());
        }
        catch (UriFormatException)
        {
            throw new ArgumentException("Streaming STT base_url must be an http(s) or ws(s) URL", nameof(baseUrl));
        }

        var scheme = uri.Scheme switch
        {
            "http" => "ws",
            "https" => "wss",
            _ => uri.Scheme,
        };
        if (scheme is not ("ws" or "wss") || string.IsNullOrEmpty(uri.Authority))
        {
            throw new ArgumentException("Streaming STT base_url must be an http(s) or ws(s) URL", nameof(baseUrl));
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/realtime", StringComparison.Ordinal))
        {
            path = $"{path}/realtime";
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            if (query["intent"] is null)
            {
                query["intent"] = "transcription";
            }

            query.Remove("model");
        }
        else if (includeModelQuery && query["model"] is null)
        {
            query["model"] = model;
        }

        var queryString = query.Count > 0 ? $"?{query}" : string.Empty;
        return $"{scheme}://{uri.Authority}{path}{queryString}";
    }
}

/// <summary>
/// <c>StatefulStreamingSTTHandler.setup</c> 的校验与装配结论。构造即校验——
/// 配置错误在启动路径暴露，不能等连接进来才发现。
/// </summary>
public sealed record StreamingSttSetup
{
    public required string EndpointUrl { get; init; }

    /// <summary>是否需要向请求头挂 Authorization（api_key 非空时）。</summary>
    public required bool UseApiKey { get; init; }

    public required IRealtimeTranscriptionProtocol Protocol { get; init; }

    /// <summary>
    /// 上游 setup() 逐条对齐：model 非空、采样率为正、超时为正、vLLM 必须 16 kHz、
    /// hosted OpenAI 必须 24 kHz。违反时抛 <see cref="ArgumentException"/>。
    /// </summary>
    public static StreamingSttSetup Create(
        IRealtimeTranscriptionProtocolFactory factory,
        string baseUrl,
        string model,
        int audioSampleRate,
        double connectTimeout,
        double finalTimeout,
        string? language,
        bool includeModelQuery,
        int pipelineSampleRate = 16_000,
        string? apiKey = null)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Streaming STT requires a model", nameof(model));
        }

        if (audioSampleRate <= 0)
        {
            throw new ArgumentException("Streaming STT audio_sample_rate must be > 0", nameof(audioSampleRate));
        }

        if (connectTimeout <= 0 || finalTimeout <= 0)
        {
            throw new ArgumentException("Streaming STT timeouts must be > 0");
        }

        var isVllm = factory.Name == "vllm-realtime";
        if (isVllm && audioSampleRate != pipelineSampleRate)
        {
            throw new ArgumentException("vLLM Realtime STT requires 16 kHz PCM", nameof(audioSampleRate));
        }

        var modelTrimmed = model.Trim();
        var endpointUrl = RealtimeEndpointUrl.Build(baseUrl, modelTrimmed, includeModelQuery);
        if (!isVllm
            && Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase)
            && audioSampleRate != 24_000)
        {
            throw new ArgumentException("Hosted OpenAI Realtime STT requires 24 kHz PCM", nameof(audioSampleRate));
        }

        var normalized = new UriBuilder(endpointUrl) { Query = string.Empty }.Uri.ToString().TrimEnd('?');
        if (apiKey is null
            && string.Equals(
                normalized,
                $"{OpenAiRealtimeProtocol.DefaultBaseUrl}/realtime",
                StringComparison.Ordinal))
        {
            apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        }

        return new StreamingSttSetup
        {
            EndpointUrl = endpointUrl,
            UseApiKey = !string.IsNullOrEmpty(apiKey),
            Protocol = factory.Create(modelTrimmed, string.IsNullOrWhiteSpace(language) ? null : language.Trim(),
                audioSampleRate),
        };
    }
}

/// <summary>协议工厂：按方言名创建冻结协议实例（上游 <c>protocol_type</c> 的对应物）。</summary>
public interface IRealtimeTranscriptionProtocolFactory
{
    string Name { get; }

    IRealtimeTranscriptionProtocol Create(string model, string? language, int audioSampleRate);
}

public sealed class OpenAiRealtimeProtocolFactory : IRealtimeTranscriptionProtocolFactory
{
    public string Name => "openai-realtime";

    public IRealtimeTranscriptionProtocol Create(string model, string? language, int audioSampleRate)
        => new OpenAiRealtimeProtocol(model, language, audioSampleRate);
}

public sealed class VllmRealtimeProtocolFactory : IRealtimeTranscriptionProtocolFactory
{
    public string Name => "vllm-realtime";

    public IRealtimeTranscriptionProtocol Create(string model, string? language, int audioSampleRate)
        => new VllmRealtimeProtocol(model, language, audioSampleRate);
}
