using System.Text.RegularExpressions;

namespace SpeechToSpeech.Evals.BigBenchAudio;

/// <summary>
/// 把口述回复变成可比较的 Big Bench Audio 答案（上游 <c>scoring.py</c>）。
///
/// 纯文本工作：无网络、无音频、无管线。两条提取路径按序尝试：
/// <c>marker</c> —— 回复以显式答案句收尾（"Final answer: No"），默认 eval 指令要求
/// 恰好这个句式，marker 尾内**第一个**候选胜出；
/// <c>scan</c> —— marker 没能在转写中存活，全文扫描**最后一个**候选胜出
/// （这些题要多步推理，模型把结论放在推理之后）。
/// 完全没有候选则记 <c>correct=None</c>（未解析），把「推理错了」与
/// 「没给出可评分的答案」区分开。
/// </summary>
public static class Scoring
{
    /// <summary>某一类 Big Bench Audio 题目如何拼写答案。</summary>
    public sealed record CategorySpec(AnswerKind Kind, string[] Labels);

    public enum AnswerKind
    {
        Choice,
        Integer,
    }

    public static readonly IReadOnlyDictionary<string, CategorySpec> CategorySpecs =
        new Dictionary<string, CategorySpec>
        {
            ["formal_fallacies"] = new CategorySpec(AnswerKind.Choice, ["valid", "invalid"]),
            ["navigate"] = new CategorySpec(AnswerKind.Choice, ["yes", "no"]),
            ["object_counting"] = new CategorySpec(AnswerKind.Integer, []),
            ["web_of_lies"] = new CategorySpec(AnswerKind.Choice, ["yes", "no"]),
        };

    /// <summary>一次评分的结果。</summary>
    public sealed record Grade(string? Extracted, bool? Correct, string? Method)
    {
        public bool Parsed => Extracted is not null;
    }

    // "Final answer: X"、"the answer is X"、"my answer: X"、"answer -- X"。
    private static readonly Regex MarkerRegex = new(
        @"\b(?:final\s+answer|the\s+answer\s+is|my\s+answer\s+is|my\s+answer|answer)\b\s*(?:is\s*)?[:\-–—]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 有意收窄。"correct"/"negative" 在对话里读作 yes/no，但也以普通词出现在题目里
    // （"the correct answer is No"、"the negative direction"）；错提取比未解析更糟。
    private static readonly Regex YesRegex = new(@"\b(?:yes|yeah|yep|yup)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoRegex = new(@"\b(?:no|nope|nah)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ValidRegex = new(@"\b(?:in\s*-?\s*valid|invalid|valid)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 匹配 "not valid" 与 "not deductively valid"，但不匹配 "not sure this is valid"。
    private static readonly Regex NegationRegex = new(@"\b(?:not|isn't|aren't|never|n't)\b(?:\s+\w+ly)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, int> NumberWords = new()
    {
        ["zero"] = 0,
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
        ["eleven"] = 11,
        ["twelve"] = 12,
        ["thirteen"] = 13,
        ["fourteen"] = 14,
        ["fifteen"] = 15,
        ["sixteen"] = 16,
        ["seventeen"] = 17,
        ["eighteen"] = 18,
        ["nineteen"] = 19,
        ["twenty"] = 20,
    };

    private static readonly Regex NumberRegex = new(
        @"\b(\d{1,3}|" + string.Join("|", NumberWords.Keys) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // marker 尾只含答案句；再往后就是新的絮叨。
    private const int MarkerTailChars = 60;

    private static string? MarkerTail(string text)
    {
        Match? last = null;
        foreach (Match match in MarkerRegex.Matches(text))
        {
            last = match;
        }

        if (last is null)
        {
            return null;
        }

        var start = last.Index + last.Length;
        var length = Math.Min(MarkerTailChars, text.Length - start);
        return length <= 0 ? string.Empty : text.Substring(start, length);
    }

    private static bool Negated(string text, int start)
    {
        var windowStart = Math.Max(0, start - 24);
        return NegationRegex.IsMatch(text[windowStart..start]);
    }

    private static string? ExtractYesNo(string text, bool first)
    {
        var matches = new List<(string Label, Match Match)>();
        foreach (Match m in YesRegex.Matches(text))
        {
            matches.Add(("yes", m));
        }

        foreach (Match m in NoRegex.Matches(text))
        {
            matches.Add(("no", m));
        }

        if (matches.Count == 0)
        {
            return null;
        }

        matches.Sort((a, b) => a.Match.Index.CompareTo(b.Match.Index));
        var (label, match) = first ? matches[0] : matches[^1];
        if (Negated(text, match.Index))
        {
            return label == "yes" ? "no" : "yes";
        }

        return label;
    }

    private static string? ExtractValid(string text, bool first)
    {
        var matches = ValidRegex.Matches(text);
        if (matches.Count == 0)
        {
            return null;
        }

        var match = first ? matches[0] : matches[^1];
        var label = match.Value.ToLowerInvariant().Replace("-", "").Replace(" ", "") == "valid"
            ? "valid"
            : "invalid";
        if (label == "valid" && Negated(text, match.Index))
        {
            return "invalid";
        }

        return label;
    }

    private static string? ExtractInteger(string text, bool first)
    {
        var matches = NumberRegex.Matches(text);
        if (matches.Count == 0)
        {
            return null;
        }

        var token = (first ? matches[0] : matches[^1]).Groups[1].Value.ToLowerInvariant();
        return NumberWords.TryGetValue(token, out var value) ? value.ToString() : int.Parse(token).ToString();
    }

    private static string? ExtractForSpec(string text, CategorySpec spec, bool first)
    {
        if (spec.Kind == AnswerKind.Integer)
        {
            return ExtractInteger(text, first);
        }

        return spec.Labels is ["valid", "invalid"] ? ExtractValid(text, first) : ExtractYesNo(text, first);
    }

    /// <summary>从回复中抽取规范化答案 token；无可评分内容时返回 (null, null)。未知类别不猜测。</summary>
    public static (string? Answer, string? Method) ExtractAnswer(string category, string reply)
    {
        if (!CategorySpecs.TryGetValue(category, out var spec) || string.IsNullOrWhiteSpace(reply))
        {
            return (null, null);
        }

        var tail = MarkerTail(reply);
        if (tail is not null)
        {
            var markerAnswer = ExtractForSpec(tail, spec, first: true);
            if (markerAnswer is not null)
            {
                return (markerAnswer, "marker");
            }
        }

        var answer = ExtractForSpec(reply, spec, first: false);
        return answer is not null ? (answer, "scan") : (null, null);
    }

    /// <summary>把数据集官方答案规范化到与 <see cref="ExtractAnswer"/> 相同的 token 空间。</summary>
    public static string NormalizeOfficial(string category, string officialAnswer)
    {
        var text = officialAnswer.Trim();
        if (CategorySpecs.TryGetValue(category, out var spec) && spec.Kind == AnswerKind.Integer)
        {
            return int.Parse(text).ToString();
        }

        return text.ToLowerInvariant();
    }

    /// <summary>按数据集官方答案给一条助手回复评分。</summary>
    public static Grade GradeOne(string category, string officialAnswer, string reply)
    {
        var (extracted, method) = ExtractAnswer(category, reply);
        if (extracted is null)
        {
            return new Grade(Extracted: null, Correct: null, Method: null);
        }

        return new Grade(
            Extracted: extracted,
            Correct: extracted == NormalizeOfficial(category, officialAnswer),
            Method: method);
    }
}
