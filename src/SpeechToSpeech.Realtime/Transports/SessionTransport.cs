namespace SpeechToSpeech.Realtime.Transports;

/// <summary>
/// 会话传输抽象（上游 <c>transports.py</c> 的 <c>SessionTransport</c>）。
///
/// 事件与出向音频如何到达客户端由传输决定：WebSocket 把音频编码为 base64
/// delta 事件；WebRTC 把音频写进 RTP 媒体轨、事件走 <c>oai-events</c> 数据频道。
/// 连接层（事件分发、管线驱动、打断语义）与传输无关，只依赖本抽象。
/// </summary>
public abstract class SessionTransport
{
    /// <summary>线上传输标识：<c>websocket</c> / <c>webrtc</c>。事件门控按它分流。</summary>
    public abstract string Kind { get; }

    /// <summary>投递一条已序列化的 JSON 事件。</summary>
    public abstract ValueTask SendEventAsync(string payload);

    /// <summary>
    /// 投递一段管线采样率的 PCM16 出向音频。
    ///
    /// WebSocket 传输把它编码成 <c>response.output_audio.delta</c> 事件；
    /// WebRTC 传输把它写进 paced 媒体轨（服务端掌控播放节奏）。
    /// </summary>
    public abstract ValueTask SendAudioChunkAsync(
        string responseId, string itemId, int outputIndex, byte[] pcm16);

    /// <summary>
    /// 丢弃尚未到达客户端的缓冲音频。
    ///
    /// WebSocket 客户端在自己一侧缓冲播放，这里无事可做（ unheard 尾巴归客户端管）；
    /// WebRTC 由服务端 pacing，打断时必须 flush 轨道缓冲，否则打断后助手还在出声。
    /// </summary>
    public abstract void DiscardPendingAudio();

    /// <summary>关闭传输。幂等，失败吞掉（清理路径不允许抛）。</summary>
    public abstract ValueTask CloseAsync();
}

public static class TransportKinds
{
    public const string WebSocket = "websocket";
    public const string WebRtc = "webrtc";
}

/// <summary>上游 <c>_dispatch_client_event</c> 的传输门控错误类型（逐字对齐）。</summary>
public static class TransportGate
{
    public const string InvalidEventForTransport = "invalid_event_for_transport";
    public const string AppendNotSupportedOverWebRtc =
        "In WebRTC mode audio arrives via the media track; input_audio_buffer.append is not supported.";
    public const string ClearWebSocketOnly =
        "output_audio_buffer.clear is only supported on the WebRTC transport.";
}
