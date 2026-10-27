using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using SpeechToSpeech.Backends.OpenLipSync.Inference.Audio;

namespace SpeechToSpeech.Backends.OpenLipSync.Inference;

/// <summary>
/// OpenLipSync 推理后端：加载 ONNX 口型模型，为每条音频流维护独立上下文。
/// 移植自 BasisVR/Basis com.basis.openlipsync 的 OpenLipSyncBackend（MIT），
/// 去掉 Unity/Android/Web 平台分支。
///
/// InferenceSession 共享且线程安全；卷积缓存与概率缓冲按上下文独立。
/// </summary>
public sealed class OpenLipSyncBackend : IDisposable
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<long, VisemeContext> _contexts = new();
    private InferenceSession? _onnxSession;
    private ModelConfig? _modelConfig;
    private AudioProcessingConfig? _audioConfig;
    private long _nextContextId = 1;
    private int _inputSampleRate;
    private bool _initialized;
    private bool _disposed;

    private bool _isMultiLabel;
    private bool _isStreaming;
    private bool _legacyNormalizeWindow;
    private int _numVisemes = VisemeFrame.VisemeCount;

    public bool IsStreaming => _isStreaming;
    public bool IsInitialized => _initialized;
    public int SampleRate => _inputSampleRate;
    public string LastError { get; private set; } = "";

    // 调试计数
    public int DebugMelFramesProduced { get; private set; }
    public int DebugInferenceRuns { get; private set; }

    /// <summary>任一上下文每算完一个 mel 帧触发（平滑后输出快照）。测试缝与逐帧消费用。</summary>
    public event Action<float[]>? VisemeFrameComputed;

    /// <summary>从模型字节与 config JSON 初始化。</summary>
    public bool InitializeFromBytes(int sampleRate, byte[] modelBytes, string configJson)
    {
        if (_disposed)
        {
            return false;
        }

        if (_initialized)
        {
            return true;
        }

        _inputSampleRate = sampleRate;

        if (modelBytes.Length == 0)
        {
            LastError = "模型字节为空";
            return false;
        }

        if (string.IsNullOrEmpty(configJson))
        {
            LastError = "缺少 config JSON";
            return false;
        }

        try
        {
            _modelConfig = System.Text.Json.JsonSerializer.Deserialize<ModelConfig>(
                configJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = false });
        }
        catch (Exception ex)
        {
            LastError = $"config JSON 解析失败: {ex.Message}";
            return false;
        }

        if (_modelConfig?.Audio is null)
        {
            LastError = "config 缺少 audio 段";
            return false;
        }

        _audioConfig = AudioProcessingConfig.FromModelConfig(_modelConfig);
        // 新模型把多标签声明在 "model" 下；老模型在 "training" 下。
        _isMultiLabel = _modelConfig.Model?.MultiLabel ?? _modelConfig.Training?.MultiLabel ?? false;
        _numVisemes = _modelConfig.Model?.NumVisemes ?? VisemeFrame.VisemeCount;

        // C# 前端出的是原始 dB mel。声明了归一化的模型说明输入契约对不上——
        // 流式场景拿不到整段统计，退而按 mel 窗口自身均值/方差归一化（因果），尽量兑现契约。
        string norm = _modelConfig.Audio.Normalization;
        _legacyNormalizeWindow = !string.IsNullOrEmpty(norm)
                                 && !norm.StartsWith("none", StringComparison.OrdinalIgnoreCase)
                                 && !norm.Equals("baked_into_graph", StringComparison.OrdinalIgnoreCase);

        try
        {
            var sessionOptions = new SessionOptions();
            sessionOptions.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
            sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            sessionOptions.InterOpNumThreads = 1;
            sessionOptions.IntraOpNumThreads = 1;

            _onnxSession = new InferenceSession(modelBytes, sessionOptions);
            _isStreaming = StreamingSession.IsStreamingModel(_onnxSession);
        }
        catch (Exception ex)
        {
            LastError = $"初始化失败: {ex.Message}";
            _onnxSession?.Dispose();
            _onnxSession = null;
            return false;
        }

        _initialized = true;
        return true;
    }

    public void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var context in _contexts.Values)
            {
                context.Dispose();
            }

            _contexts.Clear();
            _onnxSession?.Dispose();
            _onnxSession = null;
            _initialized = false;
        }
    }

    public long CreateContext()
    {
        if (!_initialized || _audioConfig is null)
        {
            throw new InvalidOperationException(LastError is { Length: > 0 } ? LastError : "后端未初始化");
        }

        StreamingSession? streaming = _isStreaming
            ? new StreamingSession(_onnxSession!, _audioConfig.NMels, _numVisemes)
            : null;

        var context = new VisemeContext(_inputSampleRate, _audioConfig, _numVisemes, streaming, _isMultiLabel, this);
        context.OnFrameComputed = frame => VisemeFrameComputed?.Invoke(frame);
        long id = _nextContextId++;
        _contexts[id] = context;
        return id;
    }

    public bool DestroyContext(long context)
    {
        if (!_initialized)
        {
            return false;
        }

        if (_contexts.TryRemove(context, out var audioContext))
        {
            audioContext.Dispose();
            return true;
        }

        return false;
    }

    public bool ResetContext(long context)
    {
        if (!_initialized)
        {
            return false;
        }

        if (_contexts.TryGetValue(context, out var audioContext))
        {
            audioContext.Reset();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 喂入一帧音频（float 单声道，输入采样率），推进推理，并把最新概率写进 <paramref name="frame"/>。
    /// </summary>
    public bool ProcessFrame(long context, ReadOnlySpan<float> audio, ref VisemeFrame frame)
    {
        if (!_initialized || _onnxSession is null)
        {
            LastError = "后端未初始化或模型未加载";
            return false;
        }

        if (!_contexts.TryGetValue(context, out var audioContext))
        {
            LastError = $"无效上下文: {context}";
            return false;
        }

        try
        {
            audioContext.ProcessAudio(audio);

            int melCount = 0;
            if (audioContext.IsStreaming)
            {
                // 每 mel 帧一步工作；卷积缓存携带全部时序上下文。
                while (audioContext.TryGetNextMelFrame(out var melFeatures))
                {
                    if (audioContext.StepStreaming(melFeatures))
                    {
                        DebugInferenceRuns++;
                    }

                    melCount++;
                }
            }
            else
            {
                while (audioContext.TryGetNextMelFrame(out var melFeatures))
                {
                    audioContext.AccumulateMelFrame(melFeatures);
                    melCount++;
                }

                if (audioContext.AccumulatedMelFrames >= 5)
                {
                    RunSequenceInference(audioContext);
                    audioContext.ApplyProbabilities(audioContext.GetInferenceBuffer());
                    DebugInferenceRuns++;
                }
            }

            DebugMelFramesProduced += melCount;
            audioContext.UpdateFrame(ref frame);
            return true;
        }
        catch (ObjectDisposedException)
        {
            // 拆卸期预期错误：上下文已释放而后台任务仍在处理。
            return false;
        }
        catch (Exception ex)
        {
            LastError = $"ProcessFrame: {ex.Message}";
            return false;
        }
    }

    private void RunSequenceInference(VisemeContext audioContext)
    {
        var melSeq = audioContext.GetMelSequence();
        int seqLen = audioContext.AccumulatedMelFrames;
        int melBands = audioContext.MelBands;
        var destination = audioContext.GetInferenceBuffer();

        if (_onnxSession is null || seqLen <= 0)
        {
            Array.Clear(destination, 0, destination.Length);
            return;
        }

        try
        {
            int inputSize = seqLen * melBands;
            var inputData = audioContext.GetInferenceInputBuffer();
            melSeq.AsSpan(0, inputSize).CopyTo(inputData);

            if (_legacyNormalizeWindow)
            {
                // legacy 模型按窗口自身均值/方差归一化（流式拿不到整段统计的因果替代）。
                var span = inputData.AsSpan(0, inputSize);
                double sum = 0.0;
                for (int i = 0; i < inputSize; i++)
                {
                    sum += span[i];
                }

                float mean = (float)(sum / inputSize);

                double sq = 0.0;
                for (int i = 0; i < inputSize; i++)
                {
                    float d = span[i] - mean;
                    sq += (double)d * d;
                }

                float std = MathF.Sqrt((float)(sq / inputSize));
                float inv = 1f / MathF.Max(std, 1e-5f);

                for (int i = 0; i < inputSize; i++)
                {
                    span[i] = (span[i] - mean) * inv;
                }
            }

            var inputTensor = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(
                new Memory<float>(inputData, 0, inputSize), [1, seqLen, melBands]);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("audio_features", inputTensor),
            };

            using var results = _onnxSession.Run(inputs);

            var outputTensor = results.First().AsTensor<float>();
            var dims = outputTensor.Dimensions;

            int numVisemes = Math.Min(destination.Length, _numVisemes);

            int outBase;
            if (dims.Length == 3)
            {
                outBase = (dims[1] - 1) * dims[2];
            }
            else if (dims.Length == 2)
            {
                outBase = (dims[0] - 1) * dims[1];
            }
            else if (dims.Length == 1)
            {
                outBase = 0;
            }
            else
            {
                Array.Clear(destination, 0, destination.Length);
                return;
            }

            ReadOnlySpan<float> outSpan = outputTensor is Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float> denseOut
                ? denseOut.Buffer.Span
                : outputTensor.ToArray();

            ApplyActivation(outSpan.Slice(outBase, numVisemes), destination, numVisemes, _isMultiLabel);

            if (numVisemes < destination.Length)
            {
                Array.Clear(destination, numVisemes, destination.Length - numVisemes);
            }
        }
        catch (Exception ex)
        {
            LastError = $"SequenceInference: {ex.Message}";
            Array.Clear(destination, 0, destination.Length);
        }
    }

    internal static void ApplyActivation(ReadOnlySpan<float> logits, Span<float> destination, int numVisemes, bool multiLabel)
    {
        // 多标签 = 逐 viseme sigmoid（口型可共发音，这是协同发音的真实形态）；
        // 否则 = softmax 竞争。
        if (multiLabel)
        {
            for (int i = 0; i < numVisemes; i++)
            {
                float x = Math.Clamp(logits[i], -50f, 50f);
                destination[i] = 1f / (1f + MathF.Exp(-x));
            }
        }
        else
        {
            float maxLogit = float.MinValue;
            for (int i = 0; i < numVisemes; i++)
            {
                if (logits[i] > maxLogit)
                {
                    maxLogit = logits[i];
                }
            }

            float sum = 0f;
            for (int i = 0; i < numVisemes; i++)
            {
                float e = MathF.Exp(logits[i] - maxLogit);
                destination[i] = e;
                sum += e;
            }

            if (sum > 0f)
            {
                float inv = 1f / sum;
                for (int i = 0; i < numVisemes; i++)
                {
                    destination[i] *= inv;
                }
            }
            else
            {
                for (int i = 0; i < numVisemes; i++)
                {
                    destination[i] = 0f;
                }
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Shutdown();
            _disposed = true;
        }
    }
}
