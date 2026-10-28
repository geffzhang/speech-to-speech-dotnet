using System.Text.Json.Nodes;

namespace SpeechToSpeech.Cli;

/// <summary>
/// 打包客户端（<c>talk</c> / <c>local</c>）的配置，字段与上游
/// <c>RealtimeAudioClientConfig</c>（api/openai_realtime/audio_client.py）逐项对应。
///
/// 本轮未移植的参数（见差异账 D035 的 accepted 部分）：
/// <list type="bullet">
/// <item><c>tool-module</c> —— 上游用 Python 的 import 机制加载 <c>TOOLS</c> +
/// <c>execute_tool</c> 契约；C# 没有等价物，需要另设计程序集加载契约，不静默降级。</item>
/// <item><c>voices</c>（终端箭头切 voice）与 friendly renderer 的原地刷新细节。</item>
/// <item><c>connection-retry-timeout</c> —— 上游对环回端点做 30 s 重试；
/// 本实现沿用启动前 300 ms 延迟（local 服务端在本进程内，先后顺序可控）。</item>
/// </list>
/// </summary>
internal sealed class RealtimeAudioClientOptions
{
    /// <summary>麦克风上行采样率。16000 走服务端原生速率（session.update 里不声明 format）。</summary>
    public int SendRate { get; init; } = 16_000;

    /// <summary>扬声器下行采样率。16000 走服务端原生速率。</summary>
    public int RecvRate { get; init; } = 16_000;

    /// <summary>开播前要攒的音频毫秒数。上游默认 0；local 模式下 TTS 后端为 openai 时默认 196。</summary>
    public double PlaybackBufferMs { get; init; } = 0.0;

    /// <summary>麦克风与扬声器回调块大小（采样数），与上游 chunk_size 同义。</summary>
    public int ChunkSize { get; init; } = 1024;

    /// <summary>输入设备索引；null = 平台默认设备。</summary>
    public int? InputDevice { get; init; }

    /// <summary>输出设备索引；null = 平台默认设备。</summary>
    public int? OutputDevice { get; init; }

    public string? Instructions { get; init; }
    public string? Voice { get; init; }
    public bool PrintJson { get; init; }
    public bool Verbose { get; init; }

    /// <summary>播放期间暂停麦克风采集。上游默认关（关着才能 barge-in）。</summary>
    public bool BlockMicDuringPlayback { get; init; }

    /// <summary>
    /// talk 的参数装配（裸名，与上游 parse_talk_arguments 同名同默认）。
    /// 单独成方法是为了可测——装配错误（比如速率写错）应该在单测里暴露，
    /// 而不是等到真连麦克风时。
    /// </summary>
    public static RealtimeAudioClientOptions FromTalkArgs(CommandLine line) => new()
    {
        SendRate = line.IntValue(16_000, "send-rate", "send_rate"),
        RecvRate = line.IntValue(16_000, "recv-rate", "recv_rate"),
        PlaybackBufferMs = line.DoubleValue(0.0, "playback-buffer-ms", "playback_buffer_ms"),
        ChunkSize = line.IntValue(1024, "chunk-size", "chunk_size"),
        InputDevice = NullableInt(line, "input-device", "input_device"),
        OutputDevice = NullableInt(line, "output-device", "output_device"),
        Instructions = line.Value("instructions"),
        Voice = line.Value("voice"),
        PrintJson = line.Flag("print-json", "print_json"),
        Verbose = line.Flag("verbose"),
        BlockMicDuringPlayback = line.Flag("block-mic-during-playback", "block_mic_during_playback"),
    };

