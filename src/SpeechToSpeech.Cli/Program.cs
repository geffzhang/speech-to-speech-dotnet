using SpeechToSpeech.Backends.ManySpeech;
using SpeechToSpeech.Backends.OpenAiCompatible;
using SpeechToSpeech.Backends.OpenLipSync;
using SpeechToSpeech.Backends.QwenTts;
using SpeechToSpeech.Backends.TensorSharp;
using SpeechToSpeech.Cli.Audio;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Realtime;
using SpeechToSpeech.Transport.WebRtc;

namespace SpeechToSpeech.Cli;

/// <summary>
/// 命令行入口。
///
/// 三个命令与上游同名同义：
/// <list type="bullet">
/// <item><c>serve</c>：只跑服务端，等客户端连过来；</item>
/// <item><c>talk</c>：只跑打包客户端，连一个已存在的服务端；</item>
/// <item><c>local</c>：进程内同时跑 serve 与 talk，一条命令就能说话。</item>
/// </list>
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        UseUtf8Console();

        try
        {
            var line = CommandLine.Parse(args);

            if (line.Command == "list-backends")
            {
                PrintBackends();
                return 0;
            }

            RegisterBackends();
            var context = OptionsBuilder.Build(line);
            WarnIfTurnEndUnavailable(context);

            var options = new RealtimeServerOptions
            {
                Host = line.Value("host") ?? "127.0.0.1",
                Port = line.IntValue(8765, "port"),
                Verbose = line.Flag("verbose"),
                DefaultVoice = line.Value("voice", "tts_speaker") ?? "ryan",
                DefaultInstructions = line.Value("instructions")
                                     ?? "You are a helpful voice assistant. Answer in one or two short spoken sentences.",
            };

            return line.Command switch
            {
                "serve" => await ServeAsync(options, context).ConfigureAwait(false),
                "talk" => await TalkAsync(line).ConfigureAwait(false),
                "local" => await LocalAsync(options, context, line).ConfigureAwait(false),
                _ => Unknown(line.Command),
            };
        }
        catch (BackendUnavailableException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 控制台输出走 UTF-8。
    ///
    /// 上游是 Python：PEP 528 让 Windows 控制台也用 UTF-8，所以中文日志在任何终端都正常。
    /// .NET 在**重定向**时已经是 UTF-8，但直接接控制台时会用系统代码页（简中环境是 936），
    /// 于是同一条中文告警在 UTF-8 终端里会变成乱码——这不影响管线行为，但会让
    /// 「缺 Smart Turn 判定器」这类必须被看见的告警恰好读不出来。
    ///
    /// 拿不到控制台句柄时（管道已关闭、某些 CI 环境）会抛 IOException，忽略即可：
    /// 此时输出本来就是重定向的，默认值已经是 UTF-8。
    /// </summary>
    private static void UseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static void RegisterBackends()
    {
        ManySpeechBackends.Register();
        TensorSharpBackends.Register();
        QwenTtsBackends.Register();
        OpenAiCompatibleBackends.Register();
        OpenLipSyncBackends.Register();
    }

    /// <summary>
    /// Smart Turn 默认开启（照上游）。判定器构造失败（缺模型、加载失败）已在参数装配时
    /// 打过告警并退回 disabled；这里兜的是「连 <see cref="BackendContext"/> 都没拿到判定器」
    /// 的其它路径。这条差异必须在启动时说出来——它不会报错、不会丢数据，
    /// 只会让助手偶尔抢话，事后从日志里也查不出来。
    /// </summary>
    private static void WarnIfTurnEndUnavailable(BackendContext context)
    {
        if (!context.TurnEnd.Enabled || context.TurnEndClassifier is not null)
        {
            return;
        }

        Console.Error.WriteLine(
            "warning: Smart Turn 已启用但没有可用判定器（差异账 D018）：" +
            $"端点判定按 disabled 处理，输出宽限 {context.TurnEnd.SpeculativeReopenMs}ms、处理延迟 0。" +
            "用户说到一半停顿时回复可能提前放行。加 --no_smart_turn 可显式关掉本条告警。");
    }

    private static async Task<int> ServeAsync(RealtimeServerOptions options, BackendContext context)
    {
        var server = new RealtimeServer(options, context);
        var app = server.Build();
        app.MapWebRtcCalls(options, context);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        await app.StartAsync().ConfigureAwait(false);
        Console.WriteLine($"listening on {server.Endpoint}");
        Console.WriteLine("models load lazily on the first turn");

        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await app.StopAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> TalkAsync(CommandLine line)
    {
        var url = line.Value("url") ?? "ws://127.0.0.1:8765/v1/realtime";

        if (!TryCreateAudioDevices(out var devices))
        {
            return 1;
        }

        await using var client = new AudioClient(new Uri(url), devices);
        try
        {
            await client.RunAsync().ConfigureAwait(false);
        }
        catch (System.Net.WebSockets.WebSocketException ex)
        {
            Console.Error.WriteLine($"error: 无法连接 {url}：{ex.Message}");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// 建音频后端。不支持的平台在这里一次性报清楚，而不是等用户戴上耳机才发现没声音。
    /// </summary>
    private static bool TryCreateAudioDevices(out IAudioDeviceSet devices)
    {
        try
        {
            devices = AudioDevices.Create();
            return true;
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            devices = null!;
            return false;
        }
    }

    private static async Task<int> LocalAsync(RealtimeServerOptions options, BackendContext context, CommandLine line)
    {
        // 先确认音频后端可用，再花几十秒加载模型——不支持的平台不该等模型加载完才报错。
        if (!TryCreateAudioDevices(out var devices))
        {
            return 1;
        }

        var port = line.IntValue(8765, "port");
        var localOptions = new RealtimeServerOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Verbose = options.Verbose,
            DefaultVoice = options.DefaultVoice,
            DefaultInstructions = options.DefaultInstructions,
            DefaultSampleRate = options.DefaultSampleRate,
        };

        var server = new RealtimeServer(localOptions, context);
        var app = server.Build();
        await app.StartAsync().ConfigureAwait(false);

        Console.WriteLine("Loading models…");

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            // local 的语义是"服务端与客户端在同一进程里走环回"，
            // 所以这里必须等宿主真正开始监听后再连，否则会撞上竞态。
            await Task.Delay(300, shutdown.Token).ConfigureAwait(false);

            await using var client = new AudioClient(new Uri(server.Endpoint), devices);
            await client.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await app.StopAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"error: 未知命令 '{command}'");
        return 2;
    }

    private static void PrintBackends()
    {
        Console.WriteLine("STT 后端：");
        foreach (var spec in BackendRegistry.SttBackends)
        {
            Console.WriteLine($"  {spec.Name,-24} {(spec.Implemented ? "可用" : "不可用 — " + spec.UnsupportedReason)}");
        }

        Console.WriteLine();
        Console.WriteLine("LLM 后端：");
        foreach (var spec in BackendRegistry.LlmBackends)
        {
            Console.WriteLine($"  {spec.Name,-24} {(spec.Implemented ? "可用" : "不可用 — " + spec.UnsupportedReason)}");
        }

        Console.WriteLine();
        Console.WriteLine("TTS 后端：");
        foreach (var spec in BackendRegistry.TtsBackends)
        {
            Console.WriteLine($"  {spec.Name,-24} {(spec.Implemented ? "可用" : "不可用 — " + spec.UnsupportedReason)}");
        }
    }
}
