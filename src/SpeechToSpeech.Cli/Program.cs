using SpeechToSpeech.Backends.ManySpeech;
using SpeechToSpeech.Backends.OpenAiCompatible;
using SpeechToSpeech.Backends.QwenTts;
using SpeechToSpeech.Backends.TensorSharp;
using SpeechToSpeech.Cli.Audio;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Realtime;

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

    private static void RegisterBackends()
    {
        ManySpeechBackends.Register();
        TensorSharpBackends.Register();
        QwenTtsBackends.Register();
        OpenAiCompatibleBackends.Register();
    }

    private static async Task<int> ServeAsync(RealtimeServerOptions options, BackendContext context)
    {
        var server = new RealtimeServer(options, context);
        var app = server.Build();

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