    /// <summary>
    /// local 的参数装配（<c>--local-audio-*</c> 前缀，上游 LocalAudioArguments 同名）。
    /// playback_buffer_ms 缺省按 TTS 后端分支：openai → 196（OpenAI 流式有节奏间隔），
    /// 其它（本地整段合成）→ 0，与上游 OPENAI_TTS_PLAYBACK_BUFFER_MS 一致。
    /// </summary>
    public static RealtimeAudioClientOptions FromLocalArgs(CommandLine line) => new()
    {
        ChunkSize = line.IntValue(1024, "local_audio_chunk_size", "local-audio-chunk-size"),
        PlaybackBufferMs = line.DoubleValue(
            (line.Value("tts") ?? "qwen3") == "openai" ? 196.0 : 0.0,
            "local_audio_playback_buffer_ms", "local-audio-playback-buffer-ms"),
        InputDevice = NullableInt(line, "local_audio_input_device", "local-audio-input-device"),
        OutputDevice = NullableInt(line, "local_audio_output_device", "local-audio-output-device"),
        PrintJson = line.Flag("local_audio_print_json", "local-audio-print-json"),
        Verbose = line.Flag("verbose"),
        BlockMicDuringPlayback = line.Flag(
            "local_audio_block_mic_during_playback", "local-audio-block-mic-during-playback"),
    };

    /// <summary>可空整数参数；「给过但不是整数」按上游 argparse 的口径直接报错。</summary>
    private static int? NullableInt(CommandLine line, params string[] names)
    {
        var value = line.Value(names);
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, out var parsed))
        {
            throw new ArgumentException($"argument {names[0]}: invalid int value: '{value}'");
        }

        return parsed;
    }

    /// <summary>
    /// 构造连接后立即发送的 session.update。
    ///
    /// 逐项对齐上游 <c>build_session_update</c>：
    /// <list type="bullet">
    /// <item>16000 时 format **整体省略**——上游 SDK 模型只在显式声明 24 kHz PCM 时校验格式，
    /// 省略即用服务端原生 16 kHz 管线速率；24000 时显式声明；其余值报错（同上游文案语义）。</item>
    /// <item><c>turn_detection</c> 恒为 <c>{"type": "server_vad", "interrupt_response": true}</c>。</item>
    /// <item><c>extensions</c> 恒订阅实时转写快照——服务端侧该开关缺省 false，不订阅就收不到
    /// 逐字转写（差异账 D035 的核心功能缺失）。</item>
    /// <item>voice / instructions 有值才写；tools 只有配了工具模块才有，本轮不支持。</item>
    /// </list>
    /// </summary>
    public static JsonObject BuildSessionUpdate(RealtimeAudioClientOptions options)
    {
        var input = new JsonObject
        {
            ["turn_detection"] = new JsonObject
            {
                ["type"] = "server_vad",
                ["interrupt_response"] = true,
            },
        };
        var output = new JsonObject();

        if (MaybePcmFormat(options.SendRate) is { } inputFormat)
        {
            input["format"] = inputFormat;
        }

        if (MaybePcmFormat(options.RecvRate) is { } outputFormat)
        {
            output["format"] = outputFormat;
        }

        if (!string.IsNullOrEmpty(options.Voice))
        {
            output["voice"] = options.Voice;
        }

        var session = new JsonObject
        {
            ["type"] = "realtime",
            ["extensions"] = new JsonArray("speech_to_speech.input_audio_transcription.snapshot"),
            ["audio"] = new JsonObject
            {
                ["input"] = input,
                ["output"] = output,
            },
        };

        if (!string.IsNullOrEmpty(options.Instructions))
        {
            session["instructions"] = options.Instructions;
        }

        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = session,
        };
    }

    /// <summary>上游 maybe_pcm_format：16 k 返回 null（省略），24 k 显式声明，其余直接拒绝。</summary>
    private static JsonObject? MaybePcmFormat(int rate) => rate switch
    {
        16_000 => null,
        24_000 => new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24_000 },
        _ => throw new ArgumentException(
            $"Unsupported rate {rate}. Use 16000 for the local pipeline default or 24000 for the OpenAI Realtime PCM schema."),
    };
}
