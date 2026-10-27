using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Realtime;
using SpeechToSpeech.Realtime.Protocol;
using SpeechToSpeech.Realtime.Transports;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 传输门控语义（上游 <c>_dispatch_client_event</c> L422-446 的三条规则）：
/// <list type="bullet">
/// <item><c>input_audio_buffer.append</c> 在 WebRTC 会话被拒绝——音频走媒体轨，显式 append 是协议错误；</item>
/// <item><c>output_audio_buffer.clear</c> 仅 WebRTC 支持——WebSocket 的未播音频在客户端，服务端无从清起；</item>
/// <item><c>conversation.item.truncate</c> 两种传输都接受且无动作。</item>
/// </list>
/// 错误形状走 make_error 线（type=invalid_event_for_transport，无 code），带 event_id 关联。
/// </summary>
public sealed class TransportGateTests
{
    private static RealtimeServerOptions Options() => new()
    {
        Host = "127.0.0.1",
        Port = 0,
        DefaultVoice = "ryan",
        DefaultInstructions = "test",
    };

    private static BackendContext Backends() => new()
    {
        Vad = new VadOptions(),
        Stt = new SttOptions(),
        Llm = new LlmOptions(),
        Tts = new TtsOptions(),
    };

    /// <summary>记录事件并把入向音频吞掉的假传输：连接不会碰任何真实 IO。</summary>
    private sealed class FakeTransport(string kind) : SessionTransport
    {
        public List<string> Events { get; } = [];
        public List<byte[]> Audio { get; } = [];
        public int DiscardCount { get; private set; }

        public override string Kind => kind;

        public override ValueTask SendEventAsync(string payload)
        {
            Events.Add(payload);
            return ValueTask.CompletedTask;
        }

        public override ValueTask SendAudioChunkAsync(
            string responseId, string itemId, int outputIndex, byte[] pcm16)
        {
            Audio.Add(pcm16);
            return ValueTask.CompletedTask;
        }

        public override void DiscardPendingAudio() => DiscardCount++;

        public override ValueTask CloseAsync() => ValueTask.CompletedTask;
    }

    private static (RealtimeConnection Connection, FakeTransport Transport) Make(string kind)
    {
        var transport = new FakeTransport(kind);
        var connection = new RealtimeConnection(transport, Options(), Backends());
        return (connection, transport);
    }

    private static JsonObject Parse(string payload)
        => JsonNode.Parse(payload)!.AsObject();

    private static string LastEventType(FakeTransport transport)
        => Parse(transport.Events[^1])["type"]!.GetValue<string>();

    private static (string? Type, string Message, string? EventId) LastError(FakeTransport transport)
    {
        var json = Parse(transport.Events[^1]);
        var error = json["error"]!.AsObject();
        return (
            error["type"]?.GetValue<string>(),
            error["message"]!.GetValue<string>(),
            error["event_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task Append_OverWebSocket_IsAccepted()
    {
        var (connection, transport) = Make(TransportKinds.WebSocket);
        await connection.DispatchClientEventAsync(
            """{"type":"input_audio_buffer.append","event_id":"evt_a","audio":"AQID"}""");

        // 合法 append 无任何回执（静默入队），只有错误才会发事件
        Assert.Empty(transport.Events);
    }

    [Fact]
    public async Task Append_OverWebRtc_IsRejectedWithCorrelation()
    {
        var (connection, transport) = Make(TransportKinds.WebRtc);
        await connection.DispatchClientEventAsync(
            """{"type":"input_audio_buffer.append","event_id":"evt_b","audio":"AQID"}""");

        Assert.Equal(EventNames.Error, LastEventType(transport));
        var (type, message, eventId) = LastError(transport);
        Assert.Equal(TransportGate.InvalidEventForTransport, type);
        Assert.Equal(TransportGate.AppendNotSupportedOverWebRtc, message);
        Assert.Equal("evt_b", eventId);
    }

    [Fact]
    public async Task Clear_OverWebSocket_IsRejected()
    {
        var (connection, transport) = Make(TransportKinds.WebSocket);
        await connection.DispatchClientEventAsync(
            """{"type":"output_audio_buffer.clear","event_id":"evt_c"}""");

        var (type, message, eventId) = LastError(transport);
        Assert.Equal(TransportGate.InvalidEventForTransport, type);
        Assert.Equal(TransportGate.ClearWebSocketOnly, message);
        Assert.Equal("evt_c", eventId);
    }

    [Fact]
    public async Task Clear_OverWebRtc_IsAcceptedAndDiscards()
    {
        var (connection, transport) = Make(TransportKinds.WebRtc);
        await connection.DispatchClientEventAsync(
            """{"type":"output_audio_buffer.clear","event_id":"evt_d"}""");

        // 无活动响应时 clear 只 flush 传输缓冲，不发任何事件
        Assert.Empty(transport.Events);
        Assert.Equal(1, transport.DiscardCount);
    }

    [Fact]
    public async Task Truncate_IsAcceptedSilently_OnBothTransports()
    {
        foreach (var kind in new[] { TransportKinds.WebSocket, TransportKinds.WebRtc })
        {
            var (connection, transport) = Make(kind);
            await connection.DispatchClientEventAsync(
                """{"type":"conversation.item.truncate","event_id":"evt_t","item_id":"item_x"}""");

            Assert.Empty(transport.Events);
        }
    }

    [Fact]
    public async Task ResponseCancel_AlwaysDiscardsPendingAudio()
    {
        // 上游 response.cancel 的 dispatch 对所有传输调 discard_pending_audio；
        // WebSocket no-op / WebRTC flush——计数断言的是「被调用」，具体行为归传输。
        var (connection, transport) = Make(TransportKinds.WebRtc);
        await connection.DispatchClientEventAsync("""{"type":"response.cancel"}""");

        Assert.Equal(1, transport.DiscardCount);
    }
}
