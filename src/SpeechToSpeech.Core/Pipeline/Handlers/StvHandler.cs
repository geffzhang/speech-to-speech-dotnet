using System.Collections.Concurrent;
using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Core.Pipeline.Handlers;

/// <summary>
/// 口型提取阶段（上游 <c>STV/w2v_stv_handler.py</c> 的 <c>Wav2Vec2STVHandler</c>）。
///
/// 位置：TTS 之后、发送循环之前。开启时 TTS 的输出去这里而不是直连发送队列——
/// 上游同样把 <c>tts_output_queue</c> 换成中间队列，再加这一级。
///
/// 三件事与上游逐条对齐：
/// <list type="number">
/// <item><b>整窗缓冲</b>：攒满 0.5 秒才推理一次（上游 <c>MIN_AUDIO_LENGTH</c>），
/// 一个窗口一次独立推理，不跨窗口携带声学状态；</item>
/// <item><b>样本偏移</b>：区间时间相对本窗起点，累加「已推理过的样本数」得到响应音频时间线上的绝对时刻；</item>
/// <item><b>重分块</b>：把一个窗口的音频按 32 ms 块重新发出，口型区间只挂在**第一块**上
/// （上游 <c>BLOCKSIZE=512 @16k</c> 的等价时长）。分块边界会因此与不开口型时不同——
/// 上游也是这么变的。</item>
/// </list>
///
/// 不走歪的几条：
/// <list type="bullet">
/// <item>取消代际过期时**丢弃**窗口但它们既然已经发出去了音频，就不能扣着不放——上游在同一处
/// <c>_flush</c> 里先算偏移再判陈旧，本实现照做；</item>
/// <item>解析失败不吞音频：上游 <c>speech_to_visemes</c> 内部 except 后返回空列表，
/// 本实现同样只在提取这一层兜底，音频照发；</item>
/// <item>响应结束标记必须穿过：它是发送循环收口的唯一依据。</item>
/// </list>
/// </summary>
public sealed class StvHandler : BaseHandler<object, object>
{
    /// <summary>上游 <c>MIN_AUDIO_LENGTH = 0.5</c> 秒。</summary>
    private const double WindowSeconds = 0.5;

    /// <summary>上游 <c>BLOCKSIZE = 512</c> 样本（在 16 kHz 上是 32 ms）。</summary>
    private const int UpstreamBlockSamples = 512;
    private const int UpstreamSampleRate = 16_000;

    private readonly IVisemeExtractor _extractor;
    private readonly CancelScope _cancelScope;
    private readonly int _sampleRate;

    /// <summary>一个推理窗口的字节数（16 位单声道）。</summary>
    private readonly int _windowBytes;

    /// <summary>重分块输出的块字节数。</summary>
    private readonly int _blockBytes;

    private readonly List<byte[]> _pending = [];
    private int _pendingBytes;
    private int _pendingSamples;

    /// <summary>已经推理过（已加上偏移）的样本数——区间绝对时刻的锚点。</summary>
    private long _sampleOffset;

    private (string? ResponseKey, int? CancelGeneration)? _context;
    private int _disposed;

    public StvHandler(
        CancellationToken stopping,
        BlockingCollection<object> queueIn,
        BlockingCollection<object> queueOut,
        IVisemeExtractor extractor,
        CancelScope cancelScope,
        int sampleRate)
        : base(stopping, queueIn, queueOut)
    {
        _extractor = extractor;
        _cancelScope = cancelScope;
        _sampleRate = sampleRate > 0 ? sampleRate : UpstreamSampleRate;

        // 0.5 秒 × 每秒样本数 × 2 字节 = 每秒样本数。
        _windowBytes = _sampleRate;
        _blockBytes = Math.Max(2, (int)Math.Round(_sampleRate * (double)UpstreamBlockSamples / UpstreamSampleRate) * 2);
    }

    public override CancelScope CancelScope => _cancelScope;

    protected override IEnumerable<object> Process(object input)
    {
        switch (input)
        {
            case EndOfResponse end:
                foreach (var flushed in HandleResponseDone(end))
                {
                    yield return flushed;
                }

                // 终态事件必须原样穿过，否则发送循环永远等不到响应收口。
                yield return end;
                yield break;

            case AudioOutput audio:
                foreach (var output in ProcessAudio(audio.Audio, audio.ResponseKey, audio.CancelGeneration))
                {
                    yield return output;
                }

                yield break;

            case byte[] pcm:
                foreach (var output in ProcessAudio(pcm, null, null))
                {
                    yield return output;
                }

                yield break;

            default:
                throw new InvalidOperationException($"Unexpected viseme input: {input.GetType().Name}");
        }
    }

    /// <summary>
    /// 基类的丢弃规则之外加一条上游特有处置：被丢弃的项若属于当前上下文，
    /// 说明这一轮已经作废，缓冲与偏移必须归零——否则下一轮的口型会带上一轮的尾巴。
    /// </summary>
    protected override bool ShouldProcessInput(object item)
    {
        if (base.ShouldProcessInput(item))
        {
            return true;
        }

        if (_context is { } context && context == ContextOf(item))
        {
            Reset();
        }

        return false;
    }

