using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SpeechToSpeech.Audio.Diarization;
using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Backends.PyannoteDiarization;

/// <summary>
/// pyannote segmentation-3.0 的<b>帧级</b>读法：一个窗口进，逐帧、逐本地说话人的概率出，重叠原样保留
/// ——它就是在线分离器驱动的 <see cref="ISpeakerMaskSegmentation"/>。
///
/// <para>几何全部来自模型的 metadata（<see cref="PyannoteSegmentationModel"/>），所以重新导出的模型自带参数。
/// <c>windowSamples</c> 可以缩短窗口，拿准确度换前瞻——但前提是图真的接受动态长度；
/// 定长导出在构造期就拒绝，而不是悄悄补零或截断（那样得到的窗口是假的，前瞻也就跟着是假的）。</para>
///
/// <para>实例可以被多条连接共用：每次 <see cref="Analyze"/> 分配自己的输入缓冲，不保留跨调用状态，
/// 而 ONNX 的 <c>InferenceSession.Run</c> 本身线程安全。权重只加载一次，由装配方持有并负责释放。</para>
/// </summary>
public sealed class PyannoteOnnxMaskSegmentation : ISpeakerMaskSegmentation, IDisposable
{
    private readonly InferenceSession _session;
    private readonly PyannoteSegmentationModel _model;
    private readonly float[][] _powersetMapping;
    private readonly string _inputName;
    private readonly string _outputName;
    private int _disposed;

    /// <param name="modelPath">pyannote segmentation ONNX 的路径（模型来源与校验和见 <c>porting/00-reference.md</c>）。</param>
    /// <param name="windowSamples">
    /// 要跑的窗口长度，<c>0</c> 表示用模型自己的。更短的窗口压缩前瞻（<c>(window − hop) / 2</c>），
    /// 代价是一些准确度；更长的会被图拒绝。
    /// </param>
    /// <param name="threadCount">ONNX 会话的算子内并行度（默认 1：分离 worker 本身就在后台线程）。</param>
    public PyannoteOnnxMaskSegmentation(string modelPath, int windowSamples = 0, int threadCount = 1)
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
            _model = PyannoteSegmentationModel.FromMetadata(_session.ModelMetadata.CustomMetadataMap);
            _inputName = _session.InputMetadata.First().Key;
            _outputName = _session.OutputMetadata.First().Key;
            _powersetMapping = PowersetMaskDecoder.BuildPowersetMapping(
                _model.NumClasses, _model.NumSpeakers, _model.PowersetMaxClasses);

            // 默认窗口向下取整到帧网格：模型自己的 window_size 未必是帧步长的整数倍（sherpa 的
            // pyannote 导出就是 160000 对 270），而对外的 WindowSamples 必须是 Analyze 真正接受的**那一个**长度。
            // 取整后的窗口仍然是「整 592 帧 = 9.99 秒」，丢掉的是 0.01% 的尾巴，换来下游时间映射的整数性。
            int shift = Math.Max(1, _model.ReceptiveFieldShift);
            WindowSamples = windowSamples > 0 ? windowSamples : Math.Max(shift, _model.WindowSize / shift * shift);

            // 引擎只要求「窗口够长，能跑出一帧」（帧数 = ⌊(window − 感受野) / 步长⌋ + 1）。参照实现额外要求
            // 窗口是帧步长的整数倍，可它钉的正是 160000 / 270 这个导出，于是真实模型一加载就被这条自设的规矩
            // 拒掉（真跑记录见 00-reference.md 的 voxa 小节）——那条校验在这里不存在。
            if (WindowSamples < _model.ReceptiveFieldSize)
            {
                throw new ArgumentException(
                    $"窗口 {WindowSamples} 短于模型的感受野 {_model.ReceptiveFieldSize} 个样本，跑不出一帧。",
                    nameof(windowSamples));
            }

            var declared = _session.InputMetadata.First().Value.Dimensions;
            int declaredLength = declared.Length > 0 ? declared[^1] : -1;
            if (declaredLength > 0 && declaredLength != WindowSamples)
            {
                throw new ArgumentException(
                    $"这个导出是定长 {declaredLength} 样本输入；windowSamples={WindowSamples} 会被图拒绝。" +
                    "要缩短窗口，请用动态长度轴重新导出。",
                    nameof(windowSamples));
            }
        }
        catch (Exception ex) when (ex is not ArgumentException && ex is not BackendUnavailableException)
        {
            throw new BackendUnavailableException(
                "pyannote-diarization", $"说话人分割模型加载失败：{modelPath}（{ex.Message}）");
        }
    }

    /// <inheritdoc />
    public int SampleRate => _model.SampleRate;

    /// <inheritdoc />
    public int WindowSamples { get; }

    /// <inheritdoc />
    public int FrameShiftSamples => _model.ReceptiveFieldShift;

    /// <inheritdoc />
    public int FrameOffsetSamples => _model.ReceptiveFieldSize / 2;

    /// <inheritdoc />
    public int MaxLocalSpeakers => _model.NumSpeakers;

    /// <inheritdoc />
    public SpeakerMaskWindow Analyze(ReadOnlySpan<float> window)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (window.Length != WindowSamples)
            throw new ArgumentException($"期望正好 {WindowSamples} 个样本；收到 {window.Length}。", nameof(window));

        int classes = _model.NumClasses;

        // 每次调用自己的输入缓冲：共用一个实例的多条连接不会互相踩输入。
        var input = new float[WindowSamples];
        window.CopyTo(input);

        var tensor = new DenseTensor<float>(input, [1, 1, WindowSamples]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        var data = results.First().AsEnumerable<float>().ToArray();

        if (classes <= 0 || data.Length % classes != 0)
        {
            throw new InvalidOperationException(
                $"pyannote 输出长度 {data.Length} 不是 {classes} 个类别的整数倍——" +
                "模型的 num_classes metadata 与它的输出宽度不一致。");
        }

        int frames = data.Length / classes;
        var rows = new float[frames][];
        for (int f = 0; f < frames; f++)
        {
            var row = new float[MaxLocalSpeakers];
            PowersetMaskDecoder.FrameSpeakerProbabilities(
                data.AsSpan(f * classes, classes), _powersetMapping, row);
            rows[f] = row;
        }

        return new SpeakerMaskWindow(rows);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.Dispose();
    }
}
