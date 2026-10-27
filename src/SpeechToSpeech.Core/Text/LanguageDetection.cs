using System.Text;

namespace SpeechToSpeech.Core.Text;

/// <summary>一条语言判定：ISO 639-1 码 + 置信度（0..1，同一文本的各候选之和为 1）。</summary>
public readonly record struct LanguageCandidate(string Code, double Confidence);

/// <summary>
/// 文本语言识别的后端接缝。
///
/// 上游用的是 <c>lingua-py</c>；C# 侧对应 <c>Panlingo.LanguageIdentification.Lingua</c>
/// （同样是 lingua 算法，按 RID 分发原生库）。抽成接口是为了让决策层能离线单测——
/// 阈值、长度门槛、CJK 短路这些规则与用哪个检测器无关，混在一起就只能靠真跑发现回归。
/// </summary>
public interface ILanguageDetector
{
    /// <summary>
    /// 按置信度降序给出候选，对应上游 <c>compute_language_confidence_values</c>。
    /// 无法判定时返回空列表（不是「返回一个低置信度候选」）。
    /// </summary>
    IReadOnlyList<LanguageCandidate> Rank(string text);
}

/// <summary>
/// 语言检测的决策层。上游 <c>pipeline/language_detection.py</c> 的 <c>detect_language_from_text</c>。
///
/// 检测器本身给出的是「一堆候选 + 置信度」，真正决定「认不认这个结果」的是这里的三条规则：
/// 长度门槛、极短 CJK 放宽、置信度差下限。三者都不依赖具体检测器，因此可以逐值比对。
/// </summary>
public static class LanguageDetection
{
    /// <summary>助手输出语言的最低置信度差。上游 <c>MIN_ASSISTANT_CONFIDENCE_GAP</c>。</summary>
    public const double MinAssistantConfidenceGap = 0.1;

    /// <summary>低于这个长度不做判定——短句的语言特征太弱，判了也是噪声。</summary>
    public const int MinimumLength = 20;

    /// <summary>
    /// 含 CJK / 假名 / 谚文时放宽到的长度。
    ///
    /// 不是因为 CJK 更好判，而是因为这几个文字系统本身就把语言候选收窄到了个位数：
    /// 「今日はいい天気ですね」12 个字已经足够，而 12 个拉丁字母的英文句子不够。
    /// </summary>
    public const int MinimumShortCjkLength = 4;

    /// <summary>助手语言探针的滑动窗口上限（上游 <c>[-256:]</c>）。</summary>
    public const int AssistantProbeLimit = 256;

    /// <summary>
    /// 判定文本语言，返回 ISO 639-1 码。
    ///
    /// 与上游逐条对齐：
    /// <list type="number">
    /// <item>检测器缺失 → null（不猜）</item>
    /// <item>去空白后的字符数低于门槛 → null</item>
    /// <item>要求置信度差时：候选为空，或前两名差距不足 → null</item>
    /// <item>归一化 <c>nb</c> → <c>no</c>（挪威语书面语的两种写法在上游合并成一个）</item>
    /// </list>
    /// </summary>
    public static string? DetectFromText(
        string text,
        ILanguageDetector? detector,
        double minimumConfidenceGap = 0,
        bool allowShortCjk = false)
    {
        if (detector is null)
        {
            return null;
        }

        var stripped = text.Trim();

        // 按码点计数而不是 UTF-16 单元：上游是 Python，len() 数的是码点。
        // 用 Length 会让含表情符的短句突然「够长」，凭空多出一个判定。
        var minimum = MinimumLength;
        if (allowShortCjk && ContainsCjk(stripped))
        {
            minimum = MinimumShortCjkLength;
        }

        if (RuneCount(stripped) < minimum)
        {
            return null;
        }

        // 两条上游分支（detect_language_of / compute_language_confidence_values 取首位）
        // 等价于「按置信度降序的第一个」，所以只调一次 Rank。
        var candidates = detector.Rank(text);
        if (candidates.Count == 0)
        {
            return null;
        }

        // 只要差距不够就整条放弃，不退而求其次取第二名：
        // 「可能是英语也可能是荷兰语」拿去选音色，错的那一半听起来比沉默更糟。
        if (minimumConfidenceGap > 0
            && candidates.Count > 1
            && candidates[0].Confidence - candidates[1].Confidence < minimumConfidenceGap)
        {
            return null;
        }

        return NormalizeCode(candidates[0].Code);
    }

