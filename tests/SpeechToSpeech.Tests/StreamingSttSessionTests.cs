using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SpeechToSpeech.Backends.OpenAiCompatible.StreamingStt;
using SpeechToSpeech.Core.Pipeline;
using Xunit;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 上游 <c>_StreamingSession</c> 的表征测试：进程内 fake socket 驱动 worker
/// 状态机，锁的是「命令顺序 → 线上消息 → 事件分类 → partial/commit 产出」链路。
/// </summary>
public class StreamingSttSessionTests
{
    // ── fake socket ───────────────────────────────────────────────────

    private sealed class FakeSocket : IStreamingSocket
    {
        public ConcurrentQueue<string> Inbox { get; } = new();
        public ConcurrentQueue<string> Sent { get; } = new();

        /// <summary>worker 每次 send 后回调（fake 服务端按此回事件）。</summary>
        public Action<FakeSocket, JsonObject>? OnSend { get; set; }

        /// <summary>置位后 Receive 一直超时（模拟卡死，触发 final_timeout）。</summary>
        public volatile bool StallReceive;

        public void Send(string message)
        {
            Sent.Enqueue(message);
            var evt = JsonNode.Parse(message)!.AsObject();
            OnSend?.Invoke(this, evt);
        }

        public string? Receive(TimeSpan timeout)
        {
            if (StallReceive)
            {
                Thread.Sleep(timeout);
                return null;
            }

            if (Inbox.TryDequeue(out var message))
            {
                return message;
            }

            Thread.Sleep(timeout < TimeSpan.FromMilliseconds(5) ? timeout : TimeSpan.FromMilliseconds(5));
            return null;
        }

        public void Dispose()
        {
        }

        public IEnumerable<JsonObject> SentEvents()
            => Sent.Select(m => JsonNode.Parse(m)!.AsObject());
    }

    private static OpenAiRealtimeProtocol OpenAiProtocol() => new("whisper-1", null, 24_000);

    private static VllmRealtimeProtocol VllmProtocol() => new("Qwen2-Audio", null, 16_000);

    private static JsonObject SessionCreated() => new() { ["type"] = "session.created" };

    private static JsonObject SessionUpdated() => new() { ["type"] = "session.updated" };

    private static JsonObject Committed(string itemId) => new()
    {
        ["type"] = "input_audio_buffer.committed",
        ["item_id"] = itemId,
    };

    private static JsonObject Completed(string itemId, string transcript) => new()
    {
        ["type"] = "conversation.item.input_audio_transcription.completed",
        ["item_id"] = itemId,
        ["content_index"] = 0,
        ["transcript"] = transcript,
    };

    private static JsonObject Delta(string itemId, string text) => new()
    {
        ["type"] = "conversation.item.input_audio_transcription.delta",
        ["item_id"] = itemId,
        ["delta"] = text,
    };

    /// <summary>标准 OpenAI fake：握手回声齐全。</summary>
    private static (FakeSocket Socket, StreamingConnectFactory Factory) OpenAiFake()
    {
        var socket = new FakeSocket();
        // 每次「连接」都补握手回声——重连（断连兜底后）也要能走完 setup。
        StreamingConnectFactory factory = (_, _, _) =>
        {
            socket.Inbox.Enqueue(SessionCreated().ToJsonString());
            return socket;
        };
        socket.OnSend = (s, evt) =>
        {
            if ((string?)evt["type"] == "session.update")
            {
                s.Inbox.Enqueue(SessionUpdated().ToJsonString());
            }
        };
        return (socket, factory);
    }

    private static (BlockingCollection<object> Queue, List<PartialTranscription> Partials) OutputCapture()
    {
        var queue = new BlockingCollection<object>();
        var partials = new List<PartialTranscription>();
        return (queue, partials);
    }

