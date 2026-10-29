using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElBruno.QwenTTS.Pipeline;

namespace SpeechToSpeech.Cli;

internal sealed record LocalModelAsset(string Group, string RelativePath, long Size, string Sha256);

internal sealed record LocalModelDefaultPath(string OptionName, string[] LookupNames, string Path);

internal sealed record LocalModelCustomPath(string OptionName, string Path, bool IsDirectory, string[] RequiredFiles);

internal sealed class LocalModelPlan(
    string modelsDirectory,
    string manifestPath,
    string scriptPath,
    string verificationCachePath,
    IReadOnlyList<string> selectedGroups,
    IReadOnlyList<LocalModelAsset> manifestEntries,
    IReadOnlyList<LocalModelDefaultPath> defaultPaths,
    IReadOnlyList<LocalModelCustomPath> customPaths)
{
    public string ModelsDirectory { get; } = modelsDirectory;
    public string ManifestPath { get; } = manifestPath;
    public string ScriptPath { get; } = scriptPath;
    public string VerificationCachePath { get; } = verificationCachePath;
    public IReadOnlyList<string> SelectedGroups { get; } = selectedGroups;
    public IReadOnlyList<LocalModelAsset> ManifestEntries { get; } = manifestEntries;
    public IReadOnlyList<LocalModelDefaultPath> DefaultPaths { get; } = defaultPaths;
    public IReadOnlyList<LocalModelCustomPath> CustomPaths { get; } = customPaths;
}

internal static class LocalModelPreparation
{
    private static readonly string[] KnownGroups = ["vad", "whisper", "llm", "tts", "smart-turn"];
    private const string DefaultLlmFile = "llm/Qwen3.5-0.8B-Q4_K_M.gguf";
    private const string DefaultSmartTurnFile = "smart-turn/smart-turn-v3.2-cpu.onnx";

    private sealed record VerificationReceipt(long Size, long LastWriteTimeUtcTicks, string Sha256);

    public static LocalModelPlan BuildPlan(CommandLine line, string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var manifestPath = Path.Combine(root, "tools", "models", "models.tsv");
        var scriptPath = Path.Combine(root, OperatingSystem.IsWindows() ? "download-models.ps1" : "download-models.sh");

        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Model manifest was not found: {manifestPath}");
        }

        if (!File.Exists(scriptPath))
        {
            throw new InvalidOperationException($"Model downloader was not found: {scriptPath}");
        }

        var modelsDirectory = Path.Combine(root, "models");
        var entries = ReadManifest(manifestPath, modelsDirectory);
        var groups = new List<string>();
        var defaults = new List<LocalModelDefaultPath>();
        var custom = new List<LocalModelCustomPath>();

        var vadPath = line.Value("vad_model_dir", "vad-model-dir");
        if (vadPath is null)
        {
            AddDefaultGroup("vad", "vad_model_dir", ["vad_model_dir", "vad-model-dir"], Path.Combine(modelsDirectory, "vad"));
        }
        else
        {
            custom.Add(new("vad_model_dir", vadPath, true, ["*.onnx"]));
        }

        var sttBackend = line.Value("stt") ?? "whisper";
        if (sttBackend == "whisper")
        {
            var whisperPath = line.Value("whisper_model_dir", "stt_model_dir", "whisper-model-dir", "stt-model-dir");
            if (whisperPath is null)
            {
                AddDefaultGroup("whisper", "whisper_model_dir",
                    ["whisper_model_dir", "stt_model_dir", "whisper-model-dir", "stt-model-dir"],
                    Path.Combine(modelsDirectory, "whisper"));
            }
            else
            {
                custom.Add(new("whisper_model_dir", whisperPath, true,
                    ["encoder*.onnx", "decoder*.onnx", "conf.json"]));
            }
        }

