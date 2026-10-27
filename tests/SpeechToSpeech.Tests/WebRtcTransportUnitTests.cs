using SpeechToSpeech.Transport.WebRtc;

namespace SpeechToSpeech.Tests;

/// <summary>
/// WebRTC 出向 paced 轨道的表征测试（时钟注入，对齐上游 PipelineAudioTrack 的语义）：
/// <list type="bullet">
/// <item>时钟原点在首帧取到时定格；每帧时间戳步长 960（48 kHz × 20 ms）；</item>
/// <item>缓冲枯竭后重来的数据把时间戳<b>追赶到</b>墙钟对应位置（整帧抑制，RFC 7587 §3.1.3）；</item>
/// <item>缓冲空时时钟冻结；</item>
/// <item>Clear() 清缓冲并推进代际，在途帧按代际丢弃。</item>
/// </list>
/// 重采样器锁的是连续性（分块喂入结果与整块一致）与跨率游标状态。
/// </summary>
public sealed class WebRtcTransportUnitTests
{
    private static short[] Sine(int samples, double amplitude = 8000)
    {
        var result = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            result[i] = (short)(amplitude * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
        }

        return result;
    }

    [Fact]
    public void Pacer_EmptyBuffer_NeedsMoreData_AndClockStaysUnstarted()
    {
        var clock = 100.0;
        var pacer = new WebRtcAudioPacer(() => clock);

        var step = pacer.Tick();
        Assert.Equal(PacerOutcome.NeedsMoreData, step.Outcome);
        Assert.Equal(0, pacer.BufferedSamples);
    }

    [Fact]
    public void Pacer_FirstFrame_StartsClockAtZeroTimestamp()
    {
        var clock = 100.0;
        var pacer = new WebRtcAudioPacer(() => clock);
        pacer.Write(Sine(WebRtcAudioPacer.FrameSamples * 3));

        clock += 5; // 数据到达晚于任何时钟
        var step = pacer.Tick();
        Assert.Equal(PacerOutcome.Frame, step.Outcome);
        Assert.Equal(0u, step.Timestamp);
        Assert.Equal(WebRtcAudioPacer.FrameSamples, step.Samples.Length);
    }

    [Fact]
    public void Pacer_TimestampAdvancesByFrameSamples()
    {
        var clock = 100.0;
        var pacer = new WebRtcAudioPacer(() => clock);
        pacer.Write(Sine(WebRtcAudioPacer.FrameSamples * 3));

        Assert.Equal(0u, pacer.Tick().Timestamp);
        clock += WebRtcAudioPacer.PtimeSeconds;
        Assert.Equal((uint)WebRtcAudioPacer.FrameSamples, pacer.Tick().Timestamp);
        clock += WebRtcAudioPacer.PtimeSeconds;
        Assert.Equal((uint)(WebRtcAudioPacer.FrameSamples * 2), pacer.Tick().Timestamp);
    }

    [Fact]
    public void Pacer_SleepsUntilFrameDeadline()
    {
        var clock = 100.0;
        var pacer = new WebRtcAudioPacer(() => clock);
        pacer.Write(Sine(WebRtcAudioPacer.FrameSamples * 4));

        var first = pacer.Tick();
        Assert.Equal(PacerOutcome.Frame, first.Outcome);
        // 时钟只走了 5ms（帧只出了一部分）：下一帧未到截止期 → Sleep。
        clock += 0.005;
        var step = pacer.Tick();
        Assert.Equal(PacerOutcome.Sleep, step.Outcome);
        Assert.Equal(WebRtcAudioPacer.PtimeSeconds - 0.005, step.SleepSeconds, 6);
    }

