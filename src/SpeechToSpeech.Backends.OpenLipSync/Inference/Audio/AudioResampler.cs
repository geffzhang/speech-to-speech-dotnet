using System.Collections.Concurrent;

namespace SpeechToSpeech.Backends.OpenLipSync.Inference.Audio;

/// <summary>
/// Blackman 窗 sinc 多相重采样器（48 tap / 1024 相位），带跨调用保持的延迟线状态。
/// 移植自 BasisVR/Basis com.basis.openlipsync（MIT）。
///
/// 与一次性整段重采样不同：输入按块喂入，输出连续——TTS 分片流进 mel 前端时
/// 相位不重启、不产生接缝。
/// </summary>
public sealed class AudioResampler : IDisposable
{
    private readonly int _inputSampleRate;
    private readonly int _outputSampleRate;
    private readonly double _inputPerOutput;

    private readonly int _filterTaps;
    private readonly int _halfTaps;
    private readonly int _numPhases;
    private readonly float[] _coeffTable;

    // 手动管理的延迟线：有效样本占 _buf[_bufHead .. _bufHead + _bufCount - 1]。
    // 丢头部 O(1)（推进 head），head 走过半数组时压缩。
    private float[] _buf = new float[256];
    private int _bufHead;
    private int _bufCount;

    private double _time;
    private bool _primed;
    private bool _disposed;

    // 输出缓冲只增不减；返回的 span 携带精确样本数，底层允许更长。
    private float[] _outputArray = Array.Empty<float>();

    // 系数表缓存：同一 (inputRate, outputRate, taps, phases) 只建一次
    // （49152 个 float ≈ 196 KB）。
    private static readonly ConcurrentDictionary<long, float[]> CoeffCache = new();

    private static long MakeCacheKey(int inputSampleRate, int outputSampleRate, int taps, int phases) =>
        ((long)inputSampleRate << 48) | (((long)outputSampleRate & 0xFFFF) << 32) |
        (((long)taps & 0xFFFF) << 16) | ((long)phases & 0xFFFF);

    public AudioResampler(int inputSampleRate, int outputSampleRate, int filterTaps = 48, int numPhases = 1024, double cutoffScale = 0.9)
    {
        if (inputSampleRate <= 0)
        {
            throw new ArgumentException("Input sample rate must be positive", nameof(inputSampleRate));
        }

        if (outputSampleRate <= 0)
        {
            throw new ArgumentException("Output sample rate must be positive", nameof(outputSampleRate));
        }

        if (filterTaps < 8 || filterTaps % 2 != 0)
        {
            throw new ArgumentException("Filter taps must be even and >= 8", nameof(filterTaps));
        }

        if (numPhases < 8)
        {
            throw new ArgumentException("Number of phases must be >= 8", nameof(numPhases));
        }

        if (cutoffScale <= 0 || cutoffScale >= 1)
        {
            throw new ArgumentException("cutoffScale must be in (0,1)", nameof(cutoffScale));
        }

        _inputSampleRate = inputSampleRate;
        _outputSampleRate = outputSampleRate;
        _inputPerOutput = (double)inputSampleRate / outputSampleRate;
        _filterTaps = filterTaps;
        _halfTaps = filterTaps / 2;
        _numPhases = numPhases;

        long key = MakeCacheKey(inputSampleRate, outputSampleRate, filterTaps, numPhases);
        if (!CoeffCache.TryGetValue(key, out var cached))
        {
            var table = new float[_numPhases * _filterTaps];
            double fc = 0.5 * Math.Min(1.0, (double)_outputSampleRate / _inputSampleRate) * cutoffScale;
            BuildCoefficientTable(table, fc);
            CoeffCache[key] = table;
            cached = table;
        }

        _coeffTable = cached;
    }

    public int InputSampleRate => _inputSampleRate;
    public int OutputSampleRate => _outputSampleRate;