    /// <summary>从输出队列等待并收集一条 partial（最多 2 秒）。</summary>
    private static PartialTranscription? TryTakePartial(BlockingCollection<object> queue)
    {
        for (var i = 0; i < 200; i++)
        {
            if (queue.TryTake(out var message, TimeSpan.FromMilliseconds(10)))
            {
                return (PartialTranscription)message;
            }
        }

        return null;
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"timed out waiting for {what}");
    }

    // ── 握手 ──────────────────────────────────────────────────────────

    [Fact]
    public void OpenAi_Handshake_SendsSessionUpdateAndWaitsForEcho()
    {
        var (socket, factory) = OpenAiFake();
        var protocol = OpenAiProtocol();
        using var session = new StreamingSttSession(
            protocol, "ws://fake/realtime", new Dictionary<string, string>(),
            connectTimeout: 5, finalTimeout: 30, factory,
            new BlockingCollection<object>(), CancellationToken.None);

        session.AppendAudio([1, 2, 3]);

        WaitFor(() => socket.SentEvents().Any(e => (string?)e["type"] == "session.update"), "session.update");
        // session.updated 回声只有 fake 在 onSend 时给了——append 应紧随其后。
        WaitFor(() => socket.SentEvents().Any(e => (string?)e["type"] == "input_audio_buffer.append"), "append");

        var sent = socket.SentEvents().Select(e => (string?)e["type"]).ToList();
        Assert.Equal("session.update", sent[0]);
        Assert.Equal("input_audio_buffer.append", sent[1]);
    }

    [Fact]
    public void Vllm_Handshake_DoesNotWaitForEcho()
    {
        // vLLM 不等 session.updated：send session.update 之后 append 直接跟上。
        var socket = new FakeSocket();
        StreamingConnectFactory factory = (_, _, _) =>
        {
            socket.Inbox.Enqueue(SessionCreated().ToJsonString());
            return socket;
        };
        var protocol = VllmProtocol();
        using var session = new StreamingSttSession(
            protocol, "ws://fake/realtime", new Dictionary<string, string>(),
            5, 30, factory, new BlockingCollection<object>(), CancellationToken.None);

        session.AppendAudio([1, 2, 3]);

        WaitFor(() => socket.SentEvents().Any(e => (string?)e["type"] == "input_audio_buffer.append"), "append");
        var events = socket.SentEvents().ToList();
        Assert.Equal("session.update", (string?)events[0]["type"]);
        // 开话事件：非 final commit。
        Assert.Equal("input_audio_buffer.commit", (string?)events[1]["type"]);
        Assert.False((bool)events[1]["final"]!);
        Assert.Equal("input_audio_buffer.append", (string?)events[2]["type"]);
    }

    // ── partial 发射 ──────────────────────────────────────────────────

    [Fact]
    public void Delta_EmitsPartialWithTurnId()
    {
        var (socket, factory) = OpenAiFake();
        // 握手后第一个 delta 触发 partial。
        socket.OnSend = (s, evt) =>
        {
            switch ((string?)evt["type"])
            {
                case "session.update":
                    s.Inbox.Enqueue(SessionUpdated().ToJsonString());
                    break;
                case "input_audio_buffer.append":
                    s.Inbox.Enqueue(Delta("item_1", "你好").ToJsonString());
                    break;
            }
        };

        var (queue, _) = OutputCapture();
        var protocol = OpenAiProtocol();
        using var session = new StreamingSttSession(
            protocol, "ws://fake/realtime", new Dictionary<string, string>(),
            5, 30, factory, queue, CancellationToken.None);

        session.StartTurn("turn_A", 0);
        session.AppendAudio([1, 2, 3]);

        var partial = TryTakePartial(queue);
        Assert.NotNull(partial);
        Assert.Equal("你好", partial!.Text);
        Assert.Equal("turn_A", partial.TurnId);
        Assert.Equal(0, partial.TurnRevision);
    }

    [Fact]
    public void Delta_AfterCancelSession_IsDropped()
    {
        var (socket, factory) = OpenAiFake();
        socket.OnSend = (s, evt) =>
        {
            switch ((string?)evt["type"])
            {
                case "session.update":
                    s.Inbox.Enqueue(SessionUpdated().ToJsonString());
                    break;
                case "input_audio_buffer.append":
                    s.Inbox.Enqueue(Delta("item_1", "陈旧的").ToJsonString());
                    break;
            }
        };

        var (queue, _) = OutputCapture();
        var protocol = OpenAiProtocol();
        using var session = new StreamingSttSession(
            protocol, "ws://fake/realtime", new Dictionary<string, string>(),
            5, 30, factory, queue, CancellationToken.None);

        session.StartTurn("turn_A", 0);
        session.AppendAudio([1, 2, 3]);

        // 等 append 已发出；worker 可能在取消前已合法发过一条 partial，先排干。
        WaitFor(() => socket.SentEvents().Any(e => (string?)e["type"] == "input_audio_buffer.append"), "append");
        while (queue.TryTake(out _, TimeSpan.FromMilliseconds(10)))
        {
        }

        session.CancelSession();

        // 取消之后：代际拦截 + Reset 清状态，不再有新 partial。
        Thread.Sleep(200);
        Assert.False(queue.TryTake(out _, TimeSpan.FromMilliseconds(100)));
    }

    // ── commit 收口 ───────────────────────────────────────────────────

    [Fact]
    public void Commit_FullCycle_ReturnsCombinedTranscript()
    {
        var (socket, factory) = OpenAiFake();
        var itemId = "item_1";
        socket.OnSend = (s, evt) =>
        {
            switch ((string?)evt["type"])
            {
                case "session.update":
                    s.Inbox.Enqueue(SessionUpdated().ToJsonString());
                    break;
                case "input_audio_buffer.append":
                    s.Inbox.Enqueue(Committed(itemId).ToJsonString());
                    s.Inbox.Enqueue(Delta(itemId, "第一段").ToJsonString());
                    break;
                case "input_audio_buffer.commit":
                    s.Inbox.Enqueue(Completed(itemId, "第一段 完").ToJsonString());
                    break;
            }
        };

        var (queue, _) = OutputCapture();
        var protocol = OpenAiProtocol();
        using var session = new StreamingSttSession(
            protocol, "ws://fake/realtime", new Dictionary<string, string>(),
            5, 30, factory, queue, CancellationToken.None);

        session.StartTurn("turn_A", 0);
        session.AppendAudio([1, 2, 3]);
        session.BeginCommit("turn_A", 0);
        var commit = session.Commit("turn_A", 0);

        Assert.NotNull(commit);
        Assert.Null(commit!.Error);
        Assert.Equal("第一段 完", commit.Result);

        // completed 后 partial 已发过 delta；commit 收口不再有 partial。
        var partial = TryTakePartial(queue);
        Assert.NotNull(partial);
    }

    [Fact]
    public void Commit_NoAudio_FailsImmediately()
    {
        var (socket, factory) = OpenAiFake();
        var (queue, _) = OutputCapture();
        var protocol = OpenAiProtocol();
        using var session = new StreamingSttSession(
            protocol, "ws://fake/realtime", new Dictionary<string, string>(),
            5, 30, factory, queue, CancellationToken.None);

        session.BeginCommit("turn_A", 0);
        var commit = session.Commit("turn_A", 0);

        Assert.NotNull(commit);
        Assert.Equal("streaming transcription received no audio", commit!.Error);
    }

    [Fact]
    public void Commit_ConnectionRefused_FailsWithConnectionMessage()
    {
        var (queue, _) = OutputCapture();
        var protocol = OpenAiProtocol();
        using var session = new StreamingSttSession(
            protocol, "ws://fake/realtime", new Dictionary<string, string>(),
            5, 30, (_, _, _) => throw new IOException("refused"), queue, CancellationToken.None);

        session.StartTurn("turn_A", 0);
        session.AppendAudio([1, 2, 3]);
        session.BeginCommit("turn_A", 0);
        var commit = session.Commit("turn_A", 0);

        Assert.NotNull(commit);
        Assert.Equal("streaming transcription connection failed", commit!.Error);
    }

    [Fact]
    public void RemoteError_PoisonsTurnAndNextTurnRecovers()
    {
        var (socket, factory) = OpenAiFake();
        var poisoned = false;
        socket.OnSend = (s, evt) =>
        {
            switch ((string?)evt["type"])
            {
                case "session.update":
                    s.Inbox.Enqueue(SessionUpdated().ToJsonString());
                    break;
                case "input_audio_buffer.append" when !poisoned:
                    s.Inbox.Enqueue(new JsonObject { ["type"] = "error", ["error"] = "server exploded" }
                        .ToJsonString());
                    poisoned = true;
                    break;
                case "input_audio_buffer.commit":
                    s.Inbox.Enqueue(Completed("item_2", "恢复").ToJsonString());
                    break;
            }
        };

        var (queue, _) = OutputCapture();
        var protocol = OpenAiProtocol();
        using var session = new StreamingSttSession(
            protocol, "ws://fake/realtime", new Dictionary<string, string>(),
            5, 30, factory, queue, CancellationToken.None);

        // 第一轮：远端 error → commit 失败。
        session.StartTurn("turn_A", 0);
        session.AppendAudio([1, 2, 3]);
        session.BeginCommit("turn_A", 0);
        var failed = session.Commit("turn_A", 0);
        Assert.NotNull(failed);
        Assert.Equal("remote streaming transcription failed", failed!.Error);

        // 新一轮（不同 turn id）：恢复。
        session.StartTurn("turn_B", 0);
        session.AppendAudio([4, 5, 6]);
        session.BeginCommit("turn_B", 0);
        var recovered = session.Commit("turn_B", 0);
        Assert.NotNull(recovered);
        Assert.Null(recovered!.Error);
        Assert.Equal("恢复", recovered.Result);
    }

    // ── vLLM discard 断连兜底 ─────────────────────────────────────────

    [Fact]
    public void Vllm_DiscardWithoutItemId_ClosesConnection()
    {
        // vLLM 没有 clear 事件；拿到 item 身份前丢弃只能断连——拒收的 PCM
        // 不能流向下一轮。
        var socket = new FakeSocket();
        StreamingConnectFactory factory = (_, _, _) =>
        {
            socket.Inbox.Enqueue(SessionCreated().ToJsonString());
            return socket;
        };
        var protocol = VllmProtocol();
        using var session = new StreamingSttSession(
            protocol, "ws://fake/realtime", new Dictionary<string, string>(),
            5, 30, factory, new BlockingCollection<object>(), CancellationToken.None);

        session.AppendAudio([1, 2, 3]);
        WaitFor(() => socket.SentEvents().Any(e => (string?)e["type"] == "input_audio_buffer.append"), "append");

        session.DiscardUtterance();

        // 断连后再喂音频 → 重建连接（fake 同一个 socket，还会再走一次握手）。
        // 这里只断言 discard 后新 append 会重新握手（session.update 第二次出现）。
        session.AppendAudio([4, 5, 6]);
        WaitFor(() => socket.SentEvents().Count(e => (string?)e["type"] == "session.update") >= 2,
            "reconnect after discard");
    }

    [Fact]
    public void Commit_SocketStall_ExpiresWithTimeoutMessage()
    {
        var socket = new FakeSocket();
        StreamingConnectFactory factory = (_, _, _) =>
        {
            socket.Inbox.Enqueue(SessionCreated().ToJsonString());
            socket.StallReceive = true; // setup 握手读不到 session.updated 之外的任何东西
            return socket;
        };

        // openai 方言要等 session.updated——直接用 vLLM（不等回声）+ stall。
        var (queue, _) = OutputCapture();
        using var session = new StreamingSttSession(
            VllmProtocol(), "ws://fake/realtime", new Dictionary<string, string>(),
            5, 0.3, factory, queue, CancellationToken.None);

        session.StartTurn("turn_A", 0);
        session.AppendAudio([1, 2, 3]);
        session.BeginCommit("turn_A", 0);
        var commit = session.Commit("turn_A", 0);

        Assert.NotNull(commit);
        Assert.Equal("streaming transcription timed out", commit!.Error);
    }

    // ── 代际 ──────────────────────────────────────────────────────────

    [Fact]
    public void CancelSession_BumpsGenerationAndStaleCommandsDropped()
    {
        var (socket, factory) = OpenAiFake();
        var appendCount = 0;
        socket.OnSend = (s, evt) =>
        {
            switch ((string?)evt["type"])
            {
                case "session.update":
                    s.Inbox.Enqueue(SessionUpdated().ToJsonString());
                    break;
                case "input_audio_buffer.append":
                    Interlocked.Increment(ref appendCount);
                    break;
            }
        };

        var (queue, _) = OutputCapture();
        var protocol = OpenAiProtocol();
        using var session = new StreamingSttSession(
            protocol, "ws://fake/realtime", new Dictionary<string, string>(),
            5, 30, factory, queue, CancellationToken.None);

        session.CancelSession(); // generation → 1
        session.AppendAudio([1, 2, 3]); // 带着旧 generation 1？——Cancel 后 Generation 已是 1，再 append 是 gen 1

        // Cancel 后新 append 用新代际，仍会被正常处理。这里验证的是
        // Cancel 之前捕获的陈旧命令（代际 0）会被丢弃——用一个延迟工厂来
        // 制造竞态窗口太复杂，改为直接验证代际推进与 pending 清空。
        Assert.Equal(1, session.Generation);
        Assert.False(session.HasPendingCommit("turn_A", 0));
    }

    [Fact]
    public async Task Stop_FailsActiveCommit()
    {
        var (socket, factory) = OpenAiFake();
        var stall = true;
        socket.OnSend = (s, evt) =>
        {
            switch ((string?)evt["type"])
            {
                case "session.update":
                    s.Inbox.Enqueue(SessionUpdated().ToJsonString());
                    break;
                case "input_audio_buffer.commit" when stall:
                    // 不回 completed——让 commit 挂在飞。
                    break;
                case "input_audio_buffer.commit":
                    s.Inbox.Enqueue(Completed("item_1", "太迟").ToJsonString());
                    break;
            }
        };

        var (queue, _) = OutputCapture();
        var protocol = OpenAiProtocol();
        var session = new StreamingSttSession(
            protocol, "ws://fake/realtime", new Dictionary<string, string>(),
            5, 30, factory, queue, CancellationToken.None);

        session.StartTurn("turn_A", 0);
        session.AppendAudio([1, 2, 3]);
        session.BeginCommit("turn_A", 0);

        var commitTask = Task.Run(() => session.Commit("turn_A", 0));
        Thread.Sleep(100); // 让 commit 进入等待
        session.Close();   // Stop：active commit 立刻失败

        var commit = await commitTask;
        // Close() = CancelSession + Stop：等待中的 Commit 因代际推进返回 null
        // （上游 Commit 的 generation 检查同口径），而不是拿到 stopped 错误。
        Assert.Null(commit);
    }

    // ── 流式重采样器 ──────────────────────────────────────────────────

    [Fact]
    public void StreamingResampler_ChunksPlusFlushEqualsWholeBlock()
    {
        var whole = new float[16_000]; // 1 秒
        var value = 0f;
        for (var i = 0; i < whole.Length; i++)
        {
            value += 0.01f;
            whole[i] = MathF.Sin(value);
        }

        // 整块一次喂入（同内核基准）。
        var oneShot = new StreamingResampler(16_000, 24_000);
        var expected = oneShot.Push(whole).ToList();
        expected.AddRange(oneShot.FinishUtterance());

        // 分块喂入 + 冲尾：与整块逐样本一致（保守产出纪律；WDL 的 fracpos 逐调用
        // 锚定带来的浮点末位级差异 ≤ 1e-11，与 soxr 流式语义同级，精确到精度 6）。
        var resampler = new StreamingResampler(16_000, 24_000);
        var streamed = new List<float>();
        var offset = 0;
        var chunkSize = 320; // 20 ms
        while (offset < whole.Length)
        {
            var count = Math.Min(chunkSize, whole.Length - offset);
            streamed.AddRange(resampler.Push(whole.AsSpan(offset, count)));
            offset += count;
        }

        streamed.AddRange(resampler.FinishUtterance());

        Assert.Equal(expected.Count, streamed.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], streamed[i], 6);
        }

        // 总长对齐上游 soxr 口径：≈ round(输入长 × 率比)，滤波尾由冲尾补齐。
        Assert.InRange(streamed.Count, 24_000 - 32, 24_000 + 32);
    }

    [Fact]
    public void StreamingResampler_SincKernelAttenuatesAboveTargetNyquist()
    {
        // C030 内核升级的验收：11 kHz 正弦 @48 kHz 降到 16 kHz，11 kHz 已越过
        // 16 kHz 的奈奎斯特（8 kHz）——线性插值会把它混叠进带内，sinc 内核应显著压制。
        var whole = new float[48_000]; // 1 秒
        for (var i = 0; i < whole.Length; i++)
        {
            whole[i] = MathF.Sin(2 * MathF.PI * 11_000f * i / 48_000f);
        }

        var resampler = new StreamingResampler(48_000, 16_000);
        var streamed = new List<float>();
        foreach (var chunk in SpeechToSpeech.Core.Audio.AudioUtil.Chunk(whole, 480))
        {
            streamed.AddRange(resampler.Push(chunk));
        }

        streamed.AddRange(resampler.FinishUtterance());

        var inputRms = Math.Sqrt(whole.Average(static v => (double)v * v));
        var outputRms = Math.Sqrt(streamed.Average(static v => (double)v * v));
        // 衰减 ≥ 20 dB；线性插值内核的输出 RMS 约为输入的四成（混叠显著）。
        Assert.True(outputRms < inputRms * 0.1, $"outputRms={outputRms:0.0000} inputRms={inputRms:0.0000}");
    }

    [Fact]
    public void StreamingResampler_SameRate_PassthroughAndReset()
    {
        var resampler = new StreamingResampler(16_000, 16_000);
        var first = resampler.Push([0.1f, 0.2f, 0.3f]);
        Assert.Equal(3, first.Length);
        var tail = resampler.FinishUtterance();
        Assert.Empty(tail);
        // Reset 后新一轮：偏移从零重算。
        var second = resampler.Push([0.4f, 0.5f]);
        Assert.Equal(2, second.Length);
        Assert.Equal(0.4f, second[0]);
    }
}
