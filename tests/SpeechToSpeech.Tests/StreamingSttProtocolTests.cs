using System.Text.Json.Nodes;
using SpeechToSpeech.Backends.OpenAiCompatible.StreamingStt;
using Xunit;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 上游 <c>streaming_handler.py</c> 协议层的表征测试。
///
/// 锁的是线上 JSON 的形状与 parse_event 的逐分支分类——两边实现独立写成，
/// 事件形状错一个键就是连不上或收不到结果。
/// </summary>
public class StreamingSttProtocolTests
{
    // ── OpenAI session.update ──────────────────────────────────────────

    [Fact]
    public void OpenAiSessionUpdate_WhisperModel_SingularLanguageKey()
    {
        var protocol = new OpenAiRealtimeProtocol("whisper-1", "zh", 24_000);
        var json = protocol.SessionUpdate();

        Assert.Equal("session.update", (string?)json["type"]);
        var session = (JsonObject)json["session"]!;
        Assert.Equal("transcription", (string?)session["type"]);
        var input = (JsonObject)((JsonObject)session["audio"]!)["input"]!;
        Assert.Equal("audio/pcm", (string?)((JsonObject)input["format"]!)["type"]);
        Assert.Equal(24_000, (int?)((JsonObject)input["format"]!)["rate"]);
        var transcription = (JsonObject)input["transcription"]!;
        Assert.Equal("whisper-1", (string?)transcription["model"]);
        Assert.Equal("zh", (string?)transcription["language"]);
        // turn_detection 显式 null（键在、值为 JSON null——STJ 里就是 null 引用），不是省略。
        Assert.True(input.ContainsKey("turn_detection"));
        Assert.Null(input["turn_detection"]);
    }

    [Fact]
    public void OpenAiSessionUpdate_GptTranscribe_UsesPluralLanguagesKey()
    {
        var protocol = new OpenAiRealtimeProtocol("gpt-transcribe", "en", 24_000);
        var transcription = (JsonObject)((JsonObject)((JsonObject)((JsonObject)protocol.SessionUpdate()["session"]!)["audio"]!)["input"]!)["transcription"]!;

        Assert.Null(transcription["language"]);
        Assert.Equal("en", (string?)((JsonArray)transcription["languages"]!)[0]);
    }

    [Fact]
    public void OpenAiSessionUpdate_NoLanguage_OmitsKey()
    {
        var protocol = new OpenAiRealtimeProtocol("whisper-1", null, 24_000);
        var transcription = (JsonObject)((JsonObject)((JsonObject)((JsonObject)protocol.SessionUpdate()["session"]!)["audio"]!)["input"]!)["transcription"]!;

        Assert.Null(transcription["language"]);
        Assert.Null(transcription["languages"]);
    }

    // ── OpenAI audio events ───────────────────────────────────────────

