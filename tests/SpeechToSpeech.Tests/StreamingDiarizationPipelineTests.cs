using System.Collections.Concurrent;
using SpeechToSpeech.Audio.Diarization;
using SpeechToSpeech.Backends.PyannoteDiarization;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Pipeline.Handlers;

namespace SpeechToSpeech.Tests;

/// <summary>
/// D009：流式说话人分离在管线里的那一级。
///
/// 这一级不做推理决策，它负责三件事，每件都对应一种只有接上客户端才看得见的错：
/// 模型慢不能把对话拖慢（队列满 ⇒ 时间轴缺口，而不是背压）、会话结束必须把尾巴冲出来
/// （否则最后几秒没有说话人）、非 16 kHz 输入必须重采样而不是静默关掉分离
/// （否则 24 kHz 的 WebRTC 路径上永远没有标签）。
/// </summary>
public sealed class DiarizationHandlerTests
{
    private const int WindowSamples = 3_200; // 200 ms
    private const int ChunkSamples = 320;    // 20 ms

    private static StreamingDiarizerConfig Config() => new()
    {
        SampleRate = DiarizationTone.SampleRate,
        WindowSamples = WindowSamples,
        HopSamples = 640,
        Onset = 0.5,
        Offset = 0.35,
        MinSilenceDuration = 0.04,
        MinEmbeddingDuration = 0.08,
        MinNewSpeakerEvidence = 0.15,
        ContinuityWindow = 0.3,
        MaxBufferedSeconds = 8,
    };

