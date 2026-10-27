using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Backends;
using ManySpeech.WhisperAsr;

namespace SpeechToSpeech.Backends.ManySpeech;

/// <summary>
/// Whisper STT 后端（ManySpeech 的 ONNX 实现）。
///
/// 对应上游的 <c>--stt whisper</c>。语言有两处来源，别混：
/// <list type="bullet">
///   <item>请求侧：<c>language</c> 参数（上游默认 <c>en</c>，<c>auto</c> 表示让模型自己判）。</item>
///   <item>响应侧：<c>result.Language</c>，即上游 <c>transcription.language_code</c> 的来源。
///         上游明确要求报「实际转写所用语言」而不是「请求的语言」（英文专用模型会忽略请求）。</item>
/// </list>
/// </summary>
public sealed class WhisperSttBackend : ManySpeechSttBackendBase
{
    private readonly SttOptions _options;
    private OfflineRecognizer? _recognizer;

    public WhisperSttBackend(SttOptions options)
    {
        _options = options;
    }

    public override string Name => "whisper";

    protected override void Load()
    {
        var encoder = ModelFiles.Require(_options.ModelDir, "whisper", "encoder*.onnx");
        var decoder = ModelFiles.Require(_options.ModelDir, "whisper", "decoder*.onnx");
        var config = ModelFiles.Require(_options.ModelDir, "whisper", "conf.json", "*.json");

        _recognizer = new OfflineRecognizer(
            encoderFilePath: encoder,
            decoderFilePath: decoder,
            confEntity: null!,
            configFilePath: ResolveConfig(config, _options.Language),
            threadsNum: Math.Max(1, _options.ThreadsNum));
    }

    /// <summary>
    /// 把 <c>language</c> 落进一份临时 conf，返回库该读哪个文件。
    ///
    /// 为什么不能只传参数：ManySpeech 的离线识别器没有运行期语言入口，只认 conf.json 里的
    /// <c>decoding_options.language</c>；而直接改模型目录里的原件会让一次运行污染用户资产
    /// （下次跑别的语言还得记着改回来），所以写临时副本。
    ///
    /// 口径对齐上游 <c>WhisperSTTHandlerArguments.language</c>：默认 <c>en</c>；
    /// <c>"auto"</c> 表示自动检测——此时**不覆盖** conf，原样交给模型（上游是把这个键从
    /// gen_kwargs 里 pop 掉，faster-whisper 只在完全不传时才自动判定）。
    /// 语言代码大小写与前后空白一律规整，避免 <c>"ZH"</c> 这类写法被库当成未知语言。
    /// </summary>
    internal static string ResolveConfig(string configPath, string? language)
    {
        var requested = (string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim())
            .ToLowerInvariant();
        if (requested == "auto")
        {
            return configPath;
        }

        var json = File.ReadAllText(configPath);
        var root = JsonNode.Parse(json)
                   ?? throw new BackendUnavailableException("whisper", $"无法解析 {configPath}");

        if (root["decoding_options"] is not JsonObject decoding)
        {
            throw new BackendUnavailableException(
                "whisper", $"{configPath} 缺 decoding_options 节点，无法指定识别语言");
        }

        decoding["language"] = requested;

        // 文件名带源 conf 的哈希：同一台机器上可能并存 tiny/base/large 三份 conf，
        // 只按语言命名会互相覆盖，重启后就可能读到别的模型的维度配置。
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath))))[..8];

        var tempPath = Path.Combine(
            Path.GetTempPath(), $"s2s-whisper-conf-{fingerprint}-{requested.ToLowerInvariant()}.json");

        File.WriteAllText(tempPath, root.ToJsonString());
        return tempPath;
    }

    /// <summary>上游 <c>WhisperSTTHandlerArguments.language</c> 的默认值。</summary>
    internal const string DefaultLanguage = "en";

    protected override (string Text, string? Language) Recognize(float[] samples)
    {
        var recognizer = _recognizer
                         ?? throw new BackendUnavailableException(Name, "模型尚未加载");

        using var stream = recognizer.CreateOfflineStream();
        stream.AddSamples(samples);
        var result = recognizer.GetResult(stream);
        return (result.Text ?? string.Empty, result.Language);
    }

    public override void Dispose() => _recognizer?.Dispose();
}
