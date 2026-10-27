using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Pipeline.Handlers;
using SpeechToSpeech.Realtime.Protocol;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 线路形状测试。
///
/// 这些断言锁的是"客户端看到什么"，不是"我们内部怎么想"：字段名、层次、默认值。
/// 协议层的等价性只能靠这一类测试守住——它能编译、能跑，但不代表 OpenAI Agents SDK
/// 认得出这些事件。
/// </summary>
public sealed class ProtocolShapeTests
{
    [Fact]
    public void Error_MakeErrorShapeCarriesTypeOnly()
    {
        // 上游 make_error（build_error_event）形状：error 只有 message + type，无 code/param。
        // 与 ServerEvent.Error 的 schema 校验路径（invalid_request_error + code）是两条线（D027）。
        var payload = ServerEvent.TypeError("unknown_or_invalid_event", "Unknown or invalid event: foo");

        Assert.Equal("error", payload["type"]!.GetValue<string>());
        Assert.Equal("unknown_or_invalid_event", payload["error"]!["type"]!.GetValue<string>());
        Assert.Equal("Unknown or invalid event: foo", payload["error"]!["message"]!.GetValue<string>());
        Assert.Null(payload["error"]!["code"]);
        Assert.Null(payload["error"]!["param"]);
        Assert.NotNull(payload["event_id"]);
    }

    [Fact]
    public void Error_MakeErrorShapeCarriesCorrelatedEventId()
    {
        // 上游 send_correlated 把客户端 event_id 写进 error.event_id（事件级 event_id 仍是服务端新生的）。
        var payload = ServerEvent.TypeError(
            "conversation_item_not_found", "No conversation item with id 'x' found.", "event_client_1");

        Assert.Equal("event_client_1", payload["error"]!["event_id"]!.GetValue<string>());
        Assert.NotEqual("event_client_1", payload["event_id"]!.GetValue<string>());
    }

    [Fact]
    public void ResponseDone_CompletedCarriesCompletedStatusDetails()
    {        // 上游 _build_response 对任何终态都发 status_details，completed 也发，type 与 status 同名。
        // 原先 completed 不发这段，客户端只能靠 status 字段反推「正常结束」——
        // 而按判别器解析的实现会把它当成缺字段。
        var payload = ServerEvent.ResponseDone(Snapshot("completed"));

        Assert.Equal("response.done", payload["type"]!.GetValue<string>());
        Assert.Equal("resp_1", payload["response"]!["id"]!.GetValue<string>());
        Assert.Equal("completed", payload["response"]!["status"]!.GetValue<string>());
        Assert.Equal("completed", payload["response"]!["status_details"]!["type"]!.GetValue<string>());
        Assert.NotNull(payload["event_id"]);
    }

    [Fact]
    public void ResponseDone_IncompleteCarriesReason()
    {
        var payload = ServerEvent.ResponseDone(Snapshot("incomplete", "max_output_tokens"));

        Assert.Equal("incomplete", payload["response"]!["status"]!.GetValue<string>());
        var details = payload["response"]!["status_details"]!;
        Assert.Equal("incomplete", details["type"]!.GetValue<string>());
        Assert.Equal("max_output_tokens", details["reason"]!.GetValue<string>());
    }

    [Fact]
    public void TranscriptionCompleted_CarriesDurationUsage()
    {
        // usage 在官方 schema 里是必填（duration / tokens 二选一）。缺了不会降级，
        // 是整条事件被严格客户端拒收——所以这条不能靠「客户端宽容」蒙过去
        var payload = ServerEvent.TranscriptionCompleted("item_a", "你好", audioSeconds: 3.5);

        var usage = payload["usage"]!;
        Assert.Equal("duration", usage["type"]!.GetValue<string>());
        Assert.Equal(3.5, usage["seconds"]!.GetValue<double>());
    }

    [Fact]
    public void ResponseDone_CancelledUsesCancelledStatusDetails()
    {
        // 回归：status_details.type 曾恒写 "incomplete"，取消时会产出
        // 「type=incomplete + reason=client_cancelled」这种官方 schema 里不存在的组合
        var payload = ServerEvent.ResponseDone(Snapshot("cancelled", "client_cancelled"));

        Assert.Equal("cancelled", payload["response"]!["status"]!.GetValue<string>());
        var details = payload["response"]!["status_details"]!;
        Assert.Equal("cancelled", details["type"]!.GetValue<string>());
        Assert.Equal("client_cancelled", details["reason"]!.GetValue<string>());
    }

