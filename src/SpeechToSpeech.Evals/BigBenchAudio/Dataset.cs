using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;
using SpeechToSpeech.Core.Audio;

namespace SpeechToSpeech.Evals.BigBenchAudio;

/// <summary>
/// 钉死版本的 Big Bench Audio 子集与其指向的音频（上游 <c>dataset.py</c>）。
///
/// vibe 样本用固定种子 + 钉死的修订；full 跑加载该修订的全部 metadata；
/// 自定义样本可存成 JSON manifest。metadata 与音频从 HF Hub 拉取并落缓存
/// （端点遵循 <c>HF_ENDPOINT</c>，与 huggingface_hub 的惯例一致）。
/// </summary>
public static class Dataset
{
    public const string DatasetRepoId = "ArtificialAnalysis/big_bench_audio";

    /// <summary>钉住修订，保证每次运行都从同一批 1000 条录音中选题。</summary>
    public const string DatasetRevision = "af7bb9c25b015792583ca4da3ee27ec62cb79fe6";

    public const string DatasetMetadataFile = "metadata.jsonl";
    public const string SubsetSchema = "big-bench-audio-subset/1";
    public const string DefaultSubset = "vibe";
    public const int SampleRateHz = 16000;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var endpoint = Environment.GetEnvironmentVariable("HF_ENDPOINT") ?? "https://huggingface.co";
        var token = Environment.GetEnvironmentVariable("HF_TOKEN");
        var client = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("speech-to-speech-dotnet-evals/1.0");
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        return client;
    }

    private static string CacheDir()
    {
        var root = Environment.GetEnvironmentVariable("HF_HUB_CACHE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "speech-to-speech-evals");
        return root;
    }

    public sealed record EvalItem(int Id, string Category, string OfficialAnswer, string FileName)
    {
        public static EvalItem FromDict(JsonObject raw) => new(
            Id: raw["id"]?.GetValue<int>() ?? throw new JsonException("EvalItem requires int id"),
            Category: Str(raw["category"]),
            OfficialAnswer: Str(raw["official_answer"]),
            FileName: Str(raw["file_name"]).Replace("\\/", "/"));

        public JsonObject ToDict() => new()
        {
            ["id"] = Id,
            ["category"] = Category,
            ["official_answer"] = OfficialAnswer,
            ["file_name"] = FileName,
        };

        private static string Str(JsonNode? node) => node?.GetValue<string>() ?? throw new JsonException("expected string field");
    }

    /// <summary>一个命名的、钉住修订的数据集选题。</summary>
    public sealed record Subset(
        string Name,
        IReadOnlyList<EvalItem> Items,
        string Dataset = DatasetRepoId,
        string Revision = DatasetRevision,
        string Description = "",
        int? Seed = null)
    {
        public int Count => Items.Count;

        /// <summary>保留选择中已有顺序的前 limit 项。</summary>
        public Subset Head(int? limit)
        {
            if (limit is <= 0)
            {
                throw new ArgumentException("limit must be positive", nameof(limit));
            }

            if (limit is null || limit >= Items.Count)
            {
                return this;
            }

            return this with { Items = Items.Take(limit.Value).ToList() };
        }

        public JsonObject ToDict() => new()
        {
            ["schema"] = SubsetSchema,
            ["name"] = Name,
            ["description"] = Description,
            ["dataset"] = Dataset,
            ["revision"] = Revision,
            ["seed"] = Seed,
            ["items"] = new JsonArray(Items.Select(static item => item.ToDict()).ToArray()),
        };

        public static Subset FromDict(JsonObject raw)
        {
            var schema = Str(raw["schema"]);
            if (schema != SubsetSchema)
            {
                throw new InvalidDataException($"Unsupported subset schema '{schema}'; expected '{SubsetSchema}'.");
            }

            return new Subset(
                Name: Str(raw["name"]),
                Items: raw["items"]!.AsArray().Select(node => EvalItem.FromDict(node!.AsObject())).ToList(),
                Dataset: raw["dataset"] is JsonValue ds ? ds.GetValue<string>() : DatasetRepoId,
                Revision: raw["revision"] is JsonValue rev ? rev.GetValue<string>() : DatasetRevision,
                Description: raw["description"] is JsonValue desc ? desc.GetValue<string>() : "",
                Seed: raw["seed"] is JsonValue seed && seed.TryGetValue<int>(out var s) ? s : null);
        }

        private static string Str(JsonNode? node) => node?.GetValue<string>() ?? throw new JsonException("expected string field");
    }

    /// <summary>内建选题的名字。</summary>
    public static IReadOnlyList<string> AvailableSubsets() => ["full", "vibe"];

    /// <summary>从钉死的数据集中选，或按路径加载自定义 manifest。</summary>
    public static Subset LoadSubset(string nameOrPath = DefaultSubset)
    {
        switch (nameOrPath)
        {
            case "vibe":
                return BuildSubset(40, seed: 0);
            case "full":
                return new Subset(
                    Name: "full",
                    Items: LoadDatasetMetadata(),
                    Description: "All questions from the pinned Big Bench Audio dataset in upstream order.");
            default:
                if (!File.Exists(nameOrPath))
                {
                    throw new FileNotFoundException(
                        $"No subset '{nameOrPath}'. Available selections: {string.Join(", ", AvailableSubsets())}.");
                }

                return Subset.FromDict(JsonNode.Parse(File.ReadAllText(nameOrPath))!.AsObject());
        }
    }

    /// <summary>整数答案按数值排，其余按字母排（上游 _answer_sort_key）。</summary>
    private static int CompareAnswers(string a, string b)
    {
        var aIsInt = int.TryParse(a, out var av);
        var bIsInt = int.TryParse(b, out var bv);
        if (aIsInt && bIsInt)
        {
            return av.CompareTo(bv);
        }

        if (aIsInt != bIsInt)
        {
            return aIsInt ? -1 : 1;
        }

        return string.CompareOrdinal(a, b);
    }

    /// <summary>测试缝：覆盖 metadata 来源（上游测试 monkeypatch hf_hub_download 的对应物）。</summary>
    internal static Func<string, IReadOnlyList<EvalItem>>? MetadataOverride { get; set; }

    /// <summary>下载并解析数据集的 metadata.jsonl（一个小文件，不含音频）。</summary>
    public static List<EvalItem> LoadDatasetMetadata(string revision = DatasetRevision)
    {
        if (MetadataOverride is { } loader)
        {
            return [.. loader(revision)];
        }

        var path = HfHubDownload(DatasetRepoId, DatasetMetadataFile, revision);
        var items = new List<EvalItem>();
        foreach (var line in File.ReadLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                items.Add(EvalItem.FromDict(JsonNode.Parse(line)!.AsObject()));
            }
        }

        return items;
    }

    /// <summary>
    /// 从数据集中抽 *size* 条的均衡子集（上游 <c>build_subset</c>）。
    ///
    /// 双重均衡：四个类别各占等额，类别内再按官方答案等额分——让只会说「No」的模型
    /// 无法靠运气拿 50%。条目按类别轮转输出，截断 manifest 也不会破坏均衡。
    /// 抽样用 CPython 的 MT19937（<see cref="PythonRandom"/>），与上游同种子同选题。
    /// </summary>
    public static Subset BuildSubset(
        int size,
        string name = DefaultSubset,
        string description = "",
        int seed = 0,
        string revision = DatasetRevision,
        IReadOnlyList<EvalItem>? items = null)
    {
        var pool = items ?? LoadDatasetMetadata(revision);
        if (pool.Count == 0)
        {
            throw new InvalidOperationException("Dataset metadata is empty.");
        }

        var categories = pool.Select(static item => item.Category).Distinct().OrderBy(static c => c).ToList();
        if (size < categories.Count)
        {
            throw new ArgumentException($"size must be at least {categories.Count} to cover every category.");
        }

        var rng = new PythonRandom(seed);
        var buckets = new Dictionary<string, Dictionary<string, List<EvalItem>>>();
        foreach (var item in pool)
        {
            if (!buckets.TryGetValue(item.Category, out var byAnswer))
            {
                byAnswer = [];
                buckets[item.Category] = byAnswer;
            }

            if (!byAnswer.TryGetValue(item.OfficialAnswer, out var list))
            {
                list = [];
                byAnswer[item.OfficialAnswer] = list;
            }

            list.Add(item);
        }

        var perCategory = new Dictionary<string, List<EvalItem>>();
        for (var index = 0; index < categories.Count; index++)
        {
            var category = categories[index];
            // 余数摊给前面的类别，让总数正好落在 size。
            var quota = size / categories.Count + (index < size % categories.Count ? 1 : 0);
            var answers = buckets[category].Keys
                .OrderBy(static answer => answer, Comparer<string>.Create(CompareAnswers))
                .ToList();
            var queues = new Dictionary<string, List<EvalItem>>();
            foreach (var answer in answers)
            {
                var queue = buckets[category][answer].OrderBy(static item => item.Id).ToList();
                rng.Shuffle(queue);
                queues[answer] = queue;
            }

            var picked = new List<EvalItem>();
            while (picked.Count < quota)
            {
                var drained = true;
                foreach (var answer in answers)
                {
                    if (picked.Count == quota)
                    {
                        break;
                    }

                    if (queues[answer].Count > 0)
                    {
                        // 上游 pop() 从队尾出（LIFO）。
                        var queue = queues[answer];
                        picked.Add(queue[^1]);
                        queue.RemoveAt(queue.Count - 1);
                        drained = false;
                    }
                }

                if (drained)
                {
                    throw new InvalidOperationException($"Category '{category}' has fewer than {quota} items.");
                }
            }

            perCategory[category] = picked;
        }

        var ordered = new List<EvalItem>();
        var remaining = new SortedDictionary<string, List<EvalItem>>(perCategory.ToDictionary(
            static pair => pair.Key,
            static pair => new List<EvalItem>(pair.Value)));
        while (remaining.Count > 0)
        {
            foreach (var category in remaining.Keys.ToList())
            {
                var queue = remaining[category];
                ordered.Add(queue[0]);
                queue.RemoveAt(0);
                if (queue.Count == 0)
                {
                    remaining.Remove(category);
                }
            }
        }

        return new Subset(
            Name: name,
            Items: ordered,
            Revision: revision,
            Description: description,
            Seed: seed);
    }

    /// <summary>条目录音的本地路径，必要时先下载进缓存。</summary>
    public static string ResolveAudio(EvalItem item, Subset subset) =>
        HfHubDownload(subset.Dataset, item.FileName, subset.Revision);

    private static string HfHubDownload(string repoId, string fileName, string revision)
    {
        if (fileName.Contains(".."))
        {
            throw new ArgumentException($"Unsafe dataset path '{fileName}'");
        }

        var cached = Path.Combine(CacheDir(), repoId, revision, fileName);
        if (File.Exists(cached))
        {
            return cached;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        var url = $"datasets/{repoId}/resolve/{revision}/{fileName}";
        using var response = Http.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var stream = response.Content.ReadAsStream();
        using var file = File.Create(cached);
        stream.CopyTo(file);
        return cached;
    }

    /// <summary>把任意数据集录音解码成单声道小端 PCM16（上游 soundfile + resample_poly 的对应物）。</summary>
    /// <remarks>Mp3FileReader（NAudio Windows 包）走 MediaFoundation 解码，仅 Windows 可用。</remarks>
    [SupportedOSPlatform("windows")]
    public static byte[] LoadPcm16Mono(string path, int rate = SampleRateHz)
    {
        using var reader = new Mp3FileReader(path);
        var sampleProvider = reader.ToSampleProvider();
        var channels = reader.WaveFormat.Channels;
        var sourceRate = reader.WaveFormat.SampleRate;

        // 交织样本按声道平均（上游 always_2d + mean(axis=1) 的对应物）。
        var mono = new List<float>();
        var buffer = new float[channels * 4096];
        int read;
        while ((read = sampleProvider.Read(buffer)) > 0)
        {
            var frames = read / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                double sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += buffer[frame * channels + channel];
                }

                mono.Add((float)(sum / channels));
            }
        }

        var samples = mono.ToArray();
        if (sourceRate != rate)
        {
            // 与管线同一内核（WDL sinc，C030）：上游是 scipy resample_poly，
            // 重采样内核差异记入差异账。
            var resampler = new SpeechToSpeech.Backends.OpenAiCompatible.StreamingStt.StreamingResampler(sourceRate, rate);
            samples = [.. resampler.Push(samples), .. resampler.FinishUtterance()];
        }

        // 上游 (clip(-1,1) * 32767).astype("<i2")——astype 是向零截断，不是四舍五入。
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f) * 32767f;
            var value = (short)Math.Clamp((int)clamped, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)value;
            pcm[i * 2 + 1] = (byte)((int)value >> 8);
        }

        return pcm;
    }

    /// <summary>按类别的条数统计，供 --dry-run 与报告头使用。</summary>
    public sealed class SubsetStats
    {
        public int Total { get; set; }
        public Dictionary<string, int> ByCategory { get; } = new();
        public Dictionary<string, Dictionary<string, int>> ByAnswer { get; } = new();
    }

    public static SubsetStats Describe(Subset subset)
    {
        var stats = new SubsetStats { Total = subset.Count };
        foreach (var item in subset.Items)
        {
            stats.ByCategory[item.Category] = stats.ByCategory.GetValueOrDefault(item.Category) + 1;
            if (!stats.ByAnswer.TryGetValue(item.Category, out var answers))
            {
                answers = [];
                stats.ByAnswer[item.Category] = answers;
            }

            answers[item.OfficialAnswer] = answers.GetValueOrDefault(item.OfficialAnswer) + 1;
        }

        return stats;
    }
}
