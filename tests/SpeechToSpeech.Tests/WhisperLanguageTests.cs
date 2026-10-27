using System.Text.Json.Nodes;
using SpeechToSpeech.Backends.ManySpeech;
using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Tests;

/// <summary>
/// Whisper 识别语言接线（D034）。
///
/// 上游把 <c>language</c> 传给 faster-whisper（默认 <c>en</c>，<c>auto</c> 交给模型判定）。
/// 本实现原先接受 <c>--language</c> 却**静默忽略**——参数在、行为不在，是最难发现的一类差异：
/// 用户设了中文却按英文模型的口径解码，转写变成一串似是而非的谐音字。
///
/// 这里锁三件事：值真的写进了交给库的 conf；模型目录里的原件不被改动；
/// <c>auto</c> 与「默认 en」两条口径与上游一致。
/// </summary>
public sealed class WhisperLanguageTests : IDisposable
{
    private readonly string _dir;

    public WhisperLanguageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"s2s-whisper-lang-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响断言结果，别让它把测试标红。
        }
    }

    private string WriteConfig(string language = "")
    {
        var path = Path.Combine(_dir, "conf.json");
        var config = new JsonObject
        {
            ["is_multilingual"] = true,
            ["model_dimensions"] = new JsonObject { ["n_mels"] = 80 },
            ["decoding_options"] = new JsonObject
            {
                ["task"] = "transcribe",
                ["language"] = language,
                ["temperature"] = 0.0,
            },
        };
        File.WriteAllText(path, config.ToJsonString());
        return path;
    }

    private static string LanguageOf(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!["decoding_options"]!["language"]!.GetValue<string>();

    [Fact]
    public void Explicit_language_is_written_into_the_config_handed_to_the_library()
    {
        var source = WriteConfig();

        var resolved = WhisperSttBackend.ResolveConfig(source, "zh");

        Assert.NotEqual(source, resolved);
        Assert.Equal("zh", LanguageOf(resolved));
        // 其它解码选项必须原样带过去，不能因为改语言就把整份 conf 重写成默认值。
        Assert.Equal("transcribe", JsonNode.Parse(File.ReadAllText(resolved))!["decoding_options"]!["task"]!.GetValue<string>());
    }

    [Fact]
    public void The_model_directory_copy_is_never_modified()
    {
        var source = WriteConfig();
        var before = File.ReadAllText(source);

        WhisperSttBackend.ResolveConfig(source, "zh");

        Assert.Equal(before, File.ReadAllText(source));
    }

    [Fact]
    public void Missing_language_falls_back_to_the_upstream_default_of_en()
    {
        var source = WriteConfig();

        var resolved = WhisperSttBackend.ResolveConfig(source, null);

        Assert.Equal("en", LanguageOf(resolved));
    }

    [Fact]
    public void Auto_hands_the_config_through_untouched_so_the_model_detects_the_language()
    {
        var source = WriteConfig();

        // 上游对 "auto" 的处理是把这个键从 gen_kwargs 里 pop 掉：完全不传，faster-whisper
        // 才会自动判定。所以这里必须原样返回源文件，不能写一个 language="auto" 的副本
        // （那会被库当成未知语言）。
        var resolved = WhisperSttBackend.ResolveConfig(source, "auto");

        Assert.Equal(source, resolved);
        Assert.Equal(string.Empty, LanguageOf(source));
    }

    [Theory]
    [InlineData("ZH")]
    [InlineData("  zh  ")]
    [InlineData("Zh")]
    public void Language_codes_are_normalised_before_reaching_the_library(string requested)
    {
        var source = WriteConfig();

        var resolved = WhisperSttBackend.ResolveConfig(source, requested);

        Assert.Equal("zh", LanguageOf(resolved));
    }

    [Fact]
    public void Different_configs_do_not_share_a_temporary_file()
    {
        var first = WriteConfig();
        var second = Path.Combine(_dir, "conf-tiny.json");
        File.Copy(first, second);

        var a = WhisperSttBackend.ResolveConfig(first, "zh");
        var b = WhisperSttBackend.ResolveConfig(second, "zh");

        // 只按语言命名会让 tiny 与 large 的 conf 互相覆盖，重启后可能读到别的模型的维度。
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_config_without_decoding_options_fails_loudly()
    {
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, """{"is_multilingual":true}""");

        var error = Assert.Throws<BackendUnavailableException>(
            () => WhisperSttBackend.ResolveConfig(path, "zh"));

        Assert.Contains("decoding_options", error.Message);
    }
}
