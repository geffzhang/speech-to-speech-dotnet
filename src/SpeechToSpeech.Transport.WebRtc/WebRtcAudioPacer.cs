namespace SpeechToSpeech.Transport.WebRtc;

/// <summary>Pacer 单步决策结果。</summary>
public enum PacerOutcome
{
    /// <summary>缓冲无数据：时钟冻结，等待下一次 Write。</summary>
    NeedsMoreData,

    /// <summary>距下一帧截止期还有时间：调用方按 <see cref="PacerStep.SleepSeconds"/> 睡眠。</summary>
    Sleep,

    /// <summary>产出一帧 48 kHz 样本（不足补零）。</summary>
    Frame,
}

public readonly record struct PacerStep(
    PacerOutcome Outcome,
    double SleepSeconds,
    short[] Samples,
    uint Timestamp,
    long Generation);

/// <summary>
/// WebRTC 出向音频的 paced 轨道（上游 <c>PipelineAudioTrack</c> 的 C# 对应物）。
///
/// 发送侧把生成音频快速写入缓冲；取帧按墙钟 20 ms 步长推进。语义对齐上游：
/// <list type="bullet">
/// <item>首帧取到时定格时钟原点；此后每帧时间戳 = 上一帧 + 960（48 kHz × 20 ms）。</item>
/// <item>缓冲枯竭后重新来数据：时间戳 <b>追赶到</b> 墙钟对应位置（RFC 7587 §3.1.3 整帧抑制），
/// 时钟原点与下一帧截止期保持不变——浏览器端 jitter buffer 据此补静音。追赶帧数用
/// <c>ceil</c>（上游 <c>math.ceil</c>），追赶只在「等过数据」的取帧里生效。</item>
/// <item>截止期到达时缓冲有任意数据（不足一帧）→ 取走并<b>补零成整帧</b>发出（上游
/// <c>payload += b"\x00" * (needed - len(payload))</c>，不等凑满）。</item>
/// <item>音频结束后继续补 <see cref="DrainFrames"/> 帧（100 ms）静音，排空编码器与
/// 接收端预取（上游 <c>_drain_remaining</c>）。</item>
/// <item><see cref="Clear"/> 原子地清缓冲并推进代际：编码在途的帧按代际判据丢弃（barge-in flush）。</item>
/// </list>
/// 时钟经 <c>Func&lt;double&gt;</c> 注入（单调秒），与 turns 层同一套可测性纪律。
/// </summary>
public sealed class WebRtcAudioPacer
{
    public const int SampleRate = 48_000;
    public const int FrameSamples = SampleRate / 50; // 20 ms
    public const double PtimeSeconds = 0.02;

    /// <summary>音频结束后排空的静音帧数（上游 WEBRTC_DRAIN_FRAMES）。</summary>
    public const int DrainFrames = 5;

    private readonly Func<double> _clock;
    private readonly object _lock = new();
    private readonly List<short> _buffer = [];

    private double? _start;
    private long _lastTimestamp;
    private long _generation;
    private int _drainRemaining;
    private bool _awaited; // 本次取帧等过数据（上游 waited_for_audio，追赶判据）

    /// <summary>
    /// Sleep 在途时已定型的帧时间戳。上游在<b>同一次</b> recv() 调用里先算 timestamp
    /// 再 sleep、醒后沿用（不重算）——Tick 模型里若每次重算，睡过截止期会让
    /// ceil 再跳一帧，形成无限追逐。
    /// </summary>
    private long? _pendingTimestamp;

    public WebRtcAudioPacer(Func<double>? clock = null)
    {
        _clock = clock ?? (MonotonicSeconds);
    }

    public long Generation
    {
        get { lock (_lock) { return _generation; } }
    }

    public int BufferedSamples
    {
        get { lock (_lock) { return _buffer.Count; } }
    }

    public void Write(short[] samples48k)
    {
        if (samples48k.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            _buffer.AddRange(samples48k);
        }
    }

    /// <summary>barge-in：清空未播音频并推进代际。编码在途的帧按代际丢弃。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
            _drainRemaining = 0;
            _generation++;
        }
    }

    /// <summary>
    /// 状态机单步：调用方循环调用，按返回值睡眠或编码发送。
    /// 纯逻辑、时钟注入，可在没有 WebRTC 栈的环境下表征测试。
    /// 逐行对齐上游 <c>recv()</c> 的 while 循环（见方法内注释）。
    /// </summary>
    public PacerStep Tick()
    {
        short[] samples;
        long generation;
        long timestamp;

        lock (_lock)
        {
            generation = _generation;
            var now = _clock();

            // 上游外层 while not buffer and not drain_remaining: await data_available。
            // 一次 Tick 对应一次 recv()：缓冲与排空都空 → 等数据（时钟冻结），
            // 并记住「等过」——之后带着数据进来的取帧触发追赶（waited_for_audio）。
            if (_buffer.Count == 0 && _drainRemaining == 0)
            {
                _awaited = true;
                _pendingTimestamp = null; // 上游 continue：丢弃睡眠中算好的时间戳，回到等待
                return new PacerStep(PacerOutcome.NeedsMoreData, 0.01, [], 0, generation);
            }

            if (_start is null)
            {
                // 时钟原点在首帧取到时定格；首帧时间戳为 0、立即发出（上游同口径：
                // _start = now, timestamp = 0，首帧分支不算 wait）。
                _start = now;
                timestamp = 0;
            }
            else
            {
                if (_pendingTimestamp is long pending)
                {
                    // 上游：同一次 recv() 里睡眠醒后沿用睡眠前算好的 timestamp。
                    timestamp = pending;
                }
                else
                {
                    timestamp = _lastTimestamp + FrameSamples;
                    if (_awaited)
                    {
                        // 追赶（上游 waited_for_audio 分支）：整帧抑制，帧数用 ceil。
                        var elapsedFrames = (long)Math.Ceiling((now - _start.Value) / PtimeSeconds);
                        timestamp = Math.Max(timestamp, elapsedFrames * FrameSamples);
                    }
                }

                var wait = _start.Value + timestamp / (double)SampleRate - now;
                if (wait > 1e-9)
                {
                    // 浮点累加的亚微秒噪声不算「未到截止期」。
                    _pendingTimestamp = timestamp;
                    return new PacerStep(PacerOutcome.Sleep, wait, [], 0, generation);
                }
            }

            _pendingTimestamp = null;

            // 截止期已到（或首帧）：取走至多一帧，不足补零（上游 payload += b"\x00" * …）。
            samples = new short[FrameSamples];
            var take = Math.Min(FrameSamples, _buffer.Count);
            if (take > 0)
            {
                _buffer.CopyTo(0, samples, 0, take);
                _buffer.RemoveRange(0, take);
            }

            // 排空编码器/接收端预取：payload 非空重置 5 帧；空帧递减（上游 _drain_remaining）。
            _drainRemaining = take > 0 ? DrainFrames : _drainRemaining - 1;
            _lastTimestamp = timestamp;
            _awaited = false;
        }

        return new PacerStep(PacerOutcome.Frame, 0, samples, (uint)timestamp, generation);
    }

    private static double MonotonicSeconds() => System.Diagnostics.Stopwatch.GetTimestamp()
        / (double)System.Diagnostics.Stopwatch.Frequency;
}
