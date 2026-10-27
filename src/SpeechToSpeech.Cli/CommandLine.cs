using SpeechToSpeech.Backends.SmartTurn;
using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Cli;

/// <summary>
/// 命令行解析。
///
/// 有意手工解析而不是引第三方库：上游用 HuggingFace 的 <c>HfArgumentParser</c> 按 dataclass
/// 生成参数，行为（含"未知参数报错"）是它的一部分；这里用一张显式表复现同样的
/// 取值与报错形态，不引入额外依赖，也不会因库版本变化改变错误文案。
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public string Command { get; private set; } = "serve";

    public static readonly string[] Commands = ["serve", "local", "talk", "list-backends"];

    public static CommandLine Parse(string[] args)
    {
        var line = new CommandLine();
        var index = 0;

        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            if (!Commands.Contains(args[0], StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"未知命令 '{args[0]}'；可用命令：{string.Join(", ", Commands)}");
            }

            line.Command = args[0];
            index = 1;
        }

        for (; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"无法识别的参数 '{arg}'（选项必须以 -- 开头）");
            }

            var body = arg[2..];
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                line._values[body[..eq]] = body[(eq + 1)..];
                continue;
            }

            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                line._values[body] = args[++index];
            }
            else
            {
                line._flags.Add(body);
            }
        }

        return line;
    }

    public bool Flag(params string[] names)
    {
        foreach (var name in names)
        {
            if (_flags.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    public string? Value(params string[] names)
    {
        foreach (var name in names)
        {
            if (_values.TryGetValue(name, out var value))
            {
                return value;
            }

            // 后端名可能自带连字符（chat-completions），拼出的键是
            // "chat-completions_base_url"——而命令行惯例写的是下划线。
            // 上游 HfArgumentParser 的字段名全下划线，等价键按归一化补查。
            var normalized = name.Replace('-', '_');
            if (normalized != name && _values.TryGetValue(normalized, out value))
            {
                return value;
            }
        }

        return null;
    }

    public int IntValue(int fallback, params string[] names)
        => int.TryParse(Value(names), out var parsed) ? parsed : fallback;

    public bool BoolValue(bool fallback, params string[] names)
    {
        var value = Value(names);
        if (value is null)
        {
            return fallback;
        }

        return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    /// <summary>
    /// 正负成对的布尔开关。上游用 <c>HfArgumentParser</c> 的 bool 字段，命令行上
    /// 既可写 <c>--smart_turn</c> / <c>--no_smart_turn</c>，也可写 <c>--smart_turn false</c>。
    /// 三种写法都要认，否则「关掉」这件事只能靠删参数，而删参数等于回到默认值。
    /// </summary>
    public bool BoolFlag(bool fallback, string positive, string negative)
    {
        if (Value(positive, positive.Replace('_', '-')) is { } explicitValue)
        {
            return explicitValue.Equals("true", StringComparison.OrdinalIgnoreCase) || explicitValue == "1";
        }

        if (Flag(negative, negative.Replace('_', '-')))
        {
            return false;
        }

        return Flag(positive, positive.Replace('_', '-')) || fallback;
    }

    public double DoubleValue(double fallback, params string[] names)
        => double.TryParse(Value(names), out var parsed) ? parsed : fallback;
}

/// <summary>把命令行映射成后端上下文与服务器选项。</summary>
internal static class OptionsBuilder
{
    public static BackendContext Build(CommandLine line)
    {
        var ttsBackend = line.Value("tts") ?? "qwen3";
        var sttBackend = line.Value("stt") ?? "whisper";
        var llmBackend = line.Value("llm_backend", "llm-backend") ?? "tensorsharp";

        var vad = new VadOptions
        {
            ModelPath = line.Value("vad_model_dir", "vad-model-dir"),
            ConfigPath = line.Value("vad_config_path", "vad-config-path"),
            Threshold = (float)line.DoubleValue(0.0, "vad_threshold", "vad-threshold"),
            SampleRate = 16_000,
            ThreadsNum = line.IntValue(1, "vad_threads"),
        };

        var stt = new SttOptions
        {
            ModelDir = line.Value($"{sttBackend}_model_dir", "stt_model_dir", $"{sttBackend}-model-dir", "stt-model-dir"),
            Language = line.Value($"{sttBackend}_language", "stt_language", "language"),
            ThreadsNum = line.IntValue(2, $"{sttBackend}_threads", "stt_threads"),
            BaseUrl = line.Value($"{sttBackend}_base_url", "stt_base_url", "base_url"),
            ApiKey = line.Value($"{sttBackend}_api_key", "stt_api_key", "api_key"),
            ModelName = sttBackend,
            // 流式后端（openai-realtime / vllm-realtime）：模型 id、目标采样率与两个超时
            // 都有各自的默认值（上游 openai_realtime_stt_arguments / vllm_realtime_stt_arguments）。
            StreamingModel = line.Value($"{sttBackend}_model", "stt_model"),
            SampleRate = line.IntValue(
                sttBackend switch
                {
                    "openai-realtime" => 24_000,
                    "vllm-realtime" => 16_000,
                    _ => 16_000,
                },
                $"{sttBackend}_sample_rate", "stt_sample_rate"),
            ConnectTimeoutSeconds = line.DoubleValue(10.0, $"{sttBackend}_connect_timeout"),
            FinalTimeoutSeconds = line.DoubleValue(60.0, $"{sttBackend}_final_timeout"),
        };

        var llm = new LlmOptions
        {
            ModelPath = line.Value("llm_model_path", "model_path", "model_name"),
            MmProjPath = line.Value("llm_mmproj_path", "mmproj_path"),
            Backend = line.Value("llm_backend_device", "device"),
            MaxNewTokens = line.IntValue(512, "max_new_tokens", "max-new-tokens"),
            Temperature = line.DoubleValue(0.6, "temperature"),
            BaseUrl = line.Value($"{llmBackend}_base_url", "llm_base_url", "base_url"),
            ApiKey = line.Value($"{llmBackend}_api_key", "llm_api_key", "api_key"),
            ModelName = llmBackend,
        };

        var tts = new TtsOptions
        {
            ModelDir = line.Value($"{ttsBackend}_model_dir", "tts_model_dir", $"{ttsBackend}-model-dir", "tts-model-dir"),
            Speaker = line.Value($"{ttsBackend}_speaker", "tts_speaker", "speaker", "voice"),
            Language = line.Value($"{ttsBackend}_language", "tts_language"),
            Instruct = line.Value($"{ttsBackend}_instruct", "tts_instruct"),
            BaseUrl = line.Value($"{ttsBackend}_base_url", "tts_base_url", "base_url"),
            ApiKey = line.Value($"{ttsBackend}_api_key", "tts_api_key", "api_key"),
            ModelName = ttsBackend,
            Voice = line.Value("voice"),
            MaxConcurrency = line.IntValue(1, "tts_max_concurrency"),
        };

        var detectLlmOutputLanguage = line.Flag("detect_llm_output_language", "detect-llm-output-language");

        var turnEnd = new TurnEndOptions
        {
            // 上游默认开启。本构建没有 ONNX 判定器，开着也只是报 disabled——
            // 但开关语义照着来，将来补上判定器不必再改默认值。
            Enabled = line.BoolFlag(true, "smart_turn", "no_smart_turn"),
            SpeculativeReopenMs = line.IntValue(800, "speculative_reopen_ms", "speculative-reopen-ms"),
            UnansweredReopenMs = line.IntValue(7000, "unanswered_reopen_ms", "unanswered-reopen-ms"),
            MaxWaitMs = line.IntValue(2000, "smart_turn_max_wait_ms", "smart-turn-max-wait-ms"),
            IncompleteDelayMs = line.IntValue(600, "smart_turn_incomplete_delay_ms", "smart-turn-incomplete-delay-ms"),
            ModelPath = line.Value("smart_turn_model_path", "smart-turn-model-path"),
            Threshold = line.DoubleValue(0.5, "smart_turn_threshold", "smart-turn-threshold"),
            CpuCount = line.IntValue(1, "smart_turn_cpu_count", "smart-turn-cpu-count"),
        };

        // 组合非法就在启动时失败。上游在 VADHandler.__init__ 里抛，那是启动路径；
        // 本实现的 handler 是每条连接懒建的，等到那时才报错等于「连上来才知道配置错了」。
        turnEnd.Validate();
        // 判定器在启动路径构造（上游在 VADHandler.__init__，时机更晚但同为「用之前」）。
        // 缺模型不算致命：打告警、走 disabled 分支——与上游「用户显式关掉」行为一致，
        // 但必须说出来（差异账 D018：上游缺模型会在启动时直接抛 FileNotFoundError）。
        ITurnEndClassifier? turnEndClassifier = null;
        if (turnEnd.Enabled)
        {
            try
            {
                turnEndClassifier = new SmartTurnOnnxClassifier(
                    turnEnd.ModelPath, turnEnd.Threshold, turnEnd.CpuCount);
            }
            catch (BackendUnavailableException ex)
            {
                Console.Error.WriteLine($"warning: {ex.Message}");
                Console.Error.WriteLine(
                    "warning: Smart Turn 按 disabled 处理：端点判定退回投机宽限 " +
                    $"{turnEnd.SpeculativeReopenMs}ms、处理延迟 0。加 --no_smart_turn 可关掉本条告警。");
            }
        }

        // 口型提取。上游是 --enable_visemes + --stv_model_name（HF 仓库名）；
        // 本构建的引擎吃本地 ONNX 目录，所以参数换成 --stv_model_dir，
        // 另认 SPEECH_TO_SPEECH_VISEME_MODEL_DIR（与端到端测试同一个环境变量）。
        bool visemesEnabled = line.BoolFlag(false, "enable_visemes", "no_enable_visemes");
        var stv = new StvOptions
        {
            Enabled = visemesEnabled,
            ModelDirectory = line.Value("stv_model_dir", "stv-model-dir")
                             ?? Environment.GetEnvironmentVariable("SPEECH_TO_SPEECH_VISEME_MODEL_DIR")
                             ?? (visemesEnabled ? Path.Combine("models", "viseme") : null),
            ConfigPath = line.Value("stv_config_path", "stv-config-path"),
        };

        stv.Validate();

        // 打开口型却在启动时找不到模型：直接失败。上游此时抛 FileNotFoundError，
        // 而「开了口型但永远没有 visemes 事件」会让客户端以为自己的解析坏了。
        if (stv.Enabled)
        {
            string modelPath = Path.Combine(stv.ModelDirectory!, "model.onnx");
            string configPath = stv.ConfigPath ?? Path.Combine(stv.ModelDirectory!, "config.json");
            if (!File.Exists(modelPath) || !File.Exists(configPath))
            {
                throw new BackendUnavailableException(
                    "openlipsync",
                    $"启用口型提取但模型未就位（需要 {modelPath} 与 {configPath}）；" +
                    "用 --stv_model_dir 指定目录，或去掉 --enable_visemes");
            }
        }

        return new BackendContext
        {
            Vad = vad,
            Stt = stt,
            Llm = llm,
            Tts = tts,
            TurnEnd = turnEnd,
            TurnEndClassifier = turnEndClassifier,
            Stv = stv,
            // 标志开着就先预热（上游同口径），否则第一次用语言识别时才加载模型。
            LanguageDetector = new LazyLanguageDetector(preload: detectLlmOutputLanguage),
            DetectLlmOutputLanguage = detectLlmOutputLanguage,
            Log = message =>
            {
                if (line.Flag("verbose"))
                {
                    Console.Error.WriteLine(message);
                }
            },
        };
    }
}
