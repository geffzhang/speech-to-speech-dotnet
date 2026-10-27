using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SpeechToSpeech.Realtime.Transports;

namespace SpeechToSpeech.Transport.WebRtc;

/// <summary>
/// WebRTC 会话传输（上游 <c>webrtc_session.py</c> 的 <c>WebRTCSession</c>）。
///
/// 音频走 Opus 48 kHz RTP 媒体轨（Opus 编解码用 Concentus，纯托管）；
/// 事件走 <c>oai-events</c> 数据频道，与 WebSocket 传输同一线协议。
/// 管线集成全部经回调进入连接层：客户端事件、入向 PCM、频道开启、关闭——
/// 本类不触碰队列与服务，只做传输。
/// </summary>
public sealed class WebRtcSession : SessionTransport, IAsyncDisposable
{
    public const string DataChannelLabel = "oai-events";
    public const int WebRtcSampleRate = 48_000;
    public const int PipelineSampleRate = 16_000;
    public const double IceGatheringTimeoutSeconds = 5.0;
    public const double ConnectTimeoutSeconds = 30.0;
    public const string IceServersEnv = "SPEECH_TO_SPEECH_ICE_SERVERS";

    private readonly RTCPeerConnection _pc;
    private readonly Func<string, Task> _onClientEvent;
    private readonly Action<byte[]> _onAudio;
    private readonly Func<Task> _onOpen;
    private readonly Action _onClosed;

    private readonly WebRtcAudioPacer _pacer = new();
    private readonly WebRtcResampler _outResampler = new(PipelineSampleRate, WebRtcSampleRate);
    private readonly WebRtcResampler _inResampler = new(WebRtcSampleRate, PipelineSampleRate);
    private readonly ConcurrentQueue<string> _dcMessages = new();
    private readonly CancellationTokenSource _closing = new();

    private IOpusEncoder? _encoder;
    private IOpusDecoder? _decoder;
    private AudioFormat? _remoteFormat;
    private int _sendPayloadId;
    private RTCDataChannel? _dc;
    private Task? _pacerTask;
    private Task? _dcConsumerTask;
    private int _closed;
    private int _generation;

    /// <summary>SDP 协商完成后的会话 id（HTTP 端点分配，用于 DELETE 挂断与日志）。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>会话是否已关闭（幂等关闭的观察口，HTTP DELETE 用它跳过已释放会话）。</summary>
    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    public WebRtcSession(
        RTCPeerConnection peerConnection,
        Func<string, Task> onClientEvent,
        Action<byte[]> onAudio,
        Func<Task> onOpen,
        Action onClosed)
    {
        _pc = peerConnection;
        _onClientEvent = onClientEvent;
        _onAudio = onAudio;
        _onOpen = onOpen;
        _onClosed = onClosed;
    }

    public override string Kind => TransportKinds.WebRtc;

    /// <summary>
    /// 从 <see cref="IceServersEnv"/> 环境变量构建 RTC 配置。
    ///
    /// 变量是 JSON 数组，如 <c>[{"urls":"stun:stun.example.com:3478"},
    /// {"urls":"turn:turn.example.com","username":"u","credential":"c"}]</c>；
    /// 未设置或非法时返回 null（SIPSorcery 默认行为，上游 rtc_configuration_from_env 同口径）。
    /// </summary>
    public static RTCConfiguration? ConfigurationFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(IceServersEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var servers = new List<RTCIceServer>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var server = new RTCIceServer
                {
                    urls = entry.GetProperty("urls").GetString() ?? "",
                };
                if (entry.TryGetProperty("username", out var username))
                {
                    server.username = username.GetString();
                }

                if (entry.TryGetProperty("credential", out var credential))
                {
                    server.credential = credential.GetString();
                }

                servers.Add(server);
            }