    [Fact]
    public void ResponseDone_FailedCarriesErrorTypeOnly()
    {
        // 上游只带 error.type（恒 "response_failed"）。失败详情走独立的 error 事件——
        // 两处都写 message 时客户端只显示其中一处，另一处就永久沉默。
        var payload = ServerEvent.ResponseDone(Snapshot("failed"));

        Assert.Equal("failed", payload["response"]!["status"]!.GetValue<string>());
        var details = payload["response"]!["status_details"]!;
        Assert.Equal("failed", details["type"]!.GetValue<string>());
        Assert.Equal("response_failed", details["error"]!["type"]!.GetValue<string>());
        Assert.Null(details["error"]!["message"]);
    }

    [Fact]
    public void Response_CarriesUsageModalitiesVoiceAndConversation()
    {
        // 这四项都是「客户端读得到但缺了不报错」的字段：缺 usage 时下游按 response 计费读到 0，
        // 缺 conversation_id 时历史回放把每轮当成不同会话。所以只能靠断言守。
        var response = ServerEvent.BuildResponse(Snapshot("completed"));

        Assert.Equal("conv_1", response["conversation_id"]!.GetValue<string>());
        Assert.Equal("zf_xiaobei", response["audio"]!["output"]!["voice"]!.GetValue<string>());
        var modalities = response["output_modalities"]!.AsArray();
        Assert.Single(modalities);
        Assert.Equal("audio", modalities[0]!.GetValue<string>());

        var usage = response["usage"]!;
        Assert.Equal(12, usage["input_tokens"]!.GetValue<int>());
        Assert.Equal(7, usage["output_tokens"]!.GetValue<int>());
        Assert.Equal(19, usage["total_tokens"]!.GetValue<int>());
        // audio_tokens / cached_tokens / image_tokens 在纯文本管线里恒 0：
        // 字段要在（客户端按 schema 读），值恒 0（这几维没被支持）。
        Assert.Equal(0, usage["input_token_details"]!["audio_tokens"]!.GetValue<int>());
        Assert.Equal(12, usage["input_token_details"]!["text_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Response_NoVoiceMeansEmptyAudioOutput()
    {
        // 没配音色时上游发 {"output": {}}，不是省掉整个 audio 键——省键会让解析器
        // 在解引用 audio.output.voice 时踩空。
        var response = ServerEvent.BuildResponse(Snapshot("in_progress") with { Voice = null });

        Assert.NotNull(response["audio"]!["output"]);
        Assert.Null(response["audio"]!["output"]!["voice"]);
    }

    [Fact]
    public void Response_CreatedAndDoneShareTheSameFieldSet()
    {
        // 客户端用 created 的载荷建立「当前响应」，再用 done 的替换它。
        // 两次字段集不一致，严格解析器会在其中一次失败。唯一的合法差异是 status_details——
        // 它只在终态出现（上游同样如此：in_progress 时不构造 status_details）。
        var created = ServerEvent.ResponseCreated(Snapshot("in_progress"))["response"]!.AsObject();
        var done = ServerEvent.ResponseDone(Snapshot("completed"))["response"]!.AsObject();

        var createdKeys = created.Select(pair => pair.Key).Order().ToList();
        var doneKeys = done.Select(pair => pair.Key).Order().ToList();

        Assert.Equal(createdKeys.Concat(["status_details"]).Order(), doneKeys);
    }

    [Fact]
    public void Response_TerminalOutputItemMatchesOutputItemDone()
    {
        // 终态 output 里的消息项与 output_item.done 里的那份必须逐字段相同：
        // 客户端会把它们当同一条消息的两份快照。不一致时后到的 done 会把先渲染的内容盖掉。
        var item = ServerEvent.AssistantMessageItem("item_1", "completed", wantsAudio: true, "你好");
        var donePayload = ServerEvent.OutputItemDone("resp_1", "item_1", "你好");
        var terminal = ServerEvent.BuildResponse(Snapshot("completed") with { Output = [item] });

        Assert.Equal(
            donePayload["item"]!.ToJsonString(),
            terminal["output"]![0]!.ToJsonString());
    }

    [Fact]
    public void Error_UsesNestedErrorObjectWithCode()
    {
        var payload = ServerEvent.Error(ErrorCodes.ConversationAlreadyHasActiveResponse, "already busy");

        Assert.Equal("error", payload["type"]!.GetValue<string>());
        Assert.Equal("conversation_already_has_active_response", payload["error"]!["code"]!.GetValue<string>());
        Assert.Equal("already busy", payload["error"]!["message"]!.GetValue<string>());
        Assert.Equal("invalid_request_error", payload["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void AudioDelta_CarriesResponseAndItemIdsAndBase64Payload()
    {
        var payload = ServerEvent.OutputAudioDelta("resp_1", "item_1", "AAAA");

        Assert.Equal("response.output_audio.delta", payload["type"]!.GetValue<string>());
        Assert.Equal("resp_1", payload["response_id"]!.GetValue<string>());
        Assert.Equal("item_1", payload["item_id"]!.GetValue<string>());
        Assert.Equal("AAAA", payload["delta"]!.GetValue<string>());
        Assert.Equal(0, payload["output_index"]!.GetValue<int>());
        Assert.Equal(0, payload["content_index"]!.GetValue<int>());
    }

    [Fact]
    public void Serialize_ProducesCompactJson()
    {
        var json = ServerEvent.Serialize(ServerEvent.OutputAudioDone("resp_1", "item_1"));

        Assert.DoesNotContain('\n', json);
        Assert.StartsWith("{\"type\":", json);
    }

    [Fact]
    public void EveryServerEventHasAnEventId()
    {
        var events = new JsonObject[]
        {
            ServerEvent.SessionUpdated(new JsonObject()),
            ServerEvent.SpeechStarted("item_1", 0),
            ServerEvent.SpeechStopped("item_1", 100),
            ServerEvent.InputAudioBufferCommitted("item_1", null),
            ServerEvent.TranscriptionDelta("item_1", "hi"),
            ServerEvent.TranscriptionCompleted("item_1", "hi"),
            ServerEvent.TranscriptionFailed("item_1", "boom"),
            ServerEvent.ConversationItemCreated("item_1", "user"),
            ServerEvent.ConversationItemDeleted("item_1"),
            ServerEvent.ResponseCreated(Snapshot("in_progress")),
            ServerEvent.OutputItemAdded("resp_1", "item_1", 0),
            ServerEvent.ContentPartAdded("resp_1", "item_1", 0, 0, wantsAudio: true),
            ServerEvent.OutputTextDelta("resp_1", "item_1", "hi"),
            ServerEvent.OutputTextDone("resp_1", "item_1", "hi"),
            ServerEvent.ContentPartDone("resp_1", "item_1", "hi"),
            ServerEvent.OutputItemDone("resp_1", "item_1", "hi"),
            ServerEvent.OutputAudioDone("resp_1", "item_1"),
            ServerEvent.OutputAudioTranscriptDelta("resp_1", "item_1", "hi"),
            ServerEvent.OutputAudioTranscriptDone("resp_1", "item_1", "hi"),
            ServerEvent.FunctionCallArgumentsDone("resp_1", "item_1", "call_1", "search", "{}"),
            ServerEvent.ResponseDone(Snapshot("completed")),
        };

        foreach (var payload in events)
        {
            Assert.True(payload.ContainsKey("event_id"), $"missing event_id on {payload["type"]}");
        }
    }

    /// <summary>一份字段齐备的响应快照。默认值刻意取非空，缺字段的回归才会被断言抓到。</summary>
    private static ResponseSnapshot Snapshot(string status, string? reason = null) => new()
    {
        Id = "resp_1",
        Status = status,
        Reason = reason,
        Voice = "zf_xiaobei",
        ConversationId = "conv_1",
        OutputModalities = ["audio"],
        Output = [],
        Usage = new ResponseUsage(12, 7),
    };
}

/// <summary>
/// 助手消息项的生命周期形状。
///
/// 这一族事件是 OpenAI 官方客户端的渲染契约：靠 <c>output_item.added</c> 建立消息项、
/// 靠 <c>content_part.added</c> 建立内容片、靠 <c>done</c> 收口。形状错了客户端的做法
/// 是静默不显示，而不是报错——所以只能靠断言守。
/// </summary>
public sealed class AssistantItemLifecycleTests
{
    [Fact]
    public void OutputItemAdded_OpensAssistantMessageWithEmptyContent()
    {
        var payload = ServerEvent.OutputItemAdded("resp_1", "item_1", 0);

        Assert.Equal("response.output_item.added", payload["type"]!.GetValue<string>());
        var item = payload["item"]!;
        Assert.Equal("item_1", item["id"]!.GetValue<string>());
        Assert.Equal("message", item["type"]!.GetValue<string>());
        Assert.Equal("assistant", item["role"]!.GetValue<string>());
        Assert.Equal("in_progress", item["status"]!.GetValue<string>());
        // 空数组而非 null：客户端会直接遍历它
        Assert.Empty(item["content"]!.AsArray());
        Assert.Equal(0, payload["output_index"]!.GetValue<int>());
    }

    [Fact]
    public void ContentPartAdded_AudioModalityUsesAudioPartShape()
    {
        var payload = ServerEvent.ContentPartAdded("resp_1", "item_1", 0, 0, wantsAudio: true);

        var part = payload["part"]!;
        Assert.Equal("audio", part["type"]!.GetValue<string>());
        Assert.Equal("", part["audio"]!.GetValue<string>());
        Assert.Equal("", part["transcript"]!.GetValue<string>());
    }

    [Fact]
    public void ContentPartAdded_TextModalityUsesTextPartShape()
    {
        var payload = ServerEvent.ContentPartAdded("resp_1", "item_1", 0, 0, wantsAudio: false);

        var part = payload["part"]!;
        Assert.Equal("text", part["type"]!.GetValue<string>());
        Assert.Equal("", part["text"]!.GetValue<string>());
        Assert.Null(part["transcript"]);
    }

    [Fact]
    public void OutputItemDone_AudioModalityCarriesTranscriptInContentArray()
    {
        var payload = ServerEvent.OutputItemDone("resp_1", "item_1", "你好。", wantsAudio: true);

        var content = payload["item"]!["content"]!.AsArray();
        Assert.Single(content);
        Assert.Equal("output_audio", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("你好。", content[0]!["transcript"]!.GetValue<string>());
        Assert.Equal("completed", payload["item"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void OutputItemDone_TextModalityCarriesTextInContentArray()
    {
        var payload = ServerEvent.OutputItemDone("resp_1", "item_1", "你好。", wantsAudio: false);

        var content = payload["item"]!["content"]!.AsArray();
        Assert.Equal("output_text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("你好。", content[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void OutputItemDone_IncompleteMarksStatusSoClientKnowsItWasCut()
    {
        var payload = ServerEvent.OutputItemDone("resp_1", "item_1", "我说到一半", completed: false);

        Assert.Equal("incomplete", payload["item"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void FunctionCallOutputItemAdded_OpensWithEmptyArgumentsInProgress()
    {
        // added 阶段参数恒空、状态恒 in_progress：客户端先建立可关联的项，再等参数流。
        var payload = ServerEvent.FunctionCallOutputItemAdded(
            "resp_1", "item_call", 1, "call_a", "get_weather");

        Assert.Equal("response.output_item.added", payload["type"]!.GetValue<string>());
        Assert.Equal(1, payload["output_index"]!.GetValue<int>());
        var item = payload["item"]!;
        Assert.Equal("function_call", item["type"]!.GetValue<string>());
        Assert.Equal("realtime.item", item["object"]!.GetValue<string>());
        Assert.Equal("call_a", item["call_id"]!.GetValue<string>());
        Assert.Equal("get_weather", item["name"]!.GetValue<string>());
        Assert.Equal("", item["arguments"]!.GetValue<string>());
        Assert.Equal("in_progress", item["status"]!.GetValue<string>());
        Assert.Null(item["content"]);
    }

    [Fact]
    public void FunctionCallOutputItemDone_ItemMatchesOutputSnapshot()
    {
        // done 的 item 与 response.output 里的终态项必须逐字段相同——
        // 客户端把它们当同一条调用记录的两份快照，不一致时后到的 done 盖掉先渲染的内容。
        var item = ServerEvent.FunctionCallItem(
            "item_call", "call_a", "get_weather", "{\"city\":\"广州\"}", "completed");
        var payload = ServerEvent.FunctionCallOutputItemDone("resp_1", item, 1);

        Assert.Equal("response.output_item.done", payload["type"]!.GetValue<string>());
        Assert.Equal(1, payload["output_index"]!.GetValue<int>());
        Assert.Equal("{\"city\":\"广州\"}", payload["item"]!["arguments"]!.GetValue<string>());
        Assert.Equal("completed", payload["item"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void ResponseOutput_MergeSortsMixedItemsByIndex()
    {
        // 消息项与函数调用项按 output_index 合并：客户端用 done 的 output[] 驱动函数执行，
        // 顺序与流式事件的下标一致是协议承诺。
        var message = ServerEvent.AssistantMessageItem("item_text", "completed", wantsAudio: true, "我查一下。");
        var call = ServerEvent.FunctionCallItem("item_call", "call_a", "get_weather", "{}", "completed");

        var merged = ResponseOutput.Merge(
            messages: [(1, message)],
            functionCalls: [(0, call)]);

        Assert.Equal(2, merged.Count);
        Assert.Equal("function_call", merged[0]["type"]!.GetValue<string>());
        Assert.Equal("item_call", merged[0]["id"]!.GetValue<string>());
        Assert.Equal("message", merged[1]["type"]!.GetValue<string>());
        Assert.Equal("item_text", merged[1]["id"]!.GetValue<string>());
    }

    [Fact]
    public void ContentPartDone_AudioModalityKeepsAudioEmpty()
    {
        var payload = ServerEvent.ContentPartDone("resp_1", "item_1", "你好。", wantsAudio: true);

        Assert.Equal("你好。", payload["part"]!["transcript"]!.GetValue<string>());
        // 服务端不回灌音频，回放由客户端自己缓冲
        Assert.Equal("", payload["part"]!["audio"]!.GetValue<string>());
    }

    [Fact]
    public void TranscriptionFailed_IsItemScopedNotGlobalError()
    {
        var payload = ServerEvent.TranscriptionFailed("item_1", "empty transcript");

        // 用 error 代替会让客户端误判整条连接出错
        Assert.Equal("conversation.item.input_audio_transcription.failed", payload["type"]!.GetValue<string>());
        Assert.Equal("transcription_error", payload["error"]!["type"]!.GetValue<string>());
        Assert.Equal("transcription_failed", payload["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void InputAudioBufferCommitted_OmitsPreviousItemIdForFirstItem()
    {
        var first = ServerEvent.InputAudioBufferCommitted("item_1", null);
        var chained = ServerEvent.InputAudioBufferCommitted("item_2", "item_1");

        // 首项必须整字段省略，给 null 会让客户端以为链断在此处
        Assert.False(first.ContainsKey("previous_item_id"));
        Assert.Equal("item_1", chained["previous_item_id"]!.GetValue<string>());
    }

    [Fact]
    public void ConversationItemCreated_CarriesContentArrayAndPredecessor()
    {
        var payload = ServerEvent.ConversationItemCreated(
            "item_1",
            "user",
            [new JsonObject { ["type"] = "input_audio" }],
            "item_prev");

        var content = payload["item"]!["content"]!.AsArray();
        Assert.Single(content);
        Assert.Equal("input_audio", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("item_prev", payload["previous_item_id"]!.GetValue<string>());
    }
}

/// <summary>TTS 句级切分。</summary>
public sealed class TtsSplitTests
{
    [Fact]
    public void SplitForSynthesis_ReturnsOneEntryPerSentence()
    {
        var sentences = TtsHandler.SplitForSynthesis("First. Second. Third.").ToList();

        Assert.Equal(3, sentences.Count);
    }

    [Fact]
    public void SplitForSynthesis_DropsWhitespaceOnlySegments()
    {
        var sentences = TtsHandler.SplitForSynthesis("   ").ToList();

        Assert.Empty(sentences);
    }

    [Fact]
    public void PrepareForSpeech_StripsMarkdownAndEmoji()
    {
        var speech = LmOutputProcessor.PrepareForSpeech("## Heading\nHello **world** 😀!");

        Assert.DoesNotContain("##", speech);
        Assert.DoesNotContain("**", speech);
        Assert.DoesNotContain("😀", speech);
        Assert.Contains("Hello world", speech);
    }
}

/// <summary>
/// 收尾事件规划的测试。
///
/// 这一类不是在测"发得对不对"，而是在测"该不该发"：少一个事件客户端会一直等，
/// 多一个事件客户端会渲染出多余的空消息。两者都不会抛异常，只会在真实设备上表现为
/// "回合结束不下来"或"凭空多一条空消息"，所以必须在这里锁死。
/// </summary>
public sealed class ResponseTeardownTests
{
    [Fact]
    public void Plan_AudioOnlyResponse_StillClosesAudioStream()
    {
        // 回归用例：曾经用「文本片数 > 0」当收尾开关，只出音频不出文本的响应
        // 因此永远不发 output_audio.done，客户端一直等下一段音频。
        var plan = ResponseTeardown.Plan(
            itemOpened: true, wantsAudio: true, audioStreamed: true, hasText: false);

        Assert.Equal(
            [TeardownEvent.OutputAudioDone, TeardownEvent.ContentPartDone, TeardownEvent.OutputItemDone],
            plan);
    }

    [Fact]
    public void Plan_PureToolCallEmitsNoTeardownEvents()
    {
        // 没有消息项可关，也不该回一个空音频流——那会被渲染成一段静音
        var plan = ResponseTeardown.Plan(
            itemOpened: false, wantsAudio: true, audioStreamed: false, hasText: false);

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_AudioWithText_ClosesAudioThenTranscriptThenPartThenItem()
    {
        var plan = ResponseTeardown.Plan(
            itemOpened: true, wantsAudio: true, audioStreamed: true, hasText: true);

        Assert.Equal(
            [
                TeardownEvent.OutputAudioDone,
                TeardownEvent.OutputAudioTranscriptDone,
                TeardownEvent.ContentPartDone,
                TeardownEvent.OutputItemDone,
            ],
            plan);
    }

    [Fact]
    public void Plan_TextModalityNeverEmitsAudioEvents()
    {
        var plan = ResponseTeardown.Plan(
            itemOpened: true, wantsAudio: false, audioStreamed: false, hasText: true);

        Assert.Equal(
            [TeardownEvent.OutputTextDone, TeardownEvent.ContentPartDone, TeardownEvent.OutputItemDone],
            plan);
        Assert.DoesNotContain(TeardownEvent.OutputAudioDone, plan);
        Assert.DoesNotContain(TeardownEvent.OutputAudioTranscriptDone, plan);
    }

    [Fact]
    public void Plan_TextWithoutAudioSamplesSkipsAudioDone()
    {
        // TTS 一个样本都没出：有文本、有消息项，但没有音频流可关
        var plan = ResponseTeardown.Plan(
            itemOpened: true, wantsAudio: true, audioStreamed: false, hasText: true);

        Assert.Equal(
            [TeardownEvent.OutputAudioTranscriptDone, TeardownEvent.ContentPartDone, TeardownEvent.OutputItemDone],
            plan);
    }

    [Fact]
    public void Plan_AlwaysEndsWithContentPartThenItem()
    {
        // 渲染契约的骨架：part 必须先于 item 收口，否则客户端拿到"已完成的 item
        // 里挂着一个未完成的 part"
        var variants = new[]
        {
            ResponseTeardown.Plan(true, true, true, true),
            ResponseTeardown.Plan(true, true, true, false),
            ResponseTeardown.Plan(true, false, false, true),
            ResponseTeardown.Plan(true, true, false, true),
        };

        foreach (var plan in variants)
        {
            Assert.Equal(TeardownEvent.OutputItemDone, plan[^1]);
            Assert.Equal(TeardownEvent.ContentPartDone, plan[^2]);
        }
    }

    [Fact]
    public void Plan_OnlyResponseDoneWhenNothingWasOpenedEvenWithText()
    {
        // 文本存在但消息项没开：说明文本根本没上线（例如全部被 Trim 成空），
        // 此时补发 content_part.done 会指向一个不存在的 item
        var plan = ResponseTeardown.Plan(
            itemOpened: false, wantsAudio: true, audioStreamed: true, hasText: true);

        Assert.Empty(plan);
    }
}
