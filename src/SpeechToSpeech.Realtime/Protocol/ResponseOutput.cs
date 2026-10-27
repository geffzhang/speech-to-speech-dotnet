using System.Text.Json.Nodes;

namespace SpeechToSpeech.Realtime.Protocol;

/// <summary>
/// 终态 <c>response.output</c> 数组的组装规则。
///
/// 消息项与函数调用项按 output_index 合并排序——客户端用 <c>response.done</c> 的
/// <c>output[]</c> 驱动函数执行，顺序与流式事件的下标一致是协议承诺
/// （上游 <c>_build_output_items</c> 的口径）。item 本身不带 index 字段，
/// 所以排序只能发生在这里，<see cref="ServerEvent.BuildResponse"/> 原样保留传入顺序。
/// </summary>
public static class ResponseOutput
{
    /// <summary>按下标合并两类输出项；同下标冲突视为实现错误（文本与调用不可共占一项）。</summary>
    public static IReadOnlyList<JsonObject> Merge(
        IEnumerable<(int OutputIndex, JsonObject Item)> messages,
        IEnumerable<(int OutputIndex, JsonObject Item)> functionCalls)
    {
        var byIndex = new SortedDictionary<int, JsonObject>();
        foreach (var (index, item) in messages)
        {
            byIndex.Add(index, item);
        }

        foreach (var (index, item) in functionCalls)
        {
            byIndex.Add(index, item);
        }

        return [.. byIndex.Values];
    }
}
