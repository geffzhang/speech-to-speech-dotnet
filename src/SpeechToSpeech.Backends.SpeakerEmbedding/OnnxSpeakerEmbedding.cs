using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SpeechToSpeech.Audio.Diarization;

namespace SpeechToSpeech.Backends.SpeakerEmbedding;

/// <summary>
/// 说话人声纹引擎：Kaldi fbank 前端 + 3D-Speaker / WeSpeaker 系 ONNX 模型，
/// 把一段单轮音频压成一个 <b>L2 归一化</b>的说话人向量——它就是
/// <see cref="ISpeakerEmbedding"/>，在线分离器靠它判「这是不是刚才那个人」。
///
/// <para>为什么是「一段」而不是「一帧」：模型用的是统计池化（CAM++ / ResNet 的
/// temporal average pooling），整段的统计量就是它的输出。所以这条接缝的形状是
/// 「一个 local turn 进、一个向量出」，而不是逐帧出向量再自己池化——后者会把
/// 「哪一段属于谁」这个已经由分割决定的事实又猜一遍。</para>
///
/// <para>采样率不做内部重采样：接缝两边（<see cref="StreamingDiarizerConfig.SampleRate"/> 与模型
/// metadata）都声明了 16 kHz，管线里 `DiarizationHandler` 也已经把音频重采样到 16 kHz。
/// 在这里再塞一套重采样只会让两种口径悄悄分叉——宁可明确拒绝。</para>
///
/// <para>实例可被多条连接共用：每次 <see cref="Embed"/> 分配自己的缓冲、不保留跨调用状态，
/// 而 <c>InferenceSession.Run</c> 本身线程安全。</para>
/// </summary>
public sealed class OnnxSpeakerEmbedding : ISpeakerEmbedding, IDisposable
{
    private readonly InferenceSession _session;
    private readonly SpeakerEmbeddingModelInfo _info;
    private readonly KaldiFbank _fbank = new();
    private readonly string _inputName;
    private readonly string _outputName;
    private int _disposed;

    /// <param name="modelPath">声纹 ONNX 的路径（模型来源与校验和见 <c>porting/00-reference.md</c>）。</param>
    /// <param name="threadCount">ONNX 会话的算子内并行度（默认 1：声纹跑在分离 worker 的后台线程上）。</param>
    public OnnxSpeakerEmbedding(string modelPath, int threadCount = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadCount);

        try
        {
            var options = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = threadCount,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            _session = new InferenceSession(modelPath, options);
            _info = SpeakerEmbeddingModelInfo.FromMetadata(_session.ModelMetadata.CustomMetadataMap);

            if (_info.SampleRate != KaldiFbank.SampleRate)
            {
                throw new InvalidOperationException(
                    $"声纹模型声明 {_info.SampleRate} Hz，而 fbank 前端是 {KaldiFbank.SampleRate} Hz 口径——" +
                    "换采样率就得同时换帧长/帧移与 mel 频带，这不是改一个数字的事，请换模型。");
            }

            _inputName = _session.InputMetadata.First().Key;
            _outputName = _session.OutputMetadata.First().Key;
        }
        catch (OnnxRuntimeException ex)
        {
            throw new InvalidOperationException($"加载声纹模型失败：{ex.Message}", ex);
        }
    }

    /// <summary>模型每次产出的固定宽度（metadata 的 <c>output_dim</c>；CAM++ 是 192）。</summary>
    public int EmbeddingDim => _info.OutputDim;

    /// <summary>模型要求的采样率。</summary>
    public int SampleRate => _info.SampleRate;

    /// <summary>模型声明的框架（<c>3d-speaker</c> / <c>wespeaker</c>），排障时看一眼就知道加载的是什么。</summary>
    public string Framework => _info.Framework;

    public float[] Embed(ReadOnlySpan<float> audio, int sampleRate)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (sampleRate != _info.SampleRate)
        {
            throw new ArgumentException(
                $"声纹模型要 {_info.SampleRate} Hz，收到 {sampleRate} Hz——请在调用方重采样。",
                nameof(sampleRate));
        }
        if (audio.Length == 0)
        {
            throw new ArgumentException("空音频算不出声纹。", nameof(audio));
        }

        // normalize_samples = 0 的导出期望 kaldi 老习惯的 [-32768, 32768] 量级。
        // 乘在 fbank 之前，因为放大整段波形不改变 log-mel 的形状、只平移 log 能量。
        float[] samples;
        if (_info.NormalizeSamples)
        {
            samples = audio.ToArray();
        }
        else
        {
            samples = new float[audio.Length];
            for (int i = 0; i < audio.Length; i++) samples[i] = audio[i] * 32768.0f;
        }

        float[] features = _fbank.Compute(samples, out int frames);
        if (frames <= 0)
        {
            throw new ArgumentException(
                $"音频 {audio.Length} 个样本不足一帧 {KaldiFbank.FrameLength}（25 ms）。",
                nameof(audio));
        }

        if (_info.FeatureNormalizeType == "global-mean")
        {
            KaldiFbank.SubtractGlobalMean(features, frames);
        }

        var input = new DenseTensor<float>(features, [1, frames, KaldiFbank.FeatureDim]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, input)]);

        float[] embedding = results[0].AsTensor<float>().ToArray();
        if (embedding.Length != _info.OutputDim)
        {
            throw new InvalidOperationException(
                $"模型输出 {embedding.Length} 维，与 metadata 声明的 {_info.OutputDim} 维不一致。");
        }

        // 输出单位化：下游的是余弦距离，而「单位向量」是这个接缝对外承诺的形状
        // （别的实现若假设向量已归一化，未归一化的输入会让所有距离系统性偏大）。
        Normalize(embedding);
        return embedding;
    }

    private static void Normalize(float[] vector)
    {
        double sum = 0;
        for (int i = 0; i < vector.Length; i++) sum += (double)vector[i] * vector[i];

        if (sum <= 0) return; // 全零向量无从归一化；交给下游的 Distance 判成 2.0（最远）

        float scale = (float)(1.0 / Math.Sqrt(sum));
        for (int i = 0; i < vector.Length; i++) vector[i] *= scale;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.Dispose();
    }
}
