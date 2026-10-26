using SpeechToSpeech.Core.Backends;
using ManySpeech.WhisperAsr;

namespace SpeechToSpeech.Backends.ManySpeech;

/// <summary>
/// Whisper STT 后端（ManySpeech 的 ONNX 实现）。
///
/// 对应上游的 <c>--stt whisper</c>。Whisper 自带语言识别，所以 <c>language</c> 留空时
/// 由模型判定并把结果回填到转写事件上——这正是上游 <c>transcription.language_code</c> 的来源。
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
            configFilePath: config,
            threadsNum: Math.Max(1, _options.ThreadsNum));
    }

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
