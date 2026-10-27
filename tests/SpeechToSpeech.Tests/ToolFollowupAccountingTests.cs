using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Json;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Pipeline.Handlers;
using SpeechToSpeech.Core.Runtime;
using Xunit;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 工具链的两本账：<c>s2s_tool_input_call_ids</c>（哪些工具结果真的到了模型）
/// 与 <c>vad_settle_s</c>（整段输入闸门的等待）。
///
/// 这一组的共同点是「值算对了但没接上线」——D021 已经吃过一次这个教训：
/// 状态机的判定写在内存里，没有任何阶段去等它。所以每条断言都盯着**末端那个字段**，
/// 而不是中间那个函数灵不灵。
/// </summary>
public sealed class ToolFollowupAccountingTests
{
    // ── 工具输入 id 的渲染口径 ────────────────────────────────────────

    [Fact]
    public void AsciiJsonDumps_MatchesPythonJsonDumps()
    {
        // 期望值由 CPython 直接给出：
        //   json.dumps(['a"b\\c', 'd\x01e', 'ä', '😀', '\x7f', '/'])
        // 三处容易写错的地方：
        //   - 非 ASCII **要**转义（json.dumps 默认 ensure_ascii=True，与线路载荷相反）
        //   - 十六进制小写：\u00e4 而不是 \u00E4
        //   - 0x7F（DEL）也在转义范围内，界是「可打印 ASCII」而不是「控制字符」
        Assert.Equal("\"d\\u0001e\"", AsciiJsonDumps.String("d\u0001e"));
        Assert.Equal("\"\\u00e4\"", AsciiJsonDumps.String("ä"));
        Assert.Equal("\"\\ud83d\\ude00\"", AsciiJsonDumps.String("😀"));
        Assert.Equal("\"\\u007f\"", AsciiJsonDumps.String("\u007f"));
        Assert.Equal("\"/\"", AsciiJsonDumps.String("/"));
        Assert.Equal("\"a\\\"b\\\\c\"", AsciiJsonDumps.String("a\"b\\c"));
    }

    [Fact]
    public void AsciiJsonDumps_ArrayIsCompactAndOrderPreserving()
    {
        // separators=(",", ":")：逗号后不留空格。顺序由调用方给，渲染层不排序。
        Assert.Equal(
            "[\"call_b\",\"call_a\"]",
            AsciiJsonDumps.StringArray(["call_b", "call_a"]));
    }

    // ── 「哪些结果到了模型」的取数 ────────────────────────────────────

    [Fact]
    public void ToolOutputCallIds_DedupesAndSortsByCodepoint()
    {
        // 上游是 sorted({...})：先去重再按码点序。大小写敏感（call_Z 在 call_a 之前），
        // 用区域相关的比较会把顺序换掉，客户端按值比对就永远对不上。
        var chat = new ChatSession();
        chat.Append(ChatMessage.User("查一下天气"));
        chat.Append(ChatMessage.Assistant("", [new ToolCallRef { CallId = "call_b", Name = "weather" }]));
        chat.Append(ChatMessage.Tool("call_b", "下雨"));
        chat.Append(ChatMessage.Tool("call_Z", "另一个"));
        chat.Append(ChatMessage.Tool("call_b", "重复提交")); // 同一个 call_id 出现两次
        chat.Append(ChatMessage.Tool("call_a", "第三个"));

        Assert.Equal(["call_Z", "call_a", "call_b"], chat.ToolOutputCallIds());
    }

    [Fact]
    public void ToolOutputCallIds_IgnoresOutgoingToolCalls()
    {
        // assistant 消息里的 ToolCalls 是「模型发起的调用」，不是「到达模型的工具结果」。
        // 两者混淆会让服务端在还没拿到结果时就告诉客户端「结果已经进过模型」，
        // 客户端据此跳过重投——模型就永远等不到这个结果。
        var chat = new ChatSession();
        chat.Append(ChatMessage.Assistant("", [new ToolCallRef { CallId = "call_only_outgoing", Name = "x" }]));

        Assert.Empty(chat.ToolOutputCallIds());
    }

