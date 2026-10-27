using System.Text;
using System.Text.RegularExpressions;

namespace SpeechToSpeech.Core.Text;

/// <summary>
/// 助手文本的规范化与分句。
///
/// 忠实移植自上游 <c>LLM/utils.py</c>：
/// <list type="bullet">
/// <item>Markdown 代码块与行内代码在分句时被整体保护，不按标点切断；</item>
/// <item>强调标记只在保守的词边界成对移除，词中星号（乘法）保留；</item>
/// <item>不可朗读字符（emoji、控制符等）在流式增量上逐段过滤是安全的。</item>
/// </list>
/// 分句器本身不再用 NLTK Punkt（见差异账 C005），改用确定性规则。
/// </summary>
public static partial class TextNormalizer
{
    private static readonly Dictionary<char, char> SmartPunctuation = new()
    {
        ['\u2018'] = '\'',
        ['\u2019'] = '\'',
        ['\u201c'] = '"',
        ['\u201d'] = '"',
    };

    [GeneratedRegex(
        @"[^\w\s.,!?;:'""\-()\/\\@#%&*+=$€£¥₹₽¢\[\]{}<>~`^|…—–，。！？；：、\n\r\t]",
        RegexOptions.Compiled)]
    private static partial Regex SpeechablePattern();