        var llmBackend = line.Value("llm_backend", "llm-backend") ?? "tensorsharp";
        if (llmBackend == "tensorsharp")
        {
            var llmPath = line.Value("llm_model_path", "model_path", "model_name");
            if (llmPath is null)
            {
                AddDefaultGroup("llm", "llm_model_path",
                    ["llm_model_path", "model_path", "model_name", "llm-model-path"],
                    Path.Combine(modelsDirectory, DefaultLlmFile.Replace('/', Path.DirectorySeparatorChar)));
            }
            else
            {
                custom.Add(new("llm_model_path", llmPath, false, []));
            }
        }

        var ttsBackend = line.Value("tts") ?? "qwen3";
        if (ttsBackend == "qwen3")
        {
            var ttsPath = line.Value("qwen3_model_dir", "tts_model_dir", "qwen3-model-dir", "tts-model-dir");
            if (ttsPath is null)
            {
                AddDefaultGroup("tts", "qwen3_model_dir",
                    ["qwen3_model_dir", "tts_model_dir", "qwen3-model-dir", "tts-model-dir"],
                    Path.Combine(modelsDirectory, "tts"));
            }
            else
            {
                custom.Add(new("qwen3_model_dir", ttsPath, true, []));
            }
        }

        if (line.BoolFlag(true, "smart_turn", "no_smart_turn"))
        {
            var smartTurnPath = line.Value("smart_turn_model_path", "smart-turn-model-path");
            if (smartTurnPath is null)
            {
                AddDefaultGroup("smart-turn", "smart_turn_model_path",
                    ["smart_turn_model_path", "smart-turn-model-path"],
                    Path.Combine(modelsDirectory, DefaultSmartTurnFile.Replace('/', Path.DirectorySeparatorChar)));
            }
            else
            {
                custom.Add(new("smart_turn_model_path", smartTurnPath, false, []));
            }
        }

        foreach (var group in groups)
        {
            if (!entries.Any(entry => entry.Group == group))
            {
                throw new InvalidOperationException($"Model manifest contains no files for selected group '{group}'.");
            }
        }

        return new LocalModelPlan(
            modelsDirectory,
            manifestPath,
            scriptPath,
            Path.Combine(modelsDirectory, ".model-verification-cache.json"),
            groups,
            entries.Where(entry => groups.Contains(entry.Group, StringComparer.Ordinal)).ToArray(),
            defaults,
            custom);

