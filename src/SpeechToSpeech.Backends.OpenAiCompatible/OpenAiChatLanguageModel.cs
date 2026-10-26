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

            var delta = choice["delta"]?["content"]?.GetValue<string>();
            if (string.IsNullOrEmpty(delta))
            {
                continue;
            }

            foreach (var sentence in accumulator.Push(delta))
            {
                yield return Chunk(sentence);
            }
        }

        foreach (var sentence in accumulator.Flush())
        {
            yield return Chunk(sentence);
        }
    }

    private static LlmResponseChunk Chunk(string text)
        => new LlmResponseChunk { Parts = [new AssistantTextPart { Text = text }] }.Normalize();

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
