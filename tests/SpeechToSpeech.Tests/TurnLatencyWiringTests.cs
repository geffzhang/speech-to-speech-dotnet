using System.Reflection;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Pipeline.Handlers;
using SpeechToSpeech.Core.Runtime;
using SpeechToSpeech.Realtime.Protocol;
using Xunit;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 时延账的接线：四项测量点（<c>stt_s</c> / <c>vad_decision_s</c> / <c>smart_status</c> / <c>hold_s</c>）
/// 与「投机宽限真的挡住输出」。
///
/// 这一层的缺陷有个共同点：**不报错**。字段漏写只是 metadata 里那一格恒为 null；
/// 闸门漏接只是回复提前放出去了。所以每条都得有断言盯着具体值。
/// </summary>
public sealed class TurnLatencyWiringTests
{
    [Fact]
    public void VadDecision_CountsTrailingSilenceAsDecisionLatency()
    {
        // 场景：10.0s 收到最后一块音频，块里最后一个有声样本在第 4000 个（共 8000 个样本被缓冲）。
        // 尾部 4000 个样本 = 0.25s 静音，是「音频已在手、还不知道说完了」的那段。
        // 10.2s 下结论 → vad_decision_s 应为 0.45s，而不是 0.2s。
        var seconds = VadDecisionTiming.Decide(
            chunkReceivedAtSeconds: 10.0,
            decidedAtSeconds: 10.2,
            totalSamples: 8000,
            speechEndSample: 4000,
            sampleRate: 16_000);

        Assert.Equal(0.45, seconds!.Value, 6);
    }

    [Fact]
    public void VadDecision_ClampsWhenBackendReportsSampleAheadOfBuffer()
    {
        // 后端给了比已缓冲还靠后的位点：钳到 0 而不是让估计出的「语音结束」跑到未来。
        var seconds = VadDecisionTiming.Decide(10.0, 10.2, totalSamples: 100, speechEndSample: 900, 16_000);

        Assert.Equal(0.2, seconds!.Value, 6);
    }

    [Theory]
    [InlineData(null, 4000)]
    [InlineData(10.0, null)]
    public void VadDecision_IsNullWhenAnAnchorIsMissing(double? receivedAt, int? speechEndSample)
    {
        // 上游让这一格空着而不是记 0：0 会在时延分布里混进假样本。
        Assert.Null(VadDecisionTiming.Decide(receivedAt, 10.2, 8000, speechEndSample, 16_000));
    }

    [Fact]
    public void VadDecision_NeverGoesNegative()
    {
        // 收块时刻晚于下结论时刻（时钟被替换过、或帧是上一块产生的）→ 记 0 而不是负数。
        var seconds = VadDecisionTiming.Decide(10.0, 9.0, 8000, 4000, 16_000);

        Assert.Equal(0.0, seconds!.Value, 6);
    }

    [Fact]
    public void SegmentFinalized_WritesAllFourFields()
    {
        var tracker = new TurnLatencyTracker();
        var decision = new TurnEndDecision(
            GraceMs: 2000,
            ProcessingDelayMs: 600,
            Status: TurnEndStatus.Incomplete,
            AnalysisSeconds: 0.031);

        tracker.RecordSegmentFinalized(0.45, decision);

        Assert.Equal(0.45, tracker.VadDecisionSeconds!.Value, 6);
        Assert.Equal("incomplete", tracker.SmartTurnStatus);
        Assert.Equal(2.0, tracker.SmartTurnGraceSeconds!.Value, 6);
        Assert.Equal(0.6, tracker.SmartTurnProcessingDelaySeconds!.Value, 6);
        Assert.Equal(0.031, tracker.SmartTurnAnalysisSeconds!.Value, 6);

        // 计时从定稿这一刻归零，之后由真实的闸门等待累加。
        Assert.Equal(0.0, tracker.SmartTurnWaitSeconds!.Value, 6);
    }

    [Fact]
    public void SegmentFinalized_DisabledClassifierLeavesWaitUnset()
    {
        // 没有判定器时不能写「Smart Turn 宽限 = 0.8」：那会让时延分解里凭空多出一段
        // 并不存在的判定等待。上游同样只写 status 与 analysis（后者为 null）。
        var tracker = new TurnLatencyTracker();
        var decision = new TurnEndDecision(800, 0, TurnEndStatus.Disabled, null);

        tracker.RecordSegmentFinalized(0.45, decision);

        Assert.Equal("disabled", tracker.SmartTurnStatus);
        Assert.Null(tracker.SmartTurnGraceSeconds);
        Assert.Null(tracker.SmartTurnProcessingDelaySeconds);
        Assert.Null(tracker.SmartTurnWaitSeconds);
        Assert.Null(tracker.SmartTurnAnalysisSeconds);

        // 但 vad_decision_s 与判定器无关，照记。
        Assert.Equal(0.45, tracker.VadDecisionSeconds!.Value, 6);
    }

