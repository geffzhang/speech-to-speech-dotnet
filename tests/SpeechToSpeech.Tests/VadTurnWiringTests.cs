using System.Collections.Concurrent;
using System.Reflection;
using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Pipeline.Handlers;
using SpeechToSpeech.Core.Runtime;

namespace SpeechToSpeech.Tests;

/// <summary>
/// VAD 阶段与轮次状态机的接合点。
///
/// 状态机本身与上游逐值一致（`corpus/turns`），但「什么时候调它」是另一回事——
/// 接错时机不会报错，只会退化成回复卡住或停顿即两轮，而且从日志里看不出来。
/// 这里用一个按脚本吐帧的假后端，锁住三处调用与它们的前后顺序。
/// </summary>
public sealed class VadTurnWiringTests
{
    /// <summary>按脚本吐帧的假 VAD：每次 Push 消费一个预置帧组。</summary>
    private sealed class ScriptedVad : IVadBackend
    {
        private readonly Queue<VadFrame[]> _script;

        public ScriptedVad(params VadFrame[][] script) => _script = new Queue<VadFrame[]>(script);

        public string Name => "scripted";

        public IEnumerable<VadFrame> Push(float[] samples, RuntimeConfig runtimeConfig)
            => _script.Count > 0 ? _script.Dequeue() : [];

        public void ResetSession()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed record Harness(
        VadHandler Handler,
        SpeculativeTurnTracker Tracker,
        BlockingCollection<object> AudioIn,
        BlockingCollection<object> AudioOut,
        BlockingCollection<object> TextOut);

    private static Harness Build(
        ScriptedVad vad,
        int reusableReopenMs = 800,
        int unansweredReopenMs = 7000,
        ITurnEndClassifier? classifier = null,
        int smartTurnMaxWaitMs = 2000,
        int incompleteDelayMs = 600,
        SpeculativeTurnTracker? tracker = null,
        TurnLatencyStore? latency = null)
    {
        tracker ??= new SpeculativeTurnTracker();
        var audioIn = new BlockingCollection<object>(new ConcurrentQueue<object>(), 8);
        var audioOut = new BlockingCollection<object>(new ConcurrentQueue<object>(), 8);
        var textOut = new BlockingCollection<object>(new ConcurrentQueue<object>(), 32);
        var handler = new VadHandler(
            CancellationToken.None,
            audioIn,
            audioOut,
            textOut,
            vad,
            new CancelScope(),
            tracker,
            new TurnEndOptions
            {
                Enabled = classifier is not null,
                SpeculativeReopenMs = reusableReopenMs,
                UnansweredReopenMs = unansweredReopenMs,
                MaxWaitMs = smartTurnMaxWaitMs,
                IncompleteDelayMs = incompleteDelayMs,
            },
            classifier,
            log: null,
            latency: latency);
        return new Harness(handler, tracker, audioIn, audioOut, textOut);
    }

    /// <summary>直接调 <c>Process</c>：Run 的线程模型会把断言变成竞态，而这里要测的是帧处理本身。</summary>
    private static (List<object> Downstream, List<object> Text) Pump(Harness harness, int chunks)
    {
        var process = typeof(VadHandler).GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("VadHandler.Process 不存在");

        var downstream = new List<object>();
        var chunk = new float[AudioUtil.VadChunkSize];
        for (var i = 0; i < chunks; i++)
        {
            var iterator = (IEnumerable<object>)process.Invoke(harness.Handler, [chunk])!;
            downstream.AddRange(iterator);
        }

        var text = new List<object>();
        while (harness.TextOut.TryTake(out var item))
        {
            text.Add(item);
        }

        return (downstream, text);
    }

    [Fact]
    public void SpeechStarted_DispatchesTurnFromTracker()
    {
        var vad = new ScriptedVad([new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 100)]);
        var harness = Build(vad);

        var (_, text) = Pump(harness, chunks: 1);

        var started = Assert.IsType<SpeechStartedEvent>(Assert.Single(text));
        Assert.Equal("turn_1", started.TurnId);
        Assert.Equal(0, started.TurnRevision);
        Assert.False(started.Reopened);
        Assert.Equal(TurnPhase.Listening, harness.Tracker.Phase);
    }