    /// <summary>
    /// <c>nb</c>（Bokmål）归到 <c>no</c>。
    ///
    /// 上游只映射这一个：检测器分得出 Nynorsk(<c>nn</c>) 与 Bokmål(<c>nb</c>)，
    /// 但下游 TTS 的语言表只认 <c>no</c>，不归并就会挑不到音色。
    /// </summary>
    public static string NormalizeCode(string code)
        => string.Equals(code, "nb", StringComparison.Ordinal) ? "no" : code;

    /// <summary>
    /// 文本是否含 CJK 汉字 / 日文假名 / 谚文。范围与上游一字不差：
    /// U+3400–U+9FFF、U+3040–U+30FF、U+AC00–U+D7AF。
    /// </summary>
    public static bool ContainsCjk(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x3400 and <= 0x9FFF)
            {
                return true;
            }

            if (value is >= 0x3040 and <= 0x30FF)
            {
                return true;
            }

            if (value is >= 0xAC00 and <= 0xD7AF)
            {
                return true;
            }
        }

        return false;
    }

    private static int RuneCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }
}

/// <summary>
/// 助手输出语言的探针窗口。上游 <c>LMOutputProcessor._observe_assistant_language</c> 的等价物。
///
/// 流式输出是碎片化的：单看一片可能只有 "Sure,"，看不出语言。所以把若干片拼进一个滑动窗口，
/// 每来一片就试判一次，**判出来就定死不再改**——一轮回复中途换语言会让 TTS 前后音色不一致。
///
/// 窗口上限 256 字符是上游的取舍：够长到能判，又不至于让一段长回复越判越慢。
/// 超过上限时丢头保尾——结尾通常才是最完整的句子。
/// </summary>
public sealed class AssistantLanguageProbe
{
    private readonly ILanguageDetector? _detector;
    private string _window = "";

    public AssistantLanguageProbe(ILanguageDetector? detector = null) => _detector = detector;

    /// <summary>本轮已判定的语言；null 表示还没判出来（或判不出来）。</summary>
    public string? Detected { get; private set; }

    /// <summary>当前窗口内容。供探针与测试观察，不参与判定。</summary>
    public string Window => _window;

    /// <summary>
    /// 观察一片助手文本。
    ///
    /// 已判定时直接返回：后续分片不再参与，避免长回复把结论改来改去。
    /// 检测器缺失时只累积窗口不做判定——上游在这种情况下会去预热一个，
    /// 本实现没有可预热的对象，因此显式记录为「无检测器」而不是假装判过。
    /// </summary>
    public void Observe(string text)
    {
        if (Detected is not null)
        {
            return;
        }

        _window = AccumulateWindow(_window, text);
        if (_detector is null)
        {
            return;
        }

        Detected = LanguageDetection.DetectFromText(
            _window,
            _detector,
            LanguageDetection.MinAssistantConfidenceGap,
            allowShortCjk: true);
    }

    /// <summary>新一轮回复：窗口与结论都清掉。上游 <c>_reset_response</c> 同规则。</summary>
    public void Reset()
    {
        _window = "";
        Detected = null;
    }

    /// <summary>
    /// 把一片文本拼进窗口：<c>(window + " " + text).strip()</c> 后保留末尾
    /// <see cref="LanguageDetection.AssistantProbeLimit"/> 个码点。
    ///
    /// 用空格而不是直接相接：模型分片常常不带尾空格，直接拼会把两个词粘成一个。
    /// </summary>
    public static string AccumulateWindow(string window, string text)
    {
        var combined = (window + " " + text).Trim();
        return TakeLastRunes(combined, LanguageDetection.AssistantProbeLimit);
    }

    private static string TakeLastRunes(string text, int limit)
    {
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length <= limit)
        {
            return text;
        }

        return string.Concat(runes[^limit..].ToArray().Select(static rune => rune.ToString()));
    }
}
