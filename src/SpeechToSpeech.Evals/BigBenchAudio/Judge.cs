using System.Text;
using System.Text.Json.Nodes;

namespace SpeechToSpeech.Evals.BigBenchAudio;

/// <summary>
/// 规则提取器读不出来的回复走可选的 LLM judge（上游 <c>judge.py</c>）。
///
/// judge 只负责**抽取**答案 token；对错仍由与数据集的字符串比较决定。
/// 让 judge 同时裁对错会让两次运行之间产生漂移——这正是对比 harness 承受不起的。
/// </summary>
public static class Judge
{
    private static readonly Dictionary<string, string> Formats = new()
    {
        ["formal_fallacies"] = "the single word `valid` or `invalid`",
        ["navigate"] = "the single word `Yes` or `No`",
        ["web_of_lies"] = "the single word `Yes` or `No`",
        ["object_counting"] = "a single whole number, digits only",
    };

    private const string SystemPrompt =
        "You extract the final answer from a transcript of someone answering a reasoning question "
        + "out loud. Reply with the answer only: no punctuation, no explanation, no restating the "
        + "question. If the transcript never commits to an answer, reply with exactly UNPARSED.";

    /// <summary>judge 模型的可达位置。</summary>
    public sealed record JudgeConfig(
        string Model,
        string? BaseUrl = null,
        string? ApiKey = null,
        double TimeoutS = 60.0,
        int Concurrency = 4)
    {
        public JsonObject ToDict() => new()
        {
            ["model"] = Model,
            ["base_url"] = BaseUrl,
        };
    }

    private static string UserPrompt(Report.ItemResult result)
    {
        var format = Formats.GetValueOrDefault(result.Category, "the answer only");
        return $"Question category: {result.Category}\nAnswer format: {format}\n\nTranscript:\n{result.Reply}\n\nFinal answer:";
    }

    private static string? NormalizeReply(string category, string text)
    {
        var cleaned = text.Trim().Trim('.').Trim();
        if (cleaned.Length == 0 || cleaned.ToUpperInvariant() == "UNPARSED")
        {
            return null;
        }

        if (Scoring.CategorySpecs.TryGetValue(category, out var spec) && spec.Kind == Scoring.AnswerKind.Integer)
        {
            var digits = new string(cleaned.Where(char.IsDigit).ToArray());
            return digits.Length > 0 ? digits : null;
        }

        return cleaned.ToLowerInvariant();
    }

    /// <summary>给每条已回答的结果就地填上 judge_extracted / judge_correct。</summary>
    public static async Task JudgeResultsAsync(
        IReadOnlyList<Report.ItemResult> results,
        JudgeConfig config,
        HttpClient? httpClient = null)
    {
        using var client = httpClient ?? new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(config.TimeoutS);
        if (!string.IsNullOrEmpty(config.BaseUrl))
        {
            // BaseUrl 形如 https://host/v1（OpenAI 兼容惯例）。
            client.BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/");
        }

        client.DefaultRequestHeaders.Authorization = new("Bearer", config.ApiKey ?? "not-needed");
        var gate = new SemaphoreSlim(Math.Max(1, config.Concurrency));

        await Task.WhenAll(results.Select(result => JudgeOneAsync(result)));

        async Task JudgeOneAsync(Report.ItemResult result)
        {
            if (string.IsNullOrWhiteSpace(result.Reply))
            {
                return;
            }

            string raw;
            try
            {
                await gate.WaitAsync();
                try
                {
                    var body = new JsonObject
                    {
                        ["model"] = config.Model,
                        ["messages"] = new JsonArray(
                            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                            new JsonObject { ["role"] = "user", ["content"] = UserPrompt(result) }),
                        ["temperature"] = 0,
                        ["max_tokens"] = 16,
                    };
                    using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync("chat/completions", content);
                    response.EnsureSuccessStatusCode();
                    var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
                    var choices = payload["choices"]?.AsArray();
                    var message = choices is { Count: > 0 } ? choices[0]!.AsObject()["message"]?.AsObject() : null;
                    raw = message?["content"]?.GetValue<string>() ?? "";
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (Exception exc)
            {
                // judge 故障不能作废整次运行。
                Console.Error.WriteLine($"judge failed for item {result.Id}: {exc.Message}");
                return;
            }

            var extracted = NormalizeReply(result.Category, raw);
            if (extracted is null && raw.Trim().Trim('.').Trim().ToUpperInvariant() != "UNPARSED")
            {
                return;
            }

            result.JudgeCompleted = true;
            result.JudgeExtracted = extracted;
            result.JudgeCorrect = extracted is not null
                ? extracted == Scoring.NormalizeOfficial(result.Category, result.OfficialAnswer)
                : null;
        }
    }
}
