using System.Collections.Concurrent;
using System.Reflection;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Pipeline.Handlers;
using SpeechToSpeech.Core.Runtime;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 逐响应音色覆盖（D017）。
///
/// 上游允许在 <c>response.create</c> 里带 <c>audio.output.voice</c>，优先于会话配置。
/// 这类改动的坑不在解析，而在**两处取值必须同一个判断**：协议载荷报的音色与真正交给
/// 合成器的音色。只做前者会变成「客户端以为换了音色，听到的还是旧的那个」——
/// 字段对、声音错，靠读代码看不出来，只能靠耳朵发现。
/// </summary>
public sealed class VoiceOverrideTests
{
    private sealed class RecordingTts : ITtsBackend
    {
        public List<string?> Speakers { get; } = [];

        public string Name => "recording";

        public int SampleRate => 24_000;

        public IEnumerable<byte[]> Synthesize(TtsRequest request, CancellationToken cancellationToken)
        {
            Speakers.Add(request.Speaker);
            yield return [0, 0];
        }

        public void Dispose()
        {
        }
    }

    private static RuntimeConfig ConfigWithVoice(string? voice)
    {
        var config = new RuntimeConfig();
        if (voice is not null)
        {
            config.ApplySessionUpdate(new System.Text.Json.Nodes.JsonObject
            {
                ["audio"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["output"] = new System.Text.Json.Nodes.JsonObject { ["voice"] = voice },
                },
            });
        }

        return config;
    }

    [Fact]
    public void ResponseLevelVoice_OverridesSessionVoice()
    {
        var config = ConfigWithVoice("session_v");
        var input = TtsInput.Create(
            "hello",
            config,
            response: new RealtimeResponseCreateParams { Voice = "resp_v" });

        // 合成器拿到的必须是响应级那个值——这才是「换了音色」的实际含义。
        Assert.Equal("resp_v", input.Voice);
    }

    [Fact]
    public void MissingResponseLevelVoice_FallsBackToSessionVoice()
    {
        var config = ConfigWithVoice("session_v");
        var input = TtsInput.Create("hello", config, response: new RealtimeResponseCreateParams());

        Assert.Equal("session_v", input.Voice);
    }

    [Fact]
    public void NoVoiceAnywhere_StaysNull()
    {
        var input = TtsInput.Create("hello", ConfigWithVoice(null));

        // null 不是「空音色」，是「不指定、用后端默认」——不能拿空串顶替。
        Assert.Null(input.Voice);
    }

    [Fact]
    public void ResolveVoice_PrefersResponseLevelThenSession()
    {
        var config = ConfigWithVoice("session_v");

        Assert.Equal("resp_v", new RealtimeResponseCreateParams { Voice = "resp_v" }.ResolveVoice(config));
        Assert.Equal("session_v", new RealtimeResponseCreateParams().ResolveVoice(config));
        Assert.Null(new RealtimeResponseCreateParams().ResolveVoice(ConfigWithVoice(null)));

        // 空串按「未指定」处理：上游把空音色当作没给，不会拿空串去盖会话配置。
        Assert.Equal("session_v", new RealtimeResponseCreateParams { Voice = "" }.ResolveVoice(config));
    }

    [Fact]
    public void TtsHandler_PassesResolvedVoiceToBackend()
    {
        var backend = new RecordingTts();
        var handler = new TtsHandler(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 32),
            backend,
            new CancelScope(),
            new SpeculativeTurnTracker(),
            new TurnLatencyStore());

        var process = typeof(TtsHandler).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TtsHandler.Process 不存在");

        var input = TtsInput.Create(
            "hello there.",
            ConfigWithVoice("session_v"),
            response: new RealtimeResponseCreateParams { Voice = "resp_v" });

        foreach (var _ in (IEnumerable<object>) process.Invoke(handler, [input])!)
        {
        }

        // 走到后端才算真的生效；只断言 TtsInput.Voice 的话，后端那行接错仍会漏过。
        Assert.NotEmpty(backend.Speakers);
        Assert.All(backend.Speakers, speaker => Assert.Equal("resp_v", speaker));
    }
}
