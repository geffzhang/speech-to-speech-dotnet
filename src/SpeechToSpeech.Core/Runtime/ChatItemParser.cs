using System.Text.Json.Nodes;

namespace SpeechToSpeech.Core.Runtime;

/// <summary>
/// 线上会话项（<c>conversation.item.*</c> / <c>response.input</c> 数组元素）到
/// <see cref="ChatMessage"/> 的解析。
///
/// 对应上游 <c>add_supported_item</c>：只认五种受支持类型——user/system/assistant
/// 消息、function_call（必须带 call_id，后续 output 按它引用，不能悄悄代生成）、
/// function_call_output。其余类型一律拒绝。带内注入（<c>response.create</c> 的
/// <c>input</c>）与带外种子（<c>response.input</c> 走一次性聊天）共用同一套校验，
/// 但失败时机不同：带内是同步 error 事件，带外是 <c>response.failed</c>。
/// </summary>
public static class ChatItemParser
{
    /// <summary>解析单个会话项。失败时 <paramref name="error"/> 给出与上游一致的错误消息。</summary>
    public static bool TryParse(JsonObject item, out ChatMessage? message, out string? error)
    {
        message = null;
        error = null;

        var type = item["type"]?.GetValue<string>();
        var itemId = item["id"]?.GetValue<string>();

        switch (type)
        {
            case "message":
                {
                    var role = item["role"]?.GetValue<string>() ?? "user";
                    var text = ExtractText(item["content"]);
                    message = role switch
                    {
                        "user" => ChatMessage.User(text, itemId),
                        "system" => ChatMessage.System(text, itemId),
                        "assistant" => ChatMessage.Assistant(text, id: itemId),
                        _ => null,
                    };
                    if (message is null)
                    {
                        error = $"Unsupported message role: {role}";
                    }

                    return message is not null;
                }

            case "function_call":
                {
                    // call_id 必须由客户端提供：后续 function_call_output 按它引用，
                    // 这里悄悄生成一个会让工具结果永远对不上号。
                    var callId = item["call_id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(callId))
                    {
                        error = "function_call item is missing a call_id.";
                        return false;
                    }

                    message = ChatMessage.Assistant("", [
                        new ToolCallRef
                        {
                            CallId = callId,
                            Name = item["name"]?.GetValue<string>() ?? "",
                            Arguments = item["arguments"]?.GetValue<string>() ?? "{}",
                        },
                    ], itemId);
                    return true;
                }

            case "function_call_output":
                message = ChatMessage.Tool(
                    item["call_id"]?.GetValue<string>() ?? "",
                    item["output"]?.GetValue<string>() ?? "",
                    itemId);
                return true;

            default:
                error = $"Unsupported item type: {type}";
                return false;
        }
    }

    /// <summary>拼接消息 content 数组里的文本部分（<c>input_text</c> / <c>text</c>）。</summary>
    private static string ExtractText(JsonNode? content)
    {
        if (content is not JsonArray parts)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (part is JsonObject partObject)
            {
                builder.Append(partObject["text"]?.GetValue<string>() ?? "");
            }
        }

        return builder.ToString();
    }
}