    private static DiarizationOptions Options(int queueCapacity = 64) => new()
    {
        Enabled = true,
        AudioQueueCapacity = queueCapacity,
        FlushTimeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>可以把它卡在推理里的分割引擎（模拟慢模型）。</summary>
    private sealed class GatedSegmentation : ISpeakerMaskSegmentation
    {
        private readonly ToneMaskSegmentation _inner = new(DiarizationHandlerTests.WindowSamples);

        public TaskCompletionSource? Gate { get; set; }

        public int Analyses;

        public int SampleRate => _inner.SampleRate;
        public int WindowSamples => _inner.WindowSamples;
        public int FrameShiftSamples => _inner.FrameShiftSamples;
        public int FrameOffsetSamples => _inner.FrameOffsetSamples;
        public int MaxLocalSpeakers => _inner.MaxLocalSpeakers;

        public SpeakerMaskWindow Analyze(ReadOnlySpan<float> window)
        {
            Interlocked.Increment(ref Analyses);
            Gate?.Task.Wait();
            return _inner.Analyze(window);
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly List<object> _seen = [];
        private readonly Thread _sink;
        private readonly Thread _thread;

        public Harness(ISpeakerMaskSegmentation segmentation, DiarizationOptions? options = null, int queueCapacity = 64)
        {
            Options = options ?? DiarizationHandlerTests.Options(queueCapacity);
            Timeline = new SpeakerTimeline();
            Handler = new DiarizationHandler(
                Cts.Token, In, Out, segmentation, new ToneEmbedding(), Config(), Timeline, Options);

            _thread = new Thread(Handler.Run) { IsBackground = true, Name = "diarization-test" };
            _thread.Start();

            _sink = new Thread(() =>
            {
                while (!Out.IsCompleted)
                {
                    object item;
                    try
                    {
                        if (!Out.TryTake(out item!, 20)) continue;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    lock (_seen) _seen.Add(item);
                }
            })
            { IsBackground = true, Name = "diarization-test-sink" };
            _sink.Start();
        }

        public BlockingCollection<object> In { get; } = new(new ConcurrentQueue<object>());

        public BlockingCollection<object> Out { get; } = new(new ConcurrentQueue<object>());

        public CancellationTokenSource Cts { get; } = new();

        public SpeakerTimeline Timeline { get; }

        public DiarizationHandler Handler { get; }

        public DiarizationOptions Options { get; }

        public object[] Seen
        {
            get
            {
                lock (_seen) return _seen.ToArray();
            }
        }

        public void Push(float[] samples, int sampleRate = DiarizationTone.SampleRate)
            => In.Add(new VadInput(samples, sampleRate));

        public void WaitFor(Func<object[], bool> condition, string because)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (condition(Seen)) return;
                Thread.Sleep(5);
            }

            throw new TimeoutException($"条件未满足：{because}");
        }

        public void Dispose()
        {
            Cts.Cancel();
            In.Add(PipelineSentinels.PipelineEnd);
            _thread.Join(TimeSpan.FromSeconds(5));
            _sink.Join(TimeSpan.FromSeconds(2));
            Handler.Dispose();
        }
    }

    [Fact]
    public void Audio_is_forwarded_untouched_and_drives_the_clock()
    {
        using var harness = new Harness(new ToneMaskSegmentation(WindowSamples));

        for (int i = 0; i < 5; i++) harness.Push(DiarizationTone.Build(0.02, (0.0, 0.02, 0.5, 0.0)));

        harness.WaitFor(seen => seen.OfType<VadInput>().Count() == 5, "五块音频都转发出去");
        Assert.Equal(5L * ChunkSamples, harness.Handler.SamplesSeen);
    }

    [Fact]
    public void The_tail_is_flushed_and_attributable_at_session_end()
    {
        using var harness = new Harness(new ToneMaskSegmentation(WindowSamples));

        var audio = DiarizationTone.Build(1.5, (0.0, 1.5, 0.5, 0.0));
        for (int offset = 0; offset < audio.Length; offset += ChunkSamples)
            harness.Push(audio.AsSpan(offset, Math.Min(ChunkSamples, audio.Length - offset)).ToArray());

        // 会话结束会先把尾巴冲出来再往下传，所以这条控制消息本身就是「已经冲完」的信号。
        harness.In.Add(PipelineControl.SessionEnd);
        harness.WaitFor(
            seen => seen.Any(item => PipelineControl.IsControlMessage(item, ControlKind.SessionEnd)),
            "会话结束消息被转发出去");

        var attribution = harness.Timeline.Attribute(0.2, 1.2);
        Assert.Equal(0, attribution.Speaker);
        Assert.True(attribution.Confidence > 0, "一整段单人语音应该能给出有信心度的标签");
    }

    // 「会话结束」与「开新会话」是两件事：前者只停止（已判定的段还要给转写归因），后者才清空并归零。
    // 混在一起曾经让尾句永远归因到 Unknown，而且是间歇性的——worker 的迟到写入会把时间线补回来一半。
    [Fact]
    public void Session_end_keeps_the_timeline_while_a_new_session_clears_it()
    {
        using var harness = new Harness(new ToneMaskSegmentation(WindowSamples));

        var audio = DiarizationTone.Build(1.5, (0.0, 1.5, 0.5, 0.0));
        for (int offset = 0; offset < audio.Length; offset += ChunkSamples)
            harness.Push(audio.AsSpan(offset, Math.Min(ChunkSamples, audio.Length - offset)).ToArray());

        harness.In.Add(PipelineControl.SessionEnd);
        harness.WaitFor(
            seen => seen.Any(item => PipelineControl.IsControlMessage(item, ControlKind.SessionEnd)),
            "会话结束消息被转发出去");

        Assert.NotEqual(0, harness.Timeline.Count);
        Assert.Equal(audio.Length, harness.Handler.SamplesSeen);

        harness.Handler.Reset();
        harness.WaitFor(_ => harness.Handler.SamplesSeen == 0, "新会话把分离器时钟归零");
        Assert.Equal(0, harness.Timeline.Count);
    }

    [Fact]
    public void A_stalled_worker_becomes_a_timeline_gap_not_backpressure()
    {
        var segmentation = new GatedSegmentation
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        // 队列深度必须装得下一个窗口（10 块 = 200 ms）：否则音频在形成窗口之前就被当缺口丢了，
        // worker 根本进不了推理——那是另一个（也正确的）行为，掩盖了本用例要验的东西。
        using var harness = new Harness(segmentation, queueCapacity: 16);

        // 一个窗口的音频足以让 worker 卡在推理里。
        var audio = DiarizationTone.Build(0.2, (0.0, 0.2, 0.5, 0.0));
        for (int offset = 0; offset < audio.Length; offset += ChunkSamples)
            harness.Push(audio.AsSpan(offset, Math.Min(ChunkSamples, audio.Length - offset)).ToArray());

        harness.WaitFor(seen => seen.OfType<VadInput>().Count() == 10, "十块音频都转发出去");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref segmentation.Analyses) == 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Assert.True(
            Volatile.Read(ref segmentation.Analyses) > 0,
            $"worker 应该已经在推理里（samplesSeen={harness.Handler.SamplesSeen} analyses={segmentation.Analyses}）");

        // 卡住期间数据循环照常收音频：队列满之后的每一块都记成缺口，绝不阻塞。
        for (int i = 0; i < 100; i++) harness.Push(DiarizationTone.Build(0.02, (0.0, 0.02, 0.5, 0.0)));

        // 转发没有被卡住——这是「分离慢不等于对话慢」的可观察证据。
        harness.WaitFor(seen => seen.OfType<VadInput>().Count() == 110, "110 块音频全部转发");
        Assert.True(harness.Handler.DroppedSamples > 0, "队列满必须被报成丢音频");

        segmentation.Gate!.SetResult();
        harness.In.Add(PipelineControl.SessionEnd);
        harness.WaitFor(
            seen => seen.Any(item => PipelineControl.IsControlMessage(item, ControlKind.SessionEnd)),
            "收尾完成");

        // 缺口是时间轴上的空白，不是「没人说话」：分离器看到的样本数按实际到达的算。
        Assert.Equal(110L * ChunkSamples, harness.Handler.SamplesSeen);
    }

