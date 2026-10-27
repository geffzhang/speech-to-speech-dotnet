using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SpeechToSpeech.Evals.BigBenchAudio;

/// <summary>
/// Big Bench Audio vibe check 命令行（上游 <c>__main__.py</c> 的 C# 对应物）。
///
/// run / compare / show / build-subset 四个子命令；<c>run</c> 是默认命令，
/// 纯 flag 调用自动补上。服务端参数跟在 <c>--</c> 之后。
/// </summary>
public static class BigBenchAudioCli
{
    public const string DefaultUrl = "ws://127.0.0.1:8765/v1/realtime";

    private static readonly string[] SensitiveWords = ["api_key", "token", "secret", "password"];

    /// <summary>保留可复现设置，不持久化凭据。</summary>
    public static IReadOnlyList<string> RedactServerArgs(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        var redactNext = false;
        foreach (var arg in args)
        {
            var key = arg.Split('=')[0].ToLowerInvariant().Replace("-", "_");
            var sensitive = SensitiveWords.Any(word => key.Contains(word));
            if (redactNext)
            {
                result.Add("<redacted>");
                redactNext = false;
            }
            else if (arg.StartsWith("--") && sensitive)
            {
                var (name, hasValue, _) = arg.Partition('=');
                result.Add(hasValue ? $"{name}=<redacted>" : name);
                redactNext = !hasValue;
            }
            else
            {
                result.Add(arg);
            }
        }

        return result;
    }

    private static (string Left, bool HasValue, string Right) Partition(this string text, char separator)
    {
        var index = text.IndexOf(separator);
        return index < 0 ? (text, false, "") : (text[..index], true, text[(index + 1)..]);
    }

    public static JsonObject RuntimeMetadata() => new()
    {
        ["source_revision"] = Environment.GetEnvironmentVariable("S2S_SOURCE_REVISION"),
        ["job_id"] = Environment.GetEnvironmentVariable("JOB_ID"),
        ["python"] = Environment.Version.ToString(),
        ["packages"] = new JsonObject(),
    };

    /// <summary>把 <c>... -- --stt_backend whisper</c> 拆成 eval 参数与服务端参数。</summary>
    public static (List<string> EvalArgs, List<string> ServerArgs) SplitServerArgs(IReadOnlyList<string> argv)
    {
        var index = argv.ToList().IndexOf("--");
        if (index < 0)
        {
            return ([.. argv], []);
        }

        return ([.. argv.Take(index)], [.. argv.Skip(index + 1)]);
    }

    /// <summary>由服务端参数里的 <c>--host</c> / <c>--port</c> 推导 realtime URL。</summary>
    public static string UrlFromServerArgs(IReadOnlyList<string> serverArgs)
    {
        var host = "127.0.0.1";
        var port = "8765";
        for (var index = 0; index < serverArgs.Count; index++)
        {
            var arg = serverArgs[index];
            string? kind = null;
            string? value = null;
            if (arg.StartsWith("--host=", StringComparison.Ordinal))
            {
                (kind, value) = ("host", arg.Split('=', 2)[1]);
            }
            else if (arg.StartsWith("--port=", StringComparison.Ordinal))
            {
                (kind, value) = ("port", arg.Split('=', 2)[1]);
            }
            else if (arg is "--host" or "--port" && index + 1 < serverArgs.Count)
            {
                (kind, value) = (arg.TrimStart('-'), serverArgs[index + 1]);
            }

            if (kind is null)
            {
                continue;
            }

            if (kind == "host")
            {
                host = value is "0.0.0.0" or "" ? "127.0.0.1" : value!;
            }
            else
            {
                port = value!;
            }
        }

        return $"ws://{host}:{port}/v1/realtime";
    }

