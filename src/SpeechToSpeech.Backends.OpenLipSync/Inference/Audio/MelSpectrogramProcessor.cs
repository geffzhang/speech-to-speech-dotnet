using System.Numerics;

namespace SpeechToSpeech.Backends.OpenLipSync.Inference.Audio;

/// <summary>
/// 滑动窗 mel 频谱前端：Hann 窗 + FFT + 三角滤波器组 + dB。
/// 移植自 BasisVR/Basis com.basis.openlipsync（MIT）。
///
/// 几何参数（window/hop/nFft/nMels/fmin/fmax）来自模型 config——
/// 运行时前端必须与训练前端逐样本一致。
/// </summary>
public sealed class MelSpectrogramProcessor : IDisposable
{
    private readonly int _sampleRate;
    private readonly int _hopLength;
    private readonly int _windowLength;
    private readonly int _nFft;
    private readonly int _nMels;
    private readonly float _fMin;
    private readonly float _fMax;

    private readonly float[] _window;
    private readonly float[] _fftBuffer;

    // 稀疏 mel 滤波器组：每个 mel 是只触碰 (nFft/2+1) 个 bin 中连续 5–15 个的三角滤波器。
    //   _melBinStart[m] = 首个非零 bin
    //   _melWeights[m]  = 连续非零权重
    private readonly int[] _melBinStart;
    private readonly float[][] _melWeights;

    private readonly float[] _powerSpectrum;
    private readonly float[] _melSpectrum;
    private readonly float[] _windowBuffer;
    private readonly FFTProcessor _fft;

    private readonly float[] _previousSamples;
    private bool _disposed;

    public MelSpectrogramProcessor(AudioProcessingConfig config)
    {
        _sampleRate = config.SampleRate;
        _hopLength = config.HopLengthSamples;
        _windowLength = config.WindowLengthSamples;
        _nFft = config.NFft;
        _nMels = config.NMels;
        _fMin = config.FMin;
        _fMax = config.FMax;

        _window = CreateHannWindow(_windowLength);
        _fftBuffer = new float[_nFft];
        _windowBuffer = new float[_windowLength];
        _previousSamples = new float[_windowLength - _hopLength];
        (_melBinStart, _melWeights) = CreateSparseMelFilterBank();
        _powerSpectrum = new float[(_nFft / 2) + 1];
        _melSpectrum = new float[_nMels];
        _fft = new FFTProcessor(_nFft);
    }

    public int SampleRate => _sampleRate;
    public int HopLength => _hopLength;
    public int WindowLength => _windowLength;
    public int MelBands => _nMels;

