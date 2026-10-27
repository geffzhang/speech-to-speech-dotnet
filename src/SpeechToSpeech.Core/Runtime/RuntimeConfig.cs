using System.Text.Json;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Core.Runtime;

/// <summary>
/// 由 <c>session.update</c> 写入、由管线各阶段（VAD/LLM/TTS）在处理时读取的共享可变配置。
///
/// 规范性状态保存在 <see cref="Session"/>（原始 JSON）里，而不是分散到强类型字段：
/// 上游用 Pydantic 的 <c>model_fields_set</c> 表达"只合并客户端真正发来的字段"，
/// C# 的强类型模型无法区分"未设置"与"显式设为 null"，因此这里保留原始 JSON 做深合并，
/// 再通过只读访问器提供强类型视图（见差异账 C-04）。
/// </summary>
public sealed class RuntimeConfig
{
    private readonly Lock _lock = new();

    public RuntimeConfig(ChatSession? chat = null, JsonObject? session = null)
    {
        Chat = chat ?? new ChatSession();
        Session = session ?? new JsonObject { ["type"] = "realtime" };
        EnsureAudioStructure(Session);
    }

    /// <summary>本连接的对话状态。唯一写入口纪律见 <see cref="ChatSession"/>。</summary>
    public ChatSession Chat { get; }

    /// <summary>会话配置的规范状态（深合并目标）。</summary>
    public JsonObject Session { get; }

    /// <summary>助手最近一次使用的语言，供下一轮 TTS 快照使用。</summary>
    public string? LastAssistantLanguage { get; set; }

    /// <summary>
    /// 把 <paramref name="update"/> 中显式出现的字段深合并进 <see cref="Session"/>。
    /// 递归进入嵌套对象，避免局部嵌套更新覆盖未设置字段。
    /// </summary>
    public void ApplySessionUpdate(JsonObject update)
    {
        lock (_lock)
        {
            ApplyInto(Session, update);
            EnsureAudioStructure(Session);
        }
    }

