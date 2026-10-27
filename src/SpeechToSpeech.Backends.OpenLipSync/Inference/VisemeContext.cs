using SpeechToSpeech.Backends.OpenLipSync.Inference.Audio;

namespace SpeechToSpeech.Backends.OpenLipSync.Inference;

/// <summary>
/// 单个音频流（一次响应）的 音频→口型 状态。
/// 移植自 BasisVR/Basis com.basis.openlipsync 的 AudioContext（MIT），去掉 Unity 平台分支，
/// 只保留 ONNX 流式与 legacy 窗口两条桌面路径。
///
/// 流式模型每次推进一个 10 ms mel 帧（卷积缓存携带全部时序上下文）；
/// legacy 模型累积 150 帧窗口每次全量重跑。两者由模型图自动判别。
/// </summary>
public sealed class VisemeContext : IDisposable
{
    private const int LegacyMaxMelFrames = 150;

    private readonly AudioRingBuffer _ringBuffer;
    private readonly MelSpectrogramProcessor _melProcessor;
    private readonly AudioResampler? _resampler;

    private readonly VisemeSmoother _smoother;
    private readonly float[] _probabilityBuffer;
    private readonly int _melBands;

    private readonly StreamingSession? _streaming; // null => legacy
    private readonly bool _multiLabel;
    private readonly float[] _lastMel;
    private int _identicalRun;
    private bool _settled;

    // legacy 窗口路径
    private readonly float[]? _melSequence;
    private readonly float[]? _inferenceInputBuffer;
    private int _melFrameCount;

    private int _frameNumber;
    private bool _disposed;

    /// <summary>每算完一个 mel 帧触发一次（平滑后的输出快照）。测试缝：流式不变性对拍用。</summary>
    internal Action<float[]>? OnFrameComputed;

    public int MelBands => _melBands;
    public bool IsStreaming => _streaming is not null;
    public int AccumulatedMelFrames => _melFrameCount;
    internal VisemeSmoother Smoother => _smoother;

    public VisemeContext(int inputSampleRate, AudioProcessingConfig audioConfig, int modelVisemeCount,
                         StreamingSession? streaming, bool multiLabel, OpenLipSyncBackend? owner = null)
    {
        _ = owner; // 保留参数位置以贴近上游签名；所有权管理在 Backend
        _ringBuffer = new AudioRingBuffer(audioConfig.SampleRate * 3);
        _melProcessor = new MelSpectrogramProcessor(audioConfig);
        _resampler = inputSampleRate != audioConfig.SampleRate
            ? new AudioResampler(inputSampleRate, audioConfig.SampleRate)
            : null;

        int visemeCount = modelVisemeCount > 0 ? modelVisemeCount : VisemeFrame.VisemeCount;
        // 平滑按 mel 帧推进（固定 100 Hz），不随音频回调大小漂移。
        _smoother = new VisemeSmoother(visemeCount, 0.3f);
        _probabilityBuffer = new float[visemeCount];

        _melBands = audioConfig.NMels;
        _streaming = streaming;
        _multiLabel = multiLabel;
        _lastMel = new float[_melBands];

        if (_streaming is null)
        {
            _melSequence = new float[LegacyMaxMelFrames * _melBands];
            _inferenceInputBuffer = new float[LegacyMaxMelFrames * _melBands];
        }
    }

    public void ProcessAudio(ReadOnlySpan<float> audioSamples)
    {
        if (_disposed)
        {
            return;
        }

        if (_resampler is null)
        {
            _ringBuffer.Write(audioSamples);
        }
        else
        {
            _ringBuffer.Write(_resampler.Resample(audioSamples));
        }
    }

    public bool TryGetNextMelFrame(out float[] melFeatures) =>
        _melProcessor.TryProcessNextHop(_ringBuffer, out melFeatures);

    /// <summary>流式：一个 mel 帧进，一组口型概率出。</summary>
    public bool StepStreaming(float[] melFrame)
    {
        if (_disposed)
        {
            return false;
        }

        if (_streaming is null)
        {
            return false;
        }

        bool repeat = Repeats(melFrame);
        if (repeat && _settled)
        {
            _smoother.Step(_probabilityBuffer);
            NotifyFrame();
            return false;
        }

        _streaming.Step(melFrame, _probabilityBuffer, _multiLabel);
        if (repeat)
        {
            _settled = _streaming.StateUnchanged();
        }

        _smoother.Step(_probabilityBuffer);
        NotifyFrame();
        return true;
    }

    private void NotifyFrame() => OnFrameComputed?.Invoke((float[])_smoother.Output.Clone());

    private bool Repeats(float[] melFrame)
    {
        ReadOnlySpan<float> current = melFrame.AsSpan(0, _melBands);
        if (_identicalRun > 0 &&
            System.Runtime.InteropServices.MemoryMarshal.Cast<float, int>(current)
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.Cast<float, int>(_lastMel.AsSpan(0, _melBands))))
        {
            if (_identicalRun < int.MaxValue)
            {
                _identicalRun++;
            }

            return true;
        }

        current.CopyTo(_lastMel);
        _identicalRun = 1;
        _settled = false;
        return false;
    }

    public void ApplyProbabilities(float[] visemeProbs)
    {
        if (_disposed)
        {
            return;
        }

        _smoother.Step(visemeProbs);
    }

    public float[] GetInferenceBuffer() => _probabilityBuffer;

    // ── legacy 窗口路径 ──
    public void AccumulateMelFrame(float[] melFrame)
    {
        if (_disposed || _melSequence is null || melFrame.Length < _melBands)
        {
            return;
        }

        if (_melFrameCount >= LegacyMaxMelFrames)
        {
            _melSequence.AsSpan(_melBands, (LegacyMaxMelFrames - 1) * _melBands)
                .CopyTo(_melSequence.AsSpan(0));
            _melFrameCount = LegacyMaxMelFrames - 1;
        }

        melFrame.AsSpan(0, _melBands).CopyTo(_melSequence.AsSpan(_melFrameCount * _melBands));
        _melFrameCount++;
    }

    public float[] GetMelSequence() => _melSequence ?? Array.Empty<float>();

    public float[] GetInferenceInputBuffer() => _inferenceInputBuffer ?? Array.Empty<float>();

    public void UpdateFrame(ref VisemeFrame frame)
    {
        if (_disposed)
        {
            return;
        }

        frame.FrameNumber = ++_frameNumber;
        frame.FrameDelay = 0;

        float[] latest = _smoother.Output;
        int copy = Math.Min(latest.Length, frame.Visemes.Length);
        Array.Copy(latest, frame.Visemes, copy);
        if (copy < frame.Visemes.Length)
        {
            Array.Clear(frame.Visemes, copy, frame.Visemes.Length - copy);
        }

        frame.LaughterScore = 0f;
    }

    public void Reset()
    {
        if (_disposed)
        {
            return;
        }

        _ringBuffer.Clear();
        _smoother.Reset();
        _frameNumber = 0;
        _identicalRun = 0;
        _settled = false;

        _streaming?.Reset();

        if (_melSequence is not null)
        {
            _melFrameCount = 0;
            Array.Clear(_melSequence, 0, _melSequence.Length);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ringBuffer.Dispose();
        _melProcessor.Dispose();
        _streaming?.Dispose();
    }
}
