using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SpeechToSpeech.Evals.BigBenchAudio;

/// <summary>
/// 运行报告的存取与命名（上游 <c>hub.py</c>）。
///
/// C 端只移植本地与只读路径：报告是纯 JSON 文件，落本地即可用 <c>compare</c> 对比；
/// 上传 HF 的云工作流（push-to-hub / HF Jobs）不移植，见差异账。
/// </summary>
public static class Hub
{
    private static readonly Regex Unsafe = new("[^A-Za-z0-9._-]+", RegexOptions.Compiled);

    /// <summary>把运行标签压成可用于 repo 路径的安全形式。</summary>
    public static string Slugify(string text)
    {
        var slug = Unsafe.Replace(text, "-").Trim('-');
        return slug.Length > 80 ? slug[..80] : slug.Length == 0 ? "run" : slug;
    }

    /// <summary>报告在数据集 repo 内的路径（时间序命名，标签保留在名字里）。</summary>
    public static string ReportFilename(JsonObject report, string prefix = "reports")
    {
        var stamp = report["started_at"]?.GetValue<string>();
        var moment = DateTimeOffset.UtcNow;
        if (stamp is not null && DateTimeOffset.TryParse(stamp, out var parsed))
        {
            moment = parsed.ToUniversalTime();
        }

        var label = Slugify(report["label"]?.GetValue<string>() ?? "run");
        return $"{prefix}/{moment:yyyyMMdd'T'HHmmss}-{label}.json";
    }

    /// <summary>
    /// 读报告：本地路径，或 <c>&lt;repo_id&gt;:&lt;path&gt;</c>（从 Hub 下载，只读）。
    /// </summary>
    public static JsonObject LoadReport(string source)
    {
        if (File.Exists(source))
        {
            return JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        }

        var separator = source.IndexOf(':');
        if (separator <= 0 || separator == source.Length - 1)
        {
            throw new FileNotFoundException($"No report at '{source}' (expected a path, or '<repo_id>:<path-in-repo>').");
        }

        var repoId = source[..separator];
        var pathInRepo = source[(separator + 1)..];
        var endpoint = Environment.GetEnvironmentVariable("HF_ENDPOINT") ?? "https://huggingface.co";
        using var client = new HttpClient();
        if (Environment.GetEnvironmentVariable("HF_TOKEN") is { Length: > 0 } token)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        using var response = client.GetAsync($"{endpoint.TrimEnd('/')}/datasets/{repoId}/resolve/main/{pathInRepo}").GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
    }
}