    private sealed class Options
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);
        public List<string> Positional { get; } = [];

        public string? Get(string name) => Values.TryGetValue(name, out var value) ? value : null;
        public string Get(string name, string fallback) => Values.TryGetValue(name, out var value) ? value : fallback;
        public bool Has(string name) => Flags.Contains(name) || Values.ContainsKey(name);
    }

    private static Options ParseOptions(IReadOnlyList<string> args, IReadOnlySet<string> valueOptions)
    {
        var options = new Options();
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var (name, hasValue, value) = arg.Partition('=');
                var key = name[2..];
                if (valueOptions.Contains(key))
                {
                    if (hasValue)
                    {
                        options.Values[key] = value;
                    }
                    else if (index + 1 < args.Count)
                    {
                        options.Values[key] = args[++index];
                    }
                    else
                    {
                        throw new ArgumentException($"option {name} expects a value");
                    }
                }
                else
                {
                    options.Flags.Add(key);
                }
            }
            else
            {
                options.Positional.Add(arg);
            }
        }

        return options;
    }

    private static readonly IReadOnlySet<string> RunValueOptions = new HashSet<string>
    {
        "url", "api-key", "subset", "limit", "label", "out", "push-to-hub", "compare",
        "instructions", "instructions-file", "voice", "speed", "silence-ms", "trailing-silence-ms",
        "response-timeout", "retries", "judge-model", "judge-base-url", "judge-api-key",
        "spawn-timeout", "spawn-log",
    };

    private static void PrintProgress(int index, int total, Report.ItemResult result)
    {
        string mark;
        string detail;
        if (result.Error is not null)
        {
            mark = "!";
            detail = result.Error;
        }
        else if (result.FinalCorrect is true)
        {
            mark = "+";
            detail = $"{result.FinalExtracted} == {result.OfficialAnswer}";
        }
        else if (result.FinalCorrect is false)
        {
            mark = "-";
            detail = $"{result.FinalExtracted} != {result.OfficialAnswer}";
        }
        else
        {
            mark = "?";
            detail = "unparsed";
        }

        var timing = result.TtfbS is { } ttfb ? $"ttfb {ttfb:F1}s" : "ttfb n/a";
        Console.WriteLine($"[{index,3}/{total}] {mark} {result.Category,-17} id={result.Id,-4} {timing,-11} {detail}");
        Console.Out.Flush();
    }

    private static Runner.RunnerConfig RunnerConfigFrom(Options options, string url)
    {
        var instructions = Runner.DefaultInstructions;
        if (options.Get("instructions-file") is { } file)
        {
            instructions = File.ReadAllText(file).Trim();
        }
        else if (options.Get("instructions") is { } inline)
        {
            instructions = inline;
        }

        return new Runner.RunnerConfig
        {
            Url = url,
            ApiKey = options.Get("api-key")
                ?? Environment.GetEnvironmentVariable("S2S_API_KEY")
                ?? Environment.GetEnvironmentVariable("HF_TOKEN"),
            Instructions = instructions,
            Voice = options.Get("voice"),
            Speed = double.Parse(options.Get("speed", "1.0")),
            TrailingSilenceMs = int.Parse(options.Get("trailing-silence-ms", "2000")),
            SilenceDurationMs = int.Parse(options.Get("silence-ms", "900")),
            ResponseTimeoutS = double.Parse(options.Get("response-timeout", "180")),
            Retries = int.Parse(options.Get("retries", "1")),
        };
    }

    /// <summary>轮询 realtime 端点直到它能接受一个会话。</summary>
    private static async Task WaitUntilReadyAsync(Runner.RunnerConfig config, Process? process, double timeoutS)
    {
        var deadline = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency + timeoutS;
        string? lastError = null;
        while (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency < deadline)
        {
            if (process is not null && process.HasExited)
            {
                throw new InvalidOperationException($"server exited with code {process.ExitCode} before becoming ready");
            }

            try
            {
                var ws = await Runner.OpenSessionAsync(config);
                ws.Dispose();
                return;
            }
            catch (Exception exc)
            {
                // 服务端还在预热。
                lastError = $"{exc.GetType().Name}: {exc.Message}";
                await Task.Delay(2000);
            }
        }

        throw new TimeoutException($"server not ready after {timeoutS:F0}s (last error: {lastError})");
    }

    private static async Task<int> RunAsync(Options options, List<string> serverArgs)
    {
        var subset = Dataset.LoadSubset(options.Get("subset", Dataset.DefaultSubset)).Head(
            options.Get("limit") is { } limit ? int.Parse(limit) : null);
        var stats = Dataset.Describe(subset);
        Console.WriteLine($"subset {subset.Name}: {stats.Total} items -- {string.Join(", ", stats.ByCategory.Select(kv => $"[{kv.Key}, {kv.Value}]"))}");
        if (options.Has("dry-run"))
        {
            foreach (var (category, answers) in stats.ByAnswer.OrderBy(static pair => pair.Key))
            {
                Console.WriteLine($"  {category,-18} {string.Join(", ", answers.OrderBy(static pair => pair.Key).Select(kv => $"[{kv.Key}, {kv.Value}]"))}");
            }

            Console.WriteLine($"  ids: [{string.Join(", ", subset.Items.Select(static item => item.Id))}]");
            return 0;
        }

        var url = options.Get("url")
            ?? (options.Has("spawn") ? UrlFromServerArgs(serverArgs) : DefaultUrl);
        var config = RunnerConfigFrom(options, url);
        config.Validate();

        if (options.Has("push-to-hub"))
        {
            Console.Error.WriteLine("WARNING: --push-to-hub is not supported in the C# port; the report is written locally only.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        Process? process = null;
        try
        {
            if (options.Has("spawn"))
            {
                process = SpawnServer(serverArgs, options.Get("spawn-log"));
            }

            Console.WriteLine($"waiting for {config.Url} ...");
            await WaitUntilReadyAsync(
                config,
                process,
                options.Has("spawn") ? double.Parse(options.Get("spawn-timeout", "900")) : config.ConnectTimeoutS);
            Console.WriteLine("server ready");

            var results = await Runner.RunSubsetAsync(subset, config, PrintProgress);
            var durationS = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - started;

            JsonObject? judgeMeta = null;
            if (options.Get("judge-model") is { } judgeModel)
            {
                var judgeConfig = new Judge.JudgeConfig(
                    Model: judgeModel,
                    BaseUrl: options.Get("judge-base-url"),
                    ApiKey: options.Get("judge-api-key") ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
                Console.WriteLine($"judging {results.Count} replies with {judgeConfig.Model} ...");
                await Judge.JudgeResultsAsync(results, judgeConfig);
                judgeMeta = judgeConfig.ToDict();
            }

            var report = Report.BuildReport(
                results: results,
                subset: subset,
                label: options.Get("label") ?? $"{subset.Name}@{startedAt:yyyy-MM-dd'T'HH:mm}",
                target: new JsonObject
                {
                    ["url"] = url,
                    ["server_args"] = new JsonArray(RedactServerArgs(serverArgs).Select(static arg => JsonValue.Create(arg)).ToArray()),
                    ["speed"] = config.Speed,
                    ["instructions"] = config.Instructions,
                    ["silence_ms"] = config.SilenceDurationMs,
                    ["trailing_silence_ms"] = config.TrailingSilenceMs,
                    ["source_revision"] = RuntimeMetadata()["source_revision"]?.DeepClone(),
                    ["job_id"] = RuntimeMetadata()["job_id"]?.DeepClone(),
                    ["python"] = RuntimeMetadata()["python"]?.DeepClone(),
                    ["packages"] = RuntimeMetadata()["packages"]?.DeepClone(),
                },
                judge: judgeMeta,
                startedAt: startedAt,
                durationS: durationS);

            if (options.Get("out") is { } outPath)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(outPath, report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
                Console.WriteLine($"\nwrote {outPath}");
            }

            Console.WriteLine();
            Console.WriteLine(Report.RenderReport(report));

            if (options.Get("compare") is { } comparePath)
            {
                var baseline = Hub.LoadReport(comparePath);
                var comparison = Report.CompareReports(baseline, report);
                Console.WriteLine();
                Console.WriteLine(Report.RenderComparison(comparison));
            }

            return results.Any(static item => item.Error is not null) ? 1 : 0;
        }
        finally
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            process?.Dispose();
        }
    }

    private static Process SpawnServer(IReadOnlyList<string> serverArgs, string? logPath)
    {
        // 与上游 `python -m speech_to_speech.cli serve` 对应：拉起本仓库的 Cli serve。
        var cli = Environment.GetEnvironmentVariable("S2S_CLI")
            ?? FindCliExecutable()
            ?? throw new InvalidOperationException(
                "could not locate SpeechToSpeech.Cli; set S2S_CLI to the Cli dll or exe path");
        var isExe = cli.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var arguments = isExe
            ? $"serve {string.Join(' ', serverArgs)}"
            : $"\"{cli}\" serve {string.Join(' ', serverArgs)}";
        var fileName = isExe ? cli : "dotnet";
        Console.WriteLine($"spawning: {fileName} {string.Join(' ', RedactServerArgs([.. arguments.Split(' ')]))}");
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
        };
        if (logPath is not null)
        {
            Console.WriteLine($"server log: {logPath}");
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        var process = Process.Start(startInfo)!;
        if (logPath is not null)
        {
            var logFile = File.CreateText(logPath);
            process.OutputDataReceived += (_, e) => LogLine(logFile, e.Data);
            process.ErrorDataReceived += (_, e) => LogLine(logFile, e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.Exited += (_, _) => logFile.Dispose();
        }

        return process;
    }

    private static void LogLine(StreamWriter logFile, string? line)
    {
        if (line is not null)
        {
            logFile.WriteLine(line);
            logFile.Flush();
        }
    }

    private static string? FindCliExecutable()
    {
        // 从当前目录向上找仓库布局里的 Cli 产物。
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "SpeechToSpeech.Cli", "bin");
            if (Directory.Exists(candidate))
            {
                var exe = Directory.EnumerateFiles(candidate, "SpeechToSpeech.Cli.exe", SearchOption.AllDirectories)
                    .OrderByDescending(static path => File.GetLastWriteTime(path))
                    .FirstOrDefault();
                if (exe is not null)
                {
                    return exe;
                }

                var dll = Directory.EnumerateFiles(candidate, "SpeechToSpeech.Cli.dll", SearchOption.AllDirectories)
                    .OrderByDescending(static path => File.GetLastWriteTime(path))
                    .FirstOrDefault();
                if (dll is not null)
                {
                    return dll;
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static int Compare(string baselinePath, string candidatePath)
    {
        var baseline = Hub.LoadReport(baselinePath);
        var candidate = Hub.LoadReport(candidatePath);
        Console.WriteLine(Report.RenderComparison(Report.CompareReports(baseline, candidate)));
        return 0;
    }

    private static int Show(string reportPath)
    {
        Console.WriteLine(Report.RenderReport(Hub.LoadReport(reportPath)));
        return 0;
    }

    private static int BuildSubset(Options options)
    {
        var subset = Dataset.BuildSubset(
            int.Parse(options.Get("size", "40")),
            name: options.Get("name", Dataset.DefaultSubset),
            description: options.Get("description", ""),
            seed: int.Parse(options.Get("seed", "0")));
        File.WriteAllText(options.Get("out") ?? throw new ArgumentException("--out is required"),
            subset.ToDict().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        var stats = Dataset.Describe(subset);
        Console.WriteLine($"wrote {options.Get("out")}: {stats.Total} items -- {string.Join(", ", stats.ByCategory.Select(kv => $"[{kv.Key}, {kv.Value}]"))}");
        return 0;
    }

    /// <summary>run 是默认命令：纯 flag 调用自动补上（-h/--help 除外）。</summary>
    internal static List<string> NormalizeEvalArgs(IReadOnlyList<string> argv)
    {
        var evalArgs = argv.ToList();
        if (evalArgs.Count == 0 || (evalArgs[0].StartsWith('-') && evalArgs[0] is not ("-h" or "--help")))
        {
            evalArgs.Insert(0, "run");
        }

        return evalArgs;
    }

    /// <summary>CLI 入口。返回进程退出码。</summary>
    public static int Main(IReadOnlyList<string> argv)
    {
        var (evalArgs, serverArgs) = SplitServerArgs(argv);
        evalArgs = NormalizeEvalArgs(evalArgs);

        var command = evalArgs[0];
        var rest = evalArgs.Skip(1).ToList();
        try
        {
            return command switch
            {
                "run" => RunAsync(ParseOptions(rest, RunValueOptions), serverArgs).GetAwaiter().GetResult(),
                "compare" => Compare(rest[0], rest[1]),
                "show" => Show(rest[0]),
                "build-subset" => BuildSubset(ParseOptions(rest, new HashSet<string> { "size", "name", "description", "seed", "out" })),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception exc)
        {
            Console.Error.WriteLine($"error: {exc.Message}");
            return 2;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine(
            $"unknown command {command}; expected run | compare | show | build-subset");
        return 2;
    }
}
