using System.Text.Json.Nodes;

namespace SpeechToSpeech.Evals.BigBenchAudio;

/// <summary>
/// 运行报告：聚合、渲染与基线-候选对比（上游 <c>report.py</c>）。
///
/// 报告刻意用纯 JSON：整套 harness 的意义在于对 main 与换后端的 PR 各跑一次，
/// 产出两份可 diff 的文件，因此 schema 稳定、所有派生数字都能从 items 重算。
/// </summary>
public static class Report
{
    public const string ReportSchema = "big-bench-audio-vibe/1";

    /// <summary>一道题的结果：音频进到评分答案出。</summary>
    public sealed class ItemResult
    {
        public int Id { get; set; }
        public string Category { get; set; } = "";
        public string OfficialAnswer { get; set; } = "";
        public string InputTranscript { get; set; } = "";
        public string Reply { get; set; } = "";
        public string? Extracted { get; set; }
        public bool? Correct { get; set; }
        public string? Method { get; set; }
        public string? JudgeExtracted { get; set; }
        public bool? JudgeCorrect { get; set; }
        public double? TtfbS { get; set; }
        public double? TurnS { get; set; }
        public int AudioOutBytes { get; set; }
        public string? Error { get; set; }
        public bool JudgeCompleted { get; set; }

        /// <summary>跑过 judge 用 judge 结论，否则用规则提取。</summary>
        public string? FinalExtracted =>
            JudgeCompleted || JudgeExtracted is not null ? JudgeExtracted : Extracted;

        public bool? FinalCorrect =>
            JudgeCompleted || JudgeExtracted is not null ? JudgeCorrect : Correct;

        public JsonObject ToJson()
        {
            var payload = new JsonObject
            {
                ["id"] = Id,
                ["category"] = Category,
                ["official_answer"] = OfficialAnswer,
                ["input_transcript"] = InputTranscript,
                ["reply"] = Reply,
                ["extracted"] = Extracted,
                ["correct"] = Correct,
                ["method"] = Method,
                ["judge_extracted"] = JudgeExtracted,
                ["judge_correct"] = JudgeCorrect,
                ["ttfb_s"] = TtfbS,
                ["turn_s"] = TurnS,
                ["audio_out_bytes"] = AudioOutBytes,
                ["error"] = Error,
                ["judge_completed"] = JudgeCompleted,
                ["final_extracted"] = FinalExtracted,
                ["final_correct"] = FinalCorrect,
            };
            return payload;
        }

        public static ItemResult FromJson(JsonObject raw)
        {
            bool? BoolOrNull(string key) => raw[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
            double? DoubleOrNull(string key) => raw[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
            return new ItemResult
            {
                Id = raw["id"]?.GetValue<int>() ?? 0,
                Category = raw["category"]?.GetValue<string>() ?? "",
                OfficialAnswer = raw["official_answer"]?.GetValue<string>() ?? "",
                InputTranscript = raw["input_transcript"]?.GetValue<string>() ?? "",
                Reply = raw["reply"]?.GetValue<string>() ?? "",
                Extracted = raw["extracted"]?.GetValue<string>(),
                Correct = BoolOrNull("correct"),
                Method = raw["method"]?.GetValue<string>(),
                JudgeExtracted = raw["judge_extracted"]?.GetValue<string>(),
                JudgeCorrect = BoolOrNull("judge_correct"),
                TtfbS = DoubleOrNull("ttfb_s"),
                TurnS = DoubleOrNull("turn_s"),
                AudioOutBytes = raw["audio_out_bytes"]?.GetValue<int>() ?? 0,
                Error = raw["error"]?.GetValue<string>(),
                JudgeCompleted = raw["judge_completed"] is JsonValue jv && jv.TryGetValue<bool>(out var jc) && jc,
            };
        }
    }

    /// <summary>线性插值百分位；空样本返回 null。</summary>
    private static double? Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values.OrderBy(static v => v).ToList();
        if (ordered.Count == 1)
        {
            return ordered[0];
        }

        var position = fraction * (ordered.Count - 1);
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        if (low == high)
        {
            return ordered[low];
        }

        return ordered[low] + (ordered[high] - ordered[low]) * (position - low);
    }

