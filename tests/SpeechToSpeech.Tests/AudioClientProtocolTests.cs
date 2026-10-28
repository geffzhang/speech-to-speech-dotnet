using System.Text.Json.Nodes;
using SpeechToSpeech.Cli;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 打包客户端的 session.update 形状（D035）。
///
/// 上游 build_session_update 的口径：
/// <list type="bullet">
/// <item>16000 速率时 format **整体省略**（走服务端原生速率）；24000 显式声明；其余报错；</item>
/// <item>turn_detection 恒为 server_vad + interrupt_response=true；</item>
/// <item>extensions 恒订阅实时转写快照——服务端该开关缺省 false，不订阅就收不到逐字转写；</item>
/// <item>voice / instructions 有值才写。</item>
/// </list>
///
/// 旧实现硬编码双向 24000 并无条件声明 format、缺 extensions——用户在 local/talk 下
/// 永远看不到实时转写。这些用例在旧实现上要么编译不过（新类型不存在）、要么断言失败。
/// </summary>
public sealed class AudioClientProtocolTests
{
    [Fact]
    public void Default_rates_omit_format_and_subscribe_snapshots()
    {
        var update = RealtimeAudioClientOptions.BuildSessionUpdate(new RealtimeAudioClientOptions());

        Assert.Equal("session.update", update["type"]!.GetValue<string>());

        var session = update["session"]!.AsObject();
        Assert.Equal("realtime", session["type"]!.GetValue<string>());

        // extensions：不订阅服务端就不发 snapshot（服务端缺省 false）。
        var extensions = session["extensions"]!.AsArray();
        Assert.Contains(extensions, e => e!.GetValue<string>() == "speech_to_speech.input_audio_transcription.snapshot");

        var audio = session["audio"]!.AsObject();
        var input = audio["input"]!.AsObject();

        // 16000 → format 省略，turn_detection 带 interrupt_response。
        Assert.Null(input["format"]);
        Assert.Equal("server_vad", input["turn_detection"]!["type"]!.GetValue<string>());
        Assert.True(input["turn_detection"]!["interrupt_response"]!.GetValue<bool>());

        var output = audio["output"]!.AsObject();
        Assert.Null(output["format"]);

        // 缺省时 voice / instructions 不出现。
        Assert.Null(output["voice"]);
        Assert.Null(session["instructions"]);
    }