    [GeneratedRegex(@"^[ \t]{0,3}#{1,6}(?:[ \t]+|$)", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex MarkdownHeadingPattern();

    [GeneratedRegex(@"^[ \t]{0,3}[-*+][ \t]+", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex MarkdownBulletPattern();

    [GeneratedRegex(
        @"^[ \t]{0,3}`{3,}[^\r\n]*\r?\n(?<body>.*?)(?:^[ \t]{0,3}`{3,}[ \t]*$)",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex MarkdownFencedCodePattern();

    [GeneratedRegex(@"(?<ticks>`{1,})(?<body>[^\n]*?)\k<ticks>", RegexOptions.Compiled)]
    private static partial Regex MarkdownInlineCodePattern();

    [GeneratedRegex(@"^[ \t]{0,3}(?<ticks>`{3,})(?<rest>[^\r\n]*)$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex MarkdownFenceLinePattern();

    [GeneratedRegex(
        @"(?<![\w*_])(?<delimiter>\*{1,3}|_{1,2})(?![*_])(?<body>\S(?:[^\n]*?\S)??)\k<delimiter>(?![\w*_])",
        RegexOptions.Compiled)]
    private static partial Regex MarkdownBoundaryEmphasisPattern();

    /// <summary>仅保留可朗读字符（unicode 字母、数字、标点、空白）。</summary>
    public static string RemoveUnspeakable(string text)
    {
        var translated = TranslateSmartPunctuation(text);
        return SpeechablePattern().Replace(translated, string.Empty);
    }

    /// <summary>
    /// 去掉常见 Markdown 分隔符但保留包裹的正文。
    ///
    /// 必须在完整文本上运行，不能逐 token：一个分隔符序列可能被拆在两个流式块里。
    /// </summary>
    public static string RemoveMarkdown(string text)
    {
        var (protectedText, protectedCode) = ProtectMarkdownCode(text, keepDelimiters: false);
        protectedText = MarkdownHeadingPattern().Replace(protectedText, string.Empty);
        protectedText = MarkdownBulletPattern().Replace(protectedText, string.Empty);
        protectedText = MarkdownBoundaryEmphasisPattern().Replace(protectedText, m => m.Groups["body"].Value);
        return RestoreMarkdownCode(protectedText, protectedCode);
    }

    /// <summary>
    /// 分句，但绝不切断完整的 Markdown 构造。
    ///
    /// 未闭合的围栏代码块整段作为一个句子返回——此时没有可靠的边界可言。
    /// </summary>
    public static List<string> SentenceTokenizePreservingMarkdownCode(string text)
    {
        if (HasUnclosedMarkdownFence(text))
        {
            return [text];
        }

        var (protectedText, protectedCode) = ProtectMarkdownCode(text, keepDelimiters: true);
        var (emphasizedText, protectedEmphasis) = ProtectMatchedEmphasis(protectedText);

        return SentenceTokenize(emphasizedText)
            .Select(sentence => RestoreMarkdownCode(RestoreMatchedEmphasis(sentence, protectedEmphasis), protectedCode))
            .ToList();
    }

    /// <summary>
    /// 确定性分句。
    ///
    /// 规则：句末标点（含 CJK 全角标点）之后若为空白或行尾，则断句；
    /// 常见缩写与单个大写字母缩写不让断（<c>Mr.</c>、<c>e.g.</c>、<c>U.S.</c>）。
    /// 相邻的句末标点（<c>?!</c>、<c>...</c>）归入同一句尾。
    /// </summary>
    public static List<string> SentenceTokenize(string text)
    {
        var sentences = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return sentences;
        }

        var builder = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            builder.Append(ch);

            if (!IsSentenceTerminator(ch))
            {
                i++;
                continue;
            }

            // 吃掉连续的句末标点与紧随的引号/括号
            var j = i + 1;
            while (j < text.Length && (IsSentenceTerminator(text[j]) || IsClosingQuote(text[j])))
            {
                builder.Append(text[j]);
                j++;
            }

            // CJK 全角标点本身就是边界，不依赖后面的空白——中文正文不会在句末加空格
            var followedByBreak = IsCjkTerminator(ch)
                                  || j >= text.Length
                                  || char.IsWhiteSpace(text[j]);
            if (followedByBreak && !IsAbbreviation(builder.ToString()))
            {
                var sentence = builder.ToString().Trim();
                if (sentence.Length > 0)
                {
                    sentences.Add(sentence);
                }

                builder.Clear();
                while (j < text.Length && char.IsWhiteSpace(text[j]))
                {
                    j++;
                }
            }

            i = j;
        }

        var tail = builder.ToString().Trim();
        if (tail.Length > 0)
        {
            sentences.Add(tail);
        }

        return sentences;
    }

    private static bool IsSentenceTerminator(char ch)
        => ch is '.' or '!' or '?' or '。' or '！' or '？' or '；' or '…';

    /// <summary>中文句末标点：出现即断句，不看后随空白。</summary>
    private static bool IsCjkTerminator(char ch) => ch is '。' or '！' or '？' or '；';

    private static bool IsClosingQuote(char ch) => ch is '"' or '\'' or '”' or '’' or ')' or '）';

    private static bool IsAbbreviation(string candidate)
    {
        var trimmed = candidate.TrimEnd('"', '\'', '”', '’', ')', '）');
        var lastWord = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return CommonAbbreviations.Contains(lastWord)
               || Regex.IsMatch(lastWord, @"^(?:[A-Za-z]\.){2,}$");
    }

    private static readonly HashSet<string> CommonAbbreviations = new(StringComparer.Ordinal)
    {
        "Mr.", "Mrs.", "Ms.", "Dr.", "Prof.", "Sr.", "Jr.", "St.", "Mt.",
        "e.g.", "i.e.", "etc.", "vs.", "cf.", "al.", "No.", "Fig.", "approx.",
    };

    private static bool HasUnclosedMarkdownFence(string text)
    {
        int? openingTicks = null;
        foreach (Match match in MarkdownFenceLinePattern().Matches(text))
        {
            var ticks = match.Groups["ticks"].Value.Length;
            var rest = match.Groups["rest"].Value;
            if (openingTicks is null)
            {
                // 同一行后面还有反引号说明这是行内代码（如 ```code```），不是围栏
                if (!rest.Contains('`'))
                {
                    openingTicks = ticks;
                }
            }
            else if (ticks >= openingTicks && string.IsNullOrWhiteSpace(rest))
            {
                openingTicks = null;
            }
        }

        return openingTicks is not null;
    }

    private static (string Text, List<string> Protected) ProtectMarkdownCode(string text, bool keepDelimiters)
    {
        var protectedCode = new List<string>();

        string ProtectCode(Match match)
        {
            var token = $"\x00markdown-code-{protectedCode.Count}\x00";
            protectedCode.Add(keepDelimiters ? match.Value : match.Groups["body"].Value);
            return token;
        }

        text = MarkdownFencedCodePattern().Replace(text, ProtectCode);
        text = MarkdownInlineCodePattern().Replace(text, ProtectCode);
        return (text, protectedCode);
    }

    private static string RestoreMarkdownCode(string text, List<string> protectedCode)
    {
        for (var index = 0; index < protectedCode.Count; index++)
        {
            text = text.Replace($"\x00markdown-code-{index}\x00", protectedCode[index], StringComparison.Ordinal);
        }

        return text;
    }

    private static (string Text, List<string> Protected) ProtectMatchedEmphasis(string text)
    {
        var protectedEmphasis = new List<string>();

        string ProtectEmphasis(Match match)
        {
            var token = $"\x00markdown-emphasis-{protectedEmphasis.Count}\x00";
            protectedEmphasis.Add(match.Value);
            return token;
        }

        return (MarkdownBoundaryEmphasisPattern().Replace(text, ProtectEmphasis), protectedEmphasis);
    }

    private static string RestoreMatchedEmphasis(string text, List<string> protectedEmphasis)
    {
        for (var index = 0; index < protectedEmphasis.Count; index++)
        {
            text = text.Replace($"\x00markdown-emphasis-{index}\x00", protectedEmphasis[index], StringComparison.Ordinal);
        }

        return text;
    }

    private static string TranslateSmartPunctuation(string text)
    {
        if (!text.Any(SmartPunctuation.ContainsKey))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(SmartPunctuation.TryGetValue(ch, out var replacement) ? replacement : ch);
        }

        return builder.ToString();
    }
}