    [Fact]
    public void StaleChunk_IsNotEmitted()
    {
        // 用户换气后接着说，旧修订被取代：模型那边可能已经在跑了，但一个字都不该发出去。
        // 少了这道闸门，状态机算得再准也只是「记了个期限」，陈旧轮次照样抢答。
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();
        tracker.SegmentFinalized(1000, outputHoldMs: 30_000);

        // 用户说了下一句 → 上面那个修订彻底陈旧
        tracker.StartTurn();

        Assert.Empty(Invoke(Processor(tracker), Chunk(turnId, revision)));
    }

    [Fact]
    public void Chunk_IsHeldUntilGraceElapses_ThenPasses()
    {
        // 只有一个在跑的修订时，输出必须被压住一段再放行。
        // 没有「真的等过」这条断言，闸门写成直接放行也能过。
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();
        tracker.SegmentFinalized(1000, outputHoldMs: 200);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var events = Invoke(Processor(tracker), Chunk(turnId, revision));
        stopwatch.Stop();

        Assert.NotEmpty(events.OfType<AssistantOutputEvent>());
        Assert.True(stopwatch.ElapsedMilliseconds >= 120, $"只等了 {stopwatch.ElapsedMilliseconds}ms，宽限没生效");
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"等了 {stopwatch.ElapsedMilliseconds}ms，宽限没释放");
    }

    [Fact]
    public void StaleEndOfResponse_StillClosesButIsMarkedUncommitted()
    {
        // 丢掉收尾会让客户端永远等一个不会来的 response.done，所以照常收口；
        // 但这一轮的字没发出去，不能被写进聊天历史。
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();
        tracker.SegmentFinalized(1000, outputHoldMs: 30_000);
        tracker.StartTurn();

        var end = new EndOfResponse
        {
            Status = ResponseStatus.Completed,
            ResponseKey = "resp_1",
            TurnId = turnId,
            TurnRevision = revision,
        };

        var events = Invoke(Processor(tracker), end);

        var done = Assert.Single(events.OfType<AssistantResponseDoneEvent>());
        Assert.False(done.Succeeded);
        Assert.Equal(ResponseStatus.Completed, done.Status);

        // 原事件不往下递：下游会把它当成管线的正常结束。
        Assert.Empty(events.OfType<EndOfResponse>());
    }

    [Fact]
    public void CurrentEndOfResponse_IsMarkedCommitted()
    {
        var tracker = new SpeculativeTurnTracker();
        var end = new EndOfResponse { Status = ResponseStatus.Completed, ResponseKey = "resp_1" };

        var events = Invoke(Processor(tracker), end);

        var done = Assert.Single(events.OfType<AssistantResponseDoneEvent>());
        Assert.True(done.Succeeded);
        Assert.Single(events.OfType<EndOfResponse>());
    }

    [Fact]
    public void MetadataPayload_EndToEnd_CarriesTheWiredValues()
    {
        // 接线对不对，最终只能看 metadata 里那段字符串。只断言「对象里有这个键」
        // 挡不住浮点渲染与 null 处理——客户端读到的是字符串。
        var tracker = new TurnLatencyTracker
        {
            TurnId = "turn_1",
            TurnRevision = 0,
            Source = "speech",
            SttSeconds = 0.18,
        };
        tracker.RecordSegmentFinalized(0.45, new TurnEndDecision(800, 0, TurnEndStatus.Complete, 0.02));
        tracker.RecordSmartWait(10.0, 10.4);
        tracker.RecordLlm(0.24);

        var json = tracker.MetadataPayloadJson("resp_1", "completed");
        var payload = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(0.18, payload["stt_s"]!.GetValue<double>(), 6);
        Assert.Equal(0.45, payload["vad_decision_s"]!.GetValue<double>(), 6);
        Assert.Equal("complete", payload["smart_status"]!.GetValue<string>());
        Assert.Equal(0.4, payload["hold_s"]!.GetValue<double>(), 6);

        // 键按字母序：metadata 里的字符串要和 json.dumps(sort_keys=True) 逐字符相同。
        var keys = payload.Select(pair => pair.Key).ToArray();
        Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal), keys);
    }

    // ---- 夹具 ----

    private static LmOutputProcessor Processor(SpeculativeTurnTracker tracker)
        => new(
            CancellationToken.None,
            new System.Collections.Concurrent.BlockingCollection<object>(),
            new System.Collections.Concurrent.BlockingCollection<object>(),
            new System.Collections.Concurrent.BlockingCollection<object>(),
            new CancelScope(),
            tracker,
            new SpeechToSpeech.Core.Text.AssistantLanguageProbe());

    private static LlmResponseChunk Chunk(string? turnId, int? revision) => new()
    {
        Parts = [new AssistantTextPart { Text = "Hello there." }],
        RuntimeConfig = new RuntimeConfig(),
        Response = new RealtimeResponseCreateParams(),
        TurnId = turnId,
        TurnRevision = revision,
    };

    private static List<object> Invoke(LmOutputProcessor processor, object input)
    {
        var process = typeof(LmOutputProcessor).GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LmOutputProcessor.Process 不存在");

        return [.. (IEnumerable<object>)process.Invoke(processor, [input])!];
    }
}