    private static JsonObject Summarize(IReadOnlyList<ItemResult> results)
    {
        var total = results.Count;
        var errors = results.Count(static item => !string.IsNullOrEmpty(item.Error));
        var parsed = results.Count(static item => item.FinalExtracted is not null);
        var correct = results.Count(static item => item.FinalCorrect is true && string.IsNullOrEmpty(item.Error));
        return new JsonObject
        {
            ["n"] = total,
            ["errors"] = errors,
            ["answered"] = total - errors,
            ["parsed"] = parsed,
            ["correct"] = correct,
            // 头条数字：错误与不可解析都记在模型头上——「没给出可评分答案」也是可比的真实失败。
            ["accuracy"] = total > 0 ? (double)correct / total : null,
            ["accuracy_parsed"] = parsed > 0 ? (double)correct / parsed : null,
            ["parse_rate"] = total > 0 ? (double)parsed / total : null,
        };
    }

    private static JsonObject Latency(IReadOnlyList<ItemResult> results)
    {
        var ttfb = results.Where(static item => item.TtfbS is not null).Select(static item => item.TtfbS!.Value).ToList();
        var turn = results.Where(static item => item.TurnS is not null).Select(static item => item.TurnS!.Value).ToList();
        return new JsonObject
        {
            ["ttfb_p50_s"] = Percentile(ttfb, 0.5),
            ["ttfb_p95_s"] = Percentile(ttfb, 0.95),
            ["turn_p50_s"] = Percentile(turn, 0.5),
            ["turn_p95_s"] = Percentile(turn, 0.95),
        };
    }

    /// <summary>一次运行的总分与分类别分。</summary>
    public static JsonObject Aggregate(IReadOnlyList<ItemResult> results)
    {
        var byCategory = new JsonObject();
        foreach (var category in results.Select(static item => item.Category).Distinct().OrderBy(static c => c))
        {
            byCategory[category] = Summarize(results.Where(item => item.Category == category).ToList());
        }

        return new JsonObject
        {
            ["totals"] = Summarize(results),
            ["by_category"] = byCategory,
            ["latency"] = Latency(results),
        };
    }

    /// <summary>拼装一次运行的 JSON 报告。</summary>
    public static JsonObject BuildReport(
        IReadOnlyList<ItemResult> results,
        Dataset.Subset subset,
        string label,
        JsonObject target,
        JsonObject? judge = null,
        DateTimeOffset? startedAt = null,
        double? durationS = null)
    {
        var stamp = startedAt ?? DateTimeOffset.UtcNow;
        var report = new JsonObject
        {
            ["schema"] = ReportSchema,
            ["label"] = label,
            ["started_at"] = stamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'"),
            ["duration_s"] = durationS,
            ["target"] = target,
            ["subset"] = new JsonObject
            {
                ["name"] = subset.Name,
                ["dataset"] = subset.Dataset,
                ["revision"] = subset.Revision,
                ["n"] = subset.Items.Count,
                ["ids"] = new JsonArray(subset.Items.Select(static item => JsonValue.Create(item.Id)).ToArray()),
            },
            ["judge"] = judge,
        };
        var aggregate = Aggregate(results);
        foreach (var (key, value) in aggregate)
        {
            report[key] = value?.DeepClone();
        }

        report["items"] = new JsonArray(results.Select(static item => item.ToJson()).ToArray());
        return report;
    }

    private static string Pct(double? value) => value is null ? "  n/a" : $"{value * 100,5:F1}%";

    private static string Secs(double? value) => value is null ? "  n/a" : $"{value,5:F2}s";

