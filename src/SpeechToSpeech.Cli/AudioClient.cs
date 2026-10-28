using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NAudio.Wave;
using SpeechToSpeech.Cli.Audio;

namespace SpeechToSpeech.Cli;

/// <summary>
/// 打包麦克风/扬声器客户端（<c>talk</c> / <c>local</c>），行为对齐上游
/// <c>audio_client.py</c> 的主链路（差异账 D035）：
///
/// <list type="bullet">
/// <item>连接后先发 <see cref="RealtimeAudioClientOptions.BuildSessionUpdate"/>——
/// 默认 16 k 双向**不声明 format**（走服务端原生速率），并订阅实时转写快照；</item>
/// <item>麦克风 PCM 按 send_rate **直通**上行（不重采样；上游同样直发）；</item>
/// <item>下行音频进 <see cref="PlaybackBuffer"/>：首包攒满才开播、cancelled/cleared 即清；</item>
/// <item>转写与回复走控制台：conversation_only（默认）只显示对话，
/// <c>--verbose</c>/<c>--print-json</c> 叠加事件流。</item>
/// </list>
///
/// 麦克风回调只做入队（与可选的 block 判定），发送交给单一消费者线程——这不是优化而是
/// 正确性约束：<see cref="IWaveIn.DataAvailable"/> 由驱动线程反复触发，而
/// <see cref="ClientWebSocket"/> **不允许并发 Send**。设备后端由
/// <see cref="AudioDevices"/> 按平台选择，见差异账 E001；参数族与协议面见 D035。
/// </summary>
internal sealed class AudioClient : IAsyncDisposable
{
    private readonly Uri _endpoint;
    private readonly RealtimeAudioClientOptions _options;
    private readonly IAudioDeviceSet _devices;
    private readonly ClientWebSocket _socket = new();
    private readonly PlaybackBuffer _playback;
    private readonly IWavePlayer _speaker;

    /// <summary>播放侧期望延迟（ms）。上游 sounddevice 输出流未显式设延迟，这里取历史实测稳定值。</summary>
    private const int PlaybackLatencyMilliseconds = 120;

    /// <summary>conversation_only = 干净对话显示；verbose/print_json 任一开启即切换到诊断显示。</summary>
    private readonly bool _conversationOnly;