    public float[] ProcessHop(ReadOnlySpan<float> hopSamples)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MelSpectrogramProcessor));
        }

        if (hopSamples.Length != _hopLength)
        {
            throw new ArgumentException($"Expected {_hopLength} samples, got {hopSamples.Length}", nameof(hopSamples));
        }

        _previousSamples.AsSpan().CopyTo(_windowBuffer.AsSpan(0, _previousSamples.Length));
        hopSamples.CopyTo(_windowBuffer.AsSpan(_previousSamples.Length, _hopLength));
        _windowBuffer.AsSpan(_hopLength, _previousSamples.Length).CopyTo(_previousSamples.AsSpan());

        ApplyWindow(_windowBuffer, _window, _windowLength);

        Array.Clear(_fftBuffer, 0, _fftBuffer.Length);
        _windowBuffer.AsSpan().CopyTo(_fftBuffer.AsSpan(0, _windowLength));

        _fft.ForwardPower(_fftBuffer, _powerSpectrum);

        // 稀疏 mel 乘法——只走每个滤波器的非零区间。
        float[] power = _powerSpectrum;
        int[] starts = _melBinStart;
        float[][] weights = _melWeights;
        for (int mel = 0; mel < _nMels; mel++)
        {
            int start = starts[mel];
            float[] w = weights[mel];
            float sum = 0f;
            for (int i = 0; i < w.Length; i++)
            {
                sum += power[start + i] * w[i];
            }

            _melSpectrum[mel] = sum;
        }

        for (int i = 0; i < _nMels; i++)
        {
            _melSpectrum[i] = 10f * MathF.Log10(MathF.Max(_melSpectrum[i], 1e-10f));
        }

        return _melSpectrum;
    }

    private static void ApplyWindow(float[] buffer, float[] window, int length)
    {
        int simd = Vector<float>.Count;
        int i = 0;
        if (simd > 1 && length >= simd)
        {
            int vEnd = length - simd;
            for (; i <= vEnd; i += simd)
            {
                var vb = new Vector<float>(buffer, i);
                var vw = new Vector<float>(window, i);
                (vb * vw).CopyTo(buffer, i);
            }
        }

        for (; i < length; i++)
        {
            buffer[i] *= window[i];
        }
    }

    public bool CanProcessHop(AudioRingBuffer ringBuffer) => ringBuffer.AvailableSamples >= _hopLength;

    public bool TryProcessNextHop(AudioRingBuffer ringBuffer, out float[] melFeatures)
    {
        if (!CanProcessHop(ringBuffer))
        {
            melFeatures = Array.Empty<float>();
            return false;
        }

        Span<float> hopBuffer = stackalloc float[_hopLength];
        int read = ringBuffer.Read(hopBuffer, _hopLength);

        if (read == _hopLength)
        {
            melFeatures = ProcessHop(hopBuffer);
            return true;
        }

        melFeatures = Array.Empty<float>();
        return false;
    }

    private static float[] CreateHannWindow(int length)
    {
        var window = new float[length];
        for (int i = 0; i < length; i++)
        {
            window[i] = 0.5f * (1f - MathF.Cos((2f * MathF.PI * i) / (length - 1)));
        }

        return window;
    }

    private (int[] starts, float[][] weights) CreateSparseMelFilterBank()
    {
        int numBins = (_nFft / 2) + 1;
        var starts = new int[_nMels];
        var weights = new float[_nMels][];

        float melMin = HzToMel(_fMin);
        float melMax = HzToMel(_fMax);

        var melPoints = new float[_nMels + 2];
        for (int i = 0; i < melPoints.Length; i++)
        {
            melPoints[i] = melMin + ((melMax - melMin) * i / (_nMels + 1));
        }

        var binPoints = new float[melPoints.Length];
        for (int i = 0; i < binPoints.Length; i++)
        {
            binPoints[i] = (_nFft + 1) * MelToHz(melPoints[i]) / _sampleRate;
        }

        for (int mel = 0; mel < _nMels; mel++)
        {
            float left = binPoints[mel];
            float center = binPoints[mel + 1];
            float right = binPoints[mel + 2];

            // 首个非零 bin：严格大于 left 且 ≤ right 的最小整数。
            // 上升沿 bin ∈ [left, center]，下降沿 bin ∈ (center, right]；
            // bin == left 贡献 0，但保留在区间内以保持逐位等价。
            int binLo = (int)Math.Ceiling(left);
            int binHi = (int)Math.Floor(right);

            if (binLo < 0)
            {
                binLo = 0;
            }

            if (binHi > numBins - 1)
            {
                binHi = numBins - 1;
            }

            if (binHi < binLo)
            {
                starts[mel] = 0;
                weights[mel] = Array.Empty<float>();
                continue;
            }

            int len = binHi - binLo + 1;
            var w = new float[len];
            for (int k = 0; k < len; k++)
            {
                int bin = binLo + k;
                if (bin >= left && bin <= center)
                {
                    w[k] = (bin - left) / (center - left);
                }
                else if (bin > center && bin <= right)
                {
                    w[k] = (right - bin) / (right - center);
                }
                else
                {
                    w[k] = 0f;
                }
            }

            starts[mel] = binLo;
            weights[mel] = w;
        }

        return (starts, weights);
    }

    private static float HzToMel(float hz) => 2595f * MathF.Log10(1f + (hz / 700f));

    private static float MelToHz(float mel) => 700f * (MathF.Pow(10f, mel / 2595f) - 1f);

    public void Dispose() => _disposed = true;
}
