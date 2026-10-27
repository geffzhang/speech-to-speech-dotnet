using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Pipeline.Handlers;
using SpeechToSpeech.Core.Runtime;
using Xunit;

namespace SpeechToSpeech.Tests;

/// <summary>
/// <c>response.create</c> 的 <c>input</c> 数组（D025）：带内进会话、带外做一次性种子。
///
/// 上游两种入口共用 <c>add_supported_item</c> 的校验，但失败时机不同——
/// 带内是同步 error 事件（响应根本不创建），带外是 <c>response.failed</c>。
/// 每条断言都盯住这两个时机差异。
/// </summary>
public sealed class RealtimeInputItemsTests
{
    // ── 会话项解析（上游 add_supported_item 的校验面） ────────────────

    [Fact]
    public void Parser_AcceptsAllFiveSupportedItemTypes()
    {
        var user = Parse(new JsonObject
        {
            ["type"] = "message", ["role"] = "user", ["id"] = "item_u",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = "你好" } },
        });
        Assert.Equal("user", user.Role);
        Assert.Equal("你好", user.Content);
        Assert.Equal("item_u", user.Id);

        var system = Parse(new JsonObject
        {
            ["type"] = "message", ["role"] = "system",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "message", ["text"] = "规则" } },
        });
        Assert.Equal("system", system.Role);

        var assistant = Parse(new JsonObject
        {
            ["type"] = "message", ["role"] = "assistant",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "回答" } },
        });
        Assert.Equal("assistant", assistant.Role);

        var call = Parse(new JsonObject
        {
            ["type"] = "function_call", ["call_id"] = "call_1", ["name"] = "weather", ["arguments"] = "{\"city\":\"广州\"}",
        });
        Assert.Equal("assistant", call.Role);
        var toolCall = Assert.Single(call.ToolCalls);
        Assert.Equal("call_1", toolCall.CallId);
        Assert.Equal("weather", toolCall.Name);
        Assert.Equal("{\"city\":\"广州\"}", toolCall.Arguments);

        var output = Parse(new JsonObject
        {
            ["type"] = "function_call_output", ["call_id"] = "call_1", ["output"] = "下雨",
        });
        Assert.Equal("tool", output.Role);
        Assert.Equal("call_1", output.ToolCallId);
        Assert.Equal("下雨", output.Content);
    }

    [Fact]
    public void Parser_RejectsFunctionCallWithoutCallId()
    {
        // call_id 必须客户端给：后续 output 按它引用，悄悄代生成会让结果永远对不上号。
        var ok = ChatItemParser.TryParse(
            new JsonObject { ["type"] = "function_call", ["name"] = "weather" },
            out _, out var error);

        Assert.False(ok);
        Assert.Equal("function_call item is missing a call_id.", error);
    }

    [Fact]
    public void Parser_RejectsUnsupportedItemTypes()
    {
        var ok = ChatItemParser.TryParse(
            new JsonObject { ["type"] = "function_call_output_item" },
            out _, out var error);

        Assert.False(ok);
        Assert.Equal("Unsupported item type: function_call_output_item", error);
    }

    // ── 悬空调用的判定（上游 has_pending_tool_calls） ─────────────────

    [Fact]
    public void PendingToolCalls_SequenceOrderDoesNotMatter()
    {
        // 上游用字典登记/摘除：工具结果先于调用出现同样算已应答。
        var answered = new[]
        {
            new ChatMessage { Role = "tool", ToolCallId = "call_1", Content = "先到的结果" },
            ChatMessage.Assistant("", [new ToolCallRef { CallId = "call_1", Name = "weather" }]),
        };
        Assert.False(ChatSession.HasPendingToolCalls(answered));

        var pending = new[]
        {
            ChatMessage.Assistant("", [new ToolCallRef { CallId = "call_1", Name = "weather" }]),
        };
        Assert.True(ChatSession.HasPendingToolCalls(pending));
    }

    [Fact]
    public void PendingToolCalls_MergeWithInbandInputBeforeJudging()
    {
        // 带内 input 的 function_call 若本批里带了 output，不算悬空；
        // 只带调用不带结果才算。上游校验进 candidate_chat 副本（合并后判定）。
        var chat = new ChatSession();
        var input = new[]
        {
            ChatMessage.Assistant("", [new ToolCallRef { CallId = "call_in", Name = "weather" }]),
            new ChatMessage { Role = "tool", ToolCallId = "call_in", Content = "晴" },
        };
        Assert.False(ChatSession.HasPendingToolCalls(chat.Snapshot().Concat(input)));

        var dangling = new[]
        {
            ChatMessage.Assistant("", [new ToolCallRef { CallId = "call_dangling", Name = "weather" }]),
        };
        Assert.True(ChatSession.HasPendingToolCalls(chat.Snapshot().Concat(dangling)));
    }

    // ── 带外种子的三种口径（上游 build_active_chat） ──────────────────

    [Fact]
    public void OutOfBand_InputNullReadsConversationCopy()
    {
        // input 未携带 = 读会话副本（D024 之前的既有行为保持不变）。
        var config = new RuntimeConfig();
        config.Chat.Append(ChatMessage.User("上下文里的句子"));

        var end = RunLanguageModel(config, response: new RealtimeResponseCreateParams { Conversation = "none" });

        Assert.Null(end.Error);
    }

    [Fact]
    public void OutOfBand_InputArraySeedsThrowawayChat()
    {
        // input=[...]：临时聊天由这些项做种子，会话本身不参与。
        // 历史为空但种子非空 → 有上下文可生成；若种子没接上，空历史守卫会拦下。
        var config = new RuntimeConfig();
        var response = new RealtimeResponseCreateParams
        {
            Conversation = "none",
            Input =
            [
                new JsonObject
                {
                    ["type"] = "message", ["role"] = "user",
                    ["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = "种子" } },
                },
            ],
        };

        var end = RunLanguageModel(config, response);

        Assert.Null(end.Error);
    }

    [Fact]
    public void OutOfBand_InputEmptyArrayMeansFreshChat()
    {
        // input=[]：清空上下文。没有种子也没有 instructions 时，真的没有可生成的
        // 上下文——守卫仍拦（与带内空口径一致）。
        var response = new RealtimeResponseCreateParams { Conversation = "none", Input = [] };

        var end = RunLanguageModel(new RuntimeConfig(), response);

        Assert.Equal("no conversation context to respond to", end.Error);
    }

    [Fact]
    public void OutOfBand_SeedValidationErrorFailsTheResponse()
    {
        // 带外种子校验失败走 response.failed（EndOfResponse.Error），
        // 不像带内那样是同步 error 事件。
        var response = new RealtimeResponseCreateParams
        {
            Conversation = "none",
            Input = [new JsonObject { ["type"] = "function_call", ["name"] = "weather" }],
        };

        var end = RunLanguageModel(new RuntimeConfig(), response);

        Assert.Equal("function_call item is missing a call_id.", end.Error);
    }

    // ── 带内生成前的悬空调用检查（上游生成路径同一道闸） ──────────────

    [Fact]
    public void InBand_GenerationRejectsPendingToolCalls()
    {
        // 会话里有 function_call 没等到 output 时，新的生成请求直接失败——
        // 模型会把悬空调用当成已应答。
        var config = new RuntimeConfig();
        config.Chat.Append(ChatMessage.Assistant("", [new ToolCallRef { CallId = "call_x", Name = "weather" }]));

        var end = RunLanguageModel(config, response: null);

        Assert.Equal(
            "Cannot generate a response while function call outputs are pending.",
            end.Error);
    }

    // ── 夹具 ────────────────────────────────────────────────────────

    private static ChatMessage Parse(JsonObject item)
    {
        Assert.True(ChatItemParser.TryParse(item, out var message, out var error), error);
        return message!;
    }

    private static EndOfResponse RunLanguageModel(RuntimeConfig config, RealtimeResponseCreateParams? response)
    {
        var handler = new LanguageModelHandler(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new OneWordLlm(),
            new CancelScope(),
            new SpeculativeTurnTracker(),
            new TurnLatencyStore());

        var produced = InvokeProcess(
            handler,
            GenerateResponseRequest.Create(config, response, responseKey: "resp_1", inputItems: response?.Input));

        return produced.OfType<EndOfResponse>().Single();
    }

    private static IEnumerable<PipelineMessage> InvokeProcess<TParam>(object target, TParam parameter)
    {
        var method = target.GetType().GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(TParam)])!;
        return (IEnumerable<PipelineMessage>)method.Invoke(target, [parameter])!;
    }

    private sealed class OneWordLlm : ILanguageModelBackend
    {
        public string Name => "one-word";

        public async IAsyncEnumerable<LlmResponseChunk> GenerateAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new LlmResponseChunk { Text = "好的" };
        }

        public void Dispose()
        {
        }
    }
}