    [Fact]
    public void A_24k_input_is_resampled_onto_the_16k_diarization_clock()
    {
        using var harness = new Harness(new ToneMaskSegmentation(WindowSamples));

        // 0.5 秒的 24 kHz 音频：只从 16 kHz 的设备来是做不到的，而 24 kHz 正是本构建 TTS 与
        // 部分 WebRTC 客户端送进来的采样率。
        var audio = new float[12_000];
        harness.Push(audio, sampleRate: 24_000);

        harness.WaitFor(seen => seen.OfType<VadInput>().Count() == 1, "这块音频被转发");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (harness.Handler.SamplesSeen == 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);

        Assert.Equal(8_000L, harness.Handler.SamplesSeen); // 0.5 s @16 kHz，不是 12 000
    }

    [Fact]
    public void Non_audio_items_pass_straight_through()
    {
        using var harness = new Harness(new ToneMaskSegmentation(WindowSamples));

        var message = new SpeechStartedEvent { AudioStartMs = 100 };
        harness.In.Add(message);

        harness.WaitFor(seen => seen.Contains(message), "非音频项原样转发");
    }
}

/// <summary>
/// 转写与段的区间 join（D009）。归因是「谁说的这句话」的全部实现：重叠最多者胜、按说话人累加、
/// 并列取先开口的、还没提交就借用紧邻前一位。
/// </summary>
public sealed class SpeakerTimelineTests
{
    private static SpeakerSegmentUpdate Segment(string id, int speaker, double start, double end, int revision = 0) =>
        new(id, speaker, start, end, IsFinal: true, Revision: revision, Confidence: 0.9);

