using System.Net.WebSockets;
using System.Text;

namespace SpeechToSpeech.Realtime.Transports;

/// <summary>
/// JSON-over-WebSocket 传输：音频以 base64 <c>response.output_audio.delta</c> 事件下发。
/// 即 D003 之前 RealtimeConnection 的直连行为，原样搬进抽象。
/// </summary>
public sealed class WebSocketTransport : SessionTransport
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketTransport(WebSocket socket)
    {
        _socket = socket;
    }

    public override string Kind => TransportKinds.WebSocket;

    public override async ValueTask SendEventAsync(string payload)
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
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
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

    public override async ValueTask SendAudioChunkAsync(
        string responseId, string itemId, int outputIndex, byte[] pcm16)
    {
        await SendEventAsync(Protocol.ServerEvent.Serialize(Protocol.ServerEvent.OutputAudioDelta(
            responseId, itemId, Convert.ToBase64String(pcm16), outputIndex))).ConfigureAwait(false);
    }

    public override void DiscardPendingAudio()
    {
        // 未播的音频在客户端一侧；截断是客户端的责任（上游同口径 no-op）。
    }

    public override async ValueTask CloseAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>读循环的入口：连接层用自己的停止令牌驱动。</summary>
    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _socket.ReceiveAsync(buffer, cancellationToken);

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public ValueTask DisposeAsync()
    {
        _sendLock.Dispose();
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