    [Fact]
    public void SpeechStopped_FinalizesSegmentAndHoldsOutputForGrace()
    {
        var vad = new ScriptedVad(
            [new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 0)],
            [
                new VadFrame(
                    VadFrameKind.SpeechStopped,
                    Audio: [0.1f, 0.2f],
                    AudioEndMs: 1200,
                    DurationSeconds: 1.2),
            ]);
        var harness = Build(vad);

        var (downstream, _) = Pump(harness, chunks: 2);

        var audio = Assert.IsType<VadAudio>(Assert.Single(downstream));
        Assert.Equal("turn_1", audio.TurnId);
        Assert.Equal(2, audio.Audio.Length);

        // 定稿之后输出先被压住：宽限期结束前提交会把「用户只是换气」判成「说完了」
        Assert.Equal(TurnPhase.SoftEnded, harness.Tracker.Phase);
        var gate = harness.Tracker.Gate(audio.TurnId, audio.TurnRevision);
        Assert.Equal(TurnGateAction.Hold, gate.Action);

        // 复开窗口必须能容纳宽限本身
        Assert.True(harness.Tracker.CanReopen(1200));
    }

    [Fact]
    public void EmptySegment_DiscardsInsteadOfFinalizing()
    {
        var vad = new ScriptedVad(
            [new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 0)],
            [new VadFrame(VadFrameKind.SpeechStopped, Audio: [], AudioEndMs: 900)]);
        var harness = Build(vad);

        var (downstream, _) = Pump(harness, chunks: 2);

        // 空段不放行：既不能进 STT，也不能留在「正在听」——否则下一句会被并进同一轮
        Assert.Empty(downstream);
        Assert.Equal(TurnPhase.SoftEnded, harness.Tracker.Phase);
        Assert.Equal(TurnGateAction.Accept, harness.Tracker.Gate("turn_1", 0).Action);
    }

    [Fact]
    public void ResumedSpeechWithinWindow_ReopensSameTurnWithNextRevision()
    {
        var vad = new ScriptedVad(
            [new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 0)],
            [
                new VadFrame(
                    VadFrameKind.SpeechStopped,
                    Audio: [0.1f],
                    AudioEndMs: 1000,
                    DurationSeconds: 1.0),
            ],
            [new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 1500)]);
        var harness = Build(vad);

        var (_, text) = Pump(harness, chunks: 3);

        var starts = text.OfType<SpeechStartedEvent>().ToList();
        Assert.Equal(2, starts.Count);

        // 同一轮、下一个修订：客户端看到的仍是同一条输入项，这是「停顿」与「又说一句」的差别
        Assert.Equal("turn_1", starts[1].TurnId);
        Assert.Equal(1, starts[1].TurnRevision);
        Assert.True(starts[1].Reopened);
    }

    [Fact]
    public void SpeechAfterWindow_StartsNewTurn()
    {
        var vad = new ScriptedVad(
            [new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 0)],
            [
                new VadFrame(
                    VadFrameKind.SpeechStopped,
                    Audio: [0.1f],
                    AudioEndMs: 1000,
                    DurationSeconds: 1.0),
            ],
            [new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 9000)]);
        var harness = Build(vad, reusableReopenMs: 300, unansweredReopenMs: 500);

        var (_, text) = Pump(harness, chunks: 3);

        var starts = text.OfType<SpeechStartedEvent>().ToList();
        Assert.Equal(2, starts.Count);
        Assert.Equal("turn_2", starts[1].TurnId);
        Assert.Equal(0, starts[1].TurnRevision);
        Assert.False(starts[1].Reopened);
    }

    [Fact]
    public void ReopenWindow_CoversGraceEvenWhenGraceExceedsConfiguredWindow()
    {
        // 宽限比可复开上限还长时，窗口取两者较大者——否则宽限还没过就判成不能复开
        var vad = new ScriptedVad([new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 0)]);
        var harness = Build(vad, reusableReopenMs: 900, unansweredReopenMs: 100);

        Pump(harness, chunks: 1);
        harness.Tracker.SegmentFinalized(audioMs: 1000);

        // 900ms 的宽限 + 1000ms 的音频位置：差 900 仍在上限内
        Assert.True(harness.Tracker.CanReopen(1900));
        Assert.False(harness.Tracker.CanReopen(1901));
    }

    // ── Smart Turn 接合点 ─────────────────────────────────────────────

    /// <summary>结论固定的判定器，顺带记下它收到的音频。</summary>
    private sealed class StubClassifier : ITurnEndClassifier
    {
        private readonly bool _complete;

        public StubClassifier(bool complete = true) => _complete = complete;

        public string Name => "stub";

        public List<float[]> Calls { get; } = [];

        public TurnEndPrediction Predict(ReadOnlySpan<float> audio, int sampleRate)
        {
            Calls.Add(audio.ToArray());
            return new TurnEndPrediction(_complete, _complete ? 0.9 : 0.1);
        }
    }

    /// <summary>可推进的虚拟时钟，用来把「宽限多久」「处理推迟多久」变成可断言的数字。</summary>
    private sealed class VirtualClock
    {
        public VirtualClock(double start) => Now = start;

        public double Now { get; set; }
    }

    [Fact]
    public void IncompleteJudgement_ExtendsGraceAndDelaysProcessing()
    {
        // 判定「没说完」时，两个期限都要跟着变。少改任何一个：宽限没变 → 回复提前放行；
        // 处理延迟没变 → STT/LLM 白跑一次随后被作废的修订。
        var clock = new VirtualClock(1000.0);
        var vad = new ScriptedVad(
            [new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 0)],
            [
                new VadFrame(
                    VadFrameKind.SpeechStopped,
                    Audio: [0.1f, 0.2f],
                    AudioEndMs: 1000,
                    DurationSeconds: 1.0),
            ]);
        var harness = Build(
            vad,
            classifier: new StubClassifier(complete: false),
            tracker: new SpeculativeTurnTracker(() => clock.Now));

        Pump(harness, chunks: 2);

        Assert.True(harness.Handler.LastTurnEndDecision.HasValue);
        Assert.Equal(TurnEndStatus.Incomplete, harness.Handler.LastTurnEndDecision!.Value.Status);

        // 宽限取 MaxWait 而不是配置的 800
        var gate = harness.Tracker.Gate("turn_1", 0);
        Assert.Equal(TurnGateAction.Hold, gate.Action);
        Assert.Equal(1002.0, gate.Deadline!.Value, 6);

        // 处理延迟独立存在：600ms 后才该开工
        Assert.Equal(1000.6, harness.Tracker.ProcessingDeadline("turn_1", 0)!.Value, 6);
    }

    [Fact]
    public void Classifier_SeesWholeTurnNotJustTheLastFragment()
    {
        // 用户分两次说完同一句时，第二次的判定要看整句。只喂后半截，
        // 模型看到的是一个孤零零的短语结尾，几乎必然判成「说完了」。
        var classifier = new StubClassifier();
        var vad = new ScriptedVad(
            [new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 0)],
            [new VadFrame(VadFrameKind.SpeechStopped, Audio: [0.1f, 0.2f], AudioEndMs: 1000)],
            [new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 1200)],
            [new VadFrame(VadFrameKind.SpeechStopped, Audio: [0.3f, 0.4f, 0.5f], AudioEndMs: 2000)]);
        var harness = Build(vad, classifier: classifier);

        Pump(harness, chunks: 4);

        Assert.Equal(2, classifier.Calls.Count);
        Assert.Equal(2, classifier.Calls[0].Length);
        Assert.Equal(5, classifier.Calls[1].Length);
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f }, classifier.Calls[1]);
    }

    [Fact]
    public void Classifier_AudioIsClearedWhenANewTurnStarts()
    {
        // 上一轮说过的话不能漏进下一轮的判定：复开是同一轮（要累积），开新轮不是。
        var classifier = new StubClassifier();
        var vad = new ScriptedVad(
            [new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 0)],
            [new VadFrame(VadFrameKind.SpeechStopped, Audio: [0.1f, 0.2f], AudioEndMs: 1000)],
            [new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 9000)],
            [new VadFrame(VadFrameKind.SpeechStopped, Audio: [0.7f], AudioEndMs: 9500)]);
        var harness = Build(
            vad,
            reusableReopenMs: 300,
            unansweredReopenMs: 500,
            classifier: classifier,
            smartTurnMaxWaitMs: 400);

        Pump(harness, chunks: 4);

        Assert.Equal(2, classifier.Calls.Count);
        Assert.Equal(new float[] { 0.7f }, classifier.Calls[1]);
    }

    [Fact]
    public void SmartTurnMaxWait_WidensReopenWindowEvenWhenOtherLimitsAreSmaller()
    {
        // 上游把 smart_turn_max_wait_ms 也算进可复开上限。否则判定刚说完「没说完」，
        // 复开窗口却已经关了——用户接着说的那几个字会被判成新的一轮。
        var vad = new ScriptedVad([new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 0)]);
        var harness = Build(
            vad,
            reusableReopenMs: 300,
            unansweredReopenMs: 500,
            classifier: new StubClassifier(),
            smartTurnMaxWaitMs: 2000);

        Pump(harness, chunks: 1);
        harness.Tracker.SegmentFinalized(audioMs: 1000, outputHoldMs: 300);

        Assert.True(harness.Tracker.CanReopen(3000));
        Assert.False(harness.Tracker.CanReopen(3001));
    }

    [Fact]
    public void NonPositiveMaxWait_IsRejectedAtConstruction()
    {
        // 0 或负的 MaxWait 会让「没说完」的宽限比「说完了」还短，语义倒置。
        // 上游在 __init__ 里就拒绝，不是等到第一次定稿才出怪现象。
        var audioIn = new BlockingCollection<object>(new ConcurrentQueue<object>(), 8);
        var audioOut = new BlockingCollection<object>(new ConcurrentQueue<object>(), 8);
        var textOut = new BlockingCollection<object>(new ConcurrentQueue<object>(), 32);

        var error = Assert.Throws<ArgumentException>(() => new VadHandler(
            CancellationToken.None,
            audioIn,
            audioOut,
            textOut,
            new ScriptedVad(),
            new CancelScope(),
            new SpeculativeTurnTracker(),
            new TurnEndOptions { MaxWaitMs = 0 }));

        Assert.Contains("smart_turn_max_wait_ms", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SegmentFinalized_RecordsVadDecisionAndSmartStatus()
    {
        // 段定稿时四项测量点是成组写的。漏了哪一项都不会报错，
        // 只表现为 metadata 里那一格恒为 null——所以这里逐项断言。
        var latency = new TurnLatencyStore();
        var vad = new ScriptedVad(
            [new VadFrame(VadFrameKind.SpeechStarted, AudioStartMs: 0)],
            [
                new VadFrame(
                    VadFrameKind.SpeechStopped,
                    Audio: [0.1f, 0.2f],
                    AudioEndMs: 1000,
                    DurationSeconds: 1.0,
                    // 最后一个有声样本在 8000 个样本处，之后是判定器自己的静音确认窗口
                    SpeechEndSample: 8000),
            ]);
        var harness = Build(vad, latency: latency);

        Pump(harness, chunks: 2);

        var tracker = latency.PendingForTurn("turn_1", 0);
        Assert.NotNull(tracker);

        // 判定器缺席时宽限退回普通投机窗口，status 记 disabled，三项 Smart Turn 不写
        Assert.Equal("disabled", tracker.SmartTurnStatus);
        Assert.Null(tracker.SmartTurnGraceSeconds);
        Assert.Null(tracker.SmartTurnWaitSeconds);

        // vad_decision_s 与判定器无关：从「最后一个有声窗」到定稿的真实间隔必须记下来。
        // 段尾时刻不能用——段尾含闭合静音，会把延迟算成 0。
        Assert.NotNull(tracker.VadDecisionSeconds);
        Assert.True(tracker.VadDecisionSeconds >= 0);
    }

    [Fact]
    public void Stt_RecordsDurationIntoPendingSlot()
    {
        // stt_s 挂在待定槽位上：转写完成时响应键通常还不存在，
        // 就地把测量丢掉会让这一格永久为 null。
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();
        var latency = new TurnLatencyStore();

        var handler = new SttHandler(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new FixedStt(),
            new CancelScope(),
            tracker,
            latency);

        var process = typeof(SttHandler).GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SttHandler.Process 不存在");

        var produced = ((IEnumerable<PipelineMessage>)process.Invoke(handler, [
            new VadAudio
            {
                Audio = [0.1f],
                SampleRate = 16_000,
                TurnId = turnId,
                TurnRevision = revision,
            },
        ])!).ToList();

        Assert.Single(produced.OfType<Transcription>());
        Assert.NotNull(latency.PendingForTurn(turnId, revision)?.SttSeconds);
    }

    [Fact]
    public void Stt_StaleTurnLeavesNoPendingEntry()
    {
        // 被作废的修订不会有响应来认领它的账目。不摘掉的话，待定槽位会一直挂着
        // 一条没人结算的记录——这是「慢性泄漏」，只会表现为内存慢慢涨。
        var tracker = new SpeculativeTurnTracker();
        var (staleTurn, staleRevision) = tracker.StartTurn();
        tracker.StartTurn(); // 用户说了下一句，上一轮作废

        var latency = new TurnLatencyStore();
        var handler = new SttHandler(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new FixedStt(),
            new CancelScope(),
            tracker,
            latency);

        var process = typeof(SttHandler).GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var produced = ((IEnumerable<PipelineMessage>)process.Invoke(handler, [
            new VadAudio
            {
                Audio = [0.1f],
                SampleRate = 16_000,
                TurnId = staleTurn,
                TurnRevision = staleRevision,
            },
        ])!).ToList();

        Assert.Empty(produced);
        Assert.Null(latency.PendingForTurn(staleTurn, staleRevision));
    }


    [Fact]
    public void Stt_WaitsForProcessingDelayBeforeTranscribing()
    {
        // 端点判定说「没说完」时，处理要推迟一段：用户接着说的那几个字
        // 应该在转写开工之前把这个修订作废。看一眼闸门就走的话，
        // STT 会白跑一次随后被丢弃的识别——处理延迟就成了纯装饰。
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();
        tracker.SegmentFinalized(audioMs: 1000, outputHoldMs: 800, processingDelayMs: 200);

        var handler = new SttHandler(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new FixedStt(),
            new CancelScope(),
            tracker);

        var process = typeof(SttHandler).GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var produced = ((IEnumerable<PipelineMessage>)process.Invoke(handler, [
            new VadAudio
            {
                Audio = [0.1f],
                SampleRate = 16_000,
                TurnId = turnId,
                TurnRevision = revision,
            },
        ])!).ToList();
        stopwatch.Stop();

        Assert.Single(produced.OfType<Transcription>());
        Assert.True(stopwatch.ElapsedMilliseconds >= 120, $"只等了 {stopwatch.ElapsedMilliseconds}ms，处理延迟没生效");
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"等了 {stopwatch.ElapsedMilliseconds}ms，处理延迟没释放");
    }

    [Fact]
    public void Stt_DoesNotWaitForTheOutputGrace()
    {
        // 宽限只压输出，不压输入。输入也压住的话，用户续说时新旧两段音频会互相等，
        // 谁也不先出转写——表现为「说话后要等两秒才开始识别」。
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();
        tracker.SegmentFinalized(audioMs: 1000, outputHoldMs: 3000);

        var handler = new SttHandler(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new FixedStt(),
            new CancelScope(),
            tracker);

        var process = typeof(SttHandler).GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _ = ((IEnumerable<PipelineMessage>)process.Invoke(handler, [
            new VadAudio
            {
                Audio = [0.1f],
                SampleRate = 16_000,
                TurnId = turnId,
                TurnRevision = revision,
            },
        ])!).ToList();
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"等了 {stopwatch.ElapsedMilliseconds}ms，宽限压到了输入");
    }

    private sealed class FixedStt : ISttBackend
    {
        public string Name => "fixed";

        public IEnumerable<PipelineMessage> Transcribe(VadAudio audio, CancellationToken cancellationToken)
        {
            yield return new Transcription
            {
                Text = "hello",
                TurnId = audio.TurnId,
                TurnRevision = audio.TurnRevision,
            };
        }

        public void Dispose()
        {
        }
    }
}
