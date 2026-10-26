using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Pipeline.Handlers;
using SpeechToSpeech.Core.Runtime;
using SpeechToSpeech.Realtime.Protocol;

namespace SpeechToSpeech.Realtime;

/// <summary>服务端选项。</summary>
public sealed class RealtimeServerOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8765;

    /// <summary>是否在 <c>session.created</c> 之外打印诊断日志。</summary>
    public bool Verbose { get; init; }

    public string DefaultVoice { get; init; } = "ryan";
    public string DefaultInstructions { get; init; } =
        "You are a helpful voice assistant. Answer in one or two short spoken sentences.";

    /// <summary>默认采样率：上游 realtime 默认 PCM 24 kHz。</summary>
    public int DefaultSampleRate { get; init; } = 24_000;
}

/// <summary>
/// 一条 realtime 连接。
///
/// 拥有六条管线线程（VAD → STT → Notifier → LLM → 输出处理器 → TTS）、
/// 一个读循环和一个写循环。三条队列纪律与上游一致：
/// <list type="bullet">
/// <item>音频方向：解码 → 重采样 16 kHz → 512 样本分块 → VAD；</item>
/// <item>事件方向：VAD/转写事件走文本队列，不等 TTS；</item>
/// <item>数据方向：响应事件与音频共用 TTS 输出队列，保证模型原始顺序。</item>
/// </list>
/// </summary>
public sealed class RealtimeConnection : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly RealtimeServerOptions _options;
    private readonly BackendContext _backends;

    private readonly RuntimeConfig _config;
    private readonly CancelScope _cancelScope = new();
    private readonly SpeculativeTurnTracker _speculativeTurns = new();
    private readonly TurnLatencyStore _latency = new();

    // 音频与事件队列
    private readonly BlockingCollection<object> _audioIn = new(new ConcurrentQueue<object>(), 64);
    private readonly BlockingCollection<object> _vadOut = new(new ConcurrentQueue<object>(), 64);
    private readonly BlockingCollection<object> _sttOut = new(new ConcurrentQueue<object>(), 128);
    private readonly BlockingCollection<object> _lmIn = new(new ConcurrentQueue<object>(), 32);
    private readonly BlockingCollection<object> _lmOut = new(new ConcurrentQueue<object>(), 256);
    private readonly BlockingCollection<object> _ttsIn = new(new ConcurrentQueue<object>(), 256);
    private readonly BlockingCollection<object> _ttsOut = new(new ConcurrentQueue<object>(), 512);
    private readonly BlockingCollection<object> _textEvents = new(new ConcurrentQueue<object>(), 512);

    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Thread> _threads = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private readonly byte[] _receiveBuffer = new byte[64 * 1024];
    private readonly MemoryStream _messageBuffer = new();

    private string? _activeResponseId;
    private string _pendingUserItemId = "item_" + Guid.NewGuid().ToString("N")[..12];
    private readonly StringBuilder _assistantTranscript = new();

    /// <summary>本响应的助手消息项 id。与转向 <c>response.output_audio.delta</c> 的 item_id 是同一个。</summary>
    private string? _assistantItemId;

    /// <summary>消息项是否已经用 <c>output_item.added</c> 开启。只开一次。</summary>
    private bool _assistantItemOpened;

    /// <summary>上一个已上线会话项的 id，用于 <c>previous_item_id</c> 顺序链。</summary>
    private string? _lastItemId;

    /// <summary>当前输入项的音频时长（秒），来自 VAD 的 speech_stopped，转写完成时进 usage。</summary>
    private double _inputAudioSeconds;

    /// <summary>本响应已收到的文本片数，决定 delta 之间是否补分隔空格（与上游一致）。</summary>
    private int _assistantPartCount;

    /// <summary>本响应是否真的往线上发过音频。决定收尾时要不要关音频流。</summary>
    private bool _audioStreamed;

    /// <summary>本响应是否出音频。由 <c>response.create</c> 的 <c>output_modalities</c> 决定。</summary>
    private bool _responseWantsAudio = true;

    public RealtimeConnection(WebSocket socket, RealtimeServerOptions options, BackendContext backends)
    {
        _socket = socket;
        _options = options;
        _backends = backends;

        var session = new JsonObject
        {
            ["type"] = "realtime",
            ["instructions"] = options.DefaultInstructions,
            ["audio"] = new JsonObject
            {
                ["input"] = new JsonObject
                {
                    ["format"] = new JsonObject
                    {
                        ["type"] = "audio/pcm",
                        ["rate"] = options.DefaultSampleRate,
                    },
                    ["turn_detection"] = new JsonObject
                    {
                        ["type"] = "server_vad",
                        ["threshold"] = 0.5,
                        ["prefix_padding_ms"] = 300,
                        ["silence_duration_ms"] = 500,
                        ["create_response"] = true,
                        ["interrupt_response"] = true,
                    },
                    ["transcription"] = new JsonObject(),
                },
                ["output"] = new JsonObject
                {
                    ["format"] = new JsonObject
                    {
                        ["type"] = "audio/pcm",
                        ["rate"] = options.DefaultSampleRate,
                    },
                    ["voice"] = options.DefaultVoice,
                },
            },
        };

        _config = new RuntimeConfig(new ChatSession(), session);
    }

    public string SessionId => _config.Chat.SessionId;

    /// <summary>启动管线线程并进入读/写循环，直到连接关闭。</summary>
    public async Task RunAsync()
    {
        StartPipeline();
        try
        {
            await SendAsync(ServerEvent.Serialize(ServerEvent.SessionCreated(SessionId, _config.SnapshotSession())));

            var sendTask = SendLoopAsync(_stopping.Token);
            await ReceiveLoopAsync(_stopping.Token);
            _stopping.Cancel();
            await SafeAwait(sendTask);
        }
        finally
        {
            await ShutdownPipelineAsync();
        }
    }

    // ── 管线装配 ──────────────────────────────────────────────────────

    private void StartPipeline()
    {
        var vad = BackendRegistry.CreateVad(_backends.Vad.ModelPath is null ? "silero" : "silero", _backends);
        var sttName = _backends.Stt.ModelName ?? "whisper";
        var stt = BackendRegistry.CreateStt(sttName, _backends);
        var llmName = _backends.Llm.ModelName ?? "tensorsharp";
        var llm = BackendRegistry.CreateLlm(llmName, _backends);
        var ttsName = _backends.Tts.ModelName ?? "qwen3";
        var tts = BackendRegistry.CreateTts(ttsName, _backends);

        var token = _stopping.Token;

        AddThread("vad", () => new VadHandler(token, _audioIn, _vadOut, _textEvents, vad, _cancelScope, _speculativeTurns).Run());
        AddThread("stt", () => new SttHandler(token, _vadOut, _sttOut, stt, _cancelScope, _speculativeTurns).Run());
        AddThread("notifier", () => new TranscriptionNotifier(
            token, _sttOut, _lmIn, _textEvents, _config.InputAudioTranscriptionSnapshotsEnabled,
            _cancelScope, _speculativeTurns).Run());
        AddThread("llm", () => new LanguageModelHandler(token, _lmIn, _lmOut, llm, _cancelScope, _speculativeTurns, _latency).Run());
        AddThread("processor", () => new LmOutputProcessor(
            token, _lmOut, _ttsIn, _textEvents, _cancelScope, _speculativeTurns, _latency).Run());
        AddThread("tts", () => new TtsHandler(token, _ttsIn, _ttsOut, tts, _cancelScope, _speculativeTurns, _latency).Run());

        foreach (var thread in _threads)
        {
            thread.Start();
        }
    }

    private void AddThread(string name, Action body)
    {
        _threads.Add(new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                TranscriptLog.Error($"pipeline thread '{name}' exited", ex);
            }
        })
        {
            Name = $"s2s-{name}",
            IsBackground = true,
        });
    }

    private async Task ShutdownPipelineAsync()
    {
        foreach (var queue in new[] { _audioIn, _vadOut, _sttOut, _lmIn, _lmOut, _ttsIn })
        {
            try
            {
                queue.Add(PipelineControl.SessionEnd);
                queue.Add(PipelineSentinels.PipelineEnd);
            }
            catch (InvalidOperationException)
            {
                // 队列已标记完成
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        foreach (var thread in _threads)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            thread.Join(remaining);
        }

        foreach (var queue in new[] { _audioIn, _vadOut, _sttOut, _lmIn, _lmOut, _ttsIn, _ttsOut, _textEvents })
        {
            queue.Dispose();
        }

        await Task.CompletedTask;
    }

    // ── 读循环 ────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(_receiveBuffer), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WebSocketException)
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            _messageBuffer.Write(_receiveBuffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var payload = Encoding.UTF8.GetString(_messageBuffer.GetBuffer(), 0, (int)_messageBuffer.Length);
            _messageBuffer.SetLength(0);

            try
            {
                await DispatchAsync(payload);
            }
            catch (Exception ex)
            {
                TranscriptLog.Error("failed to dispatch client event", ex);
                await SendAsync(ServerEvent.Serialize(ServerEvent.Error(
                    ErrorCodes.InternalError, ex.Message)));
            }
        }
    }

    private async Task DispatchAsync(string payload)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            await SendAsync(ServerEvent.Serialize(ServerEvent.Error(
                ErrorCodes.UnknownOrInvalidEvent, $"could not parse event: {ex.Message}")));
            return;
        }

        if (node is not JsonObject json || json["type"]?.GetValue<string>() is not { } type)
        {
            await SendAsync(ServerEvent.Serialize(ServerEvent.Error(
                ErrorCodes.UnknownOrInvalidEvent, "event is missing a 'type' field")));
            return;
        }

        switch (type)
        {
            case EventNames.InputAudioBufferAppend:
                HandleAudioAppend(json);
                break;

            case EventNames.SessionUpdate:
                await HandleSessionUpdateAsync(json);
                break;

            case EventNames.ConversationItemCreate:
                await HandleConversationItemCreateAsync(json);
                break;

            case EventNames.ConversationItemDelete:
                await HandleConversationItemDeleteAsync(json);
                break;

            case EventNames.ConversationItemTruncate:
                // 播放由客户端掌控；显式 response.cancel 或服务端 VAD 已经丢弃了在途生成，
                // 这里按上游做法确认收到后不做动作。
                break;

            case EventNames.ResponseCreate:
                await HandleResponseCreateAsync(json);
                break;

            case EventNames.ResponseCancel:
                await HandleResponseCancelAsync();
                break;

            case EventNames.InputAudioBufferCommit:
            case EventNames.InputAudioBufferClear:
                _pendingUserItemId = "item_" + Guid.NewGuid().ToString("N")[..12];
                break;

            default:
                await SendAsync(ServerEvent.Serialize(ServerEvent.Error(
                    ErrorCodes.UnknownOrInvalidEvent, $"unknown event type '{type}'")));
                break;
        }
    }

    private void HandleAudioAppend(JsonObject json)
    {
        var audio = json["audio"]?.GetValue<string>();
        if (string.IsNullOrEmpty(audio))
        {
            return;
        }

        byte[] pcm;
        try
        {
            pcm = Convert.FromBase64String(audio);
        }
        catch (FormatException)
        {
            return;
        }

        var samples = AudioUtil.FromPcm16(pcm);
        _audioIn.TryAdd(new VadInput(samples, _config.InputSampleRate, _config), 200);
    }

    private async Task HandleSessionUpdateAsync(JsonObject json)
    {
        if (json["session"] is not JsonObject session)
        {
            await SendAsync(ServerEvent.Serialize(ServerEvent.Error(
                ErrorCodes.InvalidValue, "session.update requires a 'session' object")));
            return;
        }

        var type = session["type"]?.GetValue<string>();
        if (type is not null && type != "realtime")
        {
            await SendAsync(ServerEvent.Serialize(ServerEvent.Error(
                ErrorCodes.InvalidSessionType, $"unsupported session type '{type}'")));
            return;
        }

        _config.ApplySessionUpdate(session);
        await SendAsync(ServerEvent.Serialize(ServerEvent.SessionUpdated(_config.SnapshotSession())));
    }

    private async Task HandleConversationItemCreateAsync(JsonObject json)
    {
        if (json["item"] is not JsonObject item)
        {
            await SendAsync(ServerEvent.Serialize(ServerEvent.Error(
                ErrorCodes.InvalidValue, "conversation.item.create requires an 'item' object")));
            return;
        }

        var itemType = item["type"]?.GetValue<string>();
        var itemId = item["id"]?.GetValue<string>() ?? "item_" + Guid.NewGuid().ToString("N")[..12];

        switch (itemType)
        {
            case "message":
                {
                    var role = item["role"]?.GetValue<string>() ?? "user";
                    var text = ExtractText(item);
                    if (role is "user" or "system" && text.Length > 0)
                    {
                        _config.Chat.Append(role == "system"
                            ? ChatMessage.System(text, itemId)
                            : ChatMessage.User(text, itemId));
                    }

                    // 原样回带客户端给的内容数组：客户端可能塞了多种 part，
                    // 只回 {"role":...} 会让它的渲染器拿不到消息体。
                    var content = item["content"] is JsonArray parts
                        ? (JsonArray)parts.DeepClone()
                        : new JsonArray();

                    await SendAsync(ServerEvent.Serialize(
                        ServerEvent.ConversationItemCreated(itemId, role, content, _lastItemId)));
                    _lastItemId = itemId;
                    break;
                }

            case "function_call_output":
                {
                    var callId = item["call_id"]?.GetValue<string>() ?? "";
                    var output = item["output"]?.ToString() ?? "";
                    _config.Chat.Append(ChatMessage.Tool(callId, output, itemId));
                    await SendAsync(ServerEvent.Serialize(ServerEvent.ConversationItemCreated(
                        itemId,
                        "tool",
                        [new JsonObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = output }],
                        _lastItemId)));
                    _lastItemId = itemId;
                    break;
                }

            default:
                await SendAsync(ServerEvent.Serialize(ServerEvent.Error(
                    ErrorCodes.InvalidValue, $"unsupported item type '{itemType}'")));
                break;
        }
    }

    /// <summary>
    /// <c>conversation.item.delete</c>：从上下文里抹掉一条会话项。
    ///
    /// 是"删掉"而不是"隐藏"——下一轮模型不该再看到它。删的是链尾时把
    /// <c>previous_item_id</c> 回退到仍在的前驱，否则客户端续传会指向一个已不存在的项。
    /// </summary>
    private async Task HandleConversationItemDeleteAsync(JsonObject json)
    {
        var itemId = json["item_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(itemId))
        {
            await SendAsync(ServerEvent.Serialize(ServerEvent.Error(
                ErrorCodes.InvalidValue, "conversation.item.delete requires an 'item_id'")));
            return;
        }

        if (_config.Chat.Delete(itemId) is null)
        {
            await SendAsync(ServerEvent.Serialize(ServerEvent.Error(
                ErrorCodes.ConversationItemNotFound,
                $"No conversation item with id '{itemId}' found.",
                eventId: json["event_id"]?.GetValue<string>())));
            return;
        }

        if (_lastItemId == itemId)
        {
            _lastItemId = _config.Chat.ItemIds().LastOrDefault();
        }

        await SendAsync(ServerEvent.Serialize(ServerEvent.ConversationItemDeleted(itemId)));
    }

    private static string ExtractText(JsonObject item)
    {
        if (item["content"] is not JsonArray content)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var part in content)
        {
            if (part is JsonObject partObject)
            {
                builder.Append(partObject["text"]?.GetValue<string>() ?? "");
            }
        }

        return builder.ToString();
    }

    private async Task HandleResponseCreateAsync(JsonObject json)
    {
        if (_activeResponseId is not null)
        {
            await SendAsync(ServerEvent.Serialize(ServerEvent.Error(
                ErrorCodes.ConversationAlreadyHasActiveResponse,
                "conversation already has an active response")));
            return;
        }

        _cancelScope.NewResponse();
        _activeResponseId = "resp_" + Guid.NewGuid().ToString("N")[..12];

        RealtimeResponseCreateParams? overrides = null;
        if (json["response"] is JsonObject response)
        {
            overrides = new RealtimeResponseCreateParams
            {
                Instructions = response["instructions"]?.GetValue<string>(),
                Temperature = response["temperature"]?.GetValue<double>(),
                MaxOutputTokens = response["max_output_tokens"]?.GetValue<int>(),
                OutputModalities = ReadModalities(response),
            };
        }

        BeginAssistantResponse(overrides);

        await SendAsync(ServerEvent.Serialize(ServerEvent.ResponseCreated(_activeResponseId)));

        _lmIn.TryAdd(GenerateResponseRequest.Create(_config, overrides, responseKey: _activeResponseId), 200);
    }

    /// <summary>读 <c>output_modalities</c>。允许是字符串数组，也容忍单个字符串。</summary>
    private static IReadOnlyList<string>? ReadModalities(JsonObject response)
        => response["output_modalities"] switch
        {
            JsonArray array => [.. array.Select(node => node?.GetValue<string>() ?? "").Where(s => s.Length > 0)],
            JsonValue value when value.TryGetValue<string>(out var single) => [single],
            _ => null,
        };

    /// <summary>
    /// 重置一次响应的助手消息项状态。
    ///
    /// 模态在响应开始时就定死——上游同一个 <c>response_wants_audio</c> 既决定发哪种 delta、
    /// 也决定 item 的 content 类型，中途不能改，否则前后件类型自相矛盾。
    /// </summary>
    private void BeginAssistantResponse(RealtimeResponseCreateParams? overrides)
    {
        _assistantTranscript.Clear();
        _assistantItemId = "item_" + Guid.NewGuid().ToString("N")[..12];
        _assistantItemOpened = false;
        _assistantPartCount = 0;
        _audioStreamed = false;
        _responseWantsAudio = overrides?.WantsAudio ?? true;
    }

    /// <summary>
    /// 取消当前响应。
    ///
    /// 必须回一个 <c>response.done</c>（<c>status=cancelled</c>）：客户端发 <c>response.cancel</c>
    /// 之后会一直等这一轮的终态，收不到就再也发不出下一轮。
    /// 收尾事件顺序与正常完成一致，只有 item 状态变 <c>incomplete</c>。
    /// </summary>
    private async Task HandleResponseCancelAsync()
    {
        var responseId = _activeResponseId;

        // 先让代际失效：之后到达的音频与文本分片都按过期丢弃
        _cancelScope.Cancel();

        if (responseId is not null)
        {
            var text = _assistantTranscript.ToString();
            await EmitTeardownAsync(responseId, text, completed: false);
            await SendAsync(ServerEvent.Serialize(
                ServerEvent.ResponseDone(responseId, "cancelled", "client_cancelled")));

            // 说了一半的话不能留在历史里——否则下一轮 LLM 会把它当成已经说出口的回复。
            // 上游同样只在 status == "completed" 时 finalize，其余状态回滚。
            _config.Chat.RollbackToCommit();
        }

        _activeResponseId = null;
        // 取消后不能再往已关闭的消息项里追加——不清掉的话，迟到的分片会写进下一轮的 item
        _assistantItemId = null;
        _assistantItemOpened = false;
        _assistantPartCount = 0;
        _audioStreamed = false;
        _assistantTranscript.Clear();
    }

    // ── 写循环 ────────────────────────────────────────────────────────

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var progressed = false;

            // 文本事件优先：它们不依赖音频，早一秒到客户端就少一秒"对方没听见"的错觉
            while (_textEvents.TryTake(out var textEvent))
            {
                progressed = true;
                await EmitAsync(textEvent);
            }

            while (_ttsOut.TryTake(out var item))
            {
                progressed = true;
                await EmitAsync(item);
            }

            if (!progressed)
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EmitAsync(object item)
    {
        switch (item)
        {
            case SpeechStartedEvent started:
                if ((_config.InterruptResponseEnabled || started.InterruptResponse) && _activeResponseId is not null)
                {
                    await HandleResponseCancelAsync();
                }

                await SendAsync(ServerEvent.Serialize(ServerEvent.SpeechStarted(_pendingUserItemId, started.AudioStartMs)));
                break;

            case SpeechStoppedEvent stopped:
                // 该输入项的音频时长，转写完成时要作为 usage.seconds 上报。
                // VAD 给的是这一段的时长（不是连接总时长），与上游 input_item.audio_duration_s 同义。
                if (stopped.DurationSeconds > 0)
                {
                    _inputAudioSeconds = stopped.DurationSeconds;
                }

                await SendAsync(ServerEvent.Serialize(ServerEvent.SpeechStopped(_pendingUserItemId, stopped.AudioEndMs)));

                // 上游在 speech_stopped 之后、转写之前发这两条：先宣告这段音频定稿，
                // 再宣告它成为一个会话项。顺序反了，客户端会先看到无主的项再看到提交确认。
                await SendAsync(ServerEvent.Serialize(
                    ServerEvent.InputAudioBufferCommitted(_pendingUserItemId, _lastItemId)));
                await SendAsync(ServerEvent.Serialize(ServerEvent.ConversationItemCreated(
                    _pendingUserItemId,
                    "user",
                    [new JsonObject { ["type"] = "input_audio" }],
                    _lastItemId)));
                _lastItemId = _pendingUserItemId;
                break;

            case PartialTranscriptionEvent partial:
                await SendAsync(ServerEvent.Serialize(
                    ServerEvent.TranscriptionDelta(_pendingUserItemId, partial.Delta)));
                break;

            case TranscriptionSnapshotEvent snapshot:
                await SendAsync(ServerEvent.Serialize(
                    ServerEvent.TranscriptionSnapshot(_pendingUserItemId, snapshot.Snapshot)));
                break;

            case TranscriptionCompletedEvent completed:
                await HandleTranscriptionCompletedAsync(completed);
                break;

            case TranscriptionFailedEvent failed:
                // item 作用域的失败通告，不用全局 error：客户端据此只把这一条输入标记为
                // 转写失败并继续对话，而不是把整条连接当成出错。
                await SendAsync(ServerEvent.Serialize(
                    ServerEvent.TranscriptionFailed(_pendingUserItemId, failed.Message)));
                _lastItemId = _pendingUserItemId;
                break;

            case AssistantOutputEvent output:
                await HandleAssistantOutputAsync(output);
                break;

            case AssistantToolCallReadyEvent toolReady:
                await SendAsync(ServerEvent.Serialize(ServerEvent.FunctionCallArgumentsDone(
                    toolReady.ResponseKey ?? _activeResponseId ?? "resp_unknown",
                    "item_" + toolReady.Part.Tool.CallId,
                    toolReady.Part.Tool.CallId,
                    toolReady.Part.Tool.Name,
                    toolReady.Part.Tool.Arguments)));
                break;

            case AssistantResponseDoneEvent done:
                await FinishResponseAsync(done);
                break;

            case AudioOutput audio:
                await EmitAudioAsync(audio);
                break;

            case byte[] pcm:
                await EmitAudioAsync(new AudioOutput { Audio = pcm });
                break;

            case EndOfResponse end when end.Error is not null:
                await SendAsync(ServerEvent.Serialize(ServerEvent.Error(
                    ErrorCodes.InternalError, end.Error)));
                break;

            case TokenUsageEvent:
            case ResponseGenerationDoneEvent:
            case ResponseFailedEvent:
            case AudioInputCompletedEvent:
                // 侧信道事件，不进线上协议
                break;
        }
    }

    private async Task HandleTranscriptionCompletedAsync(TranscriptionCompletedEvent completed)
    {
        var itemId = _pendingUserItemId;
        TranscriptLog.Transcript("user", completed.Transcript);

        _config.Chat.Append(ChatMessage.User(completed.Transcript, itemId));
        _config.Chat.CommitTurn();

        await SendAsync(ServerEvent.Serialize(
            ServerEvent.TranscriptionCompleted(itemId, completed.Transcript, audioSeconds: _inputAudioSeconds)));

        // 时长属于这一项，转写完就归零——否则下一轮没跑 VAD 的路径会继承上一轮的数字
        _inputAudioSeconds = 0;
        _pendingUserItemId = "item_" + Guid.NewGuid().ToString("N")[..12];

        if (_config.CreateResponseEnabled && _activeResponseId is null)
        {
            _activeResponseId = "resp_" + Guid.NewGuid().ToString("N")[..12];
            BeginAssistantResponse(overrides: null);
            await SendAsync(ServerEvent.Serialize(ServerEvent.ResponseCreated(_activeResponseId)));
            _lmIn.TryAdd(GenerateResponseRequest.Create(_config, responseKey: _activeResponseId), 200);
        }
    }

    /// <summary>
    /// 开启本响应的助手消息项，只在首次输出时发一次。
    ///
    /// 官方客户端靠 <c>output_item.added</c> 建立消息项、靠 <c>content_part.added</c> 建立内容片；
    /// 两者都到位之前的增量没有落点。所以任何 delta（文本或音频）之前都必须先过这里。
    /// </summary>
    private async Task EnsureAssistantItemAsync(string responseId)
    {
        if (_assistantItemOpened || _assistantItemId is null)
        {
            return;
        }

        _assistantItemOpened = true;
        await SendAsync(ServerEvent.Serialize(
            ServerEvent.OutputItemAdded(responseId, _assistantItemId, 0)));
        await SendAsync(ServerEvent.Serialize(
            ServerEvent.ContentPartAdded(responseId, _assistantItemId, 0, 0, _responseWantsAudio)));
    }

    private async Task HandleAssistantOutputAsync(AssistantOutputEvent output)
    {
        // 取消之后迟到的分片必须丢弃：消息项已经关了，再挂上去会写进下一轮的 item，
        // 也会在已经回滚的历史里凭空多出一条助手消息。上游用代际闸门做同样的判断。
        if (_cancelScope.Discarding || _assistantItemId is null)
        {
            return;
        }

        var responseId = output.ResponseKey ?? _activeResponseId ?? "resp_unknown";

        // 按 part 逐个上线：一个 part 一个 delta。音频模态要 strip（念不出首尾空白），
        // 纯文本模态原样保留（客户端可能做 diff 渲染，多删字符会错位）。
        var texts = new List<string>();
        foreach (var part in output.Parts)
        {
            if (part is AssistantTextPart textPart)
            {
                var text = _responseWantsAudio ? textPart.Text.Trim() : textPart.Text;
                if (text.Length > 0)
                {
                    texts.Add(text);
                }
            }
        }

        // Parts 为空的调用点退回用 Text，避免上游只填聚合文本时整段丢失
        if (texts.Count == 0 && output.Parts.Count == 0)
        {
            var text = _responseWantsAudio ? output.Text.Trim() : output.Text;
            if (text.Length > 0)
            {
                texts.Add(text);
            }
        }

        if (texts.Count == 0)
        {
            return;
        }

        await EnsureAssistantItemAsync(responseId);

        foreach (var text in texts)
        {
            // 多片之间补一个空格，否则 "Hello" 与 "world" 会粘成 "Helloworld"。
            // 首片不加，否则整段转写会以空格开头。
            var delta = _responseWantsAudio && _assistantPartCount > 0 ? " " + text : text;
            _assistantPartCount++;
            _assistantTranscript.Append(delta);

            await SendAsync(ServerEvent.Serialize(_responseWantsAudio
                ? ServerEvent.OutputAudioTranscriptDelta(responseId, _assistantItemId!, delta)
                : ServerEvent.OutputTextDelta(responseId, _assistantItemId!, delta)));
        }

        _config.Chat.Append(ChatMessage.Assistant(output.Text, id: _assistantItemId));
        TranscriptLog.Transcript("assistant", output.Text);
    }

    /// <summary>
    /// 发一轮响应的收尾事件：模态 done → content_part.done → output_item.done。
    ///
    /// 正常完成与取消共用这一段，区别只在 <paramref name="completed"/>——
    /// 上游也是同一个 <c>finish_response</c> 处理两种状态，item 状态取
    /// <c>"completed" if status == "completed" else "incomplete"</c>。
    /// 哪些事件该发由 <see cref="ResponseTeardown.Plan"/> 决定。
    /// </summary>
    private async Task EmitTeardownAsync(string responseId, string text, bool completed)
    {
        foreach (var step in ResponseTeardown.Plan(
            _assistantItemOpened, _responseWantsAudio, _audioStreamed, hasText: text.Length > 0))
        {
            var payload = step switch
            {
                TeardownEvent.OutputAudioDone =>
                    ServerEvent.OutputAudioDone(responseId, _assistantItemId!),
                TeardownEvent.OutputAudioTranscriptDone =>
                    ServerEvent.OutputAudioTranscriptDone(responseId, _assistantItemId!, text),
                TeardownEvent.OutputTextDone =>
                    ServerEvent.OutputTextDone(responseId, _assistantItemId!, text),
                TeardownEvent.ContentPartDone =>
                    ServerEvent.ContentPartDone(responseId, _assistantItemId!, text, wantsAudio: _responseWantsAudio),
                TeardownEvent.OutputItemDone =>
                    ServerEvent.OutputItemDone(responseId, _assistantItemId!, text,
                        wantsAudio: _responseWantsAudio, completed: completed),
                _ => throw new ArgumentOutOfRangeException(nameof(step), step, "未知的收尾事件"),
            };

            await SendAsync(ServerEvent.Serialize(payload));
        }
    }

    private async Task FinishResponseAsync(AssistantResponseDoneEvent done)
    {
        var responseId = done.ResponseKey ?? _activeResponseId;
        if (responseId is null)
        {
            // 已取消：取消路径自己负责收口。迟到的完成不得再写线上状态，
            // 否则客户端会收到一个它已经丢弃的 response 的终态。
            _assistantTranscript.Clear();
            return;
        }

        var status = done.Status == ResponseStatus.Incomplete ? "incomplete" : "completed";
        var reason = done.Reason switch
        {
            ResponseIncompleteReason.MaxOutputTokens => "max_output_tokens",
            ResponseIncompleteReason.ContentFilter => "content_filter",
            _ => null,
        };

        var text = _assistantTranscript.ToString();
        await EmitTeardownAsync(responseId, text, completed: status == "completed");

        if (status == "incomplete")
        {
            // 不完整响应要回滚历史，否则下一轮会把没念完的半句当成已说过的话
            _config.Chat.RollbackToCommit();
        }

        await SendAsync(ServerEvent.Serialize(ServerEvent.ResponseDone(responseId, status, reason)));

        _cancelScope.ResponseDone();
        _activeResponseId = null;
        _assistantItemId = null;
        _assistantItemOpened = false;
        _assistantPartCount = 0;
        _audioStreamed = false;
        _assistantTranscript.Clear();
    }

    private async Task EmitAudioAsync(AudioOutput audio)
    {
        if (_cancelScope.Discarding || audio.CleanupOnly)
        {
            return;
        }

        var responseId = audio.ResponseKey ?? _activeResponseId ?? "resp_unknown";

        // 音频可能先于文本到达：item 必须先开，否则 delta 挂不到任何消息项上
        await EnsureAssistantItemAsync(responseId);

        if (_assistantItemId is null)
        {
            return;
        }

        await SendAsync(ServerEvent.Serialize(ServerEvent.OutputAudioDelta(
            responseId, _assistantItemId, Convert.ToBase64String(audio.Audio))));

        // 一旦真的发过音频，收尾就必须回一个 output_audio.done 把流关掉；
        // 没发过音频的响应（纯工具调用、TTS 没出样本）不能发，否则客户端会收到一段静音。
        _audioStreamed = true;
    }

    private async Task SendAsync(string payload)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _stopping.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 客户端断开不是错误
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task SafeAwait(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await ShutdownPipelineAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _sendLock.Dispose();
        _socket.Dispose();
    }
}