    [Fact]
    public void The_speaker_covering_the_most_of_the_span_wins()
    {
        var timeline = new SpeakerTimeline();
        timeline.Apply(new DiarizerUpdate([Segment("a", 0, 0.0, 1.0), Segment("b", 1, 1.0, 2.0)], []));

        Assert.Equal(0, timeline.Attribute(0.2, 0.9).Speaker);
        Assert.Equal(1, timeline.Attribute(1.1, 1.9).Speaker);
        // 横跨换人：两点之间 0.9 秒是 0 号，0.1 秒是 1 号。
        Assert.Equal(0, timeline.Attribute(0.2, 1.1).Speaker);
    }

    [Fact]
    public void Overlap_is_summed_per_speaker()
    {
        var timeline = new SpeakerTimeline();
        // 0 号被切成两段（一次修订、或并进来的邻居），1 号是一整段：按段比会判错，
        // 按说话人累加才对——一个人说了 1.4 秒，另一个人说了 0.6 秒。
        timeline.Apply(new DiarizerUpdate(
            [Segment("a1", 0, 0.0, 0.7), Segment("a2", 0, 0.7, 1.4), Segment("b", 1, 1.2, 1.8)], []));

        Assert.Equal(0, timeline.Attribute(0.0, 1.5).Speaker);
    }

    [Fact]
    public void Ties_go_to_the_earlier_starting_segment()
    {
        var timeline = new SpeakerTimeline();
        timeline.Apply(new DiarizerUpdate([Segment("late", 1, 0.5, 1.5), Segment("early", 0, 0.0, 1.0)], []));

        // [0.5, 1.0] 被两段等量覆盖：先开口的那个说话人赢，规则确定，不看插入顺序。
        Assert.Equal(0, timeline.Attribute(0.6, 0.9).Speaker);
    }

    [Fact]
    public void A_span_beyond_the_committed_timeline_borrows_the_previous_speaker()
    {
        var timeline = new SpeakerTimeline(new SpeakerTimelineOptions { ContinuityWindow = 1.0 });
        timeline.Apply(new DiarizerUpdate([Segment("a", 1, 0.0, 1.0)], []));

        // 分离器落后一个前瞻：转写先到，标签借用紧挨着它前面说话的人。
        Assert.Equal(1, timeline.Attribute(1.3, 1.5).Speaker);
        // 超出连续性窗口就不猜了。
        Assert.Equal(SpeakerSegmentUpdate.UnknownSpeaker, timeline.Attribute(2.5, 2.7).Speaker);
    }

    [Fact]
    public void A_merge_remaps_every_tracked_segment()
    {
        var timeline = new SpeakerTimeline();
        timeline.Apply(new DiarizerUpdate([Segment("a", 2, 0.0, 1.0)], []));
        timeline.Apply(new DiarizerUpdate([], [new SpeakerMergeInfo(RetiredSpeaker: 2, SurvivingSpeaker: 0, AtSeconds: 1.0)]));

        Assert.Equal(0, timeline.Attribute(0.2, 0.8).Speaker);
    }

    [Fact]
    public void An_older_revision_does_not_roll_a_label_back()
    {
        var timeline = new SpeakerTimeline();
        timeline.Apply(new DiarizerUpdate([Segment("a", 1, 0.0, 1.0, revision: 2)], []));
        timeline.Apply(new DiarizerUpdate([Segment("a", 0, 0.0, 1.0, revision: 1)], []));

        Assert.Equal(1, timeline.Attribute(0.2, 0.8).Speaker);
    }

    [Fact]
    public void A_newer_revision_replaces_the_state_it_supersedes()
    {
        var timeline = new SpeakerTimeline();
        timeline.Apply(new DiarizerUpdate([Segment("a", 0, 0.0, 1.0, revision: 0)], []));
        // 续段：同一个 id，end 变长，speaker 在合并后换了。
        timeline.Apply(new DiarizerUpdate([Segment("a", 1, 0.0, 2.0, revision: 1)], []));

        Assert.Equal(1, timeline.Attribute(1.5, 1.9).Speaker);
    }

