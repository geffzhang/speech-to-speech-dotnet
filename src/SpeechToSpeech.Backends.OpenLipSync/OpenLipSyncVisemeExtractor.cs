using System.Buffers.Binary;
using SpeechToSpeech.Backends.OpenLipSync.Inference;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Backends.OpenLipSync;

/// <summary>
/// <see cref="IVisemeExtractor"/> 的 OpenLipSync 实现：一次调用 = 一个 0.5 秒音频窗口。
///
/// 上游 STV 每个窗口做一次 wav2vec2 推理；本引擎同样每个窗口从干净上下文开始
/// （<see cref="OpenLipSyncBackend.ResetContext"/>）——窗口之间不共享声学状态，
/// 所以两类引擎在「什么时候算一次」这件事上口径一致，差异只在口型集合与产出方式（C033）。
///
/// 输入是管线的原生音频率（本构建 TTS 出 24 kHz），引擎内部按自己的 <c>48 tap</c>
/// 多相重采样降到模型要求的 16 kHz，与上游「管线固定 16 kHz」在频域上等价。
/// </summary>
public sealed class OpenLipSyncVisemeExtractor : IVisemeExtractor
{
    private const string ModelFileName = "model.onnx";
    private const string ConfigFileName = "config.json";

    private readonly OpenLipSyncBackend _backend;
    private readonly long _context;
    private readonly List<float[]> _frames = [];
    // ref 传参要求可写字段（ProcessFrame 把结果写回这个框）。
    private VisemeFrame _frame = new();
    private int _disposed;
    private OpenLipSyncVisemeExtractor(OpenLipSyncBackend backend, long context)
    {
        _backend = backend;
        _context = context;
        _backend.SetFrameObserver(_context, frame => _frames.Add((float[])frame.Clone()));
    }

    public int SampleRate => _backend.SampleRate;

    /// <summary>
    /// 从模型目录加载（<c>model.onnx</c> + <c>config.json</c>）。
    ///
    /// <paramref name="sampleRate"/> 是管线原生音频率：引擎按它建立到 16 kHz 的重采样器。
    /// </summary>
    public static OpenLipSyncVisemeExtractor Load(string modelDirectory, int sampleRate, string? configPath = null)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            throw new BackendUnavailableException("openlipsync", "未给出模型目录（--stv_model_dir）");
        }

        string modelPath = Path.Combine(modelDirectory, ModelFileName);
        string resolvedConfig = configPath ?? Path.Combine(modelDirectory, ConfigFileName);

        if (!File.Exists(modelPath))
        {
            throw new BackendUnavailableException("openlipsync", $"缺少模型文件 {modelPath}");
        }

        if (!File.Exists(resolvedConfig))
        {
            throw new BackendUnavailableException("openlipsync", $"缺少模型配置 {resolvedConfig}");
        }

        var backend = new OpenLipSyncBackend();
        try
        {
            if (!backend.InitializeFromBytes(sampleRate, File.ReadAllBytes(modelPath), File.ReadAllText(resolvedConfig)))
            {
                throw new BackendUnavailableException(
                    "openlipsync", string.IsNullOrEmpty(backend.LastError) ? "模型加载失败" : backend.LastError);
            }

            return new OpenLipSyncVisemeExtractor(backend, backend.CreateContext());
        }
        catch
        {
            backend.Dispose();
            throw;
        }
    }

    public IReadOnlyList<Viseme> Extract(ReadOnlySpan<byte> pcm16)
    {
        int sampleCount = pcm16.Length / 2;
        if (sampleCount == 0)
        {
            return [];
        }

        var audio = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            // 小端 PCM16 → [-1, 1) 单声道浮点（Python 侧 audio/32768.0 的等价）。
            audio[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2)) / 32768f;
        }

        _frames.Clear();
        _backend.ResetContext(_context);

        if (!_backend.ProcessFrame(_context, audio, ref _frame))
        {
            throw new InvalidOperationException(
                string.IsNullOrEmpty(_backend.LastError) ? "口型推理失败" : _backend.LastError);
        }

        return VisemeTimeline.BuildIntervals(_frames, _backend.FrameDurationSeconds);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _backend.SetFrameObserver(_context, null);
        _backend.DestroyContext(_context);
        _backend.Dispose();
    }
}