    private static void ApplyInto(JsonObject current, JsonObject update)
    {
        foreach (var (key, value) in update)
        {
            if (value is JsonObject newObj
                && current.TryGetPropertyValue(key, out var oldNode)
                && oldNode is JsonObject oldObj)
            {
                ApplyInto(oldObj, newObj);
            }
            else
            {
                current[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>保证 <c>audio.input</c> 与 <c>audio.output</c> 永不为 null。</summary>
    private static void EnsureAudioStructure(JsonObject session)
    {
        if (session["audio"] is not JsonObject audio)
        {
            audio = new JsonObject();
            session["audio"] = audio;
        }

        if (audio["input"] is not JsonObject)
        {
            audio["input"] = new JsonObject();
        }

        if (audio["output"] is not JsonObject)
        {
            audio["output"] = new JsonObject();
        }
    }

    // ── 强类型视图 ────────────────────────────────────────────────────

    public string? Instructions => GetString(Session, "instructions");

    /// <summary>最终语音是否应自动调度生成。缺省为 true（保持既有自动行为）。</summary>
    public bool CreateResponseEnabled
    {
        get
        {
            var value = GetTurnDetectionValue("create_response");
            return value is null ? true : value.GetValue<bool>();
        }
    }

    /// <summary>打断是否应取消在途响应。缺省 true（OpenAI 默认）。</summary>
    public bool InterruptResponseEnabled
    {
        get
        {
            var value = GetTurnDetectionValue("interrupt_response");
            return value is null ? true : value.GetValue<bool>();
        }
    }

    /// <summary>客户端是否在 <c>extensions</c> 里显式订阅了可替换的投机转写快照。</summary>
    public bool InputAudioTranscriptionSnapshotsEnabled
    {
        get
        {
            if (Session["extensions"] is not JsonArray extensions)
            {
                return false;
            }

            return extensions.Any(e => e?.GetValue<string>() == "speech_to_speech.input_audio_transcription.snapshot");
        }
    }

    /// <summary>输入转写语言；<c>"auto"</c> 原样返回，供 TTS 侧判定为"交给模型"。</summary>
    public string? SelectedLanguage
    {
        get
        {
            var language = GetString(Session, "audio", "input", "transcription", "language");
            return language is not null && language.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? "auto"
                : language;
        }
    }

    public string? Voice => GetString(Session, "audio", "output", "voice");

    public string? InputAudioFormatType => GetString(Session, "audio", "input", "format", "type");

    public int InputSampleRate
        => GetInt(Session, "audio", "input", "format", "rate") ?? 24_000;

    public string? OutputAudioFormatType => GetString(Session, "audio", "output", "format", "type");

    public int OutputSampleRate
        => GetInt(Session, "audio", "output", "format", "rate") ?? 24_000;

    public double? Temperature => GetDouble(Session, "temperature");

    public int? MaxResponseOutputTokens => GetInt(Session, "max_response_output_tokens");

    /// <summary>会话级工具定义（function 类型）。</summary>
    public IReadOnlyList<ToolDefinition> Tools
    {
        get
        {
            if (Session["tools"] is not JsonArray tools)
            {
                return [];
            }

            var result = new List<ToolDefinition>();
            foreach (var node in tools)
            {
                if (node is not JsonObject tool)
                {
                    continue;
                }

                var name = tool["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                result.Add(new ToolDefinition(
                    name,
                    tool["description"]?.GetValue<string>(),
                    tool["parameters"]?.DeepClone() as JsonObject));
            }

            return result;
        }
    }

    /// <summary>VAD 阈值；缺省 0.5（与 OpenAI ServerVAD 默认一致）。</summary>
    public double VadThreshold => GetTurnDetectionValue("threshold")?.GetValue<double>() ?? 0.5;

    public int VadPrefixPaddingMs => GetTurnDetectionValue("prefix_padding_ms")?.GetValue<int>() ?? 300;

    public int VadSilenceDurationMs => GetTurnDetectionValue("silence_duration_ms")?.GetValue<int>() ?? 500;

    public string? TurnDetectionType => GetTurnDetectionValue("type")?.GetValue<string>();

    /// <summary>转为线上 <c>session</c> 对象的快照（用于 <c>session.created</c> / <c>session.updated</c>）。</summary>
    public JsonObject SnapshotSession()
    {
        lock (_lock)
        {
            return (JsonObject)Session.DeepClone();
        }
    }

    // ── JSON 取值辅助 ─────────────────────────────────────────────────

    private JsonNode? GetTurnDetectionValue(string key)
    {
        lock (_lock)
        {
            if (Session["audio"]?["input"]?["turn_detection"] is not JsonObject td)
            {
                return null;
            }

            return td[key]?.DeepClone();
        }
    }

    private string? GetString(JsonObject root, params string[] path)
        => Traverse(root, path)?.GetValue<string>();

    private static int? GetInt(JsonObject root, params string[] path)
    {
        var node = Traverse(root, path);
        if (node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.Number => node.GetValue<int>(),
            JsonValueKind.String when int.TryParse(node.GetValue<string>(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static double? GetDouble(JsonObject root, params string[] path)
    {
        var node = Traverse(root, path);
        if (node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.Number => node.GetValue<double>(),
            JsonValueKind.String when double.TryParse(node.GetValue<string>(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static JsonNode? Traverse(JsonObject root, params string[] path)
    {
        JsonNode? node = root;
        foreach (var segment in path)
        {
            node = node?[segment];
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }
}

/// <summary>会话级工具定义。</summary>
public sealed record ToolDefinition(string Name, string? Description, JsonObject? Parameters);

/// <summary>单次响应的覆盖参数（来自 <c>response.create</c>）。</summary>
public sealed class RealtimeResponseCreateParams
{
    public string? Instructions { get; init; }
    public JsonObject? ToolChoice { get; init; }
    public double? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// <c>output_modalities</c>。官方是数组（<c>["audio"]</c> / <c>["text"]</c>）。
    /// </summary>
    public IReadOnlyList<string>? OutputModalities { get; init; }

    /// <summary>
    /// <c>audio.output.voice</c>：本次响应指定的音色。
    ///
    /// 这是**逐响应覆盖**，优先于会话配置。它必须一路走到 TTS 后端，不能只在载荷上出现——
    /// 报一个没生效的音色比不报更难查（客户端以为换了音色，听到的还是旧的那个）。
    /// </summary>
    public string? Voice { get; init; }

    /// <summary>
    /// <c>conversation</c>：<c>"auto"</c>（缺省，进默认会话）或 <c>"none"</c>（带外响应）。
    /// </summary>
    public string? Conversation { get; init; }

    /// <summary>
    /// 带外响应（<c>conversation="none"</c>）。对应上游 <c>is_out_of_band</c>。
    ///
    /// 带外响应对着一份临时上下文生成，从不接进默认会话：输出不写回历史、
    /// <c>conversation_id</c> 报 null、metadata 不挂工具输入、不参与工具跟进抑制。
    /// <c>"auto"</c>、缺省或任意其它值一律按带内处理。
    /// </summary>
    public bool IsOutOfBand => Conversation == "none";

    /// <summary>
    /// <c>input</c>：随响应携带的会话项数组（原样保留，逐项校验后使用）。
    ///
    /// null = 未携带；空数组也是有效值。带内时逐项进默认会话（进历史）；
    /// 带外时不碰会话，随请求走，给一次性聊天做种子——input=null 读会话副本、
    /// input=[] 用空聊天、input=[...] 用这些项做种子（上游 build_active_chat 三种口径）。
    /// </summary>
    public List<JsonObject>? Input { get; init; }

    /// <summary>
    /// 本次响应是否要出音频。
    ///
    /// 复现上游 <c>response_wants_audio</c>：缺省（null 或空数组）或含 <c>"audio"</c> 即为真；
    /// 非空且不含 <c>"audio"</c>（如 <c>["text"]</c>）为假。<c>"audio"</c> 已隐含转写，
    /// 所以两个取值互斥，不存在既要音频又要纯文本的情形。
    /// </summary>
    public bool WantsAudio => OutputModalities is null || OutputModalities.Count == 0
        || OutputModalities.Contains("audio", StringComparer.Ordinal);

    /// <summary>取本次响应指令：优先响应级，其次会话级。</summary>
    public string? ResolveInstructions(RuntimeConfig config) => Instructions ?? config.Instructions;

    /// <summary>取本次响应温度：优先响应级，其次会话级。</summary>
    public double? ResolveTemperature(RuntimeConfig config) => Temperature ?? config.Temperature;

    /// <summary>取本次响应最大输出 token：优先响应级，其次会话级。</summary>
    public int? ResolveMaxOutputTokens(RuntimeConfig config) => MaxOutputTokens ?? config.MaxResponseOutputTokens;

    /// <summary>
    /// 取本次响应的音色：优先响应级，其次会话级。
    ///
    /// 上游 <c>_build_response</c> 与 <c>_apply_session_voice_override</c> 用的是同一条顺序，
    /// 所以载荷里报的值与真正拿去合成的值出自同一个判断——不做这一步就会出现
    /// 「载荷说换了音色、合成还在用旧的」。
    /// </summary>
    public string? ResolveVoice(RuntimeConfig? config) => ResolveVoice(this, config);

    /// <summary>
    /// 音色解析的唯一表达式。
    ///
    /// 协议层（报什么）与 TTS 层（用什么）都走这一条：分成两份写法时，改一处忘另一处
    /// 只会表现为「字段换了、声音没换」，读代码看不出来。响应级为空即视为未指定。
    /// </summary>
    public static string? ResolveVoice(RealtimeResponseCreateParams? response, RuntimeConfig? config)
        => (string.IsNullOrEmpty(response?.Voice) ? null : response.Voice) ?? config?.Voice;
}
