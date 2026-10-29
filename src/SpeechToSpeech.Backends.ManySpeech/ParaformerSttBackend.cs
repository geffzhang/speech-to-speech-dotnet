using SpeechToSpeech.Core.Backends;
using ManySpeech.AliParaformerAsr;

namespace SpeechToSpeech.Backends.ManySpeech;

/// <summary>
/// Paraformer / SenseVoice STT 后端（ManySpeech 的 ONNX 实现）。
///
/// 对应上游的 <c>--stt paraformer</c> 与 <c>--stt sense-voice</c>。
/// 两者在 ManySpeech 侧共用同一套离线识别器，只是模型目录不同，
/// 因此用同一个类、由 <see cref="ModelKind"/> 区分。
/// </summary>
public sealed class ParaformerSttBackend : ManySpeechSttBackendBase
{
    private readonly SttOptions _options;
    private readonly string _kind;
    private OfflineRecognizer? _recognizer;

    public ParaformerSttBackend(SttOptions options, bool senseVoice)
    {
        _options = options;
        _kind = senseVoice ? "sense-voice" : "paraformer";
    }

    public override string Name => _kind;

    protected override void Load()
    {
        var model = ModelFiles.Require(_options.ModelDir, _kind, "model*.onnx", "*.onnx");
        var config = _kind == "sense-voice"
            ? ModelFiles.Optional(_options.ModelDir, "config.yaml", "*.yaml") ?? string.Empty
            : ModelFiles.Require(_options.ModelDir, _kind, "config.yaml", "*.yaml");
        var mvn = ModelFiles.Optional(_options.ModelDir, "am.mvn", "*.mvn");
        var tokens = ModelFiles.Require(_options.ModelDir, _kind, "tokens.txt");

        _recognizer = new OfflineRecognizer(
            modelFilePath: model,
            configFilePath: config,
            mvnFilePath: mvn ?? string.Empty,
            tokensFilePath: tokens,
            modelebFilePath: string.Empty,
            hotwordFilePath: string.Empty,
            batchSize: 1,
            threadsNum: Math.Max(1, _options.ThreadsNum));
    }

    protected override (string Text, string? Language) Recognize(float[] samples)
    {
        var recognizer = _recognizer
                         ?? throw new BackendUnavailableException(Name, "模型尚未加载");

        using var stream = recognizer.CreateOfflineStream();
        stream.AddSamples(samples);
        var result = recognizer.GetResult(stream);

        // ManySpeech.AliParaformerAsr 1.1.9 的结果实体没有 Language 字段
        // （SenseVoice 模型本身会判语言，但包未暴露）。见差异账 D004。
        return (result.Text ?? string.Empty, null);
    }

    public override void Dispose() => _recognizer?.Dispose();
}
