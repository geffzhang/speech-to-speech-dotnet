using System.Threading.Channels;
using SpeechToSpeech.Audio.Diarization;
using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Core.Pipeline.Handlers;

/// <summary>
/// 流式说话人分离在管线里的那一级，放在 <b>VAD 之前</b>的原始输入上：分离要的是连续音频（含静音），
/// 而未经闸门的音频只有这里才有。
///
/// <para>它做三件事：</para>
/// <list type="number">
/// <item><b>不挡数据循环。</b>音频转成 16 kHz float 后 <c>TryWrite</c> 进有界队列，由单独一个 worker 任务消费。
/// 模型推理永远不在数据循环上跑：模型慢只会推迟分离（队列溢出时按时间轴缺口上报），不会推迟用户听到的回答。</item>
/// <item><b>维护连续时间轴。</b>推入的样本按 16 kHz 累计，与 VAD 报出的 <c>audio_start_ms</c> 同一根时钟——
/// 两边都从会话第一个样本数起，归因才是纯区间运算而不是偏移换算。</item>
/// <item><b>把段写进时间线。</b><see cref="SpeakerTimeline"/> 是归因侧读的那份「谁在什么时候说话」；
/// 状态机本身只被这一个 worker 线程碰。</item>
/// </list>
///
/// <para>与上游的差别（D009）：上游把 VAD 选中<em>之后</em>的语音块送进 worker，用模型自带的 speaker cache
/// 跨 VAD 边界保持身份；本实现没有那个 cache（参照实现给的是声纹比对，见 D033），送连续音频反而更省事——
/// 没有「跳过的静音」要拼，也就不存在把两段不相邻的波形接成一个假边界的问题。</para>
/// </summary>
public sealed class DiarizationHandler : BaseHandler<object, object>
{
    private enum WorkKind
    {
        Audio,
        Flush,
        Reset,
    }

    private readonly struct Work
    {
        public Work(WorkKind kind, float[]? samples = null, TaskCompletionSource? done = null)
        {
            Kind = kind;
            Samples = samples;
            Done = done;
        }

        public WorkKind Kind { get; }
        public float[]? Samples { get; }
        public TaskCompletionSource? Done { get; }
    }

    private readonly ISpeakerMaskSegmentation _segmentation;
    private readonly ISpeakerEmbedding _embedding;
    private readonly StreamingDiarizerConfig _config;
    private readonly DiarizationOptions _options;
    private readonly SpeakerTimeline _timeline;
    private readonly Action<string>? _log;

    private readonly Channel<Work> _work;
    private readonly CancellationTokenSource _workerCts = new();
    private readonly Task _worker;

    private StreamingDiarizer _diarizer;
    private long _droppedSamples; // 队列拒收的样本数（累计，观测用）
    private long _pendingGap;     // 还未上报给分离器的缺口（上报即清零）

    /// <param name="segmentation">帧级分割引擎（模型侧）。</param>
    /// <param name="embedding">声纹引擎（模型侧）。</param>
    /// <param name="config">状态机可调项（已由 <see cref="DiarizationOptions.BuildConfig"/> 解算）。</param>
    /// <param name="timeline">归因侧读取的段时间线；由装配方持有并交给 STT。</param>
    /// <param name="options">这一级的队列与命名参数。</param>
    /// <param name="log">诊断日志（默认 stderr）。</param>
    public DiarizationHandler(
        CancellationToken stopping,
        System.Collections.Concurrent.BlockingCollection<object> queueIn,
        System.Collections.Concurrent.BlockingCollection<object> queueOut,
        ISpeakerMaskSegmentation segmentation,
        ISpeakerEmbedding embedding,
        StreamingDiarizerConfig config,
        SpeakerTimeline timeline,
        DiarizationOptions options,
        Action<string>? log = null)
        : base(stopping, queueIn, queueOut)
    {
        _segmentation = segmentation;
        _embedding = embedding;
        _config = config;
        _timeline = timeline;
        _options = options;
        _log = log;
        _diarizer = CreateDiarizer();

        if (_options.AudioQueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AudioQueueCapacity 必须为正。");
        }

        _work = Channel.CreateBounded<Work>(new BoundedChannelOptions(_options.AudioQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // 用不到：写入一律 TryWrite，见 Process
            SingleReader = true,
            SingleWriter = true,
        });

        _worker = Task.Run(() => WorkerAsync(_workerCts.Token), CancellationToken.None);
    }

    /// <summary>时间线（装配方也持有它，交给 STT 做归因）。</summary>
    public SpeakerTimeline Timeline => _timeline;

    /// <summary>至今有多少样本被队列拒收（累计；每次都会变成一次缺口上报）。</summary>
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    /// <summary>分离器的音频时钟已推进到哪（16 kHz 样本数）。</summary>
    public long SamplesSeen => Interlocked.Read(ref _samplesSeen);

    private long _samplesSeen;

