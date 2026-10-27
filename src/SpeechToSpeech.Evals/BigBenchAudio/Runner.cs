using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace SpeechToSpeech.Evals.BigBenchAudio;

/// <summary>
/// 把 Big Bench Audio 的口语题流进一台运行中的 Realtime 服务并收集答案（上游 <c>runner.py</c>）。
///
/// 每道题一条 WebSocket 连接——服务端按连接新建会话，重连正是让第 n 题不进
/// 第 n+1 题上下文的机制。录音长达 13–31 秒、叙述中间有停顿，默认 64 ms 的 VAD
/// hangover 会把一题切碎；runner 为会话提高 <c>silence_duration_ms</c>，真被切碎时
/// 给该题打标并保留已有回复供诊断，而不是默默接受半道题的答案。
/// </summary>
public static class Runner
{
    private const int BytesPerSample = 2;

    /// <summary>消费端重新检查「题目是否已流完」的周期。</summary>
    private static readonly TimeSpan PreSendPoll = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan SessionRetry = TimeSpan.FromSeconds(1.0);

    public const string DefaultInstructions =
        "You are taking a spoken reasoning test. The user reads one self-contained question aloud. "
        + "Work through it briefly out loud, then close with a sentence of exactly the form "
        + "'Final answer: X'. Choose X like this: if the question asks whether an argument is "
        + "deductively valid, X is the single word valid or invalid; if the question can be answered "
        + "yes or no, X is the single word Yes or No; if the question asks how many, X is a single "
        + "whole number. Never end without that final sentence. Keep the whole reply under 120 words.";

    /// <summary>一次 eval 运行的连接与轮次设置。</summary>
    public sealed class RunnerConfig
    {
        public string Url { get; init; } = "ws://127.0.0.1:8765/v1/realtime";
        public string? ApiKey { get; init; }
        public string Instructions { get; init; } = DefaultInstructions;
        public string? Voice { get; init; }

        /// <summary>1.0 = 按真实时间推流（等同麦克风）；更高更快，但吃的是服务端输入队列而非 VAD 节拍。</summary>
        public double Speed { get; init; } = 1.0;
        public int ChunkMs { get; init; } = 20;
        public int TrailingSilenceMs { get; init; } = 2000;

        /// <summary>Silero 的 64 ms 默认值会把这种长、多停顿的录音切碎。</summary>
        public int SilenceDurationMs { get; init; } = 900;
        public double? VadThreshold { get; init; }
        public double ResponseTimeoutS { get; init; } = 180.0;
        public double ConnectTimeoutS { get; init; } = 30.0;

        /// <summary>response.done 之后的静默时长，过了才算这一轮落定。</summary>
        public double GraceS { get; init; } = 2.0;
        public double SettleS { get; init; } = 0.5;
        public int Retries { get; init; } = 1;

        public int ChunkBytes => Dataset.SampleRateHz * BytesPerSample * ChunkMs / 1000;

        public void Validate()
        {
            foreach (var (name, value) in new (string, double)[]
            {
                (nameof(Speed), Speed), (nameof(ChunkMs), ChunkMs),
                (nameof(ResponseTimeoutS), ResponseTimeoutS), (nameof(ConnectTimeoutS), ConnectTimeoutS),
                (nameof(GraceS), GraceS),
            })
            {
                if (value <= 0 || double.IsInfinity(value))
                {
                    throw new ArgumentException($"{name} must be finite and positive");
                }
            }

            if (Retries < 0 || TrailingSilenceMs < 0)
            {
                throw new ArgumentException("retries and trailing_silence_ms must be nonnegative");
            }
        }
    }

    internal sealed class ResponseCapture
    {
        public string Transcript { get; set; } = "";
        public int AudioBytes { get; set; }
        public double? FirstAudioAt { get; set; }
        public double? DoneAt { get; set; }
    }

    /// <summary>一道题的连接产出的全部内容。</summary>
    internal sealed class TurnCapture
    {
        public string InputTranscript { get; set; } = "";
        public List<ResponseCapture> Responses { get; } = [];
        public double? AudioEndAt { get; set; }
        public string? Error { get; set; }

        public ResponseCapture? Last => Responses.Count > 0 ? Responses[^1] : null;
    }

