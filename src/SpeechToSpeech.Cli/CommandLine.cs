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

    public bool Flag(string name) => _flags.Contains(name);

    public string? Value(params string[] names)
    {
        foreach (var name in names)
        {
            if (_values.TryGetValue(name, out var value))
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

        return new BackendContext
        {
            Vad = vad,
            Stt = stt,
            Llm = llm,
            Tts = tts,
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