    protected override IEnumerable<object> Process(object input)
    {
        switch (input)
        {
            case VadInput vad:
                Feed(vad.Samples, vad.SampleRate);
                break;
            case float[] samples:
                Feed(samples, 16_000);
                break;
            case byte[] pcm:
                Feed(AudioUtil.FromPcm16(pcm), 24_000);
                break;
        }

        // 原样转发。分离是旁路：它读音频，但不改音频，也不改写任何控制流。
        yield return input;
    }

    /// <summary>
    /// 把一块音频按需重采样到 16 kHz 后交给 worker。
    ///
    /// <para>重采样在这里做而不是拒绝非 16 kHz 的输入：上游的 VAD 级本来就逐块重采样，这里沿用同一函数、
    /// 同一块边界，两边的 16 kHz 时钟因此对齐——不重采样就等于在 24 kHz 输入端上静默关掉分离。</para>
    /// </summary>
    private void Feed(float[] samples, int sampleRate)
    {
        if (samples.Length == 0) return;

        var mono16k = AudioUtil.Resample(samples, sampleRate, 16_000);
        if (ReferenceEquals(mono16k, samples))
        {
            // 队列持有它直到 worker 取走：原样引用调用方的缓冲，会让调用方下一块把上一块覆盖掉。
            mono16k = samples.ToArray();
        }

        Interlocked.Add(ref _samplesSeen, mono16k.Length);

        if (!_work.Writer.TryWrite(new Work(WorkKind.Audio, mono16k)))
        {
            // 队列满了：分离器落后了。把这段音频记成缺口，让它的时间轴仍然为真，
            // 然后继续往前走——在这里等一次推理，等于拿一个说话人标签换用户的回答。
            Interlocked.Add(ref _droppedSamples, mono16k.Length);
            Interlocked.Add(ref _pendingGap, mono16k.Length);
        }
    }

    protected override void OnSessionEnd()
    {
        // 先把尾巴冲出来再往下传：STT 队列里还排着这次会话的音频，它们的归因要用到尾部那几个段。
        DrainBlocking(WorkKind.Flush);
        _timeline.Clear();
    }

    protected override void Cleanup()
    {
        DrainBlocking(WorkKind.Flush);
        _work.Writer.TryComplete();
        try
        {
            _workerCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经收尾过了
        }

        try
        {
            _worker.Wait(_options.FlushTimeout);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"DiarizationHandler: worker 收尾失败（{ex.Message}）");
        }

        _workerCts.Dispose();
        // 引擎不在这里释放：它是装配方持有的共享实例（多条连接共用一份权重），
        // 每条连接拆掉就 Dispose 一次会把别人的会话一起关掉。
    }

    /// <summary>
    /// 投一个同步工作项并等它做完（会话结束、拆除时用）。
    /// 只在收尾路径上阻塞：这时候等的是「尾部那几个段」，不是在等的对话。
    /// </summary>
    private void DrainBlocking(WorkKind kind)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_work.Writer.TryWrite(new Work(kind, done: done)))
        {
            // 队列满 = worker 已经落后很多，再排队只会等更久；尾巴本来也已经被时间轴缺口覆盖了。
            return;
        }

        if (!done.Task.Wait(_options.FlushTimeout))
        {
            _log?.Invoke(
                $"DiarizationHandler: 收尾等了 {(int)_options.FlushTimeout.TotalMilliseconds} ms 仍未完成，" +
                "按已判定的一部分为准。");
        }
    }

    // ---------------------------------------------------------------- worker

    private StreamingDiarizer CreateDiarizer() => new(_segmentation, _embedding, _config);

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            while (await _work.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_work.Reader.TryRead(out var work))
                {
                    try
                    {
                        Execute(work);
                    }
                    finally
                    {
                        work.Done?.TrySetResult();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 拆除
        }
        catch (ChannelClosedException)
        {
            // 拆除
        }
        catch (Exception ex)
        {
            // 分离失败不能把会话弄坏：转写照发，标签缺失（D033 的「无生产者」形态）。
            _log?.Invoke($"DiarizationHandler: 增量分离失败，本会话后续不再给说话人标签（{ex.Message}）");
        }
    }

    private void Execute(Work work)
    {
        switch (work.Kind)
        {
            case WorkKind.Audio:
                ReportGap();
                _timeline.Apply(_diarizer.Push(work.Samples!));
                break;

            case WorkKind.Flush:
                ReportGap();
                // 冲尾会跑掉剩下的窗口并提交所有进行中的段；它发生在收尾，代价是一次有界的突发，
                // 而不是摊到每句话上的时延税。
                _timeline.Apply(_diarizer.Flush());
                break;

            case WorkKind.Reset:
                ReportGap();
                _timeline.Apply(_diarizer.Flush());
                _timeline.Clear();
                _diarizer = CreateDiarizer();
                Interlocked.Exchange(ref _samplesSeen, 0);
                break;
        }
    }

    private void ReportGap()
    {
        long dropped = Interlocked.Exchange(ref _pendingGap, 0);
        if (dropped <= 0) return;

        _log?.Invoke($"DiarizationHandler: {dropped} 个样本被队列丢弃，上报为时间轴缺口");
        _timeline.Apply(_diarizer.NotifyGap((int)Math.Min(dropped, int.MaxValue)));
    }
}