    [Fact]
    public void Rate_24k_declares_format_explicitly()
    {
        var update = RealtimeAudioClientOptions.BuildSessionUpdate(new RealtimeAudioClientOptions
        {
            SendRate = 24_000,
            RecvRate = 24_000,
        });

        var audio = update["session"]!["audio"]!.AsObject();
        var inputFormat = audio["input"]!["format"]!.AsObject();
        Assert.Equal("audio/pcm", inputFormat["type"]!.GetValue<string>());
        Assert.Equal(24_000, inputFormat["rate"]!.GetValue<int>());

        var outputFormat = audio["output"]!["format"]!.AsObject();
        Assert.Equal("audio/pcm", outputFormat["type"]!.GetValue<string>());
        Assert.Equal(24_000, outputFormat["rate"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(8_000)]
    [InlineData(32_000)]
    [InlineData(44_100)]
    public void Unsupported_rate_is_rejected_not_silently_kept(int rate)
    {
        Assert.Throws<ArgumentException>(() =>
            RealtimeAudioClientOptions.BuildSessionUpdate(new RealtimeAudioClientOptions { SendRate = rate }));
        Assert.Throws<ArgumentException>(() =>
            RealtimeAudioClientOptions.BuildSessionUpdate(new RealtimeAudioClientOptions { RecvRate = rate }));
    }

    [Fact]
    public void Voice_and_instructions_are_written_when_present()
    {
        var update = RealtimeAudioClientOptions.BuildSessionUpdate(new RealtimeAudioClientOptions
        {
            Voice = "serena",
            Instructions = "用中文回答",
        });

        var session = update["session"]!.AsObject();
        Assert.Equal("serena", session["audio"]!["output"]!["voice"]!.GetValue<string>());
        Assert.Equal("用中文回答", session["instructions"]!.GetValue<string>());
    }

    // —— 参数装配（talk 裸名 / local 前缀）——

    [Fact]
    public void Talk_args_map_to_options()
    {
        var line = CommandLine.Parse(new[]
        {
            "talk", "--url", "ws://127.0.0.1:9000/v1/realtime",
            "--send-rate", "24000", "--recv-rate", "24000",
            "--playback-buffer-ms", "50", "--chunk-size", "2048",
            "--input-device", "1", "--output-device", "2",
            "--voice", "alloy", "--instructions", "be brief",
            "--print-json", "--block-mic-during-playback",
        });

        var options = RealtimeAudioClientOptions.FromTalkArgs(line);
        Assert.Equal(24_000, options.SendRate);
        Assert.Equal(24_000, options.RecvRate);
        Assert.Equal(50, options.PlaybackBufferMs);
        Assert.Equal(2048, options.ChunkSize);
        Assert.Equal(1, options.InputDevice);
        Assert.Equal(2, options.OutputDevice);
        Assert.Equal("alloy", options.Voice);
        Assert.Equal("be brief", options.Instructions);
        Assert.True(options.PrintJson);
        Assert.True(options.BlockMicDuringPlayback);
        Assert.False(options.Verbose);
    }

    [Fact]
    public void Talk_defaults_match_upstream_config()
    {
        var line = CommandLine.Parse(new[] { "talk" });
        var options = RealtimeAudioClientOptions.FromTalkArgs(line);

        Assert.Equal(16_000, options.SendRate);
        Assert.Equal(16_000, options.RecvRate);
        Assert.Equal(0.0, options.PlaybackBufferMs);
        Assert.Equal(1024, options.ChunkSize);
        Assert.Null(options.InputDevice);
        Assert.Null(options.OutputDevice);
        Assert.Null(options.Voice);
        Assert.Null(options.Instructions);
        Assert.False(options.PrintJson);
        Assert.False(options.Verbose);
        Assert.False(options.BlockMicDuringPlayback);
    }

    [Fact]
    public void Local_args_map_and_default_buffer_by_tts_backend()
    {
        // 非 openai TTS：buffer 缺省 0。
        var line = CommandLine.Parse(new[]
        {
            "local", "--tts", "qwen3",
            "--local-audio-chunk-size", "512",
            "--local-audio-input-device", "3",
            "--local-audio-print-json",
        });
        var options = RealtimeAudioClientOptions.FromLocalArgs(line);
        Assert.Equal(0.0, options.PlaybackBufferMs);
        Assert.Equal(512, options.ChunkSize);
        Assert.Equal(3, options.InputDevice);
        Assert.Null(options.OutputDevice);
        Assert.True(options.PrintJson);
        Assert.False(options.BlockMicDuringPlayback);

        // openai TTS：buffer 缺省 196（上游 OPENAI_TTS_PLAYBACK_BUFFER_MS）。
        var openAiLine = CommandLine.Parse(new[] { "local", "--tts", "openai" });
        Assert.Equal(196.0, RealtimeAudioClientOptions.FromLocalArgs(openAiLine).PlaybackBufferMs);

        // 显式覆盖优先于后端分支。
        var explicitLine = CommandLine.Parse(new[]
        {
            "local", "--tts", "openai", "--local-audio-playback-buffer-ms", "10",
        });
        Assert.Equal(10.0, RealtimeAudioClientOptions.FromLocalArgs(explicitLine).PlaybackBufferMs);
    }

    [Fact]
    public void Local_block_mic_flag_is_consumed()
    {
        var line = CommandLine.Parse(new[]
        {
            "local", "--local-audio-block-mic-during-playback",
        });
        Assert.True(RealtimeAudioClientOptions.FromLocalArgs(line).BlockMicDuringPlayback);
    }

    [Fact]
    public void Non_integer_device_index_is_rejected()
    {
        var line = CommandLine.Parse(new[] { "talk", "--input-device", "mic" });
        Assert.Throws<ArgumentException>(() => RealtimeAudioClientOptions.FromTalkArgs(line));
    }
}
