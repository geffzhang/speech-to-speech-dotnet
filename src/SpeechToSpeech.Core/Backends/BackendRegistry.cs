using SpeechToSpeech.Audio.Diarization;
using SpeechToSpeech.Core.Text;

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

    /// <summary>口型提取（STV）参数。默认关闭，关闭时管线里不加这一级。</summary>
    public StvOptions Stv { get; init; } = new();

    /// <summary>
    /// 流式说话人分离参数。默认关闭；打开时管线在 VAD 之前加一级分离、并在 STT 里给转写补说话人。
    /// </summary>
    public DiarizationOptions Diarization { get; init; } = new();

    /// <summary>
    /// 帧级说话人分割引擎。与其它后端不同，它不按名字选：只有模型文件一条路（pyannote 系 ONNX）。
    /// null 表示没有可用实现——此时即使打开了 <see cref="Diarization"/> 也只会在启动时报出来。
    /// </summary>
    public ISpeakerMaskSegmentation? SpeakerSegmentation { get; init; }

    /// <summary>
    /// 声纹引擎（把一段音频压成一个说话人向量）。CLI 在 <c>--diarization</c> 下保证非 null
    /// （缺模型启动即失败，见差异账 D033）；为 null 只出现在「只要分割、不要身份」的装配里，
    /// 那时分离级不启动，对外行为与未开分离一致。
    /// </summary>
    public ISpeakerEmbedding? SpeakerEmbedding { get; init; }

    /// <summary>
    /// 端点判定与投机轮次配置（<c>speculative_reopen_ms</c> / <c>unanswered_reopen_ms</c> /
    /// <c>smart_turn*</c>）。上游这几项挂在 VAD 参数对象上，本实现单独一组以免
    /// 与 Silero 的模型/阈值混在一起。
    /// </summary>
    public TurnEndOptions TurnEnd { get; init; } = new();

    /// <summary>
    /// 端点判定器。null 表示本构建没有可用实现（暂无 ONNX 分类器），
    /// 此时决策层报 <c>disabled</c> 并退回普通投机宽限。
    /// </summary>
    public ITurnEndClassifier? TurnEndClassifier { get; init; }

    /// <summary>
    /// 文本语言识别后端。缺省为 null → 助手语言检测不可用（上游缺 <c>lingua</c> 时同样退化为 null）。
    ///
    /// 与其它后端不同，它没有「名字」也没有模型选择：上游只有一个实现，
    /// 缺失只影响 <c>--detect_llm_output_language</c> 的效果。
    /// </summary>
    public ILanguageDetector? LanguageDetector { get; init; }

    /// <summary>
    /// 对应上游模块参数 <c>--detect_llm_output_language</c>。
    ///
    /// 关掉时并非完全不做检测：上游仍会观察并写回 <c>last_assistant_language</c>，
    /// 只是不拿它选本轮 TTS 语言。开关语义要照这个来，否则「关掉检测」会连带改变下一轮行为。
    /// </summary>
    public bool DetectLlmOutputLanguage { get; init; }

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

    /// <summary>口型提取后端工厂（单实现，见 <see cref="RegisterViseme"/>）。</summary>
    private static Func<BackendContext, IVisemeExtractor>? VisemeFactory;

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
        Stt("openai-realtime", implemented: true, streams: true),
        Stt("vllm-realtime", implemented: true, streams: true),
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

    /// <summary>
    /// 注册口型提取后端。与 VAD/STT/LLM/TTS 不同，它只有一条实现（OpenLipSync），
    /// 上游也只有一条（wav2vec2 STV）——所以这里是单工厂而不是名字表。
    /// </summary>
    public static void RegisterViseme(Func<BackendContext, IVisemeExtractor> factory)
    {
        lock (Gate)
        {
            VisemeFactory = factory;
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

    /// <summary>
    /// 按配置构造口型提取后端。未启用时返回 null（管线里不加这一级）。
    ///
    /// 启用了却拿不到工厂属于配置错误：宁可启动/连线时报错，也不能静默变成
    /// 「打开了口型但永远没有 visemes 事件」——客户端会以为自己的解析坏了。
    /// </summary>
    public static IVisemeExtractor? CreateViseme(BackendContext context)
    {
        if (!context.Stv.Enabled)
        {
            return null;
        }

        Func<BackendContext, IVisemeExtractor>? factory;
        lock (Gate)
        {
            factory = VisemeFactory;
        }

        if (factory is null)
        {
            throw new BackendUnavailableException("openlipsync", "口型提取后端未注册工厂");
        }

        return factory(context);
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