            return new RTCConfiguration
            {
                iceServers = servers,
                X_GatherTimeoutMs = (int)(IceGatheringTimeoutSeconds * 1000),
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[WebRTC] ignoring invalid {IceServersEnv}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 接线 SIPSorcery 事件回调。在 <see cref="NegotiateAsync"/> 之前调用。
    /// </summary>
    public void Setup()
    {
        var track = new MediaStreamTrack(
            new AudioFormat((int)AudioCodecsEnum.OPUS, "OPUS", WebRtcSampleRate, 2, "minptime=10;useinbandfec=1"),
            MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(track);

        _pc.OnAudioFormatsNegotiated += formats =>
        {
            var opus = formats.FirstOrDefault(f =>
                f.Codec == AudioCodecsEnum.OPUS);
            if (opus.FormatID != 0)
            {
                _sendPayloadId = opus.FormatID;
                _encoder ??= CreateEncoder(WebRtcSampleRate, 1);
                Console.Error.WriteLine($"[WebRTC] audio negotiated: {opus.Codec} {opus.ClockRate}Hz pt={opus.FormatID}");
            }
        };

        _pc.ondatachannel += dc =>
        {
            if (dc.label != DataChannelLabel)
            {
                Console.Error.WriteLine($"[WebRTC] ignoring unexpected data channel: {dc.label}");
                return;
            }

            _dc = dc;
            _dcConsumerTask = Task.Run(() => ConsumeDataChannelMessagesAsync(_closing.Token));
            Console.Error.WriteLine($"[WebRTC] data channel '{DataChannelLabel}' received");

            if (dc.IsOpened)
            {
                _ = _onOpen();
            }
            else
            {
                dc.onopen += () => { _ = _onOpen(); };
            }

            dc.onmessage += (channel, protocol, data) =>
            {
                // 文本事件进队列按到达顺序消费；二进制消息不是本协议的载荷。
                if (protocol == DataChannelPayloadProtocols.WebRTC_String)
                {
                    _dcMessages.Enqueue(System.Text.Encoding.UTF8.GetString(data));
                }
                else
                {
                    Console.Error.WriteLine("[WebRTC] ignoring binary data-channel message");
                }
            };

            dc.onclose += () =>
            {
                Console.Error.WriteLine("[WebRTC] data channel closed");
                _ = CloseAsync();
            };
        };

        _pc.OnAudioFrameReceived += frame => OnRemoteAudioFrame(frame);

        _pc.onconnectionstatechange += state =>
        {
            Console.Error.WriteLine($"[WebRTC] connection state: {state}");
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                _ = CloseAsync();
            }
        };
    }

    /// <summary>
    /// 应用客户端 SDP offer，返回 SDP answer。
    ///
    /// 等待 ICE gathering 完成（HTTP 握手没有 trickle 通道，answer 必须自带候选）；
    /// 超时则返回部分 SDP（上游同口径）。
    /// </summary>
    public async Task<string> NegotiateAsync(string offerSdp)
    {
        var offer = SDP.ParseSDPDescription(offerSdp);
        var result = _pc.SetRemoteDescription(SIPSorcery.SIP.App.SdpType.offer, offer);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"remote description rejected: {result}");
        }

        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer).ConfigureAwait(false);

        if (_pc.iceGatheringState != RTCIceGatheringState.complete)
        {
            var gathered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pc.onicegatheringstatechange += state =>
            {
                if (state == RTCIceGatheringState.complete)
                {
                    gathered.TrySetResult();
                }
            };

            var timeout = Task.Delay(TimeSpan.FromSeconds(IceGatheringTimeoutSeconds));
            var finished = await Task.WhenAny(gathered.Task, timeout).ConfigureAwait(false);
            if (finished == timeout)
            {
                Console.Error.WriteLine("[WebRTC] ICE gathering timed out, returning partial SDP");
            }
        }

        _pacerTask = Task.Run(() => PacerLoopAsync(_closing.Token));
        _ = WatchdogAsync(_closing.Token);

        return _pc.localDescription.sdp.ToString();
    }

    // ── SessionTransport ──────────────────────────────────────────

    public override async ValueTask SendEventAsync(string payload)
    {
        var dc = _dc;
        if (dc is null || !dc.IsOpened)
        {
            return;
        }

        try
        {
            dc.send(payload);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebRTC] data channel send error: {ex.Message}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public override ValueTask SendAudioChunkAsync(
        string responseId, string itemId, int outputIndex, byte[] pcm16)
    {
        // 事件记账（隐式 VAD 路径的 response.created 等）由连接层负责；
        // 这里只把 16 kHz PCM 写进 paced 轨道。
        var samples = new short[pcm16.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
        }

        var upsampled = _outResampler.Resample(samples, 1);
        _pacer.Write(upsampled);
        return ValueTask.CompletedTask;
    }

    public override void DiscardPendingAudio()
    {
        _pacer.Clear();
        // 上游还清 aiortc 编码器 lookahead；SIPSorcery 编码即时、无内部缓冲，
        // 在途帧的丢弃由代际判据覆盖（见 PacerLoopAsync 的 generation 检查）。
        Interlocked.Increment(ref _generation);
    }

    public override async ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        await _closing.CancelAsync().ConfigureAwait(false);
        try
        {
            _pc.close();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebRTC] error closing peer connection: {ex.Message}");
        }

        _onClosed();
        Console.Error.WriteLine("[WebRTC] session closed");
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _closing.Dispose();
        _pc.Dispose();
    }

    // ── 内部 ──────────────────────────────────────────────────────

    /// <summary>
    /// paced 发送循环：Tick → 编码 → SendRtpRaw。RTP 时钟由 pacer 时间戳驱动
    /// （库管 RTP 帧化/SRTP/序号；时间戳语义对齐上游的媒体时钟）。
    /// </summary>
    private async Task PacerLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        while (!cancellationToken.IsCancellationRequested && _closed == 0)
        {
            var step = _pacer.Tick();
            switch (step.Outcome)
            {
                case PacerOutcome.NeedsMoreData:
                case PacerOutcome.Sleep:
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, step.SleepSeconds * 1000)),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case PacerOutcome.Frame:
                    if (step.Generation != _pacer.Generation || _closed != 0)
                    {
                        // clear() 竞争：编码前的这帧属于已打断的代际，丢弃。
                        continue;
                    }

                    if (_encoder is null || _sendPayloadId == 0)
                    {
                        continue;
                    }

                    var encodedLength = _encoder.Encode(
                        step.Samples.AsSpan(), step.Samples.Length, buffer.AsSpan(), buffer.Length);
                    if (encodedLength > 0)
                    {
                        var packet = new byte[encodedLength];
                        Array.Copy(buffer, packet, encodedLength);
                        _pc.SendRtpRaw(SDPMediaTypesEnum.audio, packet, step.Timestamp, 0, _sendPayloadId);
                    }

                    break;
            }
        }
    }

    /// <summary>连接看门狗：协商后 30 秒仍未 connected 就释放会话（上游同口径）。</summary>
    private async Task WatchdogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(ConnectTimeoutSeconds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_closed == 0 && _pc.connectionState != RTCPeerConnectionState.connected)
        {
            Console.Error.WriteLine(
                $"[WebRTC] peer not connected after {ConnectTimeoutSeconds:F0}s " +
                $"(state: {_pc.connectionState}); releasing session");
            await CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>数据频道消息单消费者：客户端事件按到达顺序应用（上游同口径）。</summary>
    private async Task ConsumeDataChannelMessagesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _closed == 0)
        {
            if (!_dcMessages.TryDequeue(out var message))
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await ProcessDataChannelMessageAsync(message).ConfigureAwait(false);
        }
    }

    private async Task ProcessDataChannelMessageAsync(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                Console.Error.WriteLine($"[WebRTC] non-object event on data channel: {Truncate(message)}");
                return;
            }

            await _onClientEvent(message).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"[WebRTC] invalid JSON on data channel: {Truncate(message)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebRTC] error handling client event: {ex.Message}");
        }
    }

    /// <summary>
    /// 远端音频：Opus 解码 → 多声道降混 → 重采样到管线率 → 管线。
    /// 解码器按远端协商格式重建（浏览器 offer 常为 opus/48000/2）。
    /// </summary>
    private void OnRemoteAudioFrame(EncodedAudioFrame frame)
    {
        try
        {
            var format = frame.AudioFormat;
            if (_decoder is null || _remoteFormat is null
                || _remoteFormat.Value.ClockRate != format.ClockRate
                || _remoteFormat.Value.ChannelCount != format.ChannelCount)
            {
                _decoder = CreateDecoder(format.ClockRate, Math.Max(1, format.ChannelCount));
                _remoteFormat = format;
                _inResampler.Reset(format.ClockRate, PipelineSampleRate);
            }

            var decoded = new short[format.ClockRate / 50 * Math.Max(1, format.ChannelCount) * 4];
            var frameSamples = decoded.Length / Math.Max(1, format.ChannelCount);
            var count = _decoder.Decode(frame.EncodedAudio.AsSpan(), decoded.AsSpan(), frameSamples, false);
            if (count <= 0)
            {
                return;
            }

            var upTo = count * Math.Max(1, format.ChannelCount);
            var resampled = _inResampler.Resample(decoded.AsSpan(0, upTo), Math.Max(1, format.ChannelCount));
            var pcm16 = new byte[resampled.Length * 2];
            for (var i = 0; i < resampled.Length; i++)
            {
                pcm16[i * 2] = (byte)resampled[i];
                pcm16[i * 2 + 1] = (byte)(resampled[i] >> 8);
            }

            if (pcm16.Length > 0)
            {
                _onAudio(pcm16);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebRTC] inbound audio decode error: {ex.Message}");
        }
    }

    private static IOpusEncoder CreateEncoder(int clockRate, int channels)
        => OpusCodecFactory.CreateEncoder(clockRate, channels, OpusApplication.OPUS_APPLICATION_VOIP);

    private static IOpusDecoder CreateDecoder(int clockRate, int channels)
        => OpusCodecFactory.CreateDecoder(clockRate, channels);

    private static string Truncate(string message) => message.Length <= 120 ? message : message[..120] + "…";
}
