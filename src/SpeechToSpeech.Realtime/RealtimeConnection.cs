using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpeechToSpeech.Audio.Diarization;
using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Text;
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

    /// <summary>
    /// WebRTC 会话槽位数（上游 <c>num_pipelines</c>，默认 1；超出拒绝并回 503）。
    /// WS 传输不受此限——C# 每连接独立管线，见差异账。
    /// </summary>
    public int WebRtcSessionSlots { get; init; } = 1;
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
    private readonly Transports.SessionTransport _transport;
    private readonly RealtimeServerOptions _options;
    private readonly BackendContext _backends;

    private readonly RuntimeConfig _config;
    private readonly CancelScope _cancelScope = new();
    private readonly SpeculativeTurnTracker _speculativeTurns = new();
    private readonly TurnLatencyStore _latency = new();

    // 连接级会话标识。时延账按会话清理，角色与上游的 conn_id 相同。
    private readonly string _sessionId = "conn_" + Guid.NewGuid().ToString("N");

    // 说话产生的 E2E 名额，按（轮次, 修订）共享。
    // 同一条链上的工具回答共用一个名额，否则一句话会被量出两次 E2E。
    private readonly Dictionary<(string TurnId, int Revision), SpeechLatencyAnchor> _speechAnchors = [];

    // 客户端在 response.create 里自带的 metadata。原样保留到终态，键序也不动。
    private IReadOnlyDictionary<string, string>? _responseClientMetadata;

    /// <summary>
    /// 每条响应「真正到达模型的工具结果 id」，按 response_key 挂。
    ///
    /// 按响应键而不是轮次：同一轮里工具调用与它的口头回答是两条响应，混在一起会让
    /// 客户端以为第二段也带了那些结果，于是跳过重投。
    /// </summary>
    private readonly Dictionary<string, List<string>> _responseToolInputs = new(StringComparer.Ordinal);

    /// <summary>
    /// 本响应已发出的函数调用数量。上游 <c>pending_function_calls</c> 的作用面。
    ///
    /// 它不直接参与协议载荷，只影响一件事：语音 E2E 名额要不要关。
    /// 带出待执行调用却已 completed 的响应不关名额——同一条链上还有回答要量。
    /// </summary>
    private int _pendingFunctionCalls;

    /// <summary>
    /// 输出项下标的状态机，对应上游 <c>_output_part_context</c> 的三个字段。
    ///
    /// 连续文本共享一个消息项；每个函数调用各开新项；函数调用之后的文本再开新项——
    /// 下标按到达顺序递增，终态 <c>response.output</c> 按它排序，与流式事件的下标一致。
    /// </summary>
    private int _nextOutputIndex;
    private int? _currentOutputIndex;
    private string? _currentOutputKind;

    /// <summary>
    /// 一段连续文本（一个助手消息项）的累积状态。
    /// 工具调用会把文本链切断，之后的新文本属于新的项。
    /// </summary>
    private sealed record PendingTextOutput(int OutputIndex, string ItemId)
    {
        public StringBuilder Text { get; } = new();
        public int PartCount { get; set; }
        public bool Opened { get; set; }
    }

    private readonly List<PendingTextOutput> _pendingTextOutputs = [];

    /// <summary>
    /// 本响应发出过的函数调用项，按 output_index 记。
    /// item 在到达时定格（本实现的调用一律 completed），终态 <c>response.output</c> 直接复用——
    /// 与 done 事件里那份逐字段相同，正是上游 <c>_build_output_items</c> 的口径。
    /// </summary>
    private readonly Dictionary<int, JsonObject> _pendingFunctionCallItems = new();

    // 音频与事件队列
    private readonly BlockingCollection<object> _audioIn = new(new ConcurrentQueue<object>(), 64);

    /// <summary>
    /// VAD 的输入队列。启用流式分离时，音频先经 <c>DiarizationHandler</c> 走这里；
    /// 关闭时这一级不存在，VAD 直接吃 <see cref="_audioIn"/>（与未接分离时逐字节相同）。
    /// </summary>
    private readonly BlockingCollection<object> _vadIn = new(new ConcurrentQueue<object>(), 64);

    private readonly BlockingCollection<object> _vadOut = new(new ConcurrentQueue<object>(), 64);
    private readonly BlockingCollection<object> _sttOut = new(new ConcurrentQueue<object>(), 128);
    private readonly BlockingCollection<object> _lmIn = new(new ConcurrentQueue<object>(), 32);
    private readonly BlockingCollection<object> _lmOut = new(new ConcurrentQueue<object>(), 256);
    private readonly BlockingCollection<object> _ttsIn = new(new ConcurrentQueue<object>(), 256);
    private readonly BlockingCollection<object> _ttsOut = new(new ConcurrentQueue<object>(), 512);
    private readonly BlockingCollection<object> _textEvents = new(new ConcurrentQueue<object>(), 512);

    /// <summary>
    /// 口型级输入队列。仅在启用 <c>--enable_visemes</c> 时创建：那时 TTS 的输出去这里，
    /// 由 <c>StvHandler</c> 重分块后写进 <see cref="_ttsOut"/>（上游同样把
    /// <c>tts_output_queue</c> 换成中间队列）。关闭时为 null，发送路径与未接口型时逐字节相同。
    /// </summary>
    private BlockingCollection<object>? _stvIn;

    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Thread> _threads = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private readonly byte[] _receiveBuffer = new byte[64 * 1024];
    private readonly MemoryStream _messageBuffer = new();

    private string? _activeResponseId;
    private string _pendingUserItemId = "item_" + Guid.NewGuid().ToString("N");

    // 当前响应所属的轮次。响应收口时要按它 close 提交态，否则这一轮会一直算「已应答」
    // 却又不能复开——用户紧接着说下一句就会被判成陈旧。
    private string? _responseTurnId;
    private int? _responseTurnRevision;

    /// <summary>当前正在累积的文本项（即 <see cref="_pendingTextOutputs"/> 的最后一项）。
    /// 音频字节流与转写增量都挂在它上面；函数调用到达后，下一片文本属于新的项。</summary>
    private PendingTextOutput? _currentTextOutput;

    /// <summary>上一个已上线会话项的 id，用于 <c>previous_item_id</c> 顺序链。</summary>
    private string? _lastItemId;

    /// <summary>当前输入项的音频时长（秒），来自 VAD 的 speech_stopped，转写完成时进 usage。</summary>
    private double _inputAudioSeconds;

    /// <summary>本响应是否真的往线上发过音频。决定收尾时要不要关音频流。</summary>
    private bool _audioStreamed;

    /// <summary>本响应解析到的音色。开响应时定格，避免会话中途改配置导致 created 与 done 报不同音色。</summary>
    private string? _responseVoice;

    /// <summary>本响应是否出音频。由 <c>response.create</c> 的 <c>output_modalities</c> 决定。</summary>
    private bool _responseWantsAudio = true;

    /// <summary>
    /// 本响应是否带外（<c>response.create</c> 的 <c>conversation="none"</c>）。
    ///
    /// 带外响应的输出照常上线，但四处与「接进会话」有关的动作全部跳过：
    /// 不写历史、<c>conversation_id</c> 报 null、metadata 不挂工具输入与时延、不占语音 E2E 名额。
    /// 开响应时定格，收口/取消时清零。
    /// </summary>
    private bool _responseOutOfBand;

    /// <summary>
    /// 连接级会话标识。上游每个连接生成一次、全生命周期不变，<c>response.conversation_id</c> 报的就是它。
    /// 每轮新生成会让客户端把同一段对话当成多条会话，历史回放与计费都跟着断。
    /// </summary>
    private readonly string _conversationId = "conv_" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// 本响应的 token 记账。收口时进 <c>response.usage</c> 并归零。
    /// 见 <see cref="ResponseUsageLedger"/>：按 response_key 挂起 + 已收口丢弃的规则都在那里。
    /// </summary>
    private readonly ResponseUsageLedger _usage = new();

    public RealtimeConnection(Transports.SessionTransport transport, RealtimeServerOptions options, BackendContext backends)
    {
        _transport = transport;
        _options = options;
        _backends = backends;

        // 状态机的每次真实等待都折算成 hold_s。观察者挂在账本上而不是让每个等待方各自记账：
        // 多个 worker 会同时等同一段，各自记会重复累加（区间并集的并集才是对的总时长）。
        _speculativeTurns.WaitObserver = (turnId, revision, startedAt, endedAt) =>
            _latency.RecordSmartWait(turnId, revision, startedAt, endedAt);

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
            var sendTask = SendLoopAsync(_stopping.Token);
            if (_transport.Kind == Transports.TransportKinds.WebSocket)
            {
                // WebSocket 模式：连接生命周期由客户端消息流驱动；
                // session.created 在握手后立刻发出。
                await SendSessionCreatedAsync();
                await ReceiveLoopAsync(_stopping.Token);
            }
            else
            {
                // WebRTC 模式：没有读循环——客户端事件经 datachannel 回调进
                // DispatchClientEventAsync，音频经 RTP 回调进 AppendInboundAudio，
                // 对端断开时由 WebRtcSession 调 NotifyTransportClosed。
                // session.created 由端点的 _on_open 回调（datachannel 开启）触发，
                // 与上游一致：频道未开时发了也到不了客户端。
                await WaitUntilClosedAsync();
            }

            _stopping.Cancel();
            await SafeAwait(sendTask);
        }
        finally
        {
            await ShutdownPipelineAsync();
        }
    }

    private readonly TaskCompletionSource _transportClosed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>WebRTC 模式的连接等待点：对端断开即返回。</summary>
    private Task WaitUntilClosedAsync() => _transportClosed.Task;

    /// <summary>传输关闭回调：唤醒 RunAsync 并触发整条连接的清理。</summary>
    public void NotifyTransportClosed()
    {
        _transportClosed.TrySetResult();
        _stopping.Cancel();
    }

    private int _sessionCreatedSent;

    /// <summary>
    /// 发送 <c>session.created</c>（幂等）。WebSocket 模式由 <see cref="RunAsync"/>
    /// 在握手后调用；WebRTC 模式由端点在 datachannel 开启回调里调用——
    /// 对齐上游 <c>_on_open</c> 只发一次的口径。
    /// </summary>
    public async Task SendSessionCreatedAsync()
    {
        if (Interlocked.Exchange(ref _sessionCreatedSent, 1) != 0)
        {
            return;
        }

        await SendAsync(ServerEvent.Serialize(ServerEvent.SessionCreated(SessionId, _config.SnapshotSession())))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// WebRTC 数据频道回调入口：与 WebSocket 读循环共用同一条分发路径，
    /// 包括 JSON 解析、未知事件、传输门控（顺序由调用方保证）。
    /// </summary>
    public Task DispatchClientEventAsync(string payload) => DispatchAsync(payload);

    /// <summary>
    /// WebRTC 媒体轨回调入口：远端音频（已重采样到管线采样率的 PCM16）直进 VAD，
    /// 等价于 WebSocket 模式下 input_audio_buffer.append 的入队路径。
    /// </summary>
    public void AppendInboundAudio(byte[] pcm16)
    {
        var samples = AudioUtil.FromPcm16(pcm16);
        _audioIn.TryAdd(new VadInput(samples, _config.InputSampleRate, _config), 200);
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

        // 流式 STT 后端要按连接挂资源（输出队列 / 投机轮次 tracker）。
        // Attach 返回的 sink 交给 VAD：说话过程中的音频直推远端转写会话。
        IStreamingSttSink? streamingSink = null;
        if (stt is IStreamAwareSttBackend streamAware)
        {
            streamingSink = streamAware.Attach(_sttOut, _speculativeTurns, token);
        }

        // 流式说话人分离：VAD 之前的一级（分离要连续音频，只有原始输入有）。它同时产出
        // 「谁在什么时候说话」的时间线，交给 STT 做转写归因。关闭时整级不存在，
        // VAD 直接吃原始输入队列——两条路径的音频字节完全相同。
        var vadInput = _audioIn;
        SpeakerTimeline? speakerTimeline = null;
        if (_backends.Diarization.Enabled)
        {
            if (_backends.SpeakerSegmentation is { } segmentation && _backends.SpeakerEmbedding is { } embedding)
            {
                speakerTimeline = new SpeakerTimeline(new SpeakerTimelineOptions());
                var diarizerConfig = _backends.Diarization.BuildConfig(segmentation);
                var diarizationOptions = _backends.Diarization;
                var timeline = speakerTimeline;
                vadInput = _vadIn;
                AddThread("diarization", () => new DiarizationHandler(
                    token, _audioIn, _vadIn, segmentation, embedding, diarizerConfig, timeline,
                    diarizationOptions, _backends.Log).Run());
            }
            else
            {
                // 打开了分离却缺一个模型接缝：这一级跳过，转写照发、无说话人标签。
                // 与「打开口型却没有模型」不同，这里不是配置错误而是能力缺口——
                // 缺的是声纹引擎（D033），用户没有给错参数。
                _backends.Log(
                    "warning: 已启用流式说话人分离，但本构建缺少" +
                    (_backends.SpeakerSegmentation is null ? "分割模型" : "声纹引擎") +
                    "；本会话转写不带说话人标签。");
            }
        }

        AddThread("vad", () => new VadHandler(
            token, vadInput, _vadOut, _textEvents, vad, _cancelScope, _speculativeTurns,
            _backends.TurnEnd, _backends.TurnEndClassifier, _backends.Log, _latency, streamingSink).Run());
        AddThread("stt", () => new SttHandler(
            token, _vadOut, _sttOut, stt, _cancelScope, _speculativeTurns, _latency,
            speakerTimeline, _backends.Diarization.SpeakerIdPrefix).Run());
        AddThread("notifier", () => new TranscriptionNotifier(
            token, _sttOut, _lmIn, _textEvents, _config.InputAudioTranscriptionSnapshotsEnabled,
            _cancelScope, _speculativeTurns).Run());
        AddThread("llm", () => new LanguageModelHandler(token, _lmIn, _lmOut, llm, _cancelScope, _speculativeTurns, _latency).Run());
        AddThread("processor", () => new LmOutputProcessor(
            token, _lmOut, _ttsIn, _textEvents, _cancelScope, _speculativeTurns,
            new AssistantLanguageProbe(_backends.LanguageDetector),
            _backends.DetectLlmOutputLanguage).Run());

        // 口型级：开启时它是 TTS 与发送循环之间的一级（上游 enable_visemes 的同一处插入）。
        // 关闭时整级不存在——AudioOutput.Visemes 无生产者，visemes 事件永不发出。
        var visemeExtractor = BackendRegistry.CreateViseme(_backends);
        var ttsOutput = visemeExtractor is null ? _ttsOut : new BlockingCollection<object>(new ConcurrentQueue<object>(), 256);
        if (visemeExtractor is not null)
        {
            _stvIn = ttsOutput;
        }

        AddThread("tts", () => new TtsHandler(token, _ttsIn, ttsOutput, tts, _cancelScope, _speculativeTurns, _latency).Run());
        if (visemeExtractor is not null)
        {
            var extractor = visemeExtractor;
            AddThread("stv", () => new StvHandler(
                token, ttsOutput, _ttsOut, extractor, _cancelScope, _backends.Tts.SampleRate).Run());
        }

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
        List<BlockingCollection<object>> queues =
        [
            _audioIn, _vadIn, _vadOut, _sttOut, _lmIn, _lmOut, _ttsIn,
        ];
        if (_stvIn is not null)
        {
            queues.Add(_stvIn);
        }

        foreach (var queue in queues)
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

        queues.Add(_ttsOut);
        queues.Add(_textEvents);
        foreach (var queue in queues)
        {
            queue.Dispose();
        }

        await Task.CompletedTask;
    }

    // ── 读循环 ────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var ws = (Transports.WebSocketTransport)_transport;
        while (!cancellationToken.IsCancellationRequested && ws.IsOpen)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(_receiveBuffer, cancellationToken);
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
            // 上游 parse_client_event 对缺 type 一律返回 None → unknown_or_invalid_event；
            // 消息里 raw.get('type') 是 None，f-string 渲染成字面 "None"（D027）。
            var missingTypeEventId = node is JsonObject badJson
                ? badJson["event_id"]?.GetValue<string>()
                : null;
            await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                "unknown_or_invalid_event", "Unknown or invalid event: None", missingTypeEventId)));
            return;
        }

        switch (type)
        {
            case EventNames.InputAudioBufferAppend:
                // WebRTC 模式音频走媒体轨，显式 append 是协议错误（上游同口径拒绝）。
                if (_transport.Kind == Transports.TransportKinds.WebRtc)
                {
                    await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                        Transports.TransportGate.InvalidEventForTransport,
                        Transports.TransportGate.AppendNotSupportedOverWebRtc,
                        json["event_id"]?.GetValue<string>())));
                    break;
                }

                HandleAudioAppend(json);
                break;

            case EventNames.OutputAudioBufferClear:
                // 未播音频在服务端 paced 轨道里，clear 只对 WebRTC 有意义；
                // WebSocket 模式未播音频在客户端，服务端无从清起（上游同口径）。
                if (_transport.Kind != Transports.TransportKinds.WebRtc)
                {
                    await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                        Transports.TransportGate.InvalidEventForTransport,
                        Transports.TransportGate.ClearWebSocketOnly,
                        json["event_id"]?.GetValue<string>())));
                    break;
                }

                FlushPendingAudio();
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
                _pendingUserItemId = "item_" + Guid.NewGuid().ToString("N");
                break;

            default:
                // 上游未知 type 同样归 unknown_or_invalid_event（parse_client_event 返回 None）。
                await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                    "unknown_or_invalid_event", $"Unknown or invalid event: {type}",
                    json["event_id"]?.GetValue<string>())));
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

        AppendInboundAudio(pcm);
    }

    /// <summary>
    /// WebRTC 的 <c>output_audio_buffer.clear</c>：丢掉还没 paced 出去的音频。
    ///
    /// 对齐上游 <c>_flush_queue(unit.output_queue, preserve=_keep_non_audio_output)</c>：
    /// 只清音频，队列里混排的文本/收尾事件原样保留——那些不依赖播放节奏，
    /// 丢了会让客户端看不到这轮响应的收口。
    /// </summary>
    private void FlushPendingAudio()
    {
        var keep = new List<object>();
        while (_ttsOut.TryTake(out var item))
        {
            if (item is not AudioOutput)
            {
                keep.Add(item);
            }
        }

        foreach (var item in keep)
        {
            _ttsOut.TryAdd(item, 200);
        }

        _transport.DiscardPendingAudio();
    }

    private async Task HandleSessionUpdateAsync(JsonObject json)
    {
        // 上游 handler 对 session 缺失返回 None——不发错误事件，静默忽略（D027）。
        if (json["session"] is not JsonObject session)
        {
            return;
        }

        var type = session["type"]?.GetValue<string>();
        if (type == "transcription")
        {
            // 上游 Union[RealtimeSession, TranscriptionSession]：transcription 会话
            // 是合法 pydantic 模型，handler 里显式拒绝（D027）。
            await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                "invalid_session_type",
                "Only 'realtime' session type is supported; transcription sessions are not.",
                json["event_id"]?.GetValue<string>())));
            return;
        }

        if (type is not null && type != "realtime")
        {
            // 其它任意 type 两个 union 分支都不匹配 → pydantic 校验失败 →
            // parse_client_event 返回 None → unknown_or_invalid_event（D027）。
            await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                "unknown_or_invalid_event", "Unknown or invalid event: session.update",
                json["event_id"]?.GetValue<string>())));
            return;
        }

        _config.ApplySessionUpdate(session);
        await SendAsync(ServerEvent.Serialize(ServerEvent.SessionUpdated(_config.SnapshotSession())));
    }

    private async Task HandleConversationItemCreateAsync(JsonObject json)
    {
        if (json["item"] is not JsonObject item)
        {
            // 上游 ConversationItemCreateEvent.item 是必填字段，缺失走 pydantic
            // 校验失败 → unknown_or_invalid_event（D027）。
            await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                "unknown_or_invalid_event", "Unknown or invalid event: conversation.item.create",
                json["event_id"]?.GetValue<string>())));
            return;
        }

        var itemType = item["type"]?.GetValue<string>();
        var itemId = item["id"]?.GetValue<string>() ?? "item_" + Guid.NewGuid().ToString("N");

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
                // 上游 _apply_item catch ChatItemError → invalid_conversation_item；
                // 消息与 ChatItemParser（对齐上游 add_supported_item）同口径（D027）。
                await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                    "invalid_conversation_item", $"Unsupported item type: {itemType}",
                    json["event_id"]?.GetValue<string>())));
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
            // 上游 ConversationDeleteEvent.item_id 必填，缺失走 pydantic 校验失败
            // → unknown_or_invalid_event（D027）。
            await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                "unknown_or_invalid_event", "Unknown or invalid event: conversation.item.delete",
                json["event_id"]?.GetValue<string>())));
            return;
        }

        if (_config.Chat.Delete(itemId) is null)
        {
            await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                "conversation_item_not_found",
                $"No conversation item with id '{itemId}' found.",
                json["event_id"]?.GetValue<string>())));
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
            // 上游 handle_response_create 开头的活动响应闸（response.py:815），
            // 消息与 _type 逐字对齐（D027）。
            await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                "conversation_already_has_active_response",
                "Cannot create response while another response is in progress or pending.",
                json["event_id"]?.GetValue<string>())));
            return;
        }

        RealtimeResponseCreateParams? overrides = null;
        if (json["response"] is JsonObject response)
        {
            overrides = new RealtimeResponseCreateParams
            {
                Instructions = response["instructions"]?.GetValue<string>(),
                Temperature = response["temperature"]?.GetValue<double>(),
                MaxOutputTokens = response["max_output_tokens"]?.GetValue<int>(),
                OutputModalities = ReadModalities(response),
                Voice = ReadVoice(response),
                Conversation = response["conversation"]?.GetValue<string>(),
                Input = ReadInputItems(response),
            };
            _responseClientMetadata = ResponseMetadata.ReadClientMetadata(response);
        }
        else
        {
            _responseClientMetadata = null;
        }

        // 带内 input 先校验后入会话（上游深拷贝进 candidate_chat 校验，全部通过
        // 才 _append_item）；任何一步失败都是同步 error 事件，响应根本不创建。
        // 带外不碰会话——input 随请求走，LM 层校验失败走 response.failed。
        List<ChatMessage>? inbandItems = null;
        if (overrides?.Input is { Count: > 0 } inputNodes && !overrides.IsOutOfBand)
        {
            inbandItems = [];
            foreach (var node in inputNodes)
            {
                if (!ChatItemParser.TryParse(node, out var message, out var itemError))
                {
                    // 上游 make_error 形状：error.type=invalid_input_item，无 code 字段；
                    // send_correlated 把客户端 event_id 写进 error.event_id（D027）。
                    await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                        "invalid_input_item", itemError ?? "invalid input item",
                        json["event_id"]?.GetValue<string>())));
                    return;
                }

                inbandItems.Add(message!);
            }

            // input 带进来的 function_call 若没有（本批或会话里）对应的 output，
            // 就不能开新响应——模型会把它当成已应答（上游 function_call_output_pending）。
            if (ChatSession.HasPendingToolCalls(_config.Chat.Snapshot().Concat(inbandItems)))
            {
                await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                    "function_call_output_pending",
                    "Cannot create a response while function call outputs are pending.",
                    json["event_id"]?.GetValue<string>())));
                return;
            }
        }

        _cancelScope.NewResponse();
        _activeResponseId = "resp_" + Guid.NewGuid().ToString("N");

        BeginAssistantResponse(overrides);
        // 带外响应不建时延账：上游 skip bind_response_latency_tracker，终态载荷因此
        // 不带 speech_to_speech.turn_latency——对一份临时上下文的侧问，没有「这轮说完了」可量。
        if (!_responseOutOfBand)
        {
            BeginLatency(_activeResponseId);
        }

        if (inbandItems is not null)
        {
            foreach (var message in inbandItems)
            {
                _config.Chat.Append(message);
            }

            // 客户端带来的条目算一轮已提交输入：不提交的话，下一次语音轮收口
            // 的回滚会把这些条目一起卷走（上游 add_item 直接进 chat，无此层）。
            _config.Chat.CommitTurn();
        }

        await SendAsync(ServerEvent.Serialize(ServerEvent.ResponseCreated(SnapshotOf(_activeResponseId, "in_progress"))));

        _lmIn.TryAdd(
            GenerateResponseRequest.Create(
                _config,
                overrides,
                responseKey: _activeResponseId,
                inputItems: overrides?.Input),
            200);
    }

    /// <summary>读 <c>response.input</c> 数组，逐项深拷贝原样保留；未携带返回 null。</summary>
    private static List<JsonObject>? ReadInputItems(JsonObject response)
        => response["input"] is not JsonArray items ? null : [.. items.OfType<JsonObject>().Select(node => (JsonObject)node.DeepClone())];

    /// <summary>读 <c>output_modalities</c>。允许是字符串数组，也容忍单个字符串。</summary>
    private static IReadOnlyList<string>? ReadModalities(JsonObject response)
        => response["output_modalities"] switch
        {
            JsonArray array => [.. array.Select(node => node?.GetValue<string>() ?? "").Where(s => s.Length > 0)],
            JsonValue value when value.TryGetValue<string>(out var single) => [single],
            _ => null,
        };

    /// <summary>
    /// 读 <c>audio.output.voice</c>。官方 schema 里音色既可以是内置名的字符串，
    /// 也可以是 <c>{"id": "..."}</c> 形式的自定义音色对象——两种都认，取到 id 为止。
    /// </summary>
    private static string? ReadVoice(JsonObject response)
        => response["audio"]?["output"]?["voice"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var name) && name.Length > 0 => name,
            JsonObject custom => custom["id"]?.GetValue<string>(),
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
        _currentTextOutput = null;
        _pendingTextOutputs.Clear();
        _pendingFunctionCallItems.Clear();
        _nextOutputIndex = 0;
        _currentOutputIndex = null;
        _currentOutputKind = null;
        _audioStreamed = false;
        _pendingFunctionCalls = 0;
        _responseWantsAudio = overrides?.WantsAudio ?? true;
        // 带外响应不携带轮次身份：上游把 GenerateResponseRequest 的 turn_id 置 None，
        // 投机陈旧闸门对它永远放行（侧问不该被说话中的新一轮吞掉），时延账也不挂轮次。
        _responseOutOfBand = overrides?.IsOutOfBand ?? false;
        if (_responseOutOfBand)
        {
            _responseTurnId = null;
            _responseTurnRevision = null;
        }
        // 音色在开响应时定格：会话配置中途可变，若每次取实时值，created 与 done 会报不同音色。
        // 逐响应覆盖优先于会话配置（上游 _build_response 同一顺序），TTS 侧走同一个 ResolveVoice。
        _responseVoice = RealtimeResponseCreateParams.ResolveVoice(overrides, _config);
        _usage.BeginResponse();
    }

    /// <summary>
    /// 组装 <c>response</c> 载荷。<c>response.created</c> 与 <c>response.done</c> 共用这一份口径，
    /// 对应上游的 <c>_build_response</c>。
    /// </summary>
    private ResponseSnapshot SnapshotOf(string responseId, string status, string? reason = null)
        => new()
        {
            Id = responseId,
            Status = status,
            Reason = reason,
            Voice = _responseVoice,
            // 带外响应不接进任何会话：报 null（线路省键），而不是本连接的会话 id。
            ConversationId = _responseOutOfBand ? null : _conversationId,
            Metadata = MetadataOf(responseId, status),
            // 两个值互斥：audio 已隐含带转写文本。
            OutputModalities = [_responseWantsAudio ? "audio" : "text"],
            Output = status == "in_progress" ? [] : TerminalOutput(status),
            Usage = _usage.Current,
        };

    /// <summary>
    /// 组装 <c>response.metadata</c>。
    ///
    /// <c>response.created</c> 只带客户端自带的那份——服务端测量要等到终态才有数字，
    /// 中途塞一个半成品进去，客户端会拿它当最终值缓存起来。
    /// </summary>
    private JsonObject? MetadataOf(string responseId, string status)
    {
        string? latency = null;
        if (status != "in_progress")
        {
            var tracker = _latency.GetResponse(responseId);
            if (tracker is not null)
            {
                tracker.Status = status;
                latency = tracker.MetadataPayloadJson(responseId, status);
            }
        }

        return ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Client = _responseClientMetadata,
            Status = status,
            ToolInputCallIds = _responseToolInputs.TryGetValue(responseId, out var toolInputs)
                ? toolInputs
                : [],
            OutOfBand = _responseOutOfBand,
            LatencyPayload = latency,
        });
    }

    /// <summary>
    /// 开一条响应的时延账。响应键在入队之前就要存在：worker 只查不建，
    /// 否则被取消的 worker 会给一条没人要的响应留下永远不结算的账。
    /// </summary>
    private void BeginLatency(string responseId)
    {
        var source = _responseTurnId is null ? TurnLatencySource.Client : TurnLatencySource.Speech;
        var tracker = _latency.GetOrCreateResponse(
            responseId, _responseTurnId, _responseTurnRevision, _sessionId);
        tracker.Source ??= source;
        if (source != TurnLatencySource.Speech || _responseTurnId is null)
        {
            return;
        }

        var key = (_responseTurnId, _responseTurnRevision ?? 0);
        if (!_speechAnchors.TryGetValue(key, out var anchor))
        {
            anchor = new SpeechLatencyAnchor();
            _speechAnchors[key] = anchor;
        }

        anchor.Requested = true;
        tracker.SpeechAnchor = anchor;
    }

    /// <summary>
    /// 结账：写一行时延记录，然后把账从表里摘掉。
    ///
    /// 必须先于 <c>_activeResponseId</c> 的清空——摘早了终态载荷就没数字，
    /// 摘晚了表里会留下一条永远不结算的账。
    /// </summary>
    private void EndLatency(string responseId, string status)
    {
        var tracker = _latency.Pop(responseId, _sessionId);
        if (tracker is null)
        {
            return;
        }

        tracker.Status = status;

        // 说完话才算「这条链的 E2E 已经量过」。取消或失败的那一轮不该占着名额——
        // 名额被占着的时候后续响应永远量不到 E2E。
        //
        // completed 但带出了待执行函数调用的**也不关**：同一条链上还有第二段回答要量，
        // 关了第二个响应就只能量出 null。判据见 SpeechLatencyAnchor.ShouldClose。
        if (SpeechLatencyAnchor.ShouldClose(status, _pendingFunctionCalls))
        {
            tracker.SpeechAnchor?.Close();
        }

        if (tracker.FormatLogLine() is { } line)
        {
            TranscriptLog.Info($"{line} response_key={responseId}");
        }
    }


    /// <summary>
    /// 终态 <c>response.output</c>。
    ///
    /// 上游只列真正生成过的输出项——没有输出就是空数组，不会为了「数组非空」造一个空 item。
    /// 文本项与函数调用项按 output_index 合并排序，与流式事件的下标一致
    /// （上游 <c>_build_output_items</c> 的口径）；没有音频时 transcript 是唯一的内容来源，
    /// 所以纯文本模态即使没发过音频也照常进 output。
    /// </summary>
    private IReadOnlyList<JsonObject> TerminalOutput(string status)
    {
        if (_pendingTextOutputs.Count == 0 && _pendingFunctionCallItems.Count == 0)
        {
            return [];
        }

        var itemStatus = status == "completed" ? "completed" : "incomplete";
        var messages = _pendingTextOutputs
            .Select(pending => (
                pending.OutputIndex,
                ServerEvent.AssistantMessageItem(
                    pending.ItemId,
                    itemStatus,
                    _responseWantsAudio,
                    pending.Text.ToString())))
            .ToList();
        // 函数调用项的状态在到达时就定格了（本实现一律 completed），
        // 与 done 事件里那份逐字段相同——终态直接复用，不再二次加工。
        var calls = _pendingFunctionCallItems.Select(kv => (kv.Key, kv.Value));

        return ResponseOutput.Merge(messages, calls);
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
            await EmitTeardownAsync(responseId, completed: false);
            // 取消也带完整载荷：上游 cancel 走的是同一个 finish_response，客户端读到的
            // conversation_id / usage / 已生成的消息项与正常结束没有区别，只是 status 不同。
            ApplyPendingTokenUsage(responseId);
            await SendAsync(ServerEvent.Serialize(
                ServerEvent.ResponseDone(SnapshotOf(responseId, "cancelled", "client_cancelled"))));
            EndLatency(responseId, "cancelled");

            // 说了一半的话不能留在历史里——否则下一轮 LLM 会把它当成已经说出口的回复。
            // 上游同样只在 status == "completed" 时 finalize，其余状态回滚。
            // 带外响应没写过历史，跳过——回滚会误伤提交点之后的既有条目。
            if (!_responseOutOfBand)
            {
                _config.Chat.RollbackToCommit();
            }

            // 取消也走同一条收尾（上游 handle_response_cancel → finish_response），
            // 提交态必须一起释放，否则这一轮永远停在 ANSWERING。
            // 带外响应关 null（同 finish_response 的理由：轮次身份可能已被新语音轮覆写）。
            _speculativeTurns.Close(
                _responseOutOfBand ? null : _responseTurnId,
                _responseOutOfBand ? null : _responseTurnRevision);
            CloseResponseKey(responseId);
        }

        _responseTurnId = null;
        _responseTurnRevision = null;
        _activeResponseId = null;
        // 取消后不能再往已关闭的消息项里追加——不清掉的话，迟到的分片会写进下一轮的 item
        _currentTextOutput = null;
        _pendingTextOutputs.Clear();
        _pendingFunctionCallItems.Clear();
        _nextOutputIndex = 0;
        _currentOutputIndex = null;
        _currentOutputKind = null;
        _audioStreamed = false;
        // 上游 response.cancel 的 dispatch 对所有传输调 discard_pending_audio：
        // WebSocket 是 no-op，WebRTC 必须 flush 服务端 paced 轨道，否则打断后
        // 已编码排队的那段音频会在下一个响应唤醒轨道时重新冒出来。
        _transport.DiscardPendingAudio();
        _pendingFunctionCalls = 0;
        _responseOutOfBand = false;
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

                // 记下这一轮的标识：助手响应收口时按它 close，释放「已应答」的提交态
                _responseTurnId = stopped.TurnId;
                _responseTurnRevision = stopped.TurnRevision;

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
                {
                    // 与文本、音频同一条代际闸门：打断之后迟到的调用属于被废弃的响应，
                    // 上线它会让客户端对一个已经取消的响应执行函数。
                    if (_cancelScope.IsDiscardable(toolReady.CancelGeneration))
                    {
                        break;
                    }

                    // 计数只服务一件事：这条响应带出了待执行的函数调用，语音 E2E 名额不能关——
                    // 同一条链上的第二段回答还要量。
                    _pendingFunctionCalls++;
                    await EmitFunctionCallAsync(toolReady);
                    break;
                }

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
                // 上游 service.py 对 EndOfResponse.error 的出口：make_error(message, "response_failed")。
                // 这条不经过 handle_client_event 的 send_correlated，不带关联 event_id（D027）。
                await SendAsync(ServerEvent.Serialize(ServerEvent.TypeError(
                    "response_failed", end.Error)));
                break;

            case TokenUsageEvent usage:
                HandleTokenUsage(usage);
                break;

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

        // 空转写不进历史：把一句空话写进上下文，模型下一轮会莫名其妙地"接话"。
        // 上游同一处置，并且顺手把这一轮的待定测量摘掉——不会有响应来认领它。
        if (completed.Transcript.Length > 0)
        {
            // 说话人标签只进模型上下文，不进线上的转写事件：客户端拿到的是用户说的原话，
            // 而模型需要知道这句话是谁说的（上游 SpeakerAttribution.for_llm 正是这个口径）。
            var spoken = completed.SpeakerAttribution is { } attribution
                ? $"[{attribution.SpeakerId}] {completed.Transcript}"
                : completed.Transcript;
            _config.Chat.Append(ChatMessage.User(spoken, itemId));
            _config.Chat.CommitTurn();
        }
        else
        {
            _latency.DiscardPendingTurn(completed.TurnId, completed.TurnRevision);
        }

        await SendAsync(ServerEvent.Serialize(
            ServerEvent.TranscriptionCompleted(itemId, completed.Transcript, audioSeconds: _inputAudioSeconds)));

        // 时长属于这一项，转写完就归零——否则下一轮没跑 VAD 的路径会继承上一轮的数字
        _inputAudioSeconds = 0;
        _pendingUserItemId = "item_" + Guid.NewGuid().ToString("N");

        // 空转写不触发回复（上游：`if queue and transcript and create_response_enabled`）。
        if (_config.CreateResponseEnabled && completed.Transcript.Length > 0 && _activeResponseId is null)
        {
            _activeResponseId = "resp_" + Guid.NewGuid().ToString("N");
            BeginAssistantResponse(overrides: null);
            // 语音触发的响应没有客户端 metadata
            _responseClientMetadata = null;
            BeginLatency(_activeResponseId);
            await SendAsync(ServerEvent.Serialize(ServerEvent.ResponseCreated(SnapshotOf(_activeResponseId, "in_progress"))));
            // 轮次标识随请求一起走：时延账要按它把 STT 阶段的测量并进这条响应，
            // 语音 E2E 名额也挂在（轮次,修订）上。只给 response_key 会丢掉这两层。
            _lmIn.TryAdd(
                GenerateResponseRequest.Create(
                    _config,
                    responseKey: _activeResponseId,
                    languageCode: completed.LanguageCode,
                    turnId: completed.TurnId,
                    turnRevision: completed.TurnRevision,
                    speechStoppedAtSeconds: completed.SpeechStoppedAtSeconds),
                200);
        }
    }

    /// <summary>
    /// 输出项上下文：返回一个稳定的 output_index 与 item_id。
    ///
    /// 对应上游 <c>_output_part_context</c>：连续文本共享一个消息项；
    /// 每个函数调用各开新项；函数调用之后的文本再开新项。下标按到达顺序递增。
    /// </summary>
    private (int OutputIndex, string ItemId) OutputPartContext(string kind, string? preferredItemId = null)
    {
        if (kind == "text" && _currentOutputKind == "text" && _currentOutputIndex is { } reusedIndex)
        {
            return (reusedIndex, _currentTextOutput!.ItemId);
        }

        string itemId;
        if (preferredItemId is not null)
        {
            itemId = preferredItemId;
        }
        else if (_nextOutputIndex == 0)
        {
            // 第一项沿用响应开局生成的 id：音频流与转写增量都指向它。
            itemId = _currentTextOutput?.ItemId ?? "item_" + Guid.NewGuid().ToString("N");
        }
        else
        {
            itemId = "item_" + Guid.NewGuid().ToString("N");
        }

        var outputIndex = _nextOutputIndex++;
        _currentOutputIndex = outputIndex;
        _currentOutputKind = kind;
        return (outputIndex, itemId);
    }

    /// <summary>
    /// 确保当前文本项已用 <c>output_item.added</c> 开启；函数调用之后的第一片文本
    /// 会开一个全新的项（新 id、新下标）。官方客户端靠 <c>output_item.added</c> 建立消息项、
    /// 靠 <c>content_part.added</c> 建立内容片；两者都到位之前的增量没有落点。
    /// </summary>
    private async Task EnsureTextItemAsync(string responseId)
    {
        var pending = CurrentTextOutput();
        if (pending.Opened)
        {
            return;
        }

        pending.Opened = true;
        await SendAsync(ServerEvent.Serialize(
            ServerEvent.OutputItemAdded(responseId, pending.ItemId, pending.OutputIndex)));
        await SendAsync(ServerEvent.Serialize(
            ServerEvent.ContentPartAdded(responseId, pending.ItemId, pending.OutputIndex, 0, _responseWantsAudio)));
    }

    /// <summary>取当前文本项；没有就按输出项上下文开一个并挂进列表。</summary>
    private PendingTextOutput CurrentTextOutput()
    {
        if (_currentTextOutput is not null && _currentOutputKind == "text")
        {
            return _currentTextOutput;
        }

        var (outputIndex, itemId) = OutputPartContext("text");
        var pending = new PendingTextOutput(outputIndex, itemId);
        _pendingTextOutputs.Add(pending);
        _currentTextOutput = pending;
        return pending;
    }

    private async Task HandleAssistantOutputAsync(AssistantOutputEvent output)
    {
        // 取消之后迟到的分片必须丢弃：消息项已经关了，再挂上去会写进下一轮的 item，
        // 也会在已经回滚的历史里凭空多出一条助手消息。上游用代际闸门做同样的判断——
        // 注意是**按代际**判，不是「取消过就全丢」：打断后新一轮的文本属于当前代际，
        // 必须照常发出去（见 CancelScope.IsDiscardable 的注释）。
        // 无活动响应的孤儿事件同样丢弃——它不属于任何一轮，挂上去就是凭空多出的一条。
        if (_cancelScope.IsDiscardable(output.CancelGeneration) || _activeResponseId is null)
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

        foreach (var text in texts)
        {
            var pending = CurrentTextOutput();
            await EnsureTextItemAsync(responseId);

            // 多片之间补一个空格，否则 "Hello" 与 "world" 会粘成 "Helloworld"。
            // 首片不加，否则整段转写会以空格开头。
            var delta = _responseWantsAudio && pending.PartCount > 0 ? " " + text : text;
            pending.PartCount++;
            pending.Text.Append(delta);

            await SendAsync(ServerEvent.Serialize(_responseWantsAudio
                ? ServerEvent.OutputAudioTranscriptDelta(responseId, pending.ItemId, delta, pending.OutputIndex)
                : ServerEvent.OutputTextDelta(responseId, pending.ItemId, delta, pending.OutputIndex)));
        }

        // 带外响应的输出不写回历史：它对着的上下文本来就是临时的，写回会把侧问
        // 的回答混进语音对话，模型下一轮会莫名其妙地「接侧问的话茬」。
        if (!_responseOutOfBand)
        {
            _config.Chat.Append(ChatMessage.Assistant(output.Text, id: _currentTextOutput!.ItemId));
        }
        TranscriptLog.Transcript("assistant", output.Text);
    }

    /// <summary>
    /// 函数调用的输出项生命周期：added（arguments 空、in_progress）→ arguments.done → item.done。
    ///
    /// 每个调用各开新项（上游 <c>_output_part_context(conn_id, "tool_call")</c> 的语义），
    /// item_id 与 <c>response.output</c> 里的终态项同源——客户端靠它把流式参数与
    /// 终态项关联起来。本实现的调用到达时参数已完整、状态恒 completed，
    /// 所以 done 立即发；上游「显式 in_progress 的调用在响应收口时补 done」的
    /// 分支在本实现不存在，因此收口不需要补发逻辑。
    /// </summary>
    private async Task EmitFunctionCallAsync(AssistantToolCallReadyEvent toolReady)
    {
        var responseId = toolReady.ResponseKey ?? _activeResponseId ?? "resp_unknown";
        var tool = toolReady.Part.Tool;

        // 工具项不影响文本链本身，但切断它：之后的文本属于新的消息项（上游同一语义）。
        _currentOutputKind = "tool_call";
        _currentTextOutput = null;

        var (outputIndex, itemId) = OutputPartContext("tool_call", preferredItemId: "item_" + Guid.NewGuid().ToString("N"));
        var item = ServerEvent.FunctionCallItem(
            itemId, tool.CallId, tool.Name, tool.Arguments, status: "completed");
        _pendingFunctionCallItems[outputIndex] = item;

        await SendAsync(ServerEvent.Serialize(
            ServerEvent.FunctionCallOutputItemAdded(responseId, itemId, outputIndex, tool.CallId, tool.Name)));
        await SendAsync(ServerEvent.Serialize(
            ServerEvent.FunctionCallArgumentsDone(
                responseId, itemId, tool.CallId, tool.Name, tool.Arguments, outputIndex)));
        await SendAsync(ServerEvent.Serialize(
            ServerEvent.FunctionCallOutputItemDone(responseId, item, outputIndex)));
    }

    /// <summary>
    /// 发一轮响应的收尾事件：模态 done → content_part.done → output_item.done。
    ///
    /// 正常完成与取消共用这一段，区别只在 <paramref name="completed"/>——
    /// 上游也是同一个 <c>finish_response</c> 处理两种状态，item 状态取
    /// <c>"completed" if status == "completed" else "incomplete"</c>。
    /// 哪些事件该发由 <see cref="ResponseTeardown.Plan"/> 决定。
    ///
    /// 函数调用带来的多文本项在这里展开：<c>content_part.done</c> 与
    /// <c>output_item.done</c> 每个**已开启**的文本项各发一次（各自的下标与文本）；
    /// 音频流收尾与聚合转写只发一次，挂最后一个已开启的项——音频字节流本来
    /// 就只挂在一个项上（上游 <c>pending_assistant_item_id</c> 的语义）。
    /// </summary>
    private async Task EmitTeardownAsync(string responseId, bool completed)
    {
        var opened = _pendingTextOutputs.Where(p => p.Opened).ToList();
        foreach (var step in ResponseTeardown.Plan(
            opened.Count > 0, _responseWantsAudio, _audioStreamed,
            hasText: opened.Any(p => p.Text.Length > 0)))
        {
            var last = opened.Count > 0 ? opened[^1] : null;
            if (step == TeardownEvent.ContentPartDone || step == TeardownEvent.OutputItemDone)
            {
                // 每个已开启的文本项独立收口：客户端据此结束每一条消息的 streaming 状态。
                foreach (var pending in opened)
                {
                    var payload = step == TeardownEvent.ContentPartDone
                        ? ServerEvent.ContentPartDone(
                            responseId, pending.ItemId, pending.Text.ToString(),
                            outputIndex: pending.OutputIndex, wantsAudio: _responseWantsAudio)
                        : ServerEvent.OutputItemDone(
                            responseId, pending.ItemId, pending.Text.ToString(),
                            outputIndex: pending.OutputIndex,
                            wantsAudio: _responseWantsAudio, completed: completed);
                    await SendAsync(ServerEvent.Serialize(payload));
                }

                continue;
            }

            var target = step switch
            {
                TeardownEvent.OutputAudioDone =>
                    ServerEvent.OutputAudioDone(responseId, last!.ItemId, last.OutputIndex),
                TeardownEvent.OutputAudioTranscriptDone =>
                    ServerEvent.OutputAudioTranscriptDone(
                        responseId, last!.ItemId, AggregatedTranscript(), last.OutputIndex),
                TeardownEvent.OutputTextDone =>
                    ServerEvent.OutputTextDone(
                        responseId, last!.ItemId, AggregatedTranscript(), last.OutputIndex),
                _ => throw new ArgumentOutOfRangeException(nameof(step), step, "未知的收尾事件"),
            };

            await SendAsync(ServerEvent.Serialize(target));
        }
    }

    /// <summary>
    /// 聚合整条响应的助手文本。音频模态的各片之间以空格分隔（上游 <c>_assistant_text</c>
    /// 的 <c>" ".join</c> 口径），纯文本模态直接拼接。
    /// </summary>
    private string AggregatedTranscript()
        => _responseWantsAudio
            ? string.Join(" ", _pendingTextOutputs
                .Select(p => p.Text.ToString().Trim())
                .Where(t => t.Length > 0))
            : string.Concat(_pendingTextOutputs.Select(p => p.Text.ToString()));

    /// <summary>
    /// 归账一条 token 用量。
    ///
    /// 与上游 <c>_on_token_usage</c> 同规则：只有「说的是当前响应」才直接累加；其余一律按 key 挂起。
    /// 这条分支不是优化，是正确性——投机轮次会产出用量，但那一轮的输出最终可能被丢弃，
    /// 直接累加就把丢弃轮次的账算到活的那一轮头上。
    /// </summary>
    private void HandleTokenUsage(TokenUsageEvent usage)
        => _usage.Accrue(usage.ResponseKey, usage.InputTokens, usage.OutputTokens, _activeResponseId);

    private void ApplyPendingTokenUsage(string responseKey) => _usage.ApplyPending(responseKey);

    private void CloseResponseKey(string responseKey) => _usage.Close(responseKey);

    private async Task FinishResponseAsync(AssistantResponseDoneEvent done)
    {
        var responseId = done.ResponseKey ?? _activeResponseId;
        if (responseId is null)
        {
            // 已取消：取消路径自己负责收口。迟到的完成不得再写线上状态，
            // 否则客户端会收到一个它已经丢弃的 response 的终态。
            return;
        }

        var status = done.Status == ResponseStatus.Incomplete ? "incomplete" : "completed";
        var reason = done.Reason switch
        {
            ResponseIncompleteReason.MaxOutputTokens => "max_output_tokens",
            ResponseIncompleteReason.ContentFilter => "content_filter",
            _ => null,
        };

        await EmitTeardownAsync(responseId, completed: status == "completed");

        // 工具输入要在组装终态载荷**之前**入表：metadata 是那一刻现算的，
        // 晚一步写就等于这一轮永远不带这个键。带外响应不记：上游同一处闸门
        // （on_assistant_response_done 的 is_out_of_band 检查），它的输出不在默认会话里。
        if (status == "completed" && done.InputToolCallIds.Count > 0 && !_responseOutOfBand)
        {
            _responseToolInputs[responseId] = [.. done.InputToolCallIds];
        }

        if (!_responseOutOfBand && (status == "incomplete" || !done.Succeeded))
        {
            // 不完整响应要回滚历史，否则下一轮会把没念完的半句当成已说过的话。
            // 被作废的轮次同样回滚——但**不改**上报状态：它对客户端是正常收口的，
            // 只是这一轮的字没发出去、不该留在聊天记录里。
            // 带外响应根本没写过历史，跳过——回滚会误伤提交点之后的既有条目。
            _config.Chat.RollbackToCommit();
        }

        // 收口前把挂在这个 key 上的用量结转过来——它可能早于响应成为当前响应就已到达。
        ApplyPendingTokenUsage(responseId);
        await SendAsync(ServerEvent.Serialize(
            ServerEvent.ResponseDone(SnapshotOf(responseId, status, reason))));
        CloseResponseKey(responseId);
        EndLatency(responseId, status);
        // 尾态载荷已经产出，工具输入从这里起没有读者了。留着会让长连接按响应数累积。
        _responseToolInputs.Remove(responseId);

        // 响应收口后释放这一轮的提交态。上游同样在 finish_response 里 close：
        // 不释放的话这一轮会一直算「已应答」却又不能复开，用户接着说下一句会被判成陈旧。
        // 带外响应的轮次身份在开响应时已置空，收口也必须关 null——若侧问收口时用户正开口，
        // _responseTurnId 已被新 SpeechStopped 覆写，拿它关会把还没产出响应的语音轮误关。
        _speculativeTurns.Close(
            _responseOutOfBand ? null : _responseTurnId,
            _responseOutOfBand ? null : _responseTurnRevision);

        _cancelScope.ResponseDone();
        _activeResponseId = null;
        _currentTextOutput = null;
        _pendingTextOutputs.Clear();
        _pendingFunctionCallItems.Clear();
        _nextOutputIndex = 0;
        _currentOutputIndex = null;
        _currentOutputKind = null;
        _audioStreamed = false;
        _responseOutOfBand = false;
        _responseTurnId = null;
        _responseTurnRevision = null;
        _pendingFunctionCalls = 0;
    }

    private async Task EmitAudioAsync(AudioOutput audio)
    {
        // 与文本同一条闸门：判据是代际，不是「取消过就全丢」。
        if (audio.CleanupOnly || _cancelScope.IsDiscardable(audio.CancelGeneration) || _activeResponseId is null)
        {
            return;
        }

        var responseId = audio.ResponseKey ?? _activeResponseId;

        // 音频可能先于文本到达：item 必须先开，否则 delta 挂不到任何消息项上。
        // 音频字节流与转写增量同挂当前文本项——音频模态里两者本来就是同一个
        // output_audio 内容片的两面。
        var pending = CurrentTextOutput();
        await EnsureTextItemAsync(responseId);

        // 口型是「先元数据、后音频」：上游在 begin_audio_output 之后把 visemes 事件连同
        // 这一批音频一起发，并在发完后重新校验响应仍在——发事件会让出给取消与会话释放，
        // 陈旧音频不能借这条事件把已经收口的响应重新打开。
        if (audio.Visemes.Count > 0)
        {
            await SendAsync(ServerEvent.Serialize(
                ServerEvent.OutputAudioVisemes(responseId, pending.ItemId, audio.Visemes, pending.OutputIndex)));

            if (audio.CleanupOnly
                || _cancelScope.IsDiscardable(audio.CancelGeneration)
                || _activeResponseId is null
                || responseId != _activeResponseId)
            {
                return;
            }
        }

        await _transport.SendAudioChunkAsync(responseId, pending.ItemId, pending.OutputIndex, audio.Audio);

        // 一旦真的发过音频，收尾就必须回一个 output_audio.done 把流关掉；
        // 没发过音频的响应（纯工具调用、TTS 没出样本）不能发，否则客户端会收到一段静音。
        _audioStreamed = true;
    }

    private async Task SendAsync(string payload)
    {
        await _transport.SendEventAsync(payload).ConfigureAwait(false);
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
        await _transport.CloseAsync().ConfigureAwait(false);
    }
}