    [Fact]
    public void Tracking_is_bounded()
    {
        var timeline = new SpeakerTimeline(new SpeakerTimelineOptions { MaxTrackedSegments = 4 });
        for (int i = 0; i < 20; i++)
            timeline.Apply(new DiarizerUpdate([Segment($"s{i}", 0, i, i + 0.5)], []));

        Assert.Equal(4, timeline.Count);
        // 最旧的已经丢了，最近的在。
        Assert.Equal(SpeakerSegmentUpdate.UnknownSpeaker, timeline.Attribute(0.0, 0.4).Speaker);
        Assert.Equal(0, timeline.Attribute(19.1, 19.4).Speaker);
    }
}

/// <summary>
/// powerset 的说话人级解码（D009）：这是「不要过早压平」的落点。
/// 一帧两个非零项就是两个人在说；边缘和故意不为 1。
/// </summary>
public sealed class PowersetMaskDecoderTests
{
    // pyannote segmentation-3.0：3 个说话人、最多 2 并发 ⇒ 1 + 3 + 3 = 7 类。
    private static float[][] Mapping() => PowersetMaskDecoder.BuildPowersetMapping(numClasses: 7, numSpeakers: 3, powersetMaxClasses: 2);

    [Fact]
    public void The_silence_class_leaves_every_speaker_at_zero()
    {
        var probabilities = new float[3];
        PowersetMaskDecoder.FrameSpeakerProbabilities([40f, -40f, -40f, -40f, -40f, -40f, -40f], Mapping(), probabilities);

        Assert.All(probabilities, p => Assert.Equal(0d, p, 6));
    }

    [Fact]
    public void A_single_speaker_class_lights_only_that_speaker()
    {
        var probabilities = new float[3];
        // 2 号类 = 第二个单人档（0 → 1 → 2），只点亮说话人 1。
        PowersetMaskDecoder.FrameSpeakerProbabilities([-40f, -40f, 40f, -40f, -40f, -40f, -40f], Mapping(), probabilities);

        Assert.Equal(0d, probabilities[0], 6);
        Assert.Equal(1d, probabilities[1], 6);
        Assert.Equal(0d, probabilities[2], 6);
    }

    [Fact]
    public void A_pair_class_lights_both_of_its_speakers()
    {
        var probabilities = new float[3];
        // 第 5 类 = (0,2) 这一对：两个人同时为真，这正是「一帧上两个非零项＝重叠」。
        PowersetMaskDecoder.FrameSpeakerProbabilities([-40f, -40f, -40f, -40f, -40f, 40f, -40f], Mapping(), probabilities);

        Assert.True(probabilities[0] > 0.99, $"重叠的说话人 0 应该几乎全亮，实际 {probabilities[0]}");
        Assert.True(probabilities[2] > 0.99, $"重叠的说话人 2 应该几乎全亮，实际 {probabilities[2]}");
        Assert.Equal(0d, probabilities[1], 6);
    }

    [Fact]
    public void The_marginal_is_not_normalised_because_channels_are_independent_tracks()
    {
        var probabilities = new float[3];
        // 一个单人类与一个双人类各占一半：和是 1.5，不是 1——双人类同时给它的两个成员各一份。
        PowersetMaskDecoder.FrameSpeakerProbabilities([-40f, 0f, -40f, -40f, -40f, 0f, -40f], Mapping(), probabilities);

        Assert.Equal(1.5, probabilities.Sum(), 6);
    }

    [Fact]
    public void Width_mismatches_are_rejected()
    {
        var probabilities = new float[3];
        Assert.Throws<ArgumentException>(
            () => PowersetMaskDecoder.FrameSpeakerProbabilities([1f, 2f], Mapping(), probabilities));
        Assert.Throws<ArgumentException>(
            () => PowersetMaskDecoder.FrameSpeakerProbabilities([1f, 2f, 3f, 4f, 5f, 6f, 7f], Mapping(), new float[2]));
    }
}
