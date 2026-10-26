namespace SpeechToSpeech.Core.Backends;

/// <summary>后端种类。</summary>
public enum BackendKind
{
    Stt,
    Llm,
    Tts,
}

/// <summary>
/// 一个后端的选择器元数据。
///
/// <see cref="Implemented"/> 为 false 表示该名字在上游存在、但当前 C# 构建没有对应运行时；
/// 选择它会得到一条明确的 <see cref="BackendUnavailableException"/>，而不是静默降级。
/// 这是"全后端平铺"的诚实形态：31 个名字全部可见，能跑的跑，不能跑的报清原因。
/// </summary>
public sealed record BackendSpec(
    string Name,
    BackendKind Kind,
    BackendCapabilities Capabilities,
    bool Implemented,
    string? UnsupportedReason = null);

/// <summary>创建后端时需要的上下文。</summary>
public sealed class BackendContext
{
    public required VadOptions Vad { get; init; }
    public required SttOptions Stt { get; init; }
    public required LlmOptions Llm { get; init; }
    public required TtsOptions Tts { get; init; }

    /// <summary>日志输出；默认为 stderr。</summary>
    public Action<string> Log { get; init; } = message => Console.Error.WriteLine(message);
}

/// <summary>
/// 后端注册表。
///
/// 名字与能力标记逐条取自上游 <c>backend_registry.py</c> 的 <c>STT_BACKENDS</c> /
/// <c>LLM_BACKENDS</c> / <c>TTS_BACKENDS</c>，保证同一组 <c>--stt/--llm_backend/--tts</c>
/// 取值在两边的含义一致。具体实现由各后端程序集在启动时注入工厂（见 <see cref="RegisterStt"/> 等）。
/// </summary>
public static class BackendRegistry
{
    private static readonly object Gate = new();

    private static readonly Dictionary<string, Func<BackendContext, IVadBackend>> VadFactories = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Func<BackendContext, ISttBackend>> SttFactories = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Func<BackendContext, ILanguageModelBackend>> LlmFactories = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Func<BackendContext, ITtsBackend>> TtsFactories = new(StringComparer.Ordinal);

    /// <summary>STT 后端清单，顺序与上游一致。</summary>
    public static IReadOnlyList<BackendSpec> SttBackends { get; } =
    [
        Stt("none", capabilities: new BackendCapabilities { BypassesTranscriptionNotifier = true }, reason: "直连音频输入（无 STT 路径）尚未接入 LLM 适配器"),
        Stt("whisper", implemented: true),
        Stt("whisper-mlx", reason: "MLX 仅适用于 Apple Silicon，C# 侧无对应运行时"),
        Stt("mlx-audio-whisper", reason: "MLX 仅适用于 Apple Silicon，C# 侧无对应运行时"),
        Stt("faster-whisper", reason: "faster-whisper 为 CTranslate2 实现，C# 侧改用 whisper（ONNX）"),
        Stt("parakeet-tdt", reason: "NVIDIA Parakeet TDT 无 C# 运行时；等价能力请用 paraformer / whisper"),
        Stt("parakeet-unified", reason: "NeMo 工具链无 C# 运行时"),
        Stt("nemotron-streaming", reason: "NeMo 工具链无 C# 运行时"),
        Stt("orukeet", reason: "NeMo 工具链无 C# 运行时"),
        Stt("paraformer", implemented: true),
        Stt("sense-voice", implemented: true),
        Stt("qwen3-asr", reason: "Qwen3-ASR 无 C# 运行时"),
        Stt("openai", implemented: true),
        Stt("openai-realtime", reason: "上游 realtime 转写尚未移植（见差异账 D-05）", streams: true),
        Stt("vllm-realtime", reason: "上游 realtime 转写尚未移植（见差异账 D-05）", streams: true),
    ];

    /// <summary>LLM 后端清单。</summary>
    public static IReadOnlyList<BackendSpec> LlmBackends { get; } =
    [
        Llm("transformers", implemented: true),
        Llm("mlx-lm", reason: "MLX 仅适用于 Apple Silicon；C# 侧用 tensorsharp"),
        Llm("tensorsharp", implemented: true, proxy: true),
        Llm("responses-api", reason: "OpenAI Responses API 的流式事件尚未接入"),
        Llm("chat-completions", implemented: true, proxy: true, audio: true),
    ];