    /// <summary>把服务端调进 eval 模式的 <c>session.update</c>。</summary>
    public static JsonObject BuildSessionUpdate(RunnerConfig config)
    {
        var turnDetection = new JsonObject
        {
            ["type"] = "server_vad",
            ["interrupt_response"] = true,
            ["silence_duration_ms"] = config.SilenceDurationMs,
        };
        if (config.VadThreshold is { } threshold)
        {
            turnDetection["threshold"] = threshold;
        }

        var audio = new JsonObject
        {
            ["input"] = new JsonObject { ["turn_detection"] = turnDetection },
            ["output"] = new JsonObject(),
        };
        if (config.Voice is { } voice)
        {
            audio["output"]!.AsObject()["voice"] = voice;
        }

        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "realtime",
                ["instructions"] = config.Instructions,
                ["audio"] = audio,
            },
        };
    }

    /// <summary>把 PCM 切成定长块，末块不足补零。</summary>
    public static IEnumerable<byte[]> IterChunks(byte[] pcm, int chunkBytes)
    {
        for (var offset = 0; offset < pcm.Length; offset += chunkBytes)
        {
            var length = Math.Min(chunkBytes, pcm.Length - offset);
            var chunk = new byte[chunkBytes];
            Array.Copy(pcm, offset, chunk, 0, length);
            yield return chunk;
        }
    }

    private static string AppendEvent(byte[] chunk) =>
        new JsonObject { ["type"] = "input_audio_buffer.append", ["audio"] = Convert.ToBase64String(chunk) }.ToJsonString();

    private static double Monotonic() => StopwatchProxy.GetTimestamp();

    /// <summary>单声道 16 kHz PCM 推流：题目 + 尾部静音，按 speed 倍速节拍发送。</summary>
    internal static async Task StreamAudioAsync(
        IRunnerSocket ws,
        byte[] pcm,
        RunnerConfig config,
        TurnCapture capture,
        AsyncEvent audioSent,
        CancellationToken cancellationToken)
    {
        var pause = TimeSpan.FromSeconds(config.ChunkMs / 1000.0 / Math.Max(config.Speed, 0.01));
        try
        {
            foreach (var chunk in IterChunks(pcm, config.ChunkBytes))
            {
                await ws.SendAsync(AppendEvent(chunk));
                await Task.Delay(pause, cancellationToken);
            }

            capture.AudioEndAt = Monotonic();

            var silence = AppendEvent(new byte[config.ChunkBytes]);
            for (var i = 0; i < config.TrailingSilenceMs / config.ChunkMs; i++)
            {
                await ws.SendAsync(silence);
                await Task.Delay(pause, cancellationToken);
            }
        }
        finally
        {
            // 音频结束时间戳在「题目」末尾而不是尾部静音末尾——时延从用户停止
            // 说话那一刻起算，真实通话者要等的那段 VAD hangover 也算在其中。
            capture.AudioEndAt ??= Monotonic();
            // 发送中途死亡也要置位，让消费端按期限收口而不是永远等下去。
            audioSent.Set();
        }
    }

    internal static async Task ConsumeAsync(
        IRunnerSocket ws,
        RunnerConfig config,
        TurnCapture capture,
        AsyncEvent audioSent)
    {
        ResponseCapture? current = null;
        double? deadline = null;
        string? inputItemId = null;

        ResponseCapture EnsureCurrent()
        {
            if (current is null)
            {
                current = new ResponseCapture();
                capture.Responses.Add(current);
            }

            return current;
        }

        while (true)
        {
            if (audioSent.IsSet && deadline is null)
            {
                deadline = Monotonic() + config.ResponseTimeoutS;
            }

            var settled = capture.Responses.Count > 0
                && capture.Responses[^1].DoneAt is not null
                && audioSent.IsSet;
            TimeSpan timeout;
            if (deadline is null)
            {
                // 还在推流：轮询等待，让 response 期限在最后一块发出后立即起算，
                // 而不是吃掉一次长时间阻塞的 recv。
                timeout = PreSendPoll;
            }
            else
            {
                var remaining = deadline.Value - Monotonic();
                if (remaining <= 0)
                {
                    if (!settled)
                    {
                        capture.Error = "response_timeout";
                    }

                    return;
                }

                timeout = settled ? TimeSpan.FromSeconds(Math.Min(config.GraceS, remaining)) : TimeSpan.FromSeconds(remaining);
            }

            string? raw;
            try
            {
                raw = await ws.ReceiveAsync(timeout);
            }
            catch (Exception exc)
            {
                capture.Error = $"{exc.GetType().Name}: {exc.Message}";
                return;
            }

            if (raw is null)
            {
                if (settled)
                {
                    return;
                }

                if (deadline is not null && Monotonic() >= deadline.Value)
                {
                    capture.Error = "response_timeout";
                    return;
                }

                continue;
            }

            var eventNode = JsonNode.Parse(raw) as JsonObject ?? [];
            var kind = eventNode["type"]?.GetValue<string>() ?? "";

            switch (kind)
            {
                case "input_audio_buffer.speech_started":
                {
                    var itemId = eventNode["item_id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(itemId))
                    {
                        // 最终 STT 才释放 item 身份；推测性复开可能在任何公开响应出现
                        // 之前拿到新的 ID。一旦响应已经开始，新输入意味着它只答了
                        // 录音的一部分，哪怕下一个响应还在路上。
                        if (inputItemId is not null && itemId != inputItemId && capture.Responses.Count > 0)
                        {
                            capture.Error = "split_turn (2 input items)";
                            return;
                        }

                        inputItemId = itemId;
                    }

                    break;
                }
                case "conversation.item.input_audio_transcription.completed":
                {
                    var transcript = eventNode["transcript"]?.GetValue<string>() ?? "";
                    capture.InputTranscript = string.Join(" ", new[] { capture.InputTranscript, transcript }.Where(static part => part.Length > 0));
                    break;
                }
                case "response.created":
                {
                    current = new ResponseCapture();
                    capture.Responses.Add(current);
                    break;
                }
                case "response.audio.delta":
                case "response.output_audio.delta":
                {
                    var delta = eventNode["delta"]?.GetValue<string>() ?? "";
                    if (delta.Length > 0)
                    {
                        var response = EnsureCurrent();
                        response.AudioBytes += Base64Length(delta);
                        response.FirstAudioAt ??= Monotonic();
                    }

                    break;
                }
                case "response.audio_transcript.delta":
                case "response.output_audio_transcript.delta":
                case "response.output_text.delta":
                case "response.text.delta":
                {
                    EnsureCurrent().Transcript += eventNode["delta"]?.GetValue<string>() ?? "";
                    break;
                }
                case "response.audio_transcript.done":
                case "response.output_audio_transcript.done":
                {
                    var transcript = eventNode["transcript"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(transcript))
                    {
                        EnsureCurrent().Transcript = transcript;
                    }

                    break;
                }
                case "response.done":
                {
                    EnsureCurrent().DoneAt = Monotonic();
                    var status = eventNode["response"]?.AsObject()["status"]?.GetValue<string>() ?? "completed";
                    if (status != "completed")
                    {
                        capture.Error = $"response_{status}";
                    }

                    current = null;
                    break;
                }
                case "error":
                {
                    capture.Error = eventNode["error"]?.AsObject()["message"]?.GetValue<string>() is { Length: > 0 } message
                        ? message
                        : "error event";
                    return;
                }
            }
        }
    }

    /// <summary>不解码整个音频块，只数 base64 对应的原始字节数。</summary>
    private static int Base64Length(string base64)
    {
        var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2
            : base64.EndsWith("=", StringComparison.Ordinal) ? 1
            : 0;
        return base64.Length * 3 / 4 - padding;
    }

    /// <summary>
    /// 打开一条 Realtime 连接，等 <c>session.created</c> 并应用 eval 设置。
    /// 连接断掉的那道题可能还在排空在飞的 provider 请求：先等容量再发下一题，
    /// 与重试预算无关；其余握手错误立刻失败。
    /// </summary>
    public static async Task<IRunnerSocket> OpenSessionAsync(
        RunnerConfig config,
        RunnerConnectFactory? connectFactory = null)
    {
        var deadline = Monotonic() + config.ResponseTimeoutS;

        TimeSpan HandshakeTimeout()
        {
            var remaining = deadline - Monotonic();
            if (remaining <= 0)
            {
                throw new TimeoutException("session_capacity_timeout");
            }

            return TimeSpan.FromSeconds(Math.Min(config.ConnectTimeoutS, remaining));
        }

        var factory = connectFactory ?? ClientRunnerSocket.Connect;
        while (true)
        {
            var ws = factory(new Uri(config.Url), config.ApiKey);
            try
            {
                var firstRaw = await ws.ReceiveAsync(HandshakeTimeout())
                    ?? throw new TimeoutException("handshake timeout waiting for session.created");
                var first = JsonNode.Parse(firstRaw) as JsonObject ?? new JsonObject();
                if (Str(first["type"]) == "error")
                {
                    var error = first["error"]?.AsObject() ?? new JsonObject();
                    if (Str(error["type"]) != "session_limit_reached")
                    {
                        throw new InvalidOperationException(Str(error["message"]) is { Length: > 0 } message ? message : "connection rejected");
                    }
                }
                else
                {
                    if (Str(first["type"]) != "session.created")
                    {
                        throw new InvalidOperationException($"unexpected first event: '{Str(first["type"])}'");
                    }

                    await ws.SendAsync(BuildSessionUpdate(config).ToJsonString());
                    var updatedRaw = await ws.ReceiveAsync(HandshakeTimeout())
                        ?? throw new TimeoutException("handshake timeout waiting for session.updated");
                    var updated = JsonNode.Parse(updatedRaw) as JsonObject ?? new JsonObject();
                    if (Str(updated["type"]) != "session.updated")
                    {
                        var detail = updated["error"] is not null ? updated["error"]!.ToJsonString() : Str(updated["type"]);
                        throw new InvalidOperationException($"session configuration rejected: {detail}");
                    }

                    return ws;
                }
            }
            catch
            {
                ws.Dispose();
                throw;
            }

            // session_limit_reached：这条连接已用完，丢弃后按容量等。
            ws.Dispose();
            var remaining = deadline - Monotonic();
            if (remaining <= 0)
            {
                throw new TimeoutException("session_capacity_timeout");
            }

            await Task.Delay(SessionRetry);
        }

        static string Str(JsonNode? node) => node?.GetValue<string>() ?? "";
    }

    /// <summary>对一道题开新连接提问并评分。</summary>
    public static async Task<Report.ItemResult> RunItemAsync(
        RunnerConfig config,
        Dataset.EvalItem item,
        byte[] pcm,
        RunnerConnectFactory? connectFactory = null)
    {
        var result = new Report.ItemResult { Id = item.Id, Category = item.Category, OfficialAnswer = item.OfficialAnswer };
        var capture = new TurnCapture();
        try
        {
            var ws = await OpenSessionAsync(config, connectFactory);
            try
            {
                var audioSent = new AsyncEvent();
                using var cts = new CancellationTokenSource();
                var sender = StreamAudioAsync(ws, pcm, config, capture, audioSent, cts.Token);
                try
                {
                    await ConsumeAsync(ws, config, capture, audioSent);
                }
                finally
                {
                    cts.Cancel();
                    try
                    {
                        await sender;
                    }
                    catch (Exception exc) when (exc is not OperationCanceledException)
                    {
                        // 发送中途死亡是一个可比的数据点；正常取消不是。
                        capture.Error = $"audio_send_failed: {exc.GetType().Name}: {exc.Message}";
                    }
                }
            }
            finally
            {
                ws.Dispose();
            }
        }
        catch (Exception exc)
        {
            // 每个失败都是一个可比的数据点。
            result.Error = $"{exc.GetType().Name}: {exc.Message}";
            return result;
        }

        return FinishCapture(item, capture);
    }

    /// <summary>把捕获到的内容落到结果条目并评分（RunItem 后半段，测试可直接驱动）。</summary>
    internal static Report.ItemResult FinishCapture(Dataset.EvalItem item, TurnCapture capture)
    {
        var result = new Report.ItemResult
        {
            Id = item.Id,
            Category = item.Category,
            OfficialAnswer = item.OfficialAnswer,
            InputTranscript = capture.InputTranscript,
        };
        var last = capture.Last;
        if (capture.Error is not null)
        {
            result.Error = capture.Error;
        }
        else if (last is null)
        {
            result.Error = "no_response";
        }

        if (last is not null)
        {
            result.Reply = last.Transcript;
            result.AudioOutBytes = last.AudioBytes;
            if (last.AudioBytes == 0)
            {
                result.Error ??= "no_audio_output";
            }

            if (capture.AudioEndAt is { } audioEnd)
            {
                if (last.FirstAudioAt is { } firstAudio && firstAudio >= audioEnd)
                {
                    result.TtfbS = firstAudio - audioEnd;
                }

                if (last.DoneAt is { } doneAt && doneAt >= audioEnd)
                {
                    result.TurnS = doneAt - audioEnd;
                }
            }

            if (capture.Responses.Count > 1)
            {
                // 多个响应同样说明被切碎了，哪怕服务端没有宣告不同的输入 item。
                result.Error ??= $"split_turn ({capture.Responses.Count} responses)";
            }
        }

        var verdict = Scoring.GradeOne(item.Category, item.OfficialAnswer, result.Reply);
        result.Extracted = verdict.Extracted;
        result.Correct = verdict.Correct;
        result.Method = verdict.Method;
        return result;
    }

    public delegate void ProgressCallback(int index, int total, Report.ItemResult result);

    /// <summary>顺序跑完子集里的每一道题，每题返回一条结果。</summary>
    public static async Task<List<Report.ItemResult>> RunSubsetAsync(
        Dataset.Subset subset,
        RunnerConfig config,
        ProgressCallback? progress = null,
        RunnerConnectFactory? connectFactory = null)
    {
        var results = new List<Report.ItemResult>();
        var total = subset.Count;
        for (var index = 0; index < total; index++)
        {
            var item = subset.Items[index];
            byte[]? pcm = null;
            Report.ItemResult result = null!;
            for (var attempt = 0; attempt <= config.Retries; attempt++)
            {
                try
                {
                    // MP3 解码走 NAudio MediaFoundation，仅 Windows 可用。
#pragma warning disable CA1416
                    pcm ??= Dataset.LoadPcm16Mono(Dataset.ResolveAudio(item, subset));
#pragma warning restore CA1416
                    result = await RunItemAsync(config, item, pcm, connectFactory);
                }
                catch (Exception exc)
                {
                    // 某个资产挂了也保留其余条目。
                    result = new Report.ItemResult
                    {
                        Id = item.Id,
                        Category = item.Category,
                        OfficialAnswer = item.OfficialAnswer,
                        Error = $"audio_load_failed: {exc.GetType().Name}: {exc.Message}",
                    };
                }

                if (result.Error is null || result.Error.StartsWith("split_turn") || attempt == config.Retries)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(config.SettleS));
            }

            results.Add(result);
            progress?.Invoke(index + 1, total, result);
            await Task.Delay(TimeSpan.FromSeconds(config.SettleS));
        }

        return results;
    }
}