    [Fact]
    public void OpenAiAudioEvents_AppendCommitClear()
    {
        var protocol = new OpenAiRealtimeProtocol("whisper-1", null, 24_000);

        var append = protocol.AppendAudio([1, 2, 3, 250]);
        Assert.Equal("input_audio_buffer.append", (string?)append["type"]);
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 250]), (string?)append["audio"]);

        Assert.Equal("input_audio_buffer.commit", (string?)protocol.FinishUtterance()["type"]);
        Assert.Null(protocol.StartUtterance());
        Assert.Equal("input_audio_buffer.clear", (string?)protocol.DiscardUtterance()!["type"]);
    }

    // ── OpenAI parse_event 逐分支 ─────────────────────────────────────

    [Fact]
    public void OpenAiParse_Committed_WithoutItemId_IsIgnored()
    {
        var protocol = new OpenAiRealtimeProtocol("whisper-1", null, 24_000);

        Assert.Equal(ProtocolEventKind.Ignore,
            protocol.ParseEvent(new JsonObject { ["type"] = "input_audio_buffer.committed" }).Kind);
        Assert.Equal(ProtocolEventKind.Committed,
            protocol.ParseEvent(new JsonObject { ["type"] = "input_audio_buffer.committed", ["item_id"] = "item_A" })
                .Kind);
        // 空串 item_id 视同缺失。
        Assert.Equal(ProtocolEventKind.Ignore,
            protocol.ParseEvent(new JsonObject { ["type"] = "input_audio_buffer.committed", ["item_id"] = "" })
                .Kind);
    }

    [Fact]
    public void OpenAiParse_Delta_MissingContentIndex_DefaultsZero()
    {
        var protocol = new OpenAiRealtimeProtocol("whisper-1", null, 24_000);
        var evt = protocol.ParseEvent(new JsonObject
        {
            ["type"] = "conversation.item.input_audio_transcription.delta",
            ["item_id"] = "item_A",
            ["delta"] = "你好",
        });

        Assert.Equal(ProtocolEventKind.Delta, evt.Kind);
        Assert.Equal("你好", evt.Text);
        Assert.Equal("item_A", evt.ItemId);
        Assert.Equal(0, evt.ContentIndex);
    }

    [Fact]
    public void OpenAiParse_Delta_WithoutItemId_IsIgnored()
    {
        var protocol = new OpenAiRealtimeProtocol("whisper-1", null, 24_000);
        var evt = protocol.ParseEvent(new JsonObject
        {
            ["type"] = "conversation.item.input_audio_transcription.delta",
            ["delta"] = "hi",
        });

        Assert.Equal(ProtocolEventKind.Ignore, evt.Kind);
    }

    [Fact]
    public void OpenAiParse_CompletedOrFailed_WithoutContentIndex_IsIgnored()
    {
        var protocol = new OpenAiRealtimeProtocol("whisper-1", null, 24_000);

        // delta 缺 index 有默认 0；completed/failed 没有——这是上游刻意的。
        var completed = protocol.ParseEvent(new JsonObject
        {
            ["type"] = "conversation.item.input_audio_transcription.completed",
            ["item_id"] = "item_A",
            ["transcript"] = "done",
        });
        Assert.Equal(ProtocolEventKind.Ignore, completed.Kind);

        var negative = protocol.ParseEvent(new JsonObject
        {
            ["type"] = "conversation.item.input_audio_transcription.delta",
            ["item_id"] = "item_A",
            ["content_index"] = -1,
            ["delta"] = "x",
        });
        Assert.Equal(ProtocolEventKind.Ignore, negative.Kind);
    }

    [Fact]
    public void OpenAiParse_Completed_LanguagesArrayAndFallback()
    {
        var protocol = new OpenAiRealtimeProtocol("whisper-1", null, 24_000);

        var fromArray = protocol.ParseEvent(new JsonObject
        {
            ["type"] = "conversation.item.input_audio_transcription.completed",
            ["item_id"] = "item_A",
            ["content_index"] = 0,
            ["transcript"] = "hello",
            ["languages"] = new JsonArray("zh"),
        });
        Assert.Equal(ProtocolEventKind.Completed, fromArray.Kind);
        Assert.Equal("hello", fromArray.Text);
        Assert.Equal("zh", fromArray.Language);

        var fromDict = protocol.ParseEvent(new JsonObject
        {
            ["type"] = "conversation.item.input_audio_transcription.completed",
            ["item_id"] = "item_A",
            ["content_index"] = 0,
            ["transcript"] = "hello",
            ["languages"] = new JsonArray(new JsonObject { ["code"] = "ja" }),
        });
        Assert.Equal("ja", fromDict.Language);

        var fromSingle = protocol.ParseEvent(new JsonObject
        {
            ["type"] = "conversation.item.input_audio_transcription.completed",
            ["item_id"] = "item_A",
            ["content_index"] = 0,
            ["transcript"] = "hello",
            ["language"] = "ko",
        });
        Assert.Equal("ko", fromSingle.Language);
    }

    [Fact]
    public void OpenAiParse_FailedAndError_ExtractRemoteMessage()
    {
        var protocol = new OpenAiRealtimeProtocol("whisper-1", null, 24_000);

        var failed = protocol.ParseEvent(new JsonObject
        {
            ["type"] = "conversation.item.input_audio_transcription.failed",
            ["item_id"] = "item_A",
            ["content_index"] = 0,
            ["error"] = new JsonObject { ["message"] = "quota exceeded" },
        });
        Assert.Equal(ProtocolEventKind.Failed, failed.Kind);
        Assert.Equal("quota exceeded", failed.Message);
        Assert.Equal("item_A", failed.ItemId);

        var error = protocol.ParseEvent(new JsonObject
        {
            ["type"] = "error",
            ["error"] = "invalid api key",
        });
        Assert.Equal(ProtocolEventKind.Error, error.Kind);
        Assert.Equal("invalid api key", error.Message);

        var fallback = protocol.ParseEvent(new JsonObject { ["type"] = "error" });
        Assert.Equal("remote transcription error", fallback.Message);
    }

    [Fact]
    public void OpenAiParse_UnknownType_IsIgnored()
    {
        var protocol = new OpenAiRealtimeProtocol("whisper-1", null, 24_000);

        Assert.Equal(ProtocolEventKind.Ignore,
            protocol.ParseEvent(new JsonObject { ["type"] = "session.created" }).Kind);
    }

    // ── vLLM 方言 ─────────────────────────────────────────────────────

    [Fact]
    public void Vllm_WireShapes()
    {
        var protocol = new VllmRealtimeProtocol("Qwen2-Audio-7B", null, 16_000);

        Assert.False(protocol.RequiresSessionUpdated);

        var update = protocol.SessionUpdate();
        Assert.Equal("session.update", (string?)update["type"]);
        Assert.Equal("Qwen2-Audio-7B", (string?)update["model"]);

        Assert.Equal("input_audio_buffer.commit", (string?)protocol.StartUtterance()!["type"]);
        Assert.False((bool?)protocol.StartUtterance()!["final"]);
        Assert.True((bool?)protocol.FinishUtterance()["final"]);
        Assert.Null(protocol.DiscardUtterance());
    }

    [Fact]
    public void VllmParse_DeltaDoneError()
    {
        var protocol = new VllmRealtimeProtocol("m", null, 16_000);

        var delta = protocol.ParseEvent(new JsonObject { ["type"] = "transcription.delta", ["delta"] = "部分" });
        Assert.Equal(ProtocolEventKind.Delta, delta.Kind);
        Assert.Equal("部分", delta.Text);

        var done = protocol.ParseEvent(new JsonObject { ["type"] = "transcription.done", ["text"] = "完成" });
        Assert.Equal(ProtocolEventKind.Completed, done.Kind);
        Assert.Equal("完成", done.Text);

        var error = protocol.ParseEvent(new JsonObject { ["type"] = "error", ["error"] = "boom" });
        Assert.Equal(ProtocolEventKind.Error, error.Kind);
        Assert.Equal("boom", error.Message);

        Assert.Equal(ProtocolEventKind.Ignore,
            protocol.ParseEvent(new JsonObject { ["type"] = "session.created" }).Kind);
    }

    // ── endpoint URL ──────────────────────────────────────────────────

    [Fact]
    public void EndpointUrl_SchemeMappingAndRealtimeSuffix()
    {
        Assert.Equal("ws://host:8000/v1/realtime",
            RealtimeEndpointUrl.Build("http://host:8000/v1", "m", includeModelQuery: false));
        Assert.Equal("wss://host/v1/realtime",
            RealtimeEndpointUrl.Build("https://host/v1", "m", includeModelQuery: false));
        // 已带 /realtime 不重复补。
        Assert.Equal("ws://host/realtime",
            RealtimeEndpointUrl.Build("http://host/realtime", "m", includeModelQuery: false));
    }

    [Fact]
    public void EndpointUrl_HostedOpenAI_IntentTranscription_ModelStripped()
    {
        Assert.Equal(
            "wss://api.openai.com/v1/realtime?intent=transcription",
            RealtimeEndpointUrl.Build("https://api.openai.com/v1/realtime?model=whisper-1", "whisper-1",
                includeModelQuery: true));
    }

    [Fact]
    public void EndpointUrl_SelfHosted_ModelQueryOnlyWhenRequested()
    {
        Assert.Equal("ws://host:8000/v1/realtime?model=Qwen2",
            RealtimeEndpointUrl.Build("http://host:8000/v1", "Qwen2", includeModelQuery: true));
        Assert.Equal("ws://host:8000/v1/realtime",
            RealtimeEndpointUrl.Build("http://host:8000/v1", "Qwen2", includeModelQuery: false));
    }

    [Fact]
    public void EndpointUrl_InvalidScheme_Throws()
    {
        Assert.Throws<ArgumentException>(() => RealtimeEndpointUrl.Build("ftp://host", "m", false));
        Assert.Throws<ArgumentException>(() => RealtimeEndpointUrl.Build("not a url", "m", false));
    }

    // ── setup 校验 ────────────────────────────────────────────────────

    private static StreamingSttSetup Create(
        IRealtimeTranscriptionProtocolFactory? factory = null,
        string baseUrl = "http://127.0.0.1:8000/v1",
        string model = "m",
        int sampleRate = 16_000,
        double connectTimeout = 5.0,
        double finalTimeout = 10.0,
        string? language = null,
        bool includeModelQuery = false)
        => StreamingSttSetup.Create(factory ?? new OpenAiRealtimeProtocolFactory(), baseUrl, model, sampleRate,
            connectTimeout, finalTimeout, language, includeModelQuery);

    [Fact]
    public void Setup_EmptyModel_Throws() =>
        Assert.Throws<ArgumentException>(() => Create(model: "  "));

    [Fact]
    public void Setup_NonPositiveSampleRateOrTimeouts_Throw()
    {
        Assert.Throws<ArgumentException>(() => Create(sampleRate: 0));
        Assert.Throws<ArgumentException>(() => Create(connectTimeout: 0));
        Assert.Throws<ArgumentException>(() => Create(finalTimeout: -1));
    }

    [Fact]
    public void Setup_Vllm_NonSixteenK_Throws() =>
        Assert.Throws<ArgumentException>(() => Create(new VllmRealtimeProtocolFactory(), sampleRate: 24_000));

    [Fact]
    public void Setup_Vllm_SixteenK_Ok() =>
        Assert.Equal("ws://127.0.0.1:8000/v1/realtime", Create(new VllmRealtimeProtocolFactory()).EndpointUrl);

    [Fact]
    public void Setup_HostedOpenAI_NonTwentyFourK_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Create(new OpenAiRealtimeProtocolFactory(), baseUrl: "https://api.openai.com/v1", sampleRate: 16_000));

    [Fact]
    public void Setup_HostedOpenAI_TwentyFourK_Ok()
    {
        var setup = Create(new OpenAiRealtimeProtocolFactory(),
            baseUrl: "https://api.openai.com/v1", sampleRate: 24_000);
        Assert.Equal("wss://api.openai.com/v1/realtime?intent=transcription", setup.EndpointUrl);
    }

    [Fact]
    public void Setup_SelfHostedOpenAiRealtime_SixteenK_Ok() =>
        // 24 kHz 限制只针对 hosted 域名，自建端点不设。
        Assert.Equal("ws://127.0.0.1:8000/v1/realtime",
            Create(new OpenAiRealtimeProtocolFactory(), sampleRate: 16_000).EndpointUrl);

    [Fact]
    public void Setup_OpenAiRealtimeEndpoint_WithoutApiKey_FallsBackToEnvironment()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test");
        try
        {
            var setup = Create(new OpenAiRealtimeProtocolFactory(), baseUrl: "wss://api.openai.com/v1",
                sampleRate: 24_000);
            Assert.True(setup.UseApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public void Setup_SelfHosted_WithoutApiKey_NoAuthorization()
    {
        var setup = Create(new VllmRealtimeProtocolFactory());
        Assert.False(setup.UseApiKey);
        Assert.Equal("vllm-realtime", setup.Protocol.Name);
    }

    // ── 工具函数 ──────────────────────────────────────────────────────

    [Fact]
    public void JoinTranscripts_EdgeCases()
    {
        Assert.Equal("text", StreamingSttText.JoinTranscripts("", "text"));
        Assert.Equal("prefix", StreamingSttText.JoinTranscripts("prefix", ""));
        Assert.Equal("a b", StreamingSttText.JoinTranscripts(" a ", " b "));
    }

    [Fact]
    public void RemoteErrorMessage_NestedStringAndFallback()
    {
        Assert.Equal("inner",
            StreamingSttText.RemoteErrorMessage(new JsonObject
            {
                ["error"] = new JsonObject { ["message"] = "inner" },
            }));
        Assert.Equal("plain", StreamingSttText.RemoteErrorMessage(new JsonObject { ["error"] = "plain" }));
        Assert.Equal("remote transcription error", StreamingSttText.RemoteErrorMessage(new JsonObject()));
    }
}