    // ── 事件链：历史 → EndOfResponse → done 事件 ──────────────────────

    [Fact]
    public void LanguageModel_SnapshotsToolOutputsOfTheRequestHistory()
    {
        // 「到了模型」的判据是**组装请求时的那份历史**，不是收尾那一刻的会话：
        // 生成期间客户端再追加的条目没有进过这次推理，报给客户端就是撒谎。
        var config = new RuntimeConfig();
        config.Chat.Append(ChatMessage.User("北京的天气"));
        config.Chat.Append(ChatMessage.Tool("call_late", "来了"));
        config.Chat.Append(ChatMessage.Tool("call_early", "更早的那个"));

        var end = RunLanguageModel(config);

        Assert.Equal(["call_early", "call_late"], end.InputToolCallIds);
    }

    [Fact]
    public void LmOutputProcessor_DropsToolInputsWhenTheTurnIsStale()
    {
        // 被作废的修订照常收口（否则客户端等不到 response.done），但不能说
        // 「这些结果已经到过模型」——那一轮的字压根没发出去。
        var tracker = new SpeculativeTurnTracker();
        var (staleTurn, staleRevision) = tracker.StartTurn();
        tracker.StartTurn(); // 用户接着说了下一句，上一轮作废

        var done = RunLmOutputProcessor(
            tracker,
            new EndOfResponse
            {
                ResponseKey = "resp_1",
                TurnId = staleTurn,
                TurnRevision = staleRevision,
                Status = ResponseStatus.Completed,
                InputToolCallIds = ["call_a"],
            });

        Assert.False(done.Succeeded);
        Assert.Empty(done.InputToolCallIds);
    }

    [Fact]
    public void LmOutputProcessor_KeepsToolInputsForALiveTurn()
    {
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();

        var done = RunLmOutputProcessor(
            tracker,
            new EndOfResponse
            {
                ResponseKey = "resp_1",
                TurnId = turnId,
                TurnRevision = revision,
                Status = ResponseStatus.Completed,
                InputToolCallIds = ["call_a", "call_b"],
            });

        Assert.True(done.Succeeded);
        Assert.Equal(["call_a", "call_b"], done.InputToolCallIds);
    }

    // ── E2E 名额的关闭规则 ───────────────────────────────────────────

    [Theory]
    [InlineData("completed", 0, true)]
    [InlineData("completed", 1, false)]
    [InlineData("cancelled", 0, true)]
    [InlineData("cancelled", 2, true)]
    [InlineData("incomplete", 0, true)]
    public void SpeechAnchor_KeepsTheSlotOpenOnlyForToolFollowups(
        string status,
        int pendingFunctionCalls,
        bool shouldClose)
    {
        // 上游一句表达式：status != completed or pending_function_calls。
        // 写反了只表现为「工具链第二段的 e2e_s 恒为 null」，不报错、不影响功能。
        Assert.Equal(shouldClose, SpeechLatencyAnchor.ShouldClose(status, pendingFunctionCalls));
    }

    // ── 输入闸门的等待：vad_settle_s ─────────────────────────────────

    [Fact]
    public void Stt_RecordsVadSettleWhenTheGateActuallyWaited()
    {
        // 端点判定说「没说完」时处理被推迟，输入闸门真的等了一段。这段等待就是
        // vad_settle_s 的全部含义——它与 hold_s 不是同一个量（后者的区间并集在输出侧）。
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();
        tracker.SegmentFinalized(audioMs: 1000, processingDelayMs: 120);

        var latency = new TurnLatencyStore();
        var produced = ProcessStt(
            NewSttHandler(tracker, latency),
            new VadAudio
            {
                Audio = [0.1f],
                SampleRate = 16_000,
                Mode = "final",
                TurnId = turnId,
                TurnRevision = revision,
            });

        Assert.Single(produced.OfType<Transcription>());
        var settle = latency.PendingForTurn(turnId, revision)?.VadSettleSeconds;
        Assert.NotNull(settle);
        // 只断言「至少等到了推迟量」：这条测接线，计时精度由 WaitForGateTests 负责。
        Assert.True(settle >= 0.1, $"实际等待 {settle}s，闸门没有真的挡住输入");
    }