/// <summary>可异步等待的一次性事件（asyncio.Event 的对应物）。</summary>
public sealed class AsyncEvent
{
    private volatile bool _set;

    public bool IsSet => _set;

    public void Set() => _set = true;
}

/// <summary>runner 用的 WebSocket 抽象（send / recv(timeout) / close）。</summary>
public interface IRunnerSocket : IDisposable
{
    Task SendAsync(string text);

    /// <summary>返回 null 表示超时；抛异常表示连接死亡。</summary>
    Task<string?> ReceiveAsync(TimeSpan timeout);
}

/// <summary>连接工厂；测试注入 fake（对齐上游 connect_factory 的角色）。</summary>
public delegate IRunnerSocket RunnerConnectFactory(Uri url, string? apiKey);

/// <summary>可注入的单调时钟；默认走 Stopwatch。</summary>
public static class StopwatchProxy
{
    internal static Func<double> GetTimestampImpl { get; set; } = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public static double GetTimestamp() => GetTimestampImpl();
}

/// <summary>ClientWebSocket 实现：后台接收泵 + 队列，规避取消中的 ReceiveAsync 会 abort 连接的陷阱。</summary>
public sealed class ClientRunnerSocket : IRunnerSocket
{
    public static RunnerConnectFactory Connect => (url, apiKey) => new ClientRunnerSocket(url, apiKey);

