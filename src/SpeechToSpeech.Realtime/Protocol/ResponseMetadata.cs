using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Json;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Realtime.Protocol;

/// <summary>
/// <c>response.metadata</c> 的组装入参。<c>_build_response</c> 读到的与 metadata 有关的全部东西。
/// </summary>
public sealed record ResponseMetadataInput
{
    /// <summary>客户端在 <c>response.create</c> 里自带的那份。原样保留，键序也不动。</summary>
    public IReadOnlyDictionary<string, string>? Client { get; init; }

    /// <summary><c>in_progress</c> / <c>completed</c> / <c>cancelled</c> / <c>incomplete</c> / <c>failed</c>。</summary>
    public required string Status { get; init; }

    /// <summary>本轮真正到达模型的工具结果 id。服务端自有，客户端说了不算。</summary>
    public IReadOnlyCollection<string> ToolInputCallIds { get; init; } = [];

    /// <summary>出带外响应（<c>conversation="none"</c>）不挂工具输入。</summary>
    public bool OutOfBand { get; init; }

    /// <summary>已渲染好的时延记录；null 表示这一轮没有任何测量。</summary>
    public string? LatencyPayload { get; init; }
}

/// <summary>
/// 把若干来源合成 <c>response.metadata</c>。对应上游 <c>_build_response</c> 里与 metadata 有关的那段。
///
/// 单列成纯函数的理由：这里有五条互相纠缠的规则，任何一条漏掉都不报错，只是客户端
/// 少看到或多看到几个键——
/// <list type="number">
/// <item>客户端自带的键全部保留，键序不动。</item>
/// <item>两个保留键客户端说了不算：<c>s2s_tool_input_call_ids</c> 先删（只有服务端知道
///       哪些工具结果到了模型），<c>speech_to_speech.turn_latency</c> 在终态时删。</item>
/// <item>工具输入只在 <c>completed</c> 且非出带外时挂，且受 16 槽位与 512 字符两个上限约束。</item>
/// <item>时延记录只在终态挂，同样受两个上限约束。</item>
/// <item><c>response.created</c>（<c>in_progress</c>）只带客户端那份，不带任何服务端测量。</item>
/// </list>
/// </summary>
public static class ResponseMetadata
{
    /// <summary>工具输入的结果 id 列表。服务端自有。</summary>
    public const string ToolInputKey = "s2s_tool_input_call_ids";

    /// <summary>
    /// 合成 metadata。返回 null 表示一个键都没有——上游此时发 <c>metadata: null</c>，
    /// 而不是空对象：空对象会让客户端以为「这轮确实没有元数据」，null 才是「没有」。
    /// </summary>
    public static JsonObject? Assemble(ResponseMetadataInput input)
    {
        var metadata = new JsonObject();
        if (input.Client is not null)
        {
            foreach (var (key, value) in input.Client)
            {
                metadata[key] = value;
            }
        }

        // 客户端不能自称「哪些工具结果到了模型」。先删再按服务端口径决定要不要写。
        metadata.Remove(ToolInputKey);

        if (input.Status == "completed" && !input.OutOfBand && input.ToolInputCallIds.Count > 0)
        {
            var value = RenderCallIds(input.ToolInputCallIds);
            if (metadata.Count < TurnLatencyMetadata.MaxSlots
                && value.Length <= TurnLatencyMetadata.MaxValueLength)
            {
                metadata[ToolInputKey] = value;
            }
        }

        // 终态的时延是服务端自有的：客户端给的同名键一律不信。
        if (input.Status != "in_progress")
        {
            metadata.Remove(TurnLatencyMetadata.Key);
        }

        if (input.Status != "in_progress"
            && input.LatencyPayload is not null
            && metadata.Count < TurnLatencyMetadata.MaxSlots
            && input.LatencyPayload.Length <= TurnLatencyMetadata.MaxValueLength)
        {
            metadata[TurnLatencyMetadata.Key] = input.LatencyPayload;
        }

        return metadata.Count == 0 ? null : metadata;
    }

    /// <summary>
    /// 读客户端 <c>response.create</c> 里的 <c>metadata</c>。
    ///
    /// 值必须是字符串（Realtime 元数据本来就是字符串表），非字符串的值**跳过而不是强转**：
    /// 强转会抛，把一条本来能跑的请求变成一条 400。跳过只是少一个键。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ReadClientMetadata(JsonObject response)
    {
        if (response["metadata"] is not JsonObject metadata)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (value is JsonValue node && node.TryGetValue<string>(out var text))
            {
                result[key] = text;
            }
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// <c>json.dumps(sorted(call_ids), separators=(",", ":"))</c>。
    /// 排序是**码点序**，不是区域相关的字符串序——换一套排序规则，同一批 id 会得出
    /// 不同的字符串，客户端按值比对就永远对不上。
    ///
    /// 渲染走 <see cref="AsciiJsonDumps"/> 而不是 <c>Utf8JsonWriter</c>：这里对的是
    /// <c>json.dumps</c> 的默认口径（非 ASCII 转 <c>\uXXXX</c>），与线路载荷的
    /// <c>ensure_ascii=False</c> 相反。共用一套编码器必然让其中一边错（C017 的同一课）。
    /// </summary>
    private static string RenderCallIds(IReadOnlyCollection<string> callIds)
        => AsciiJsonDumps.StringArray(callIds.OrderBy(id => id, StringComparer.Ordinal));
}