    private static bool IsTruthy(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string Str(JsonNode? node, string fallback = "") =>
        node is null ? fallback
        : node is JsonValue v && v.TryGetValue<string>(out var s) ? s
        : node.ToJsonString();

    /// <summary>一次运行的人类可读摘要。</summary>
    public static string RenderReport(JsonObject report)
    {
        var totals = report["totals"]!.AsObject();
        var subset = report["subset"]!.AsObject();
        var latency = report["latency"]?.AsObject() ?? new JsonObject();
        var lines = new List<string>
        {
            $"Big Bench Audio vibe check -- {Str(report["label"], "unlabeled")}",
            $"  subset   {Str(subset["name"])} ({subset["n"]!.GetValue<int>()} items) @ {Str(subset["dataset"])}@{Str(subset["revision"])[..7]}",
            $"  target   {Str(report["target"]?.AsObject()["url"], "?")}",
        };
        if (report["judge"] is not null)
        {
            lines.Add($"  judge    {Str(report["judge"]!.AsObject()["model"], "?")}");
        }

        lines.Add("");
        lines.Add($"  {"category",-18} {"acc",6} {"correct",9} {"parsed",8} {"errors",7}");
        lines.Add($"  {new string('-', 18)} {new string('-', 6)} {new string('-', 9)} {new string('-', 8)} {new string('-', 7)}");
        foreach (var (category, statsNode) in report["by_category"]?.AsObject() ?? new JsonObject())
        {
            var stats = statsNode!.AsObject();
            var n = stats["n"]!.GetValue<int>();
            lines.Add(
                $"  {category,-18} {Pct(OrNull(stats["accuracy"]))} {stats["correct"]!.GetValue<int>(),4}/{n,-4} " +
                $"{stats["parsed"]!.GetValue<int>(),4}/{n,-3} {stats["errors"]!.GetValue<int>(),7}");
        }

        lines.Add($"  {new string('-', 18)} {new string('-', 6)} {new string('-', 9)} {new string('-', 8)} {new string('-', 7)}");
        var totalN = totals["n"]!.GetValue<int>();
        lines.Add(
            $"  {"OVERALL",-18} {Pct(OrNull(totals["accuracy"]))} {totals["correct"]!.GetValue<int>(),4}/{totalN,-4} " +
            $"{totals["parsed"]!.GetValue<int>(),4}/{totalN,-3} {totals["errors"]!.GetValue<int>(),7}");
        lines.Add("");
        lines.Add(
            $"  latency  ttfb p50 {Secs(OrNull(latency["ttfb_p50_s"]))}  p95 {Secs(OrNull(latency["ttfb_p95_s"]))}" +
            $"   turn p50 {Secs(OrNull(latency["turn_p50_s"]))}  p95 {Secs(OrNull(latency["turn_p95_s"]))}");
        if (report["duration_s"] is not null)
        {
            lines.Add($"  wall     {report["duration_s"]!.GetValue<double>() / 60:F1} min");
        }

        return string.Join("\n", lines);
    }

    private static double? OrNull(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    /// <summary>一个范围（总体或类别）在两次运行间的对比行。</summary>
    public sealed record ComparisonRow(string Scope, double? BaselineAccuracy, double? CandidateAccuracy, int BaselineN, int CandidateN)
    {
        public double? Delta { get; init; }
    }

    public sealed class Comparison
    {
        public string BaselineLabel { get; init; } = "";
        public string CandidateLabel { get; init; } = "";
        public List<ComparisonRow> Rows { get; } = [];
        public List<string> Warnings { get; } = [];
        public Dictionary<string, double?> LatencyDelta { get; } = new();
    }

    private static ComparisonRow Row(string scope, JsonObject? baseStats, JsonObject? candStats)
    {
        baseStats ??= new JsonObject();
        candStats ??= new JsonObject();
        var baseAcc = OrNull(baseStats["accuracy"]);
        var candAcc = OrNull(candStats["accuracy"]);
        var row = new ComparisonRow(
            Scope: scope,
            BaselineAccuracy: baseAcc,
            CandidateAccuracy: candAcc,
            BaselineN: baseStats["n"]?.GetValue<int>() ?? 0,
            CandidateN: candStats["n"]?.GetValue<int>() ?? 0);
        if (baseAcc is null || candAcc is null)
        {
            return row;
        }

        return row with { Delta = candAcc - baseAcc };
    }

    /// <summary>对比两次运行，分数与时延差可见，配置不匹配给出警告。</summary>
    public static Comparison CompareReports(JsonObject baseline, JsonObject candidate)
    {
        var comparison = new Comparison
        {
            BaselineLabel = Str(baseline["label"], "baseline"),
            CandidateLabel = Str(candidate["label"], "candidate"),
        };

        var baseSubset = baseline["subset"]?.AsObject() ?? [];
        var candSubset = candidate["subset"]?.AsObject() ?? [];
        if (!IdsEqual(baseSubset["ids"], candSubset["ids"]))
        {
            comparison.Warnings.Add(
                "Runs used different question sets "
                + $"({Str(baseSubset["name"])}/{Str(baseSubset["n"], "0")} vs "
                + $"{Str(candSubset["name"])}/{Str(candSubset["n"], "0")}); accuracies are not comparable.");
        }

        if (Str(baseSubset["revision"]) != Str(candSubset["revision"]))
        {
            comparison.Warnings.Add("Runs used different dataset revisions.");
        }

        if (reportHasJudge(baseline) != reportHasJudge(candidate))
        {
            comparison.Warnings.Add("Only one run used an LLM judge; grading is not like-for-like.");
        }

        var categories = (baseline["by_category"]?.AsObject() ?? [])
            .Select(static pair => pair.Key)
            .Union((candidate["by_category"]?.AsObject() ?? []).Select(static pair => pair.Key))
            .OrderBy(static c => c);
        foreach (var category in categories)
        {
            comparison.Rows.Add(Row(
                category,
                baseline["by_category"]?.AsObject()[category]?.AsObject(),
                candidate["by_category"]?.AsObject()[category]?.AsObject()));
        }

        comparison.Rows.Add(Row("OVERALL", baseline["totals"]?.AsObject(), candidate["totals"]?.AsObject()));

        foreach (var key in (string[])["ttfb_p50_s", "turn_p50_s"])
        {
            var baseValue = OrNull(baseline["latency"]?.AsObject()[key]);
            var candValue = OrNull(candidate["latency"]?.AsObject()[key]);
            comparison.LatencyDelta[key] = baseValue is null || candValue is null ? null : candValue - baseValue;
        }

        return comparison;
    }

    private static bool reportHasJudge(JsonObject report) => report["judge"] is not null;

    private static bool IdsEqual(JsonNode? a, JsonNode? b)
    {
        if (a is not JsonArray || b is not JsonArray)
        {
            return a is null && b is null;
        }

        return a.AsArray().ToJsonString() == b.AsArray().ToJsonString();
    }

    /// <summary>两次运行的人类可读并排对比。</summary>
    public static string RenderComparison(Comparison comparison)
    {
        var lines = new List<string>
        {
            $"Big Bench Audio vibe check -- {comparison.BaselineLabel} -> {comparison.CandidateLabel}",
            "",
            $"  {"category",-18} {"base",6} {"cand",6} {"delta",8}  n (base/cand)",
            $"  {new string('-', 18)} {new string('-', 6)} {new string('-', 6)} {new string('-', 8)}  {new string('-', 24)}",
        };
        foreach (var row in comparison.Rows)
        {
            var delta = row.Delta is null
                ? "   n/a"
                : (row.Delta.Value * 100).ToString("+0.0;-0.0").PadLeft(6) + "pp";
            var separator = $"  {new string('-', 18)} {new string('-', 6)} {new string('-', 6)} {new string('-', 8)}  {new string('-', 24)}";
            if (row.Scope == "OVERALL")
            {
                lines.Add(separator);
            }

            lines.Add(
                $"  {row.Scope,-18} {Pct(row.BaselineAccuracy)} {Pct(row.CandidateAccuracy)} {delta,8}" +
                $"  {row.BaselineN}/{row.CandidateN}");
        }

        var ttfb = comparison.LatencyDelta.GetValueOrDefault("ttfb_p50_s");
        var turn = comparison.LatencyDelta.GetValueOrDefault("turn_p50_s");
        lines.Add("");
        lines.Add(
            $"  latency  ttfb p50 {(ttfb is null ? "n/a" : $"{ttfb:+0.00;-0.00}s")}" +
            $"   turn p50 {(turn is null ? "n/a" : $"{turn:+0.00;-0.00}s")}");
        foreach (var warning in comparison.Warnings)
        {
            lines.Add($"  WARNING: {warning}");
        }

        return string.Join("\n", lines);
    }
}
