using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Backends.SmartTurn;

/// <summary>
/// Smart Turn v3.2 端点判定器（上游 <c>SmartTurnAnalyzer</c> 的 C# 对应物）。
///
/// 对齐点：阈值校验、会话选项（SEQUENTIAL / inter-op 1 / graph 全量优化）、
/// 8 秒窗口（取尾部、前部补零）、<c>probability &gt; threshold</c> 的判定、
/// 非有限概率抛错、构造期用 1 秒静音预热。
///
/// 刻意不对齐的：上游缺模型时自动 <c>hf_hub_download</c>（C# 侧不做运行期下载，
/// 缺文件抛 <see cref="BackendUnavailableException"/>，由 CLI 转成启动告警 + disabled）；
/// 非 16 kHz 输入的 scipy 重采样（管线内恒为 16 kHz，上游重采样路径只服务直接调 API 的用户）。
/// </summary>
public sealed class SmartTurnOnnxClassifier : ITurnEndClassifier
{
    public const string ModelVersion = "v3.2";
    public const string DefaultModelFileName = $"smart-turn-{ModelVersion}-cpu.onnx";

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly double _threshold;

    public string Name => "smart-turn";

    /// <summary>
    /// 加载模型。<paramref name="modelPath"/> 为空时按 <see cref="DefaultModelFileName"/>
    /// 在工作目录的 <c>models/smart-turn/</c> 下找。
    /// </summary>
    /// <exception cref="BackendUnavailableException">模型文件缺失或 ONNX Runtime 加载失败。</exception>
    public SmartTurnOnnxClassifier(string? modelPath = null, double threshold = 0.5, int cpuCount = 1)
    {
        if (threshold is < 0.0 or > 1.0)
        {
            throw new ArgumentException($"Smart Turn threshold must be between 0 and 1, got {threshold}", nameof(threshold));
        }

        if (cpuCount < 1)
        {
            throw new ArgumentException($"Smart Turn cpu_count must be at least 1, got {cpuCount}", nameof(cpuCount));
        }

        var path = ResolveModelPath(modelPath);
        if (!File.Exists(path))
        {
            throw new BackendUnavailableException(
                Name,
                $"Smart Turn 模型不存在：{path}。用 --smart_turn_model_path 指定，" +
                $"或把 {DefaultModelFileName} 放到 models/smart-turn/ 下（hf-mirror.com/pipecat-ai/smart-turn-v3）");
        }

        _threshold = threshold;

        try
        {
            var options = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = cpuCount,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            _session = new InferenceSession(path, options);
            _inputName = _session.InputMetadata.First().Key;
        }
        catch (Exception ex) when (ex is not BackendUnavailableException)
        {
            throw new BackendUnavailableException(
                Name, $"Smart Turn 模型加载失败：{path}（{ex.Message}）");
        }

        // 与上游一致：构造期跑一次 1 秒静音，把首次推理的惰性开销挪到启动时。
        _ = Predict(new float[SmartTurnSampleRate], SmartTurnSampleRate);
    }

    private const int SmartTurnSampleRate = WhisperFrontend.SampleRate;

    public TurnEndPrediction Predict(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (sampleRate != SmartTurnSampleRate)
        {
            throw new ArgumentException(
                $"Smart Turn 期望 {SmartTurnSampleRate} Hz 音频，收到 {sampleRate} Hz；" +
                "管线内音频恒为 16 kHz，非 16 kHz 输入不属于判定器的职责", nameof(sampleRate));
        }

        var prepared = new float[WhisperFrontend.MaxSamples];
        if (audio.Length > WhisperFrontend.MaxSamples)
        {
            // 取尾部：模型只看最后 8 秒（上游 audio[-max_samples:]）。
            audio[^WhisperFrontend.MaxSamples..].CopyTo(prepared);
        }
        else
        {
            // 前部补零：窗口对齐到「现在」，补零代表更早的沉默。
            audio.CopyTo(prepared);
        }

        var features = WhisperFrontend.LogMel(WhisperFrontend.ZeroMeanUnitVar(prepared));
        var input = new DenseTensor<float>(features, [1, WhisperFrontend.Mels, WhisperFrontend.Frames]);
        using var outputs = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, input)]);

        var probability = outputs.First().AsTensor<float>().ToArray()[0];
        if (!float.IsFinite(probability))
        {
            throw new InvalidOperationException($"Smart Turn returned a non-finite probability: {probability}");
        }

        return new TurnEndPrediction(probability > _threshold, probability);
    }

    private static string ResolveModelPath(string? modelPath)
    {
        if (!string.IsNullOrWhiteSpace(modelPath))
        {
            return Path.GetFullPath(modelPath);
        }

        return Path.GetFullPath(Path.Combine("models", "smart-turn", DefaultModelFileName));
    }
}