    /// <summary>TTS 后端清单。</summary>
    public static IReadOnlyList<BackendSpec> TtsBackends { get; } =
    [
        Tts("chatTTS", reason: "ChatTTS 无 C# 运行时"),
        Tts("facebookMMS", reason: "VITS MMS 需要独立 G2P，尚未移植"),
        Tts("omnivoice", reason: "OmniVoice 无 C# 运行时"),
        Tts("pocket", reason: "Pocket TTS 无 C# 运行时"),
        Tts("phonon", reason: "Phonon 无 C# 运行时"),
        Tts("kokoro", reason: "Kokoro 需要 Misaki G2P，尚未移植"),
        Tts("kitten", reason: "KittenTTS 无 C# 运行时"),
        Tts("qwen3", implemented: true, assistantLanguage: true),
        Tts("openai", implemented: true),
        Tts("drama3", reason: "需 SGLang Omni 服务端，尚未接入"),
        Tts("minimax", reason: "MiniMax hosted API 尚未接入"),
        Tts("supertonic", reason: "Supertonic 无 C# 运行时"),
    ];

    private static BackendSpec Stt(string name, BackendCapabilities? capabilities = null, bool implemented = false,
        string? reason = null, bool streams = false)
        => new(name, BackendKind.Stt,
            capabilities ?? new BackendCapabilities { StreamsAudioChunks = streams },
            implemented, implemented ? null : reason ?? "该后端在本构建中未实现");

    private static BackendSpec Llm(string name, bool implemented = false, string? reason = null,
        bool proxy = false, bool audio = false)
        => new(name, BackendKind.Llm,
            new BackendCapabilities { SupportsLlmProxy = proxy, SupportsAudioInput = audio },
            implemented, implemented ? null : reason ?? "该后端在本构建中未实现");

    private static BackendSpec Tts(string name, bool implemented = false, string? reason = null,
        bool assistantLanguage = false)
        => new(name, BackendKind.Tts,
            new BackendCapabilities { PassesAssistantLanguageFlag = assistantLanguage },
            implemented, implemented ? null : reason ?? "该后端在本构建中未实现");

    // ── 工厂注入 ──────────────────────────────────────────────────────

    public static void RegisterVad(string name, Func<BackendContext, IVadBackend> factory)
    {
        lock (Gate)
        {
            VadFactories[name] = factory;
        }
    }

    public static void RegisterStt(string name, Func<BackendContext, ISttBackend> factory)
    {
        lock (Gate)
        {
            SttFactories[name] = factory;
        }
    }

    public static void RegisterLlm(string name, Func<BackendContext, ILanguageModelBackend> factory)
    {
        lock (Gate)
        {
            LlmFactories[name] = factory;
        }
    }

    public static void RegisterTts(string name, Func<BackendContext, ITtsBackend> factory)
    {
        lock (Gate)
        {
            TtsFactories[name] = factory;
        }
    }

    // ── 创建 ──────────────────────────────────────────────────────────

    public static IVadBackend CreateVad(string name, BackendContext context)
    {
        lock (Gate)
        {
            if (VadFactories.TryGetValue(name, out var factory))
            {
                return factory(context);
            }
        }

        throw new BackendUnavailableException(name, "VAD 后端未注册工厂");
    }

    public static ISttBackend CreateStt(string name, BackendContext context)
    {
        var spec = Find(SttBackends, name, BackendKind.Stt);
        lock (Gate)
        {
            if (SttFactories.TryGetValue(name, out var factory))
            {
                return factory(context);
            }
        }

        throw new BackendUnavailableException(name, spec.UnsupportedReason ?? "该后端没有注册工厂");
    }

    public static ILanguageModelBackend CreateLlm(string name, BackendContext context)
    {
        var spec = Find(LlmBackends, name, BackendKind.Llm);
        lock (Gate)
        {
            if (LlmFactories.TryGetValue(name, out var factory))
            {
                return factory(context);
            }
        }

        throw new BackendUnavailableException(name, spec.UnsupportedReason ?? "该后端没有注册工厂");
    }

    public static ITtsBackend CreateTts(string name, BackendContext context)
    {
        var spec = Find(TtsBackends, name, BackendKind.Tts);
        lock (Gate)
        {
            if (TtsFactories.TryGetValue(name, out var factory))
            {
                return factory(context);
            }
        }

        throw new BackendUnavailableException(name, spec.UnsupportedReason ?? "该后端没有注册工厂");
    }

    /// <summary>按名字查找后端；未知名字列出全部合法取值（对齐上游的报错形态）。</summary>
    public static BackendSpec Find(IReadOnlyList<BackendSpec> specs, string name, BackendKind kind)
    {
        foreach (var spec in specs)
        {
            if (string.Equals(spec.Name, name, StringComparison.Ordinal))
            {
                return spec;
            }
        }

        var supported = string.Join(", ", specs.Select(s => s.Name));
        throw new ArgumentException(
            $"未知的 {kind} 后端 {name}；可选值：{supported}", nameof(name));
    }
}
