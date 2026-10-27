using System.Net;
using System.Text;
using SpeechToSpeech.Backends.OpenAiCompatible;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Runtime;

namespace SpeechToSpeech.Tests;

/// <summary>
/// chat-completions 后端的流式 tool_calls 聚合（D028）。
///
/// 上游 _iter_chat_stream_events：按 index 分桶（name 覆盖、arguments 累加），
/// 文本与工具交错时先收束上一组（文本余量 → 工具），流末产出剩余组；
/// call_id 由客户端重新生成（call_ + 27 位 hex），提供方返回的 id 不透传。
/// </summary>
public sealed class OpenAiToolCallTests
{
    private static OpenAiChatLanguageModel CreateBackend(string sseBody)
    {
        var handler = new StubHandler(sseBody);
        var options = new LlmOptions
        {
            BaseUrl = "http://stub",
            ModelName = "mock",
        };
        var model = new OpenAiChatLanguageModel(options);
        // 把假 handler 塞进后端的 HttpClient（构造器里已建好，替换其字段）
        var field = typeof(OpenAiChatLanguageModel)
            .GetField("_client", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(model, new HttpClient(handler) { BaseAddress = new Uri("http://stub") });
        return model;
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            };
            return Task.FromResult(response);
        }
    }

    private static string Sse(params string[] datas) =>
        string.Concat(datas.Select(d => $"data: {d}\n\n")) + "data: [DONE]\n\n";

    private static string ToolDelta(int index, string? name, string? args, string? id = null)
    {
        var fn = new Dictionary<string, object?>();
        if (name is not null)
        {
            fn["name"] = name;
        }

        if (args is not null)
        {
            fn["arguments"] = args;
        }

        var call = new Dictionary<string, object?> { ["index"] = index, ["function"] = fn };
        if (id is not null)
        {
            call["id"] = id;
        }

        return Json(new { choices = new[] { new { delta = new { tool_calls = new[] { call } }, finish_reason = (string?)null } } });
    }

    private static string TextDelta(string text) =>
        Json(new { choices = new[] { new { delta = new { content = text }, finish_reason = (string?)null } } });

    private static string Finish(string reason) =>
        Json(new { choices = new[] { new { delta = new { }, finish_reason = reason } } });

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    private static async Task<List<LlmResponseChunk>> GenerateAsync(OpenAiChatLanguageModel model)
    {
        var request = new LlmRequest { History = [], Instructions = "test" };
        var chunks = new List<LlmResponseChunk>();
        await foreach (var chunk in model.GenerateAsync(request, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    [Fact]
    public async Task ToolCalls_AreAccumulatedAcrossDeltas()
    {
        // arguments 拆两片、提供方 id 不透传、call_id 本地生成（call_ + 27 hex）。
        var model = CreateBackend(Sse(
            "{\"choices\":[{\"delta\":{},\"finish_reason\":null}]}",
            ToolDelta(0, "get_weather", "{\"city\":", id: "call_from_provider"),
            ToolDelta(0, null, " \"北京\"}"),
            Finish("tool_calls")));

        var chunks = await GenerateAsync(model);

        var tool = Assert.IsType<AssistantToolCallPart>(Assert.Single(chunks).Parts.Single());
        Assert.Equal("get_weather", tool.Tool.Name);
        Assert.Equal("{\"city\": \"北京\"}", tool.Tool.Arguments);
        Assert.Matches("^call_[0-9a-f]{27}$", tool.Tool.CallId);
    }

    [Fact]
    public async Task TextThenTools_ProduceTextChunksBeforeToolCall()
    {
        // 文本先流出（分句），工具在流末统一产出——顺序保持。
        var model = CreateBackend(Sse(
            TextDelta("看一下"),
            ToolDelta(0, "get_weather", "{}"),
            Finish("tool_calls")));

        var chunks = await GenerateAsync(model);

        Assert.True(chunks.Count >= 1);
        Assert.All(chunks.Take(chunks.Count - 1),
            c => Assert.IsType<AssistantTextPart>(c.Parts.Single()));
        var last = Assert.IsType<AssistantToolCallPart>(chunks[^1].Parts.Single());
        Assert.Equal("get_weather", last.Tool.Name);
    }

    [Fact]
    public async Task TextAfterTools_FlushesPreviousGroupFirst()
    {
        // 工具组开始后又有新文本：上一组（工具）必须在新文本之前收束。
        var model = CreateBackend(Sse(
            ToolDelta(0, "get_weather", "{}"),
            TextDelta("查好了"),
            Finish("stop")));

        var chunks = await GenerateAsync(model);
        var kinds = chunks.Select(c => c.Parts.Single().GetType()).ToList();
        var toolIndex = kinds.IndexOf(typeof(AssistantToolCallPart));
        var textAfter = kinds.Skip(toolIndex + 1).ToList();

        Assert.Contains(typeof(AssistantToolCallPart), kinds);
        Assert.All(textAfter, t => Assert.Equal(typeof(AssistantTextPart), t));
    }
}
