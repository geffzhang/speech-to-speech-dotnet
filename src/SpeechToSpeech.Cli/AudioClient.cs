using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NAudio.Wave;
using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Cli.Audio;

namespace SpeechToSpeech.Cli;

/// <summary>
/// 打包的麦克风/扬声器客户端。
///
/// 对应上游 <c>speech-to-speech talk</c>。四条行为要守住：
/// <list type="bullet">
/// <item>上行统一成 24 kHz / 16 bit / 单声道 PCM 再 base64，与服务端 <c>session.update</c> 的声明一致；</item>
/// <item>下行 <c>response.output_audio.delta</c> 立即进播放缓冲，不自己攒整段；</item>
/// <item>被打断时清空播放缓冲——否则用户会听到上一轮回答的尾巴；</item>
/// <item>麦克风回调只做入队，重采样与发送交给单一消费者线程。</item>
/// </list>
///
/// 最后一条不是优化而是正确性约束：<see cref="IWaveIn.DataAvailable"/> 由驱动线程反复触发，
/// 而 <see cref="ClientWebSocket"/> **不允许并发 Send**。把发送放在回调里，两次回调重叠就会
/// 抛 <see cref="InvalidOperationException"/> 或让帧交错。同时重采样也不该占着音频回调，
/// 那会拖慢下一次读取、造成驱动侧溢出。
///
/// 设备后端由 <see cref="AudioDevices"/> 按平台选择，见差异账 E001。
/// </summary>
internal sealed class AudioClient : IAsyncDisposable
{
    private const int OutputSampleRate = 24_000;
    private const int InputSampleRate = 16_000;
    private const int MicBufferMilliseconds = 100;
    private const int PlaybackLatencyMilliseconds = 120;

    private readonly Uri _endpoint;
    private readonly IAudioDeviceSet _devices;
    private readonly ClientWebSocket _socket = new();
    private readonly BufferedWaveProvider _playback;
    private readonly IWavePlayer _speaker;

    /// <summary>麦克风原始 PCM 的单一消费者队列。有界 + 丢最旧，音频堆积没有意义。</summary>
    private readonly Channel<byte[]> _micQueue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(capacity: 64)
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

    public AudioClient(Uri endpoint, IAudioDeviceSet devices)
    {
        _endpoint = endpoint;
        _devices = devices;

        _playback = new BufferedWaveProvider(
            new WaveFormat(OutputSampleRate, 16, 1),
            TimeSpan.FromSeconds(30))
        {
            DiscardOnBufferOverflow = true,
        };

        _speaker = _devices.CreateSpeaker(PlaybackLatencyMilliseconds);
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

        await SendAsync(new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "realtime",
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = OutputSampleRate },
                        ["turn_detection"] = new JsonObject { ["type"] = "server_vad" },
                    },
                    ["output"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = OutputSampleRate },
                    },
                },
            },
        }).ConfigureAwait(false);

        // 先起消费者，再开麦克风——否则头几个缓冲会在队列无人读时被丢。
        _senderTask = Task.Run(() => SendMicrophoneLoopAsync(_stopping.Token));
        StartMicrophone();
        _speaker.Play();

        Console.WriteLine("Ready — start speaking. Ctrl+C to stop.");

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
        _microphone = _devices.CreateMicrophone(InputSampleRate, MicBufferMilliseconds);

        // 刻意是同步回调：只入队，不做重采样、不碰 socket。
        _microphone.DataAvailable += (_, args) =>
        {
            if (args.BytesRecorded <= 0)
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

    /// <summary>队列的唯一消费者：16 kHz 原始 PCM → 24 kHz → base64 → 上行。</summary>
    private async Task SendMicrophoneLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _micQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var floats = AudioUtil.FromPcm16(frame);
                var upsampled = AudioUtil.Resample(floats, InputSampleRate, OutputSampleRate);
                var pcm24k = AudioUtil.ToPcm16(upsampled);

                await SendAsync(new JsonObject
                {
                    ["type"] = "input_audio_buffer.append",
                    ["audio"] = Convert.ToBase64String(pcm24k),
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
        switch (type)
        {
            case "response.output_audio.delta":
                {
                    var delta = node?["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        var pcm = Convert.FromBase64String(delta);
                        _playback.AddSamples(pcm, 0, pcm.Length);
                    }

                    break;
                }

            case "input_audio_buffer.speech_started":
                // 打断：立刻清空还没播完的上一轮音频
                _playback.ClearBuffer();
                break;

            case "conversation.item.input_audio_transcription.completed":
                {
                    var transcript = node?["transcript"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        WriteColored(transcript, ConsoleColor.Yellow);
                    }

                    break;
                }

            case "response.output_audio_transcript.delta":
                {
                    var delta = node?["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        WriteColored(delta, ConsoleColor.Green, newLine: false);
                    }

                    break;
                }

            case "response.done":
                Console.WriteLine();
                break;

            case "error":
                {
                    var message = node?["error"]?["message"]?.GetValue<string>();
                    Console.Error.WriteLine($"server error: {message}");
                    break;
                }
        }
    }

    private static void WriteColored(string text, ConsoleColor color, bool newLine = true)
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