    private readonly ClientWebSocket _socket;
    private readonly BlockingCollection<string> _incoming = new();
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pump;

    private ClientRunnerSocket(Uri url, string? apiKey)
    {
        _socket = new ClientWebSocket();
        if (!string.IsNullOrEmpty(apiKey))
        {
            _socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
        }

        _socket.ConnectAsync(url, CancellationToken.None).GetAwaiter().GetResult();
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token));
    }

    private async Task PumpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            try
            {
                using var message = new MemoryStream();
                var segment = new byte[8192];
                while (true)
                {
                    var result = await _socket.ReceiveAsync(new ArraySegment<byte>(segment), token);
                    message.Write(segment, 0, result.Count);
                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }

                _incoming.Add(Encoding.UTF8.GetString(message.ToArray()));
            }
            catch (Exception)
            {
                _incoming.CompleteAdding();
                return;
            }
        }

        _incoming.CompleteAdding();
    }

    public async Task SendAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public Task<string?> ReceiveAsync(TimeSpan timeout)
    {
        try
        {
            if (_incoming.TryTake(out var message, timeout))
            {
                return Task.FromResult<string?>(message);
            }

            return Task.FromResult<string?>(null);
        }
        catch (ObjectDisposedException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    public void Dispose()
    {
        try
        {
            _pumpCts.Cancel();
        }
        catch
        {
        }

        try
        {
            _socket.Abort();
        }
        catch
        {
        }

        _socket.Dispose();
        _incoming.Dispose();
        _pumpCts.Dispose();
    }
}