    /// <summary>事件序列先冲刷音频：尾部音频不能跑到文本/工具切换或 <c>response.done</c> 之后。</summary>
    protected override IEnumerable<object> ProcessPipelineEvent(PipelineEvent pipelineEvent)
    {
        foreach (var output in Flush())
        {
            yield return output;
        }

        yield return pipelineEvent;
    }

    private IEnumerable<object> HandleResponseDone(EndOfResponse end)
    {
        var context = (end.ResponseKey, end.CancelGeneration);
        if (context != _context)
        {
            yield break;
        }

        if (!end.CleanupOnly)
        {
            foreach (var output in Flush())
            {
                yield return output;
            }
        }

        Reset();
    }

    private IEnumerable<object> ProcessAudio(byte[] pcm, string? responseKey, int? cancelGeneration)
    {
        if (pcm.Length % 2 != 0)
        {
            // 上游同样在这里抛 ValueError：半个样本说明上游有 bug，不能猜着补齐。
            throw new InvalidOperationException("Viseme input must contain complete PCM16 samples.");
        }

        var context = (responseKey, cancelGeneration);
        if (IsStale(context))
        {
            if (context == _context)
            {
                Reset();
            }

            yield break;
        }

        if (context != _context)
        {
            foreach (var output in Flush())
            {
                yield return output;
            }

            Reset();
            _context = context;
        }

        int offset = 0;
        while (offset < pcm.Length)
        {
            int count = Math.Min(pcm.Length - offset, _windowBytes - _pendingBytes);
            _pending.Add(pcm[offset..(offset + count)]);
            _pendingBytes += count;
            _pendingSamples += count / 2;
            offset += count;

            if (_pendingBytes == _windowBytes)
            {
                foreach (var output in Flush())
                {
                    yield return output;
                }
            }
        }
    }

    /// <summary>
    /// 冲刷一个窗口：推理、加偏移、按 32 ms 重分块发出，口型挂第一块。
    ///
    /// 偏移与缓冲计数在推理**之前**推进——上游 <c>_flush</c> 同样先
    /// <c>_sample_offset += _pending_samples</c> 再判陈旧，这样被丢弃的窗口
    /// 不会让后续窗口的时间线错位。
    /// </summary>
    private IEnumerable<object> Flush()
    {
        if (_pendingBytes == 0)
        {
            yield break;
        }

        var pcm = new byte[_pendingBytes];
        int at = 0;
        foreach (var chunk in _pending)
        {
            chunk.CopyTo(pcm, at);
            at += chunk.Length;
        }

        var context = _context;
        double offsetSeconds = (double)_sampleOffset / _sampleRate;

        _pending.Clear();
        _pendingBytes = 0;
        _sampleOffset += _pendingSamples;
        _pendingSamples = 0;

        if (IsStale(context))
        {
            yield break;
        }

        IReadOnlyList<Viseme> cues;
        try
        {
            cues = _extractor.Extract(pcm);
        }
        catch (Exception ex)
        {
            // 口型是附加信息：提取失败只丢口型，音频照发（上游同口径）。
            TranscriptLog.Error("Viseme extraction failed; preserving assistant audio", ex);
            cues = [];
        }

        if (IsStale(context))
        {
            yield break;
        }

        var shifted = cues.Count == 0
            ? new List<Viseme>()
            : cues.Select(cue => new Viseme(
                cue.VisemeId,
                Math.Round(cue.StartSeconds + offsetSeconds, 6),
                Math.Round(cue.EndSeconds + offsetSeconds, 6))).ToList();

        for (int index = 0; index < pcm.Length; index += _blockBytes)
        {
            int count = Math.Min(_blockBytes, pcm.Length - index);
            yield return new AudioOutput
            {
                Audio = pcm[index..(index + count)],
                ResponseKey = context?.ResponseKey,
                CancelGeneration = context?.CancelGeneration,
                Visemes = index == 0 ? shifted : new List<Viseme>(),
            };
        }
    }

    private bool IsStale((string? ResponseKey, int? CancelGeneration)? context)
    {
        if (context is not { } value || value.CancelGeneration is not { } generation)
        {
            return false;
        }

        return _cancelScope.IsStale(generation);
    }

    private static (string? ResponseKey, int? CancelGeneration) ContextOf(object item) => item switch
    {
        AudioOutput audio => (audio.ResponseKey, audio.CancelGeneration),
        EndOfResponse end => (end.ResponseKey, end.CancelGeneration),
        _ => (null, null),
    };

    private void Reset()
    {
        _pending.Clear();
        _pendingBytes = 0;
        _pendingSamples = 0;
        _sampleOffset = 0;
        _context = null;
    }

    protected override void OnSessionEnd() => Reset();

    protected override void Cleanup()
    {
        Reset();
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _extractor.Dispose();
        }
    }
}