    [Fact]
    public void Stt_LeavesVadSettleEmptyForNonFinalSegments()
    {
        // progressive 快照不是「这一轮等 VAD 收口」，它的等待不该记进这一格。
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();
        tracker.SegmentFinalized(audioMs: 1000, processingDelayMs: 120);

        var latency = new TurnLatencyStore();
        ProcessStt(
            NewSttHandler(tracker, latency),
            new VadAudio
            {
                Audio = [0.1f],
                SampleRate = 16_000,
                Mode = "progressive",
                TurnId = turnId,
                TurnRevision = revision,
            });

        Assert.Null(latency.PendingForTurn(turnId, revision)?.VadSettleSeconds);
    }

    // ── 空历史口径（D026） ───────────────────────────────────────────

    [Fact]
    public void LanguageModel_GeneratesFromBareInstructionsOnEmptyHistory()
    {
        // 上游没有「空会话」守卫：_apply_instructions 只要 instructions 非空就把
        // system 提示拼进聊天，模型对纯 system 上下文照常生成。新连接还没说话
        // 就 response.create 是合法用法，不能一刀切报「无上下文」。
        var config = new RuntimeConfig(session: new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "realtime",
            ["instructions"] = "你是测试助手",
        });

        var handler = new LanguageModelHandler(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new OneWordLlm(),
            new CancelScope(),
            new SpeculativeTurnTracker(),
            new TurnLatencyStore());

        var produced = InvokeProtected<GenerateResponseRequest, PipelineMessage>(
            handler, GenerateResponseRequest.Create(config, responseKey: "resp_1"));

        // 走到生成：有文本块出来，收口无错误。
        Assert.NotEmpty(produced.OfType<LlmResponseChunk>());
        Assert.Null(Assert.Single(produced.OfType<EndOfResponse>()).Error);
    }

    [Fact]
    public void LanguageModel_StillRejectsContextWithNeitherHistoryNorInstructions()
    {
        // 既无 instructions 也无历史无音频，真的没有可生成的上下文——保留守卫。
        var end = RunLanguageModel(new RuntimeConfig());
        Assert.Equal("no conversation context to respond to", end.Error);
    }

    // ── 夹具 ────────────────────────────────────────────────────────

    private static EndOfResponse RunLanguageModel(RuntimeConfig config)
    {
        var handler = new LanguageModelHandler(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new OneWordLlm(),
            new CancelScope(),
            new SpeculativeTurnTracker(),
            new TurnLatencyStore());

        var produced = InvokeProtected<GenerateResponseRequest, PipelineMessage>(
            handler,
            GenerateResponseRequest.Create(config, responseKey: "resp_1"));

        return Assert.Single(produced.OfType<EndOfResponse>());
    }

    private static AssistantResponseDoneEvent RunLmOutputProcessor(
        SpeculativeTurnTracker tracker,
        EndOfResponse end)
    {
        var handler = new LmOutputProcessor(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new CancelScope(),
            tracker);

        var produced = InvokeProtected<object, object>(handler, end);

        return Assert.Single(produced.OfType<AssistantResponseDoneEvent>());
    }

    private static SttHandler NewSttHandler(SpeculativeTurnTracker tracker, TurnLatencyStore latency)
        => new(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new FixedStt(),
            new CancelScope(),
            tracker,
            latency);

    private static List<PipelineMessage> ProcessStt(SttHandler handler, VadAudio input)
        => InvokeProtected<VadAudio, PipelineMessage>(handler, input);

    private static List<TOut> InvokeProtected<TIn, TOut>(object handler, TIn input)
    {
        var process = handler.GetType().GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{handler.GetType().Name}.Process 不存在");

        return [.. (IEnumerable<TOut>)process.Invoke(handler, [input])!];
    }

    private sealed class OneWordLlm : ILanguageModelBackend
    {
        public string Name => "one-word";

        public async IAsyncEnumerable<LlmResponseChunk> GenerateAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new LlmResponseChunk { Text = "好的" };
        }

        public void Dispose()
        {
        }
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
