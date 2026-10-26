using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using ElBruno.QwenTTS.Pipeline;

namespace SpeechToSpeech.Backends.QwenTts;

/// <summary>
/// Qwen3-TTS 本地 ONNX 后端。
///
/// 对应上游默认的 <c>--tts qwen3</c>，而且是**同一个模型**：
/// 上游用 faster-qwen3-tts / mlx-audio 跑 Qwen3-TTS-12Hz，这里用 ElBruno.QwenTTS 的
/// ONNX 移植跑同一族权重（0.6B CustomVoice，24 kHz 单声道）。换的只是运行时，不是模型。
///
/// 一个必须承认的差异：上游 Qwen3-TTS 逐帧流式吐 PCM，ElBruno 的 ONNX 管线是整句合成后
/// 返回（<c>IsProgressive = false</c>）。补偿手段是句级切分——调用方按句喂，
/// 首句音频仍然能在整段回答生成完之前就播出去。见差异账 D-01。
/// </summary>
public sealed class QwenTtsBackend : ITtsBackend
{
    private readonly TtsOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TtsPipeline? _pipeline;
    private bool _disposed;

    public QwenTtsBackend(TtsOptions options)
    {
        _options = options;
    }

    public string Name => "qwen3";

    public int SampleRate => 24_000;

    private TtsPipeline EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline is not null)
        {
            return _pipeline;
        }

        var modelDir = _options.ModelDir;
        if (string.IsNullOrWhiteSpace(modelDir))
        {
            throw new BackendUnavailableException(Name, "未指定 --tts_model_dir");
        }

        try
        {
            // CreateAsync 会在模型缺失时自动从 HuggingFace 下载（首次约 5.5 GB）
            _pipeline = TtsPipeline.CreateAsync(
                modelDir: modelDir,
                maxConcurrency: Math.Max(1, _options.MaxConcurrency)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new BackendUnavailableException(Name, $"模型加载失败：{ex.Message}");
        }

        return _pipeline;
    }

    public IEnumerable<byte[]> Synthesize(TtsRequest request, CancellationToken cancellationToken)
    {
        var pipeline = EnsureLoaded();
        var speaker = NormalizeSpeaker(request.Speaker ?? _options.Speaker ?? "ryan");
        var language = NormalizeLanguage(request.Language ?? _options.Language);

        // 一次只允许一个合成在跑：ONNX 会话不是线程安全的，
        // 并发进来只会让两条流互相拖慢，不会更快。
        _gate.Wait(cancellationToken);
        TtsAudioResult result;
        try
        {
            result = pipeline.SynthesizeToPcmAsync(
                text: request.Text,
                speaker: speaker,
                language: language,
                instruct: request.Instruct ?? _options.Instruct,
                progress: null,
                cancellationToken: cancellationToken).GetAwaiter().GetResult();
        }
        finally
        {
            _gate.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();

        var samples = result.Samples.ToArray();
        if (samples.Length == 0)
        {
            yield break;
        }

        yield return AudioUtil.ToPcm16(samples);
    }

    /// <summary>
    /// 把上游音色名映射到 Qwen3-TTS 的预设名。
    ///
    /// 上游默认用 Qwen3-TTS 的 CustomVoice 预设，名字（ryan/serena/…）本来就一致；
    /// 这里只做大小写与空值兜底，避免一个拼错的名字把整条链路搞崩。
    /// </summary>
    private static string NormalizeSpeaker(string speaker)
    {
        var normalized = speaker.Trim().ToLowerInvariant();
        return normalized.Length == 0 ? "ryan" : normalized;
    }

    /// <summary>
    /// 语言码归一。
    ///
    /// 上游把 <c>auto</c> 解释为"交给模型判语言"，这里保持同一语义：
    /// 传空串让 Qwen3-TTS 自己判定，而不是硬塞一个 <c>english</c>。
    /// </summary>
    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "auto";
        }

        var normalized = language.Trim().ToLowerInvariant();
        return normalized switch
        {
            "zh" => "chinese",
            "en" => "english",
            "ja" => "japanese",
            "ko" => "korean",
            "es" => "spanish",
            "ru" => "russian",
            _ => normalized,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _pipeline?.Dispose();
        }
        catch (Exception ex)
        {
            TranscriptLog.Error("failed to dispose Qwen TTS pipeline", ex);
        }

        _gate.Dispose();
    }
}
