using Panlingo.LanguageIdentification.Lingua;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Text;

namespace SpeechToSpeech.Backends.Lingua;

/// <summary>
/// 基于 <c>Panlingo.LanguageIdentification.Lingua</c> 的语言识别后端。
///
/// 它是 lingua 的 Rust 实现（<c>lingua-rs</c>）的 .NET 封装，与上游用的 <c>lingua-py</c>
/// 同一套算法与语言模型，因此判定结果可以逐值比对，而不是「差不多」。
///
/// 代价是原生库按 RID 分发：<c>win-x64</c> / <c>linux-x64</c> / <c>osx-arm64</c>，
/// 其余组合（含 Linux arm64、macOS x64）在运行期报 <see cref="BackendUnavailableException"/>，
/// 不静默退化成「识别不出语言」——那会让音色选错却没有任何提示。
/// </summary>
public sealed class LinguaLanguageDetector : ILanguageDetector, IDisposable
{
    /// <summary>
    /// ISO 639-3 名称 → ISO 639-1 码。
    ///
    /// 必须显式列表：检测器给的是 <c>LinguaLanguage</c>（枚举按 ISO 639-3 命名），
    /// 而线路与 TTS 要的是两位码，两者不是截断关系（<c>eng</c>→<c>en</c>、<c>zho</c>→<c>zh</c>）。
    /// 表由 lingua-py 的 <c>iso_code_639_3 → iso_code_639_1</c> 导出，不是手抄。
    /// </summary>
    private static readonly Dictionary<string, string> Iso6391ByIso6393Name = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AFRIKAANS"] = "af",
        ["ALBANIAN"] = "sq",
        ["ARABIC"] = "ar",
        ["ARMENIAN"] = "hy",
        ["AZERBAIJANI"] = "az",
        ["BASQUE"] = "eu",
        ["BELARUSIAN"] = "be",
        ["BENGALI"] = "bn",
        ["BOKMAL"] = "nb",
        ["BOSNIAN"] = "bs",
        ["BULGARIAN"] = "bg",
        ["CATALAN"] = "ca",
        ["CHINESE"] = "zh",
        ["CROATIAN"] = "hr",
        ["CZECH"] = "cs",
        ["DANISH"] = "da",
        ["DUTCH"] = "nl",
        ["ENGLISH"] = "en",
        ["ESPERANTO"] = "eo",
        ["ESTONIAN"] = "et",
        ["FINNISH"] = "fi",
        ["FRENCH"] = "fr",
        ["GANDA"] = "lg",
        ["GEORGIAN"] = "ka",
        ["GERMAN"] = "de",
        ["GREEK"] = "el",
        ["GUJARATI"] = "gu",
        ["HEBREW"] = "he",
        ["HINDI"] = "hi",
        ["HUNGARIAN"] = "hu",
        ["ICELANDIC"] = "is",
        ["INDONESIAN"] = "id",
        ["IRISH"] = "ga",
        ["ITALIAN"] = "it",
        ["JAPANESE"] = "ja",
        ["KAZAKH"] = "kk",
        ["KOREAN"] = "ko",
        ["LATIN"] = "la",
        ["LATVIAN"] = "lv",
        ["LITHUANIAN"] = "lt",
        ["MACEDONIAN"] = "mk",
        ["MALAY"] = "ms",
        ["MAORI"] = "mi",
        ["MARATHI"] = "mr",
        ["MONGOLIAN"] = "mn",
        ["NYNORSK"] = "nn",
        ["PERSIAN"] = "fa",
        ["POLISH"] = "pl",
        ["PORTUGUESE"] = "pt",
        ["PUNJABI"] = "pa",
        ["ROMANIAN"] = "ro",
        ["RUSSIAN"] = "ru",
        ["SERBIAN"] = "sr",
        ["SHONA"] = "sn",
        ["SLOVAK"] = "sk",
        ["SLOVENE"] = "sl",
        ["SOMALI"] = "so",
        ["SOTHO"] = "st",
        ["SPANISH"] = "es",
        ["SWAHILI"] = "sw",
        ["SWEDISH"] = "sv",
        ["TAGALOG"] = "tl",
        ["TAMIL"] = "ta",
        ["TELUGU"] = "te",
        ["THAI"] = "th",
        ["TSONGA"] = "ts",
        ["TSWANA"] = "tn",
        ["TURKISH"] = "tr",
        ["UKRAINIAN"] = "uk",
        ["URDU"] = "ur",
        ["VIETNAMESE"] = "vi",
        ["WELSH"] = "cy",
        ["XHOSA"] = "xh",
        ["YORUBA"] = "yo",
        ["ZULU"] = "zu",
    };

    private readonly LinguaDetector _detector;

    /// <param name="preload">
    /// 预先加载语言模型。上游在 handler 初始化时 <c>warm_language_detector()</c> 预热，
    /// 就是为了把这笔开销挪出第一句回复的关键路径。
    /// </param>
    public LinguaLanguageDetector(bool preload = true)
    {
        LinguaDetectorBuilder builder;
        try
        {
            builder = new LinguaDetectorBuilder(Enum.GetValues<LinguaLanguage>());
            if (preload)
            {
                builder = builder.WithPreloadedLanguageModels();
            }

            _detector = builder.Build();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            // 原生库缺失是部署问题，不是「这句没识别出来」。混为一谈会让音色选错却查不出原因。
            throw new BackendUnavailableException(
                "lingua",
                $"缺少 lingua 原生库（当前平台 {Environment.OSVersion.Platform}/{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}）：{ex.Message}");
        }
    }

    public string Name => "lingua";

    /// <inheritdoc />
    public IReadOnlyList<LanguageCandidate> Rank(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var predictions = _detector.PredictLanguages(text);
        if (predictions is null)
        {
            return [];
        }

        var candidates = new List<LanguageCandidate>();
        foreach (var prediction in predictions)
        {
            // 没有 ISO 639-1 对应码的语言（枚举里有几个只有三位码）直接跳过：
            // 下游按两位码选音色，报一个三位码过去等于报了个不存在的语言。
            if (!TryGetIso6391(prediction.Language, out var code))
            {
                continue;
            }

            candidates.Add(new LanguageCandidate(code, prediction.Confidence));
        }

        // 显式排序而不是依赖原生返回顺序：置信度差的判定完全建立在「前两名」上，
        // 顺序错了就整条判定失效，而这种错在单句上完全看不出来。
        candidates.Sort((left, right) => right.Confidence.CompareTo(left.Confidence));
        return candidates;
    }

    private static bool TryGetIso6391(LinguaLanguage language, out string code)
    {
        var name = Enum.GetName(language);
        if (name is not null && Iso6391ByIso6393Name.TryGetValue(name, out var mapped))
        {
            code = mapped;
            return true;
        }

        code = "";
        return false;
    }

    public void Dispose() => _detector.Dispose();
}