    /// <summary>喂入一块输入，返回连续的输出样本（可能为空——样本还不足以出一个输出点）。</summary>
    public ReadOnlySpan<float> Resample(ReadOnlySpan<float> input)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioResampler));
        }

        int taps = _filterTaps;
        int halfMinus1 = _halfTaps - 1;

        if (input.Length > 0)
        {
            if (!_primed)
            {
                EnsureBufCapacity(_halfTaps);
                Array.Clear(_buf, 0, _halfTaps);
                _bufHead = 0;
                _bufCount = _halfTaps;
                _time = halfMinus1;
                _primed = true;
            }

            int writePos = _bufHead + _bufCount;
            EnsureBufCapacity(writePos + input.Length);
            input.CopyTo(_buf.AsSpan(writePos));
            _bufCount += input.Length;
        }

        if (_bufCount < taps)
        {
            return ReadOnlySpan<float>.Empty;
        }

        // 预数输出量：满足 floor(_time + n·step) ≤ maxCenter，即 _time + n·step < maxCenter + 1。
        int maxCenter = _bufCount - _halfTaps - 1;
        int outCount = 0;
        double tEnd = _time;
        double maxBound = maxCenter + 1;
        while (tEnd < maxBound)
        {
            outCount++;
            tEnd += _inputPerOutput;
        }

        if (outCount == 0)
        {
            return ReadOnlySpan<float>.Empty;
        }

        if (_outputArray.Length < outCount)
        {
            _outputArray = new float[outCount];
        }

        float[] coeffs = _coeffTable;
        float[] buf = _buf;
        int bufHead = _bufHead;
        int phases = _numPhases;

        for (int o = 0; o < outCount; o++)
        {
            int center = (int)Math.Floor(_time);
            int leftIndex = center - halfMinus1;

            double frac = _time - center;
            int phaseIndex = (int)Math.Round(frac * phases);
            if (phaseIndex == phases)
            {
                phaseIndex = 0;
            }

            int coeffBase = phaseIndex * taps;
            int bufBase = bufHead + leftIndex;

            double sum = 0.0;
            for (int t = 0; t < taps; t++)
            {
                sum += buf[bufBase + t] * coeffs[coeffBase + t];
            }

            _outputArray[o] = (float)sum;
            _time += _inputPerOutput;
        }

        int safeToRemove = (int)Math.Floor(_time) - halfMinus1;
        if (safeToRemove > 0)
        {
            if (safeToRemove >= _bufCount)
            {
                _bufHead = 0;
                _bufCount = 0;
            }
            else
            {
                _bufHead += safeToRemove;
                _bufCount -= safeToRemove;
            }

            _time -= safeToRemove;
            if (_time < 0)
            {
                _time = 0;
            }

            if (_bufHead > (_buf.Length >> 1) && _bufCount > 0)
            {
                Array.Copy(_buf, _bufHead, _buf, 0, _bufCount);
                _bufHead = 0;
            }
        }

        return new ReadOnlySpan<float>(_outputArray, 0, outCount);
    }

    /// <summary>重置延迟线与相位（不重建系数表）。用于跨不连续流复用实例。</summary>
    public void Reset()
    {
        _bufHead = 0;
        _bufCount = 0;
        _time = 0.0;
        _primed = false;
    }

    private void EnsureBufCapacity(int needed)
    {
        if (_buf.Length >= needed)
        {
            return;
        }

        int newCap = _buf.Length;
        while (newCap < needed)
        {
            newCap <<= 1;
        }

        Array.Resize(ref _buf, newCap);
    }

    private void BuildCoefficientTable(float[] table, double fc)
    {
        for (int p = 0; p < _numPhases; p++)
        {
            double frac = (double)p / _numPhases;
            double sum = 0.0;

            for (int n = 0; n < _filterTaps; n++)
            {
                double t = n - (_halfTaps - 1) - frac;
                double sincArg = 2.0 * fc * t;
                double sinc = sincArg == 0.0 ? 1.0 : Math.Sin(Math.PI * sincArg) / (Math.PI * sincArg);

                // Blackman 窗
                double w = 0.42
                           - 0.5 * Math.Cos((2.0 * Math.PI * n) / (_filterTaps - 1))
                           + 0.08 * Math.Cos((4.0 * Math.PI * n) / (_filterTaps - 1));

                double h = 2.0 * fc * sinc * w;
                table[p * _filterTaps + n] = (float)h;
                sum += h;
            }

            if (sum != 0)
            {
                float norm = (float)(1.0 / sum);
                for (int n = 0; n < _filterTaps; n++)
                {
                    table[p * _filterTaps + n] *= norm;
                }
            }
        }
    }

    public void Dispose() => _disposed = true;
}