    [Fact]
    public void Pacer_DrainThenRefill_CatchesUpToWallClock()
    {
        var clock = 100.0;
        var pacer = new WebRtcAudioPacer(() => clock);
        pacer.Write(Sine(WebRtcAudioPacer.FrameSamples * 2));

        Assert.Equal(0u, pacer.Tick().Timestamp);      // t=0 帧
        clock += WebRtcAudioPacer.PtimeSeconds + 1e-6;
        Assert.Equal((uint)WebRtcAudioPacer.FrameSamples, pacer.Tick().Timestamp); // t=1 帧

        // 缓冲枯竭：先吐 5 帧（100 ms）排空静音（上游 _drain_remaining），逐帧推进时钟。
        for (var i = 0; i < WebRtcAudioPacer.DrainFrames; i++)
        {
            clock += WebRtcAudioPacer.PtimeSeconds;
            var drain = pacer.Tick();
            Assert.Equal(PacerOutcome.Frame, drain.Outcome);
            Assert.All(drain.Samples, s => Assert.Equal(0, s));
        }

        // 排空完毕：等数据（对应上游 await），时钟冻结。
        clock += 10.0;
        Assert.Equal(PacerOutcome.NeedsMoreData, pacer.Tick().Outcome);

        // 数据到来：追赶帧数用 ceil（上游 math.ceil），落在第 507 帧边界内 → 先睡到截止期。
        pacer.Write(Sine(WebRtcAudioPacer.FrameSamples * 2));
        var pending = pacer.Tick();
        Assert.Equal(PacerOutcome.Sleep, pending.Outcome);
        clock += pending.SleepSeconds + 0.005;

        var step = pacer.Tick();
        Assert.Equal(PacerOutcome.Frame, step.Outcome);
        Assert.Equal(507u * WebRtcAudioPacer.FrameSamples, step.Timestamp);
    }

    [Fact]
    public void Pacer_Clear_AdvancesGeneration_AndInFlightFrameIsDropped()
    {
        var clock = 100.0;
        var pacer = new WebRtcAudioPacer(() => clock);
        pacer.Write(Sine(WebRtcAudioPacer.FrameSamples * 2));

        var inFlight = pacer.Tick();
        Assert.Equal(PacerOutcome.Frame, inFlight.Outcome);

        pacer.Clear();

        // 模拟发送侧：编码时才发现 clear → 代际不匹配，帧必须丢弃。
        var step = pacer.Tick();
        Assert.NotEqual(inFlight.Generation, step.Generation);
        Assert.NotEqual(PacerOutcome.Frame, step.Outcome);
        Assert.Equal(0, pacer.BufferedSamples);
    }

    [Fact]
    public void Pacer_PartialFrame_IsZeroPaddedToFullFrame()
    {
        var clock = 100.0;
        var pacer = new WebRtcAudioPacer(() => clock);

        // 首帧即时发出：860 样本不足一帧 → 尾部补零成整帧（上游首帧同口径）。
        pacer.Write(Sine(WebRtcAudioPacer.FrameSamples - 100));
        var first = pacer.Tick();
        Assert.Equal(PacerOutcome.Frame, first.Outcome);
        Assert.Equal(0u, first.Timestamp);
        Assert.Equal(WebRtcAudioPacer.FrameSamples, first.Samples.Length);
        Assert.NotEqual(0, first.Samples[859]);
        Assert.Equal(0, first.Samples[860]);

        // 截止期到达时缓冲只剩 100 样本：取走补零，不等凑满（上游 recv() 无整帧门槛）。
        pacer.Write(Sine(100));
        clock += WebRtcAudioPacer.PtimeSeconds;
        var second = pacer.Tick();
        Assert.Equal(PacerOutcome.Frame, second.Outcome);
        Assert.Equal((uint)WebRtcAudioPacer.FrameSamples, second.Timestamp);
        Assert.Equal(WebRtcAudioPacer.FrameSamples, second.Samples.Length);
        Assert.NotEqual(0, second.Samples[99]);
        Assert.Equal(0, second.Samples[100]);
    }

    [Fact]
    public void Resampler_ChunkedFeed_MatchesSingleShot()
    {
        var whole = new WebRtcResampler(16_000, 48_000);
        var chunked = new WebRtcResampler(16_000, 48_000);

        var input = Sine(16_000, 4000);
        var expected = whole.Resample(input, 1);

        var accumulated = new List<short>();
        for (var offset = 0; offset < input.Length; offset += 160)
        {
            var count = Math.Min(160, input.Length - offset);
            accumulated.AddRange(chunked.Resample(input.AsSpan(offset, count).ToArray(), 1));
        }

        Assert.Equal(expected.Length, accumulated.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], accumulated[i]);
        }
    }

    [Fact]
    public void Resampler_DownmixStereoToMono_AveragesChannels()
    {
        var resampler = new WebRtcResampler(48_000, 48_000);
        var interleaved = new short[] { 100, 200, -300, 400 };
        var mono = resampler.Resample(interleaved, 2);

        Assert.Equal(new short[] { 150, 50 }, mono);
    }

    [Fact]
    public void Resampler_Reset_ClearsCursorState()
    {
        var resampler = new WebRtcResampler(48_000, 16_000);
        resampler.Resample(Sine(48_000), 1);
        resampler.Reset(48_000, 16_000);

        var out1 = resampler.Resample(Sine(48_000), 1);
        Assert.Equal(16_000, out1.Length);
    }
}
