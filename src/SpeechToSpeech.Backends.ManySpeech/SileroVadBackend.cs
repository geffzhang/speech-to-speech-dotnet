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
/// 差异与理由见 <c>porting/02-differences.md</c> D002。
/// </summary>
public sealed class SileroVadBackend : IVadBackend
{
    /// <summary>能量门限（RMS）。低于此值不认为有人在说话。</summary>
    private const float SpeechEnergyThreshold = 0.012f;

    /// <summary>连续多少个块超阈值才确认开口，避免噪声尖峰误触发打断。</summary>
    private const int StartConfirmChunks = 2;

    private readonly OnlineVad _vad;
    private readonly int _sampleRate;

    /// <summary>
    /// 当前在线流。**不是 readonly**：一条 <see cref="OnlineStream"/> 只能切出一段话，
    /// 原因见 <see cref="RenewStream"/>。
    /// </summary>
    private OnlineStream _stream;

    /// <summary>当前流起点在连接时间轴上的样本位（新流从 0 计数，段坐标是流内相对的）。</summary>
    private long _streamBaseSamples;

    private bool _inSpeech;
    private int _voicedChunks;

    /// <summary>本会话累计推入的样本数。与 VAD handler 的累计计数同源，用于反推时间轴。</summary>
    private long _totalSamples;

    /// <summary>当前这段有声run 的起点（连接时间轴上的样本位）；静音时清空。</summary>
    private long? _voicedRunStartSample;

    /// <summary>
    /// 最后一个有声窗的结束样本位。上游取 <c>temp_end - window_size_samples</c>，
    /// 也就是静音确认窗口**之前**那一个窗——段尾带着用来闭合的静音，那不是「说完的时刻」。
    /// 这里用同一套口径，只是判「有声」的依据从 Silero 概率换成能量门限（见类注释的取舍）。
    /// </summary>
    private int? _lastVoicedEndSample;

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

        // 有声就前移「最后说话时刻」。粒度是一整块（与上游一个窗同量级），
        // 所以记的是这一块的末尾而不是块内精确位置。
        if (_voicedChunks > 0)
        {
            _lastVoicedEndSample = (int)Math.Min(int.MaxValue, _totalSamples + samples.Length);
        }

        _totalSamples += samples.Length;

        foreach (var start in starts)
        {
            yield return start;
        }

        _stream.AddSamples(samples);

        // 两个调用缺一不可，且顺序固定：
        //
        // 1) `IsFinished(isEndpoint: true)` 是端点判定的**驱动**——它内部推进静音计数与
        //    最长语音段的上界，据此决定这一段结束，并把切好的音频备好。不调它，段永远不闭合
        //    （实测：整段 6 秒语音 + 3 秒静音，段数为 0）。
        // 2) `GetResult(_stream)` 才是取结果。批量接口 `GetResults([stream])` 是离线批处理口径，
        //    对还活着的流不推进前向，用它同样一个段都拿不到。
        //
        // 这两条都不是猜的：单测里的假后端永远「按预期吐段」，只有真跑一轮才会暴露。
        _ = _stream.IsFinished(isEndpoint: true);
        var result = _vad.GetResult(_stream);
        var waveforms = result.Waveforms;

        // 段坐标是**当前流内部**的样本位。流每收一段就换新，所以必须加上这一流的起点
        // 才能还原成连接时间轴上的位置——否则第二段报出来的 start/end 会从 0 重新数。
        var frames = new List<VadFrame>(waveforms.Count);
        for (var i = 0; i < waveforms.Count; i++)
        {
            var audio = waveforms[i];
            if (audio is null || audio.Length == 0)
            {
                continue;
            }

            var segment = result.Segments.Count > i ? result.Segments[i] : null;
            var startMs = segment is null
                ? (int)(_streamBaseSamples / 16)
                : (int)((_streamBaseSamples + segment.Start) / 16);
            var endMs = segment is null
                ? (int)(_totalSamples / 16)
                : (int)((_streamBaseSamples + segment.End) / 16);

            frames.Add(new VadFrame(
                VadFrameKind.SpeechStopped,
                audio,
                startMs,
                endMs,
                audio.Length / (double)_sampleRate,
                _lastVoicedEndSample));
        }

        if (frames.Count == 0)
        {
            yield break;
        }

        _inSpeech = false;
        _voicedChunks = 0;
        _voicedRunStartSample = null;

        // 换流必须在把帧交出去之前：下一条 `AddSamples` 会落到新流上。
        RenewStream();

        foreach (var frame in frames)
        {
            yield return frame;
        }
    }

    /// <summary>
    /// 收一段就换一条新流。
    ///
    /// 不是保守做法，是必须：<c>OnlineStream.IsFinished(isEndpoint: true)</c> 在「缓冲里全是
    /// 同一个值」（也就是只剩纯静音）时走的是 <c>PushIndexEnd()</c>——那是**流结束**的收尾路径。
    /// 走过一次之后这条流就再也切不出新段：实测 18 秒双段音频，第一条流只出 1 段，之后
    /// 18 秒里 `GetResult` 恒返回 0 段。真跑表现为「用户说第二句，服务端连 speech_stopped
    /// 都不发」，用户看到的是助手彻底不再应答。
    ///
    /// 代价是 VAD 的循环缓冲状态（<c>_states</c>）每段重建一次：音频本来就按段切开，
    /// 状态跨段延续对判定没有价值。换流的边界是静音，不会切在说话中间。
    /// </summary>
    private void RenewStream()
    {
        var previous = _stream;
        _stream = _vad.CreateOnlineStream();
        _streamBaseSamples = _totalSamples;
        previous.Dispose();
    }

    private IEnumerable<VadFrame> DetectSpeechStart(float[] samples)
    {
        var rms = ComputeRms(samples);
        if (rms >= SpeechEnergyThreshold)
        {
            // 记的是这段有声 run 的**第一块**的起点，不是确认时的位置：
            // 确认要两块，报第二块的位置会让开口时刻晚 32 ms。
            _voicedRunStartSample ??= _totalSamples;
            _voicedChunks++;
        }
        else
        {
            _voicedChunks = 0;
            _voicedRunStartSample = null;
        }

        if (!_inSpeech && _voicedChunks >= StartConfirmChunks)
        {
            _inSpeech = true;

            // 位置必须给：它同时是协议里的 audio_start_ms 和状态机 CanReopen 的判据。
            // 恒报 0 时「用户说了 20 秒后停顿再说」与「刚开口」在时间轴上无法区分，
            // 复开窗口按 max(0, audioMs - lastFinalAudioMs) 算会永远判成 0。
            var startMs = (int)((_voicedRunStartSample ?? _totalSamples) / 16);
            yield return new VadFrame(VadFrameKind.SpeechStarted)
            {
                AudioStartMs = startMs,
            };
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
        _inSpeech = false;
        _voicedChunks = 0;
        _voicedRunStartSample = null;
        _totalSamples = 0;
        _lastVoicedEndSample = null;

        // 会话重开也要换流：上一会话留在这条流里的缓冲与段计数会污染新一轮的第一段。
        RenewStream();
    }

    public void Dispose()
    {
        _stream.Dispose();
        _vad.Dispose();
    }
}
