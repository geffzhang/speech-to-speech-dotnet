using System.Text.Json.Nodes;

namespace SpeechToSpeech.Realtime.Protocol;

/// <summary>
/// 一次响应的 token 用量。对应上游 <c>UsageMetrics</c> 里被 <c>_build_response</c> 读到的那两个计数。
///
/// 只保留 input/output 两项：上游协议里的 <c>audio_tokens</c> / <c>cached_tokens</c> /
/// <c>image_tokens</c> 在纯文本管线里恒为 0，不是可观测的量，留着反而会暗示这些维度被支持。
/// </summary>
public readonly record struct ResponseUsage(int InputTokens, int OutputTokens)
{
    public static readonly ResponseUsage Empty = new(0, 0);
}

/// <summary>
/// <c>response</c> 对象的全部可见字段。
///
/// 上游把所有字段集中在一个 builder（<c>_build_response</c>）里生成，<c>response.created</c> 与
/// <c>response.done</c> 用的是同一份口径——差别只在 status 和当时的 output。本类型就是那个
/// builder 的入参：两处共用它，字段就不会漂。
/// </summary>
public sealed record ResponseSnapshot
{
    public required string Id { get; init; }

    /// <summary><c>in_progress</c> / <c>completed</c> / <c>cancelled</c> / <c>incomplete</c> / <c>failed</c>。</summary>
    public required string Status { get; init; }

    /// <summary>终态原因。官方 schema 限定为 <c>turn_detected</c> / <c>client_cancelled</c> / <c>max_output_tokens</c> / <c>content_filter</c>。</summary>
    public string? Reason { get; init; }

    /// <summary><c>failed</c> 时的错误类型，上游恒为 <c>response_failed</c>。</summary>
    public string? ErrorType { get; init; }

    /// <summary>会话配置里的音色。未配置时 <c>audio.output</c> 发空对象——上游同样不发 <c>voice</c> 键。</summary>
    public string? Voice { get; init; }

    /// <summary>连接级会话标识。上游每个连接生成一次，全生命周期不变；出带外响应时为 null。</summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// <c>response.metadata</c>。null 表示不发这个键。
    ///
    /// 与 <c>conversation_id</c> 同一约定：没有就不发，不发空对象——空对象会让客户端
    /// 以为「这一轮确实没有元数据」，少一个键才是「这一版不上报」。
    /// </summary>
    public JsonObject? Metadata { get; init; }

    /// <summary>本次响应解析到的模态。二者互斥：<c>audio</c> 已隐含带转写文本。</summary>
    public IReadOnlyList<string> OutputModalities { get; init; } = [];

    /// <summary>已生成的消息项，顺序与流式事件一致。<c>in_progress</c> 时须为空。</summary>
    public IReadOnlyList<JsonObject> Output { get; init; } = [];

    public ResponseUsage Usage { get; init; } = ResponseUsage.Empty;
}
