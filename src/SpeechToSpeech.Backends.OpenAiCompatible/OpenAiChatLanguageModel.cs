using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Text;

namespace SpeechToSpeech.Backends.OpenAiCompatible;

/// <summary>
/// OpenAI 兼容 <c>/v1/chat/completions</c> 流式后端。
///
/// 对应上游 <c>--llm_backend chat-completions</c>。请求体里的字段集合与上游一致：
/// <c>model / messages / stream / temperature / max_tokens</c>，
/// 工具与指令分别走 <c>tools</c> 与 system 消息。
/// </summary>
public sealed class OpenAiChatLanguageModel : ILanguageModelBackend
{
    private readonly LlmOptions _options;
    private readonly HttpClient _client;

    public OpenAiChatLanguageModel(LlmOptions options)
    {
        _options = options;
        _client = Http.CreateClient(options.BaseUrl ?? "https://api.openai.com", options.ApiKey);
    }

    public string Name => "chat-completions";

    public async IAsyncEnumerable<LlmResponseChunk> GenerateAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await _client
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"chat completions returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var accumulator = new SentenceAccumulator();
        var toolAccum = new SortedDictionary<int, ToolEntry>();
        var finished = false;

        while (!finished)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0)
            {
                continue;
            }

            if (data == "[DONE]")
            {
                finished = true;
                break;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(data);
            }
            catch (Exception)
            {
                continue;
            }

            var choice = node?["choices"]?[0];
            if (choice is null)
            {
                continue;
            }

            var finishReason = choice["finish_reason"]?.GetValue<string>();
            if (finishReason is "length" or "content_filter")
            {
                // 被截断的输出绝不能当成成功：下游会以为这就是完整回答
                throw new IncompleteResponseException(
                    finishReason == "length" ? ResponseIncompleteReason.MaxOutputTokens : ResponseIncompleteReason.ContentFilter,
                    $"provider stopped the response: {finishReason}");
            }

            var delta = choice["delta"];
            var textPiece = delta?["content"]?.GetValue<string>();
            // 上游 _iter_chat_stream_events：工具增量按 index 分桶聚合（name 覆盖、
            // arguments 字符串累加），流末或下一组开始时统一产出。
            var continuingTool = toolAccum.Count > 0;
            if (continuingTool)
            {
                AccumulateTools(toolAccum, delta?["tool_calls"]);
            }

            if (!string.IsNullOrEmpty(textPiece))
            {
                if (continuingTool)
                {
                    // 文本到达意味着上一组「文本段 + 工具调用」收束：先把文本余量
                    // 吐完，再按 index 顺序产出已积累的工具调用。
                    foreach (var sentence in accumulator.Flush())
                    {
                        yield return ChunkText(sentence);
                    }

                    foreach (var chunk in FlushTools(toolAccum))
                    {
                        yield return chunk;
                    }
                }

                foreach (var sentence in accumulator.Push(textPiece))
                {
                    yield return ChunkText(sentence);
                }
            }

            if (!continuingTool)
            {
                AccumulateTools(toolAccum, delta?["tool_calls"]);
            }
        }

        foreach (var sentence in accumulator.Flush())
        {
            yield return ChunkText(sentence);
        }

        foreach (var chunk in FlushTools(toolAccum))
        {
            yield return chunk;
        }
    }

    private static LlmResponseChunk ChunkText(string text)
        => new LlmResponseChunk { Parts = [new AssistantTextPart { Text = text }] }.Normalize();

    private static void AccumulateTools(SortedDictionary<int, ToolEntry> toolAccum, JsonNode? toolCalls)
    {
        if (toolCalls is not JsonArray calls)
        {
            return;
        }

        foreach (var node in calls)
        {
            if (node is not JsonObject call)
            {
                continue;
            }

            var index = call["index"]?.GetValue<int>() ?? toolAccum.Count;
            var entry = toolAccum.TryGetValue(index, out var existing)
                ? existing
                : toolAccum[index] = new ToolEntry();
            if (call["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name)
            {
                entry.Name = name;
            }

            if (call["function"]?["arguments"]?.GetValue<string>() is { Length: > 0 } arguments)
            {
                entry.Arguments += arguments;
            }
        }
    }

    private static IEnumerable<LlmResponseChunk> FlushTools(SortedDictionary<int, ToolEntry> toolAccum)
    {
        foreach (var (_, entry) in toolAccum)
        {
            if (entry.Name.Length == 0)
            {
                continue;
            }

            // 上游 _tool_calls_from_accum：call_id 由服务端新生成（LiveKit 客户端
            // 会截短超长 id，所以是 27 位 hex），提供方返回的 id 不透传。
            yield return new LlmResponseChunk
            {
                Parts =
                [
                    new AssistantToolCallPart
                    {
                        Tool = new ToolCall
                        {
                            CallId = "call_" + Guid.NewGuid().ToString("N")[..27],
                            Name = entry.Name,
                            Arguments = entry.Arguments.Length > 0 ? entry.Arguments : "{}",
                        },
                    },
                ],
            }.Normalize();
        }

        toolAccum.Clear();
    }

    private sealed class ToolEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
    }

    private JsonObject BuildPayload(LlmRequest request)
    {
        var messages = new JsonArray();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.Instructions });
        }

        foreach (var message in request.History)
        {
            messages.Add(new JsonObject
            {
                ["role"] = message.Role == "tool" ? "tool" : message.Role,
                ["content"] = message.Content,
            });
        }

        var payload = new JsonObject
        {
            ["model"] = _options.ModelName ?? "gpt-4o-mini",
            ["messages"] = messages,
            ["stream"] = true,
        };

        if (request.Temperature is not null)
        {
            payload["temperature"] = request.Temperature.Value;
        }

        if (request.MaxOutputTokens is not null)
        {
            payload["max_tokens"] = request.MaxOutputTokens.Value;
        }

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description ?? string.Empty,
                        ["parameters"] = tool.Parameters?.DeepClone() ?? new JsonObject { ["type"] = "object" },
                    },
                });
            }

            payload["tools"] = tools;
        }

        return payload;
    }

    private static string Truncate(string body) => body.Length <= 400 ? body : body[..400] + "…";

    public void Dispose() => _client.Dispose();
}

/// <summary>提供方以长度上限或内容过滤截断了响应。</summary>
public sealed class IncompleteResponseException : Exception
{
    public IncompleteResponseException(ResponseIncompleteReason reason, string message) : base(message)
    {
        Reason = reason;
    }

    public ResponseIncompleteReason Reason { get; }
}
