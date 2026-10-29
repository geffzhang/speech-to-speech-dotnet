using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SpeechToSpeech.Cli;

namespace SpeechToSpeech.Tests;

public sealed class LocalModelPreparationTests : IDisposable
{
    private static readonly string[] DefaultFiles =
    [
        "vad/silero_vad.onnx",
        "sense-voice/model.int8.onnx",
        "sense-voice/tokens.txt",
        "whisper/encoder.int8.onnx",
        "llm/Qwen3.5-0.8B-Q4_K_M.gguf",
        "tts/talker_decode.onnx",
        "smart-turn/smart-turn-v3.2-cpu.onnx",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"s2s-local-models-{Guid.NewGuid():N}");

    public LocalModelPreparationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "tools", "models"));
        File.WriteAllText(Path.Combine(_root, "download-models.ps1"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "download-models.sh"), string.Empty);
        WriteManifest(DefaultFiles);
    }

    [Theory]
    [InlineData("sense-voice", "sense-voice")]
    [InlineData("whisper", "whisper")]
    [InlineData("paraformer", "paraformer")]
    public void BuildPlan_selects_requested_stt_group(string backend, string expectedGroup)
    {
        var fixtureRoot = Path.Combine(_root, backend);
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "tools", "models"));
        File.WriteAllText(
            Path.Combine(fixtureRoot, OperatingSystem.IsWindows() ? "download-models.ps1" : "download-models.sh"),
            string.Empty);
        var manifest = Path.Combine(fixtureRoot, "tools", "models", "models.tsv");
        var payload = Encoding.UTF8.GetBytes("model fixture");
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        File.WriteAllLines(
            manifest,
            [
                $"{hash}\t{payload.Length}\tsense-voice/model.int8.onnx\thttps://example.invalid/sense-voice-fixture",
                $"{hash}\t{payload.Length}\tsense-voice/tokens.txt\thttps://example.invalid/sense-tokens",
                $"{hash}\t{payload.Length}\twhisper/encoder.int8.onnx\thttps://example.invalid/whisper",
                $"{hash}\t{payload.Length}\tparaformer/model.int8.onnx\thttps://example.invalid/paraformer-fixture",
                $"{hash}\t{payload.Length}\tparaformer/config.yaml\thttps://example.invalid/paraformer-config",
                $"{hash}\t{payload.Length}\tparaformer/tokens.txt\thttps://example.invalid/paraformer-tokens",
                $"{hash}\t{payload.Length}\tvad/silero_vad.onnx\thttps://example.invalid/vad",
                $"{hash}\t{payload.Length}\tllm/model.gguf\thttps://example.invalid/llm",
                $"{hash}\t{payload.Length}\ttts/model.onnx\thttps://example.invalid/tts",
                $"{hash}\t{payload.Length}\tsmart-turn/model.onnx\thttps://example.invalid/smart-turn",
            ],
            Encoding.ASCII);
        var line = CommandLine.Parse(["serve", "--stt", backend]);

        var plan = LocalModelPreparation.BuildPlan(line, fixtureRoot);

        Assert.Contains(expectedGroup, plan.SelectedGroups);
        Assert.DoesNotContain(
            plan.SelectedGroups.Where(group => group is "sense-voice" or "whisper" or "paraformer"),
            group => group != expectedGroup);
    }

    [Theory]
    [InlineData("sense-voice", "tokens.txt")]
    [InlineData("paraformer", "config.yaml")]
    public async Task PrepareAsync_validates_stt_custom_directory_requirements(string backend, string missingFile)
    {
        var fixtureRoot = CreateBackendFixture(backend);
        var customDirectory = Path.Combine(_root, $"custom-{backend}");
        Directory.CreateDirectory(customDirectory);
        foreach (var fileName in new[] { "model.int8.onnx", "config.yaml", "tokens.txt" }
                     .Where(file => file != missingFile))
        {
            File.WriteAllText(Path.Combine(customDirectory, fileName), "model");
        }

        var plan = LocalModelPreparation.BuildPlan(
            CommandLine.Parse(
            [
                "serve",
                "--vad_model_dir", CreateVadDirectory(fixtureRoot),
                "--stt", backend,
                "--llm_backend", "openai",
                "--tts", "openai",
                "--no_smart_turn",
                $"--{backend.Replace('-', '_')}_model_dir", customDirectory,
            ]),
            fixtureRoot);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelPreparation.PrepareAsync(plan, (_, _) => Task.FromResult(0), CancellationToken.None));

        Assert.Contains(missingFile, error.Message);
    }

    [Theory]
    [InlineData("sense-voice", "tokens.txt")]
    [InlineData("paraformer", "tokens.txt")]
    [InlineData("paraformer", "config.yaml")]
    public async Task PrepareAsync_rejects_missing_stt_custom_files(string backend, string missingFile)
    {
        var fixtureRoot = CreateBackendFixture(backend);
        var customDirectory = Path.Combine(_root, $"incomplete-{backend}-{missingFile.Replace('.', '-')}");
        Directory.CreateDirectory(customDirectory);
        foreach (var fileName in new[] { "model.int8.onnx", "config.yaml", "tokens.txt" }
                     .Where(file => file != missingFile))
        {
            File.WriteAllText(Path.Combine(customDirectory, fileName), "model");
        }

        var plan = LocalModelPreparation.BuildPlan(
            CommandLine.Parse(
            [
                "serve",
                "--vad_model_dir", CreateVadDirectory(fixtureRoot),
                "--stt", backend,
                "--llm_backend", "openai",
                "--tts", "openai",
                "--no_smart_turn",
                $"--{backend.Replace('-', '_')}_model_dir", customDirectory,
            ]),
            fixtureRoot);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelPreparation.PrepareAsync(plan, (_, _) => Task.FromResult(0), CancellationToken.None));

        Assert.Contains(missingFile, error.Message);
    }

    [Fact]
    public async Task PrepareAsync_accepts_sense_voice_custom_directory_without_config_yaml()
    {
        var fixtureRoot = CreateBackendFixture("sense-voice");
        var customDirectory = Path.Combine(_root, "custom-sense-voice");
        Directory.CreateDirectory(customDirectory);
        File.WriteAllText(Path.Combine(customDirectory, "model.int8.onnx"), "model");
        File.WriteAllText(Path.Combine(customDirectory, "tokens.txt"), "tokens");
        var plan = LocalModelPreparation.BuildPlan(
            CommandLine.Parse(
            [
                "serve",
                "--vad_model_dir", CreateVadDirectory(fixtureRoot),
                "--stt", "sense-voice",
                "--llm_backend", "openai",
                "--tts", "openai",
                "--no_smart_turn",
                "--sense_voice_model_dir", customDirectory,
            ]),
            fixtureRoot);

        await LocalModelPreparation.PrepareAsync(plan, (_, _) => Task.FromResult(0), CancellationToken.None);
    }

    [Fact]
    public void BuildPlan_uses_sense_voice_by_default_and_preserves_shared_stt_model_directory()
    {
        var line = CommandLine.Parse(["serve", "--stt_model_dir", Path.Combine(_root, "custom-stt")]);

        var plan = LocalModelPreparation.BuildPlan(line, _root);

        Assert.DoesNotContain("sense-voice", plan.SelectedGroups);
        Assert.Contains(plan.CustomPaths, path => path.OptionName == "sense_voice_model_dir");
        Assert.Equal(1, plan.CustomPaths.Count(path => path.OptionName == "sense_voice_model_dir"));
    }

    private string CreateBackendFixture(string backend)
    {
        var fixtureRoot = Path.Combine(_root, backend);
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "tools", "models"));
        CreateVadDirectory(fixtureRoot);
        File.WriteAllText(
            Path.Combine(fixtureRoot, OperatingSystem.IsWindows() ? "download-models.ps1" : "download-models.sh"),
            string.Empty);
        var payload = Encoding.UTF8.GetBytes("model fixture");
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        File.WriteAllLines(
            Path.Combine(fixtureRoot, "tools", "models", "models.tsv"),
            [
                $"{hash}\t{payload.Length}\t{backend}/model.int8.onnx\thttps://example.invalid/model",
                $"{hash}\t{payload.Length}\t{backend}/tokens.txt\thttps://example.invalid/tokens",
                $"{hash}\t{payload.Length}\t{backend}/config.yaml\thttps://example.invalid/config",
            ],
            Encoding.ASCII);
        return fixtureRoot;
    }

    private static string CreateVadDirectory(string fixtureRoot)
    {
        var vadDirectory = Path.Combine(fixtureRoot, "vad");
        Directory.CreateDirectory(vadDirectory);
        File.WriteAllText(Path.Combine(vadDirectory, "silero_vad.onnx"), "model");
        return vadDirectory;
    }

    [Fact]
    public void BuildPlan_selects_default_local_backend_groups()
    {
        var line = CommandLine.Parse(["local"]);
        var plan = LocalModelPreparation.BuildPlan(line, _root);
        LocalModelPreparation.ApplyDefaultPaths(line, plan);

        Assert.Equal(["vad", "sense-voice", "llm", "tts", "smart-turn"], plan.SelectedGroups);
        Assert.Equal(Path.Combine(_root, "models"), plan.ModelsDirectory);
        Assert.Equal(6, plan.ManifestEntries.Count);
        Assert.Equal(Path.Combine(_root, "models", "vad"), line.Value("vad_model_dir"));
        Assert.Equal(Path.Combine(_root, "models", "sense-voice"), line.Value("sense_voice_model_dir"));
        Assert.Equal(Path.Combine(_root, "models", "llm", "Qwen3.5-0.8B-Q4_K_M.gguf"), line.Value("llm_model_path"));
        Assert.Equal(Path.Combine(_root, "models", "tts"), line.Value("qwen3_model_dir"));
        Assert.Equal(
            Path.Combine(_root, "models", "smart-turn", "smart-turn-v3.2-cpu.onnx"),
            line.Value("smart_turn_model_path"));
    }

    [Fact]
    public void BuildPlan_selects_default_server_backend_groups()
    {
        var line = CommandLine.Parse(["serve"]);

        var plan = LocalModelPreparation.BuildPlan(line, _root);

        Assert.Equal(["vad", "sense-voice", "llm", "tts", "smart-turn"], plan.SelectedGroups);
    }

    [Fact]
    public void BuildPlan_omits_remote_backend_groups()
    {
        var line = CommandLine.Parse(["local", "--stt", "openai", "--llm_backend", "openai", "--tts", "openai"]);

        var plan = LocalModelPreparation.BuildPlan(line, _root);

        Assert.Equal(["vad", "smart-turn"], plan.SelectedGroups);
    }

    [Fact]
    public void BuildPlan_does_not_replace_explicit_model_paths()
    {
        var vad = Path.Combine(_root, "custom-vad");
        var senseVoice = Path.Combine(_root, "custom-sense-voice");
        var llm = Path.Combine(_root, "custom.gguf");
        var tts = Path.Combine(_root, "custom-tts");
        var line = CommandLine.Parse(
        [
            "local",
            "--vad_model_dir", vad,
            "--sense_voice_model_dir", senseVoice,
            "--llm_model_path", llm,
            "--qwen3_model_dir", tts,
        ]);

        var plan = LocalModelPreparation.BuildPlan(line, _root);
        LocalModelPreparation.ApplyDefaultPaths(line, plan);

        Assert.Equal(vad, line.Value("vad_model_dir"));
        Assert.Equal(senseVoice, line.Value("sense_voice_model_dir"));
        Assert.Equal(llm, line.Value("llm_model_path"));
        Assert.Equal(tts, line.Value("qwen3_model_dir"));
        Assert.DoesNotContain("vad", plan.SelectedGroups);
        Assert.DoesNotContain("sense-voice", plan.SelectedGroups);
        Assert.DoesNotContain("llm", plan.SelectedGroups);
        Assert.DoesNotContain("tts", plan.SelectedGroups);
        Assert.Equal(4, plan.CustomPaths.Count);
    }

    [Fact]
    public void BuildPlan_omits_custom_smart_turn_path()
    {
        var modelPath = Path.Combine(_root, "custom-smart-turn.onnx");
        var line = CommandLine.Parse(["local", "--smart_turn_model_path", modelPath]);

        var plan = LocalModelPreparation.BuildPlan(line, _root);

        Assert.DoesNotContain("smart-turn", plan.SelectedGroups);
        Assert.Contains(plan.CustomPaths, item => item.Path == modelPath);
    }

    [Fact]
    public async Task PrepareAsync_skips_process_when_required_default_files_are_valid()
    {
        var plan = LocalModelPreparation.BuildPlan(CommandLine.Parse(["local"]), _root);
        WriteManifestFiles(DefaultFiles);
        var processCalls = 0;

        await LocalModelPreparation.PrepareAsync(
            plan,
            (_, _) =>
            {
                processCalls++;
                return Task.FromResult(0);
            },
            CancellationToken.None);

        Assert.Equal(0, processCalls);
    }

    [Fact]
    public async Task PrepareAsync_reuses_verification_for_unchanged_model_files()
    {
        var plan = LocalModelPreparation.BuildPlan(CommandLine.Parse(["local"]), _root);
        WriteManifestFiles(DefaultFiles);
        await LocalModelPreparation.PrepareAsync(plan, (_, _) => Task.FromResult(0), CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var vadFile = Path.Combine(_root, "models", "vad", "silero_vad.onnx");
        using var lockedFile = new FileStream(vadFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await LocalModelPreparation.PrepareAsync(plan, (_, _) => Task.FromResult(0), CancellationToken.None);
    }

    [Fact]
    public async Task PrepareAsync_rechecks_assets_when_file_metadata_changes()
    {
        var plan = LocalModelPreparation.BuildPlan(CommandLine.Parse(["local"]), _root);
        WriteManifestFiles(DefaultFiles);
        await LocalModelPreparation.PrepareAsync(plan, (_, _) => Task.FromResult(0), CancellationToken.None);

        var vadFile = Path.Combine(_root, "models", "vad", "silero_vad.onnx");
        var oldTimestamp = File.GetLastWriteTimeUtc(vadFile);
        File.WriteAllBytes(vadFile, Encoding.UTF8.GetBytes("tampered text"));
        File.SetLastWriteTimeUtc(vadFile, oldTimestamp.AddSeconds(5));

        ProcessStartInfo? invoked = null;
        await LocalModelPreparation.PrepareAsync(
            plan,
            (startInfo, _) =>
            {
                invoked = startInfo;
                WriteManifestFiles(["vad/silero_vad.onnx"]);
                return Task.FromResult(0);
            },
            CancellationToken.None);

        Assert.NotNull(invoked);
        var groupsOption = OperatingSystem.IsWindows() ? "-Groups" : "--groups";
        var groupsIndex = Array.IndexOf(invoked.ArgumentList.ToArray(), groupsOption);
        Assert.NotEqual(-1, groupsIndex);
        Assert.Equal("vad", invoked.ArgumentList[groupsIndex + 1]);
    }

    [Fact]
    public async Task PrepareAsync_runs_process_for_missing_group_and_rechecks_files()
    {
        var plan = LocalModelPreparation.BuildPlan(CommandLine.Parse(["local"]), _root);
        WriteManifestFiles(DefaultFiles.Where(path => path != "vad/silero_vad.onnx"));
        ProcessStartInfo? invoked = null;

        await LocalModelPreparation.PrepareAsync(
            plan,
            (startInfo, _) =>
            {
                invoked = startInfo;
                WriteManifestFiles(["vad/silero_vad.onnx"]);
                return Task.FromResult(0);
            },
            CancellationToken.None);

        Assert.NotNull(invoked);
        var groupsOption = OperatingSystem.IsWindows() ? "-Groups" : "--groups";
        var groupsIndex = Array.IndexOf(invoked.ArgumentList.ToArray(), groupsOption);
        Assert.NotEqual(-1, groupsIndex);
        Assert.Equal("vad", invoked.ArgumentList[groupsIndex + 1]);
    }

    [Fact]
    public async Task PrepareAsync_returns_error_when_script_exits_nonzero()
    {
        var plan = LocalModelPreparation.BuildPlan(CommandLine.Parse(["local"]), _root);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelPreparation.PrepareAsync(
                plan,
                (_, _) => Task.FromResult(17),
                CancellationToken.None));

        Assert.Contains("17", error.Message);
    }

    [Fact]
    public async Task PrepareAsync_fails_for_missing_custom_model_before_listener()
    {
        var customModel = Path.Combine(_root, "missing-custom.gguf");
        var plan = LocalModelPreparation.BuildPlan(
            CommandLine.Parse(["local", "--llm_model_path", customModel]),
            _root);
        var processCalls = 0;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelPreparation.PrepareAsync(
                plan,
                (_, _) =>
                {
                    processCalls++;
                    return Task.FromResult(0);
                },
                CancellationToken.None));

        Assert.Contains(customModel, error.Message);
        Assert.Equal(0, processCalls);
    }

    [Fact]
    public void BuildPlan_fails_when_platform_downloader_is_missing()
    {
        var script = Path.Combine(_root, OperatingSystem.IsWindows() ? "download-models.ps1" : "download-models.sh");
        File.Delete(script);

        var error = Assert.Throws<InvalidOperationException>(() =>
            LocalModelPreparation.BuildPlan(CommandLine.Parse(["local"]), _root));

        Assert.Contains(script, error.Message);
    }

    [Fact]
    public async Task PrepareAsync_reports_when_platform_shell_cannot_start()
    {
        var plan = LocalModelPreparation.BuildPlan(CommandLine.Parse(["local"]), _root);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelPreparation.PrepareAsync(
                plan,
                (_, _) => throw new System.ComponentModel.Win32Exception("shell unavailable"),
                CancellationToken.None));

        Assert.Contains("Unable to start", error.Message);
    }

    [Fact]
    public async Task PrepareAsync_rejects_successful_process_with_invalid_downloaded_files()
    {
        var plan = LocalModelPreparation.BuildPlan(CommandLine.Parse(["local"]), _root);
        WriteManifestFiles(DefaultFiles.Where(path => path != "vad/silero_vad.onnx"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelPreparation.PrepareAsync(
                plan,
                (_, _) => Task.FromResult(0),
                CancellationToken.None));

        Assert.Contains("vad/silero_vad.onnx", error.Message);
    }

    [Fact]
    public async Task PrepareAsync_rejects_incomplete_custom_qwen_model_directory()
    {
        var customTts = Path.Combine(_root, "custom-tts");
        Directory.CreateDirectory(Path.Combine(customTts, "embeddings"));
        File.WriteAllText(Path.Combine(customTts, "talker_decode.onnx"), "model");
        File.WriteAllText(Path.Combine(customTts, "code_predictor.onnx"), "model");
        File.WriteAllText(Path.Combine(customTts, "embeddings", "config.json"), "{}");

        var plan = LocalModelPreparation.BuildPlan(
            CommandLine.Parse(["local", "--qwen3_model_dir", customTts]),
            _root);
        WriteManifestFiles(DefaultFiles.Where(path => path != "tts/talker_decode.onnx"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelPreparation.PrepareAsync(
                plan,
                (_, _) => Task.FromResult(0),
                CancellationToken.None));

        Assert.Contains(customTts, error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteManifest(IEnumerable<string> paths)
    {
        var manifest = Path.Combine(_root, "tools", "models", "models.tsv");
        var payload = Encoding.UTF8.GetBytes("model fixture");
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        File.WriteAllLines(
            manifest,
            paths.Select(path => $"{hash}\t{payload.Length}\t{path}\thttps://example.invalid/{path}"),
            Encoding.ASCII);
    }

    private void WriteManifestFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var file = Path.Combine(_root, "models", path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes("model fixture"));
        }
    }
}
