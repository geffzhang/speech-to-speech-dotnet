using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Runtime;
using ManySpeech.SileroVad;

namespace SpeechToSpeech.Backends.ManySpeech;

/// <summary>
/// Silero VAD 后端（ManySpeech 的 ONNX 实现）。
///
/// 一个必须说清的取舍：ManySpeech 的 <see cref="OnlineVad"/> 只吐<b>已闭合</b>的语音段，
/// 不提供"刚开口"这一实时信号。而 barge-in（打断在途回答）恰恰依赖这个信号。
/// 因此这里做两级检测：
/// <list type="number">
/// <item>分段权威：仍由 Silero VAD 决定语音段边界与切出的音频；</item>
/// <item>开口近似：前面串一道能量门限，超阈值即报 <see cref="VadFrameKind.SpeechStarted"/>。</item>
/// </list>
/// 差异与理由见 <c>porting/02-differences.md</c> D-02。
/// </summary>
public sealed class SileroVadBackend : IVadBackend
{
    /// <summary>能量门限（RMS）。低于此值不认为有人在说话。</summary>
    private const float SpeechEnergyThreshold = 0.012f;

    /// <summary>连续多少个块超阈值才确认开口，避免噪声尖峰误触发打断。</summary>
    private const int StartConfirmChunks = 2;

    private readonly OnlineVad _vad;
    private readonly OnlineStream _stream;
    private readonly int _sampleRate;
    private int _emittedSegments;
    private bool _inSpeech;
    private int _voicedChunks;

    public SileroVadBackend(VadOptions options)
    {
        var modelPath = ModelFiles.Require(options.ModelPath, "vad", "silero_vad.onnx", "*.onnx");
        var configPath = options.ConfigPath
                         ?? ModelFiles.Optional(Path.GetDirectoryName(modelPath), "vad.yaml", "*.yaml")
                         ?? string.Empty;

        _sampleRate = options.SampleRate;
        _vad = new OnlineVad(
            modelFilePath: modelPath,
            configFilePath: configPath,
            threshold: options.Threshold,
            sampleRate: _sampleRate,
            threadsNum: Math.Max(1, options.ThreadsNum),
            isDebug: options.IsDebug);
        _stream = _vad.CreateOnlineStream();
    }

    public string Name => "silero";

    public IEnumerable<VadFrame> Push(float[] samples, RuntimeConfig runtimeConfig)
    {
        var starts = DetectSpeechStart(samples);
        foreach (var start in starts)
        {
            yield return start;
        }

        _stream.AddSamples(samples);
        var results = _vad.GetResults([_stream]);

        foreach (var result in results)
        {
            var waveforms = result.Waveforms;
            while (_emittedSegments < waveforms.Count)
            {
                var audio = waveforms[_emittedSegments];
                var segment = result.Segments.Count > _emittedSegments
                    ? result.Segments[_emittedSegments]
                    : null;
                _emittedSegments++;

                if (audio is null || audio.Length == 0)
                {
                    continue;
                }

                _inSpeech = false;
                _voicedChunks = 0;

                // ManySpeech 的 SegmentEntity 以 16 kHz 的样本索引计，除以 16 得毫秒
                var endMs = segment?.End / 16 ?? 0;
                var startMs = segment?.Start / 16 ?? 0;
                yield return new VadFrame(
                    VadFrameKind.SpeechStopped,
                    audio,
                    startMs,
                    endMs,
                    audio.Length / (double)_sampleRate);
            }
        }
    }

    private IEnumerable<VadFrame> DetectSpeechStart(float[] samples)
    {
        var rms = ComputeRms(samples);
        if (rms >= SpeechEnergyThreshold)
        {
            _voicedChunks++;
        }
        else
        {
            _voicedChunks = 0;
        }

        if (!_inSpeech && _voicedChunks >= StartConfirmChunks)
        {
            _inSpeech = true;
            yield return new VadFrame(VadFrameKind.SpeechStarted);
        }
    }

    private static float ComputeRms(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    public void ResetSession()
    {
        _emittedSegments = 0;
        _inSpeech = false;
        _voicedChunks = 0;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _vad.Dispose();
    }
}