        void AddDefaultGroup(string group, string option, string[] lookupNames, string path)
        {
            groups.Add(group);
            defaults.Add(new LocalModelDefaultPath(option, lookupNames, path));
        }
    }

    public static void ApplyDefaultPaths(CommandLine line, LocalModelPlan plan)
    {
        foreach (var defaultPath in plan.DefaultPaths)
        {
            if (line.Value(defaultPath.LookupNames) is null)
            {
                line.SetIfAbsent(defaultPath.OptionName, defaultPath.Path);
            }
        }
    }

    public static async Task PrepareAsync(
        LocalModelPlan plan,
        Func<ProcessStartInfo, CancellationToken, Task<int>> runProcess,
        CancellationToken cancellationToken)
    {
        ValidateCustomPaths(plan.CustomPaths);
        if (plan.SelectedGroups.Count == 0)
        {
            return;
        }

        var verificationCache = ReadVerificationCache(plan.VerificationCachePath);
        var changedReceipts = new HashSet<string>(StringComparer.Ordinal);
        var missingGroups = plan.SelectedGroups
            .Where(group => plan.ManifestEntries
                .Where(entry => entry.Group == group)
                .Any(entry => !IsValidAsset(plan.ModelsDirectory, entry, verificationCache, changedReceipts)))
            .ToArray();

        if (missingGroups.Length == 0)
        {
            WriteVerificationCache(plan.VerificationCachePath, verificationCache, changedReceipts.Count > 0);
            return;
        }

        int exitCode;
        try
        {
            exitCode = await runProcess(
                CreateDownloadProcess(plan, missingGroups),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            throw new InvalidOperationException($"Unable to start the model downloader: {ex.Message}", ex);
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Model downloader exited with code {exitCode}; no Realtime listener was started.");
        }

        var invalidAssets = plan.ManifestEntries
            .Where(entry => missingGroups.Contains(entry.Group, StringComparer.Ordinal))
            .Where(entry => !IsValidAsset(plan.ModelsDirectory, entry, verificationCache, changedReceipts))
            .Select(entry => entry.RelativePath)
            .ToArray();
        if (invalidAssets.Length > 0)
        {
            throw new InvalidOperationException(
                $"Model download completed but required files are missing or invalid: {string.Join(", ", invalidAssets)}");
        }

        WriteVerificationCache(plan.VerificationCachePath, verificationCache, changedReceipts.Count > 0);
    }

    public static async Task<int> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start the model downloader: {startInfo.FileName}");
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                var manifest = Path.Combine(directory.FullName, "tools", "models", "models.tsv");
                var script = Path.Combine(directory.FullName,
                    OperatingSystem.IsWindows() ? "download-models.ps1" : "download-models.sh");
                if (File.Exists(manifest) && File.Exists(script))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException(
            "Could not locate the repository's model manifest and platform downloader from the current or application directory.");
    }

    private static IReadOnlyList<LocalModelAsset> ReadManifest(string manifestPath, string modelsDirectory)
    {
        var entries = new List<LocalModelAsset>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(manifestPath, Encoding.ASCII))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length != 4 ||
                !Regex.IsMatch(fields[0], "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant) ||
                !long.TryParse(fields[1], out var size) || size <= 0 ||
                string.IsNullOrWhiteSpace(fields[2]) ||
                !Uri.TryCreate(fields[3], UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException($"Invalid model manifest entry at {manifestPath}:{lineNumber}.");
            }

            var relativePath = fields[2].Replace('\\', '/');
            var segments = relativePath.Split('/');
            if (segments.Length < 2 ||
                segments.Any(segment => segment is "" or "." or "..") ||
                Path.IsPathRooted(relativePath) ||
                !KnownGroups.Contains(segments[0], StringComparer.Ordinal) ||
                !seenPaths.Add(relativePath))
            {
                throw new InvalidOperationException($"Invalid or duplicate model path at {manifestPath}:{lineNumber}.");
            }

            var fullPath = Path.GetFullPath(Path.Combine(modelsDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = Path.GetFullPath(modelsDirectory) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Model path escapes the model directory at {manifestPath}:{lineNumber}.");
            }

            entries.Add(new LocalModelAsset(segments[0], relativePath, size, fields[0]));
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException($"Model manifest is empty: {manifestPath}");
        }

        return entries;
    }

    private static bool IsValidAsset(
        string modelsDirectory,
        LocalModelAsset entry,
        Dictionary<string, VerificationReceipt> verificationCache,
        HashSet<string> changedReceipts)
    {
        var path = Path.Combine(modelsDirectory, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            if (verificationCache.Remove(entry.RelativePath))
            {
                changedReceipts.Add(entry.RelativePath);
            }

            return false;
        }

        var file = new FileInfo(path);
        if (file.Length != entry.Size)
        {
            if (verificationCache.Remove(entry.RelativePath))
            {
                changedReceipts.Add(entry.RelativePath);
            }

            return false;
        }

        var lastWriteTicks = file.LastWriteTimeUtc.Ticks;
        if (verificationCache.TryGetValue(entry.RelativePath, out var receipt) &&
            receipt.Size == file.Length &&
            receipt.LastWriteTimeUtcTicks == lastWriteTicks &&
            string.Equals(receipt.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        using var stream = file.OpenRead();
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (verificationCache.Remove(entry.RelativePath))
            {
                changedReceipts.Add(entry.RelativePath);
            }

            return false;
        }

        verificationCache[entry.RelativePath] = new VerificationReceipt(file.Length, lastWriteTicks, hash);
        changedReceipts.Add(entry.RelativePath);
        return true;
    }

    private static Dictionary<string, VerificationReceipt> ReadVerificationCache(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, VerificationReceipt>(StringComparer.Ordinal);
        }

        try
        {
            var receipts = JsonSerializer.Deserialize<Dictionary<string, VerificationReceipt>>(File.ReadAllText(path));
            if (receipts is not null)
            {
                return new Dictionary<string, VerificationReceipt>(receipts, StringComparer.Ordinal);
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine(
                $"warning: model verification cache is invalid ({ex.Message}); rechecking model hashes.");
        }

        return new Dictionary<string, VerificationReceipt>(StringComparer.Ordinal);
    }

    private static void WriteVerificationCache(
        string path,
        IReadOnlyDictionary<string, VerificationReceipt> receipts,
        bool changed)
    {
        if (!changed)
        {
            return;
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(receipts));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"warning: could not save model verification cache {path} ({ex.Message}); hashes will be checked again next startup.");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(
                        $"warning: could not remove temporary model verification cache {temporaryPath} ({ex.Message}).");
                }
            }
        }
    }

    private static void ValidateCustomPaths(IReadOnlyList<LocalModelCustomPath> paths)
    {
        foreach (var custom in paths)
        {
            if (custom.IsDirectory && !Directory.Exists(custom.Path))
            {
                throw MissingCustomPath(custom);
            }

            if (custom.OptionName == "qwen3_model_dir" && !ModelDownloader.IsModelReady(custom.Path))
            {
                throw MissingCustomPath(custom);
            }

            if (custom.IsDirectory)
            {
                foreach (var requiredFile in custom.RequiredFiles)
                {
                    var filePath = Path.Combine(custom.Path, requiredFile);
                    var exists = requiredFile.Contains('*')
                        ? Directory.EnumerateFiles(custom.Path, requiredFile, SearchOption.TopDirectoryOnly).Any()
                        : File.Exists(filePath);
                    if (!exists)
                    {
                        throw MissingCustomPath(custom, requiredFile);
                    }
                }
            }
            else if (!File.Exists(custom.Path))
            {
                throw MissingCustomPath(custom);
            }
        }
    }

    private static InvalidOperationException MissingCustomPath(LocalModelCustomPath custom, string? file = null)
    {
        var missing = file is null ? custom.Path : Path.Combine(custom.Path, file);
        var group = custom.OptionName switch
        {
            "vad_model_dir" => "vad",
            "whisper_model_dir" => "whisper",
            "llm_model_path" => "llm",
            "qwen3_model_dir" => "tts",
            "smart_turn_model_path" => "smart-turn",
            _ => "model",
        };
        var command = OperatingSystem.IsWindows()
            ? $".\\download-models.ps1 -Groups {group}"
            : $"bash ./download-models.sh --groups {group}";
        return new InvalidOperationException(
            $"Custom model path is missing a required file: {missing}. Prepare the custom model manually or use the pinned default with `{command}`.");
    }

    private static ProcessStartInfo CreateDownloadProcess(LocalModelPlan plan, IReadOnlyList<string> groups)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell" : "bash",
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(plan.ScriptPath);
            startInfo.ArgumentList.Add("-ModelsDir");
            startInfo.ArgumentList.Add(plan.ModelsDirectory);
            startInfo.ArgumentList.Add("-Manifest");
            startInfo.ArgumentList.Add(plan.ManifestPath);
            startInfo.ArgumentList.Add("-Groups");
            startInfo.ArgumentList.Add(string.Join(",", groups));
        }
        else
        {
            startInfo.ArgumentList.Add(plan.ScriptPath);
            startInfo.ArgumentList.Add("--models-dir");
            startInfo.ArgumentList.Add(plan.ModelsDirectory);
            startInfo.ArgumentList.Add("--manifest");
            startInfo.ArgumentList.Add(plan.ManifestPath);
            startInfo.ArgumentList.Add("--groups");
            startInfo.ArgumentList.Add(string.Join(",", groups));
        }

        return startInfo;
    }
}