    /// <summary>麦克风原始 PCM 的单一消费者队列。有界 + 丢最旧，音频堆积没有意义。</summary>
    private readonly Channel<byte[]> _micQueue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(capacity: 128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>见类注释：ClientWebSocket 不允许并发 Send。</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private IWaveIn? _microphone;
    private Task? _senderTask;
    private CancellationTokenSource? _stopping;

    public AudioClient(Uri endpoint, IAudioDeviceSet devices, RealtimeAudioClientOptions options)
    {
        _endpoint = endpoint;
        _devices = devices;
        _options = options;
        _conversationOnly = !(options.Verbose || options.PrintJson);

        _playback = new PlaybackBuffer(options.RecvRate, options.PlaybackBufferMs);
        _speaker = devices.CreateSpeaker(PlaybackLatencyMilliseconds, options.OutputDevice);
        _speaker.Init(_playback);
        _speaker.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                Console.Error.WriteLine($"speaker stopped: {args.Exception.Message}");
            }
        };
    }

    public async Task RunAsync()
    {
        _stopping = new CancellationTokenSource();
        await _socket.ConnectAsync(_endpoint, _stopping.Token).ConfigureAwait(false);
        Console.WriteLine($"connected to {_endpoint}  [audio: {_devices.Name}]");

        await SendAsync(RealtimeAudioClientOptions.BuildSessionUpdate(_options)).ConfigureAwait(false);

        // 先起消费者，再开麦克风——否则头几个缓冲会在队列无人读时被丢。
        _senderTask = Task.Run(() => SendMicrophoneLoopAsync(_stopping.Token));
        StartMicrophone();
        _speaker.Play();

        if (!_conversationOnly)
        {
            Console.WriteLine("Ready — start speaking. Ctrl+C to stop.");
        }

        try
        {
            await ReceiveLoopAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StartMicrophone()
    {
        // 上游 blocksize 是采样数；换算成毫秒交给设备层（各家缓冲粒度不同）。
        var bufferMs = Math.Max(1, _options.ChunkSize * 1000 / _options.SendRate);
        _microphone = _devices.CreateMicrophone(_options.SendRate, bufferMs, _options.InputDevice);

        // 刻意是同步回调：只入队（与可选的 block 判定），不做重采样、不碰 socket。
        _microphone.DataAvailable += (_, args) =>
        {
            if (args.BytesRecorded <= 0)
            {
                return;
            }

            if (_options.BlockMicDuringPlayback && _playback.IsActive())
            {
                return;
            }

            var frame = new byte[args.BytesRecorded];
            Buffer.BlockCopy(args.Buffer, 0, frame, 0, args.BytesRecorded);
            _micQueue.Writer.TryWrite(frame);
        };

        _microphone.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                Console.Error.WriteLine($"microphone stopped: {args.Exception.Message}");
            }
        };

        _microphone.StartRecording();
    }

    /// <summary>队列的唯一消费者：按 send_rate 直通 → base64 → 上行。</summary>
    private async Task SendMicrophoneLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _micQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendAsync(new JsonObject
                {
                    ["type"] = "input_audio_buffer.append",
                    ["audio"] = Convert.ToBase64String(frame),
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"mic sender stopped: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var payload = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            Handle(payload);
        }
    }

    /// <summary>单事件处理。事件覆盖面对齐上游 handle_server_event 的可见行为。</summary>
    private void Handle(string payload)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch
        {
            return;
        }

        var type = node?["type"]?.GetValue<string>();
        if (type is null)
        {
            return;
        }

        if (_options.PrintJson)
        {
            Console.WriteLine($"EVENT: {payload}");
        }

        // conversation_only 下隐藏部分转写不打进对话流（上游同款过滤）。
        if (_conversationOnly &&
            type is "speech_to_speech.input_audio_transcription.snapshot"
                or "conversation.item.input_audio_transcription.delta")
        {
            return;
        }

        switch (type)
        {
            case "session.created":
                if (!_conversationOnly)
                {
                    Console.WriteLine("Connected.");
                }

                break;

            case "session.updated":
                if (_conversationOnly)
                {
                    Console.WriteLine("Ready — start speaking.");
                }
                else
                {
                    Console.WriteLine($"EVENT: {type}");
                }

                break;

            case "input_audio_buffer.speech_started":
                // 本地打断：立刻清掉还没播完的上一轮音频。
                _playback.Clear();
                if (!_conversationOnly)
                {
                    Console.WriteLine();
                }

                break;

            // 上游对这三类显式 return：print_json 的完整 JSON 已在前面打过，
            // 事件名行不重复出现（output_item 的生命周期属于诊断日志，不进对话流）。
            case "input_audio_buffer.speech_stopped":
            case "response.output_item.added":
            case "response.output_item.done":
                break;

            case "speech_to_speech.input_audio_transcription.snapshot":
                {
                    var transcript = node?["transcript"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        WriteConversation($"USER: {transcript.Trim()}", ConsoleColor.Yellow, newLine: false);
                    }

                    break;
                }

            case "conversation.item.input_audio_transcription.completed":
                {
                    var transcript = node?["transcript"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        WriteConversation($"USER: {transcript.Trim()}", ConsoleColor.Yellow);
                    }

                    break;
                }

            case "conversation.item.input_audio_transcription.failed":
                {
                    var message = node?["error"]?["message"]?.GetValue<string>() ?? "Input audio transcription failed";
                    WriteConversation($"ERROR: transcription_error: {message}", ConsoleColor.Red);
                    break;
                }

            case "response.created":
                if (!_conversationOnly)
                {
                    Console.WriteLine("ASSISTANT: <response started>");
                }

                break;

            case "response.output_audio.delta":
                {
                    var delta = node?["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        _playback.Append(Convert.FromBase64String(delta));
                    }

                    break;
                }

            case "response.output_audio.done":
                _playback.Finish();
                if (!_conversationOnly)
                {
                    Console.WriteLine("ASSISTANT: <audio done>");
                }

                break;

            case "response.output_audio_transcript.delta":
                {
                    var delta = node?["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        WriteConversation("ASSISTANT: " + delta, ConsoleColor.Green, newLine: false);
                    }

                    break;
                }

            case "response.output_audio_transcript.done":
                {
                    var transcript = node?["transcript"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(transcript))
                    {
                        WriteConversation($"ASSISTANT: {transcript}", ConsoleColor.Green);
                    }

                    break;
                }

            case "response.function_call_arguments.done":
                if (!_conversationOnly)
                {
                    var name = node?["name"]?.GetValue<string>();
                    var callId = node?["call_id"]?.GetValue<string>();
                    var arguments = node?["arguments"]?.GetValue<string>();
                    Console.WriteLine($"TOOL: {name} call_id={callId} arguments={arguments}");
                }

                break;

            case "response.done":
                {
                    var status = node?["response"]?["status"]?.GetValue<string>();
                    if (status == "cancelled")
                    {
                        _playback.Clear();
                    }
                    else
                    {
                        _playback.Finish();
                    }

                    if (_conversationOnly)
                    {
                        if (status is not null && status != "completed" && status != "cancelled")
                        {
                            WriteConversation($"ERROR: response {status}", ConsoleColor.Red);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"ASSISTANT: <response {status}>");
                    }

                    break;
                }

            case "output_audio_buffer.cleared":
                _playback.Clear();
                break;

            case "error":
                {
                    var errorType = node?["error"]?["type"]?.GetValue<string>() ?? "error";
                    var message = node?["error"]?["message"]?.GetValue<string>() ?? "";
                    WriteConversation($"ERROR: {errorType}: {message}", ConsoleColor.Red);
                    break;
                }

            default:
                if (!_conversationOnly)
                {
                    Console.WriteLine($"EVENT: {type}");
                }

                break;
        }
    }

    /// <summary>
    /// 对话输出。conversation_only 走彩色控制台；诊断模式一律原样打印（与上游
    /// <c>_FriendlyEventRenderer.print_conversation</c> 的两路一致）。
    /// </summary>
    private static void WriteConversation(string text, ConsoleColor color, bool newLine = true)
    {
        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            if (newLine)
            {
                Console.WriteLine(text);
            }
            else
            {
                Console.Write(text);
            }
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }

    private async Task SendAsync(JsonObject payload)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(
                    new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping?.Cancel();
        _micQueue.Writer.TryComplete();

        if (_microphone is not null)
        {
            try
            {
                _microphone.StopRecording();
            }
            catch (Exception ex)
            {
                // 设备可能已被拔掉；退出路径上不再放大错误
                Console.Error.WriteLine($"microphone stop failed: {ex.Message}");
            }

            _microphone.Dispose();
        }

        if (_senderTask is not null)
        {
            try
            {
                await _senderTask.ConfigureAwait(false);
            }
            catch
            {
                // 已在发送循环内记录
            }
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // 关闭失败不影响退出
        }

        _speaker.Stop();
        _speaker.Dispose();
        _socket.Dispose();
        _sendLock.Dispose();
        _stopping?.Dispose();
    }
}
