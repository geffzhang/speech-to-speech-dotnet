using Microsoft.ML.OnnxRuntime;

namespace SpeechToSpeech.Backends.OpenLipSync.Inference;

/// <summary>
/// 缓存式 TCN 口型模型的逐会话流式推理状态。
/// 移植自 BasisVR/Basis com.basis.openlipsync（MIT）。
///
/// 模型的每个膨胀卷积把历史装在小型环缓存（cache_i 输入 → cache_out_i 输出）里，
/// 一个 10 ms hop 恰好一步计算。两组 IoBinding 乒乓交替：binding A 读 caches[0] 写
/// caches[1]，binding B 反之——没有任何拷贝，每帧零分配。
/// </summary>
public sealed class StreamingSession : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _nCaches;
    private readonly int _nMels;
    private readonly int _nVisemes;

    private readonly OrtMemoryInfo _memInfo;
    private readonly float[] _melBuffer;
    private readonly float[] _logitBuffer;
    private readonly OrtValue _melValue;
    private readonly OrtValue _logitValue;

    // caches[0] 与 caches[1] 每帧交替充当（输入, 输出）。
    private readonly float[][][] _cacheBuffers = new float[2][][];
    private readonly OrtValue[][] _cacheValues = new OrtValue[2][];
    private readonly OrtIoBinding[] _bindings = new OrtIoBinding[2];
    private readonly RunOptions _runOptions = new();
    private int _phase;
    private bool _disposed;

    public int MelBands => _nMels;
    public int VisemeCount => _nVisemes;

    /// <summary>ONNX 图暴露 cache_* 输入即为流式模型。</summary>
    public static bool IsStreamingModel(InferenceSession session) =>
        session.InputMetadata.Keys.Any(k => k.StartsWith("cache_", StringComparison.Ordinal));

    public StreamingSession(InferenceSession session, int nMels, int nVisemes)
    {
        _session = session;
        _nMels = nMels;
        _nVisemes = nVisemes;
        _nCaches = session.InputMetadata.Keys.Count(k => k.StartsWith("cache_", StringComparison.Ordinal));

        _memInfo = OrtMemoryInfo.DefaultInstance;

        _melBuffer = new float[nMels];
        _logitBuffer = new float[nVisemes];
        _melValue = OrtValue.CreateTensorValueFromMemory(_memInfo, _melBuffer.AsMemory(), [1, 1, nMels]);
        _logitValue = OrtValue.CreateTensorValueFromMemory(_memInfo, _logitBuffer.AsMemory(), [1, 1, nVisemes]);

        // cache 形状直接来自图本身——C# 与模型不可能不一致。
        var shapes = new long[_nCaches][];
        for (int i = 0; i < _nCaches; i++)
        {
            var meta = session.InputMetadata[$"cache_{i}"];
            shapes[i] = meta.Dimensions.Select(d => (long)d).ToArray();
            if (shapes[i].Any(d => d <= 0))
            {
                throw new InvalidOperationException($"cache_{i} 有动态维度；流式缓存必须是定长");
            }
        }

        for (int p = 0; p < 2; p++)
        {
            _cacheBuffers[p] = new float[_nCaches][];
            _cacheValues[p] = new OrtValue[_nCaches];
            for (int i = 0; i < _nCaches; i++)
            {
                int len = 1;
                foreach (var d in shapes[i])
                {
                    len *= (int)d;
                }

                _cacheBuffers[p][i] = new float[len];
                _cacheValues[p][i] = OrtValue.CreateTensorValueFromMemory(_memInfo, _cacheBuffers[p][i].AsMemory(), shapes[i]);
            }
        }

        for (int p = 0; p < 2; p++)
        {
            var b = _session.CreateIoBinding();
            b.BindInput("audio_features", _melValue);
            b.BindOutput("viseme_logits", _logitValue);
            for (int i = 0; i < _nCaches; i++)
            {
                b.BindInput($"cache_{i}", _cacheValues[p][i]); // 从 phase p 读
                b.BindOutput($"cache_out_{i}", _cacheValues[1 - p][i]); // 写到 phase 1-p
            }

            _bindings[p] = b;
        }
    }

    /// <summary>
    /// 推进一个 10 ms hop。输入是原始 dB mel（CMVN 在图内部做，勿在此归一化）。
    /// </summary>
    public void Step(ReadOnlySpan<float> melFrame, Span<float> probabilities, bool multiLabel)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(StreamingSession));
        }

        melFrame.Slice(0, _nMels).CopyTo(_melBuffer);
        _session.RunWithBinding(_runOptions, _bindings[_phase]);
        _phase ^= 1; // 上一帧的输出就是下一帧的输入——零拷贝

        if (multiLabel)
        {
            for (int i = 0; i < _nVisemes; i++)
            {
                float x = Math.Clamp(_logitBuffer[i], -50f, 50f);
                probabilities[i] = 1f / (1f + MathF.Exp(-x));
            }
        }
        else
        {
            float max = float.MinValue;
            for (int i = 0; i < _nVisemes; i++)
            {
                if (_logitBuffer[i] > max)
                {
                    max = _logitBuffer[i];
                }
            }

            float sum = 0f;
            for (int i = 0; i < _nVisemes; i++)
            {
                float e = MathF.Exp(_logitBuffer[i] - max);
                probabilities[i] = e;
                sum += e;
            }

            float inv = sum > 0f ? 1f / sum : 0f;
            for (int i = 0; i < _nVisemes; i++)
            {
                probabilities[i] *= inv;
            }
        }
    }

    /// <summary>两组缓存逐位一致说明图状态已收敛（输入重复时可跳过推理）。</summary>
    public bool StateUnchanged()
    {
        for (int i = 0; i < _nCaches; i++)
        {
            if (!MemoryMarshalCastEqual(_cacheBuffers[0][i], _cacheBuffers[1][i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MemoryMarshalCastEqual(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var ia = System.Runtime.InteropServices.MemoryMarshal.Cast<float, int>(a);
        var ib = System.Runtime.InteropServices.MemoryMarshal.Cast<float, int>(b);
        return ia.SequenceEqual(ib);
    }

    /// <summary>清空全部卷积历史。音频流重启时调用。</summary>
    public void Reset()
    {
        for (int p = 0; p < 2; p++)
        {
            for (int i = 0; i < _nCaches; i++)
            {
                Array.Clear(_cacheBuffers[p][i], 0, _cacheBuffers[p][i].Length);
            }
        }

        _phase = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var b in _bindings)
        {
            b?.Dispose();
        }

        for (int p = 0; p < 2; p++)
        {
            if (_cacheValues[p] != null)
            {
                foreach (var v in _cacheValues[p])
                {
                    v?.Dispose();
                }
            }
        }

        _melValue?.Dispose();
        _logitValue?.Dispose();
        _runOptions?.Dispose();
    }
}
