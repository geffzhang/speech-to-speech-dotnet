using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SpeechToSpeech.Evals.BigBenchAudio;
using Xunit;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 上游 tests/evals 的 C# 对应物：评分、子集采样（含 CPython MT19937 复刻）、
/// 报告聚合对比、runner 捕获状态机与 CLI 参数面。
/// </summary>
public class BigBenchAudioTests
{
    private static readonly ulong[] Seed0GetrandBits = [1, 1, 97, 3823568514, 746805015404516437];
    private static readonly string[] Seed0Shuffle10 = ["h", "i", "b", "f", "d", "e", "c", "a", "j", "g"];
    private static readonly int[] Seed42Shuffle20 = [19, 5, 14, 4, 9, 13, 15, 18, 6, 12, 17, 10, 1, 11, 2, 16, 7, 8, 0, 3];

    // 参照值由上游 CPython 3.13 random.Random 生成（见 porting/03-acceptance.md 的 D030 小节）。
    [Fact]
    public void PythonRandom_MatchesCpythonGetrandBitsAndShuffle()
    {
        var rng = new PythonRandom(0);
        foreach (var (bits, expected) in new[] { (1, Seed0GetrandBits[0]), (2, Seed0GetrandBits[1]), (7, Seed0GetrandBits[2]), (32, Seed0GetrandBits[3]) })
        {
            Assert.Equal(expected, rng.GetRandBits(bits));
        }

        // 与上游参照脚本同口径：64 位取自同一条流的第 5–6 次抽取。
        Assert.Equal(746805015404516437UL, rng.GetRandBits(64));

        var list10 = new List<string> { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j" };
        new PythonRandom(0).Shuffle(list10);
        Assert.Equal(Seed0Shuffle10, list10);

        var list20 = Enumerable.Range(0, 20).ToList();
        new PythonRandom(42).Shuffle(list20);
        Assert.Equal(Seed42Shuffle20, list20);
    }

    // ── scoring ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("web_of_lies", "Let me trace it. Final answer: No.", "no")]
    [InlineData("web_of_lies", "Osvaldo tells the truth, so my answer is Yes", "yes")]
    [InlineData("navigate", "You circle back. Final answer: Yes", "yes")]
    [InlineData("formal_fallacies", "Final answer: invalid", "invalid")]
    [InlineData("formal_fallacies", "So the argument is deductively valid.", "valid")]
    [InlineData("object_counting", "Final answer: 12", "12")]
    [InlineData("object_counting", "That gives seven instruments.", "7")]
    public void ReplyYieldsItsAnswerToken(string category, string reply, string expected)
    {
        Assert.Equal(expected, Scoring.ExtractAnswer(category, reply).Answer);
    }

    [Fact]
    public void MarkerBeatsTheReasoningThatPrecedesIt()
    {
        var reply = "First it looks valid, and valid again on the second premise. Final answer: invalid";
        Assert.Equal(("invalid", "marker"), Scoring.ExtractAnswer("formal_fallacies", reply));
    }

    [Fact]
    public void ScanWithoutAMarkerTakesTheConcludingClaim()
    {
        var reply = "At first it looked like no, but retracing the turns you do land back at the start. Yes.";
        Assert.Equal(("yes", "scan"), Scoring.ExtractAnswer("navigate", reply));
    }

    [Theory]
    [InlineData("The argument is not valid.")]
    [InlineData("That is not deductively valid.")]
    [InlineData("This isn't valid.")]
    public void NegatedValidReadsAsInvalid(string reply)
    {
        Assert.Equal("invalid", Scoring.ExtractAnswer("formal_fallacies", reply).Answer);
    }

    [Fact]
    public void HedgingWithoutANegationKeepsTheStatedAnswer()
    {
        Assert.Equal("valid", Scoring.ExtractAnswer("formal_fallacies", "I am not sure, but the argument is valid.").Answer);
    }

    [Theory]
    [InlineData("navigate", "The correct approach is to track each turn.")]
    [InlineData("navigate", "You end up facing the negative x direction.")]
    [InlineData("web_of_lies", "")]
    [InlineData("object_counting", "I could not follow the list.")]
    public void RepliesWithoutAnAnswerStayUnparsed(string category, string reply)
    {
        Assert.Equal((null, null), Scoring.ExtractAnswer(category, reply));
    }

    [Fact]
    public void UnknownCategoryIsNeverGuessed()
    {
        Assert.Equal((null, null), Scoring.ExtractAnswer("sports_trivia", "Final answer: Yes"));
    }

    [Fact]
    public void UnparsedReplyGradesAsNeitherRightNorWrong()
    {
        var verdict = Scoring.GradeOne("web_of_lies", "Yes", "Hmm, hard to say.");
        Assert.Null(verdict.Correct);
        Assert.False(verdict.Parsed);
    }

    [Fact]
    public void GradeComparesAgainstTheNormalizedOfficialAnswer()
    {
        Assert.True(Scoring.GradeOne("navigate", "No", "Final answer: no").Correct);
        Assert.False(Scoring.GradeOne("navigate", "No", "Final answer: yes").Correct);
        Assert.True(Scoring.GradeOne("object_counting", "07", "Final answer: seven").Correct);
    }

    [Fact]
    public void NormalizeOfficialMatchesTheExtractorTokenSpace()
    {
        Assert.Equal("invalid", Scoring.NormalizeOfficial("formal_fallacies", "Invalid"));
        Assert.Equal("7", Scoring.NormalizeOfficial("object_counting", "07"));
    }

    // ── dataset ──────────────────────────────────────────────────────

    private const string MetadataFile = "metadata.jsonl";

    /// <summary>上游 conftest 的离线 metadata：四类各 250 条、答案轮换。</summary>
    private static IReadOnlyList<Dataset.EvalItem> WriteBenchmarkMetadata(string revision)
    {
        Assert.Equal(Dataset.DatasetRevision, revision);
        var categories = new[] { "formal_fallacies", "navigate", "object_counting", "web_of_lies" };
        var answers = new[] { new[] { "invalid", "valid" }, new[] { "No", "Yes" }, Enumerable.Range(2, 10).Select(n => n.ToString()).ToArray(), new[] { "No", "Yes" } };
        var records = new List<string>();
        foreach (var (category, choices) in categories.Zip(answers))
        {
            for (var index = 0; index < 250; index++)
            {
                var id = records.Count;
                records.Add(new JsonObject
                {
                    ["id"] = id,
                    ["category"] = category,
                    ["official_answer"] = choices[index % choices.Length],
                    ["file_name"] = $"data/question_{id}.mp3",
                }.ToJsonString());
            }
        }

        var path = Path.Combine(Path.GetTempPath(), "bba_meta_" + Guid.NewGuid().ToString("N"), MetadataFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, records);
        return records.Select(line => Dataset.EvalItem.FromDict(JsonNode.Parse(line)!.AsObject())).ToList();
    }

    private static List<Dataset.EvalItem> FakePool()
    {
        var answers = new Dictionary<string, string[]>
        {
            ["formal_fallacies"] = ["valid", "invalid"],
            ["navigate"] = ["Yes", "No"],
            ["object_counting"] = Enumerable.Range(2, 8).Select(n => n.ToString()).ToArray(),
            ["web_of_lies"] = ["Yes", "No"],
        };
        var pool = new List<Dataset.EvalItem>();
        var itemId = 0;
        foreach (var category in answers.Keys)
        {
            for (var round = 0; round < 30; round++)
            {
                foreach (var answer in answers[category])
                {
                    pool.Add(new Dataset.EvalItem(itemId++, category, answer, $"data/question_{itemId}.mp3"));
                }
            }
        }

        return pool;
    }

    [Fact]
    public void VibeIsPinnedToOneDatasetRevisionAndBalanced()
    {
        Dataset.MetadataOverride = WriteBenchmarkMetadata;
        try
        {
            var subset = Dataset.LoadSubset("vibe");
            Assert.Equal(Dataset.DatasetRepoId, subset.Dataset);
            Assert.Equal(40, subset.Revision.Length);
            Assert.Equal(40, subset.Count);

            var stats = Dataset.Describe(subset);
            Assert.Equal(new Dictionary<string, int>
            {
                ["formal_fallacies"] = 10,
                ["navigate"] = 10,
                ["object_counting"] = 10,
                ["web_of_lies"] = 10,
            }, stats.ByCategory);
            Assert.Equal(new Dictionary<string, int> { ["invalid"] = 5, ["valid"] = 5 }, stats.ByAnswer["formal_fallacies"]);
            Assert.Equal(new Dictionary<string, int> { ["No"] = 5, ["Yes"] = 5 }, stats.ByAnswer["navigate"]);
            Assert.Equal(new Dictionary<string, int> { ["No"] = 5, ["Yes"] = 5 }, stats.ByAnswer["web_of_lies"]);
            Assert.Equal(10, stats.ByAnswer["object_counting"].Count);

            // 跨实现互证：同种子（0）下与上游 Python build_subset 抽出的 item 序列
            // 逐位一致（参照值由上游 CPython 3.13 + 上游源码生成，见差异账 C031）。
            Assert.Equal(
                new[]
                {
                    216, 348, 720, 862, 125, 261, 711, 847, 98, 440, 652, 910, 151, 319, 513, 853,
                    194, 356, 694, 844, 161, 291, 515, 901, 226, 462, 636, 912, 85, 289, 507, 869,
                    106, 416, 548, 884, 217, 399, 579, 785,
                },
                subset.Items.Select(static item => item.Id).ToList());
        }
        finally
        {
            Dataset.MetadataOverride = null;
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void TruncatingTheSubsetKeepsEveryCategoryRepresented(int limit)
    {
        Dataset.MetadataOverride = WriteBenchmarkMetadata;
        try
        {
            var head = Dataset.LoadSubset("vibe").Head(limit);
            Assert.Equal(limit, head.Count);
            foreach (var category in head.Items.GroupBy(static item => item.Category))
            {
                Assert.Equal(limit / 4, category.Count());
            }
        }
        finally
        {
            Dataset.MetadataOverride = null;
        }
    }

    [Fact]
    public void HeadBeyondTheSubsetIsTheWholeSubset()
    {
        var pool = FakePool();
        var subset = Dataset.BuildSubset(40, items: pool);
        Assert.Same(subset, subset.Head(null));
        Assert.Equal(40, subset.Head(1000).Count);
    }

    [Fact]
    public void BuildSubsetBalancesCategoriesAndAnswers()
    {
        var subset = Dataset.BuildSubset(40, items: FakePool(), seed: 7);
        var stats = Dataset.Describe(subset);
        Assert.Equal(10, stats.ByCategory["navigate"]);
        Assert.Equal(5, stats.ByAnswer["navigate"]["No"]);
        Assert.Equal(5, stats.ByAnswer["navigate"]["Yes"]);
        Assert.Equal(40, subset.Items.Select(static item => item.Id).Distinct().Count());
    }

    [Fact]
    public void BuildSubsetIsDeterministicForASeed()
    {
        var ids3 = Dataset.BuildSubset(20, items: FakePool(), seed: 3).Items.Select(static item => item.Id).ToList();
        Assert.Equal(ids3, Dataset.BuildSubset(20, items: FakePool(), seed: 3).Items.Select(static item => item.Id).ToList());
        Assert.NotEqual(ids3, Dataset.BuildSubset(20, items: FakePool(), seed: 4).Items.Select(static item => item.Id).ToList());
    }

    [Fact]
    public void BuildSubsetRejectsASizeThatCannotCoverEveryCategory()
    {
        var exc = Assert.Throws<ArgumentException>(() => Dataset.BuildSubset(3, items: FakePool()));
        Assert.Contains("at least 4", exc.Message);
    }

    [Fact]
    public void SubsetSurvivesAManifestRoundTrip()
    {
        Dataset.MetadataOverride = WriteBenchmarkMetadata;
        try
        {
            var subset = Dataset.LoadSubset("vibe");
            var path = Path.Combine(Path.GetTempPath(), $"subset_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, subset.ToDict().ToJsonString());
            var loaded = Dataset.LoadSubset(path);
            Assert.Equal(subset.Items.Select(static i => i.Id), loaded.Items.Select(static i => i.Id));
            Assert.Equal(subset.Items.Select(static i => i.OfficialAnswer), loaded.Items.Select(static i => i.OfficialAnswer));
        }
        finally
        {
            Dataset.MetadataOverride = null;
        }
    }

    [Fact]
    public void AManifestFromAnUnknownSchemaIsRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"subset_bad_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, new JsonObject { ["schema"] = "something-else", ["name"] = "x", ["items"] = new JsonArray() }.ToJsonString());
        var exc = Assert.Throws<InvalidDataException>(() => Dataset.LoadSubset(path));
        Assert.Contains("Unsupported subset schema", exc.Message);
    }

    [Fact]
    public void MissingSubsetNamesTheBuiltInSelections()
    {
        var exc = Assert.Throws<FileNotFoundException>(() => Dataset.LoadSubset("nonexistent.json"));
        Assert.Contains("full", exc.Message);
        Assert.Contains("vibe", exc.Message);
    }

    // ── report ───────────────────────────────────────────────────────

    [Fact]
    public void AggregateCountsErrorsAgainstAccuracyAndUnparsedAgainstParseRate()
    {
        var results = new List<Report.ItemResult>
        {
            new() { Id = 1, Category = "navigate", OfficialAnswer = "Yes", Reply = "Final answer: Yes", Extracted = "yes", Correct = true, Method = "marker" },
            new() { Id = 2, Category = "navigate", OfficialAnswer = "No", Reply = "Final answer: yes", Extracted = "yes", Correct = false, Method = "marker" },
            new() { Id = 3, Category = "navigate", OfficialAnswer = "No", Reply = "I am lost.", Error = "response_timeout" },
            new() { Id = 4, Category = "object_counting", OfficialAnswer = "7", Reply = "Hmm.", Correct = null },
        };
        var aggregate = Report.Aggregate(results);
        var totals = aggregate["totals"]!.AsObject();
        Assert.Equal(4, totals["n"]!.GetValue<int>());
        Assert.Equal(1, totals["errors"]!.GetValue<int>());
        // 解析数只数 final_extracted 非空的条目：错误条目与「Hmm.」都不算。
        Assert.Equal(2, totals["parsed"]!.GetValue<int>());
        Assert.Equal(1, totals["correct"]!.GetValue<int>());
        Assert.Equal(0.25, totals["accuracy"]!.GetValue<double>(), 12);
        Assert.Equal(0.5, totals["accuracy_parsed"]!.GetValue<double>(), 12);
        Assert.Equal(0.5, totals["parse_rate"]!.GetValue<double>(), 12);
    }

    [Fact]
    public void CompareReportsWarnsOnMismatchedRuns()
    {
        var baseline = new JsonObject
        {
            ["label"] = "base",
            ["subset"] = new JsonObject { ["name"] = "vibe", ["n"] = 4, ["revision"] = "rev1", ["ids"] = new JsonArray(1, 2, 3, 4) },
            ["totals"] = new JsonObject { ["n"] = 4, ["accuracy"] = 0.5 },
            ["by_category"] = new JsonObject { ["navigate"] = new JsonObject { ["n"] = 3, ["accuracy"] = 0.66 } },
            ["latency"] = new JsonObject { ["ttfb_p50_s"] = 1.0 },
        };
        var candidate = new JsonObject
        {
            ["label"] = "cand",
            ["subset"] = new JsonObject { ["name"] = "vibe", ["n"] = 4, ["revision"] = "rev2", ["ids"] = new JsonArray(1, 2, 3, 5) },
            ["totals"] = new JsonObject { ["n"] = 4, ["accuracy"] = 0.75 },
            ["by_category"] = new JsonObject { ["navigate"] = new JsonObject { ["n"] = 3, ["accuracy"] = 0.99 } },
            ["latency"] = new JsonObject { ["ttfb_p50_s"] = 0.8 },
        };
        var comparison = Report.CompareReports(baseline, candidate);
        Assert.Contains(comparison.Warnings, static w => w.Contains("different question sets"));
        Assert.Contains(comparison.Warnings, static w => w.Contains("different dataset revisions"));
        var overall = comparison.Rows.Single(static row => row.Scope == "OVERALL");
        Assert.Equal(0.5, overall.BaselineAccuracy);
        Assert.Equal(0.75, overall.CandidateAccuracy);
        Assert.Equal(0.25, overall.Delta);
        Assert.Equal(-0.2, comparison.LatencyDelta["ttfb_p50_s"]!.Value, 12);
        var rendered = Report.RenderComparison(comparison);
        Assert.Contains("WARNING", rendered);
        Assert.Contains("+25.0pp", rendered);
    }

    [Fact]
    public void FinalVerdictPrefersTheJudgeWhenItRan()
    {
        var judged = new Report.ItemResult { Extracted = "yes", Correct = false, JudgeCompleted = true, JudgeExtracted = "no", JudgeCorrect = true };
        Assert.Equal("no", judged.FinalExtracted);
        Assert.True(judged.FinalCorrect);

        var legacy = new Report.ItemResult { Extracted = "yes", Correct = false, JudgeExtracted = "no", JudgeCorrect = true };
        Assert.Equal("no", legacy.FinalExtracted);

        var unjudged = new Report.ItemResult { Extracted = "yes", Correct = true };
        Assert.Equal("yes", unjudged.FinalExtracted);
        Assert.True(unjudged.FinalCorrect);
    }

    // ── CLI ──────────────────────────────────────────────────────────

    [Fact]
    public void RedactServerArgsKeepsSettingsButNotCredentials()
    {
        var redacted = BigBenchAudioCli.RedactServerArgs(
            ["serve", "--api_key", "sk-secret", "--openai_api_key=xyz", "--model", "m", "--spawn-log", "log.txt"]);
        Assert.Equal(
            ["serve", "--api_key", "<redacted>", "--openai_api_key=<redacted>", "--model", "m", "--spawn-log", "log.txt"],
            redacted);
    }

    [Fact]
    public void UrlFromServerArgsFollowsHostAndPortForms()
    {
        Assert.Equal("ws://127.0.0.1:8765/v1/realtime", BigBenchAudioCli.UrlFromServerArgs([]));
        Assert.Equal("ws://127.0.0.1:8000/v1/realtime", BigBenchAudioCli.UrlFromServerArgs(["serve", "--port", "8000"]));
        Assert.Equal("ws://127.0.0.1:9000/v1/realtime", BigBenchAudioCli.UrlFromServerArgs(["serve", "--port=9000"]));
        Assert.Equal("ws://127.0.0.1:8765/v1/realtime", BigBenchAudioCli.UrlFromServerArgs(["--host", "0.0.0.0"]));
        Assert.Equal("ws://10.0.0.2:7000/v1/realtime", BigBenchAudioCli.UrlFromServerArgs(["--host=10.0.0.2", "--port=7000"]));
    }

    [Fact]
    public void SplitServerArgsSplitsOnDoubleDash()
    {
        var (evalArgs, serverArgs) = BigBenchAudioCli.SplitServerArgs(["run", "--limit", "5", "--", "serve", "--port", "9000"]);
        Assert.Equal(["run", "--limit", "5"], evalArgs);
        Assert.Equal(["serve", "--port", "9000"], serverArgs);
        var (noServer, empty) = BigBenchAudioCli.SplitServerArgs(["run", "--limit", "5"]);
        Assert.Equal(["run", "--limit", "5"], noServer);
        Assert.Empty(empty);
    }

    [Fact]
    public void BareFlagsDefaultToTheRunCommand()
    {
        Assert.Equal(["run", "--limit", "5"], BigBenchAudioCli.NormalizeEvalArgs(["--limit", "5"]));
        Assert.Equal(["run"], BigBenchAudioCli.NormalizeEvalArgs([]));
        Assert.Equal(["compare", "a.json", "b.json"], BigBenchAudioCli.NormalizeEvalArgs(["compare", "a.json", "b.json"]));
        Assert.Equal(["-h"], BigBenchAudioCli.NormalizeEvalArgs(["-h"]));
    }

    // ── runner ───────────────────────────────────────────────────────

    private sealed class FakeSocket : IRunnerSocket
    {
        public BlockingCollection<string> Inbox { get; } = new();
        public List<string> Sent { get; } = [];

        public void Enqueue(params string[] events)
        {
            foreach (var evt in events)
            {
                Inbox.Add(evt);
            }
        }

        public Task SendAsync(string text)
        {
            lock (Sent)
            {
                Sent.Add(text);
            }

            return Task.CompletedTask;
        }

        public Task<string?> ReceiveAsync(TimeSpan timeout) =>
            Task.FromResult(Inbox.TryTake(out var message, timeout) ? message : null);

        public void Dispose() => Inbox.Dispose();
    }

    private static Runner.RunnerConfig FastConfig(string url = "ws://127.0.0.1:8765/v1/realtime") => new()
    {
        Url = url,
        Speed = 1000,
        ChunkMs = 20,
        TrailingSilenceMs = 40,
        ResponseTimeoutS = 5,
        GraceS = 0.3,
        ConnectTimeoutS = 2,
    };

    [Fact]
    public void BuildSessionUpdateRaisesSilenceAndAppliesVoice()
    {
        var update = Runner.BuildSessionUpdate(new Runner.RunnerConfig { SilenceDurationMs = 900, Voice = "tongtong" });
        Assert.Equal("session.update", update["type"]!.GetValue<string>());
        var session = update["session"]!.AsObject();
        Assert.Equal("realtime", session["type"]!.GetValue<string>());
        Assert.Contains("Final answer", session["instructions"]!.GetValue<string>());
        var turnDetection = session["audio"]!.AsObject()["input"]!.AsObject()["turn_detection"]!.AsObject();
        Assert.Equal("server_vad", turnDetection["type"]!.GetValue<string>());
        Assert.Equal(900, turnDetection["silence_duration_ms"]!.GetValue<int>());
        Assert.Equal("tongtong", session["audio"]!.AsObject()["output"]!.AsObject()["voice"]!.GetValue<string>());
    }

    [Fact]
    public void IterChunksPadsTheLastChunk()
    {
        var chunks = Runner.IterChunks([1, 2, 3, 4, 5], 4).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(4, chunks[0].Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, chunks[0]);
        Assert.Equal(new byte[] { 5, 0, 0, 0 }, chunks[1]);
    }

    [Fact]
    public async Task ConsumeCapturesTranscriptAudioAndTimings()
    {
        var socket = new FakeSocket();
        socket.Enqueue(
            new JsonObject { ["type"] = "input_audio_buffer.speech_started", ["item_id"] = "item-1" }.ToJsonString(),
            new JsonObject { ["type"] = "conversation.item.input_audio_transcription.completed", ["transcript"] = "question one" }.ToJsonString(),
            new JsonObject { ["type"] = "response.created" }.ToJsonString(),
            new JsonObject { ["type"] = "response.output_audio.delta", ["delta"] = Convert.ToBase64String(new byte[4]) }.ToJsonString(),
            new JsonObject { ["type"] = "response.output_audio_transcript.delta", ["delta"] = "Final answer: " }.ToJsonString(),
            new JsonObject { ["type"] = "response.output_audio_transcript.delta", ["delta"] = "yes" }.ToJsonString(),
            new JsonObject { ["type"] = "response.done", ["response"] = new JsonObject { ["status"] = "completed" } }.ToJsonString());

        var capture = new Runner.TurnCapture();
        var audioSent = new AsyncEvent();
        audioSent.Set();
        await Runner.StreamAudioAsync(socket, new byte[10_000], FastConfig(), capture, audioSent, CancellationToken.None);
        await Runner.ConsumeAsync(socket, FastConfig(), capture, audioSent);

        var item = new Dataset.EvalItem(1, "navigate", "Yes", "data/question_1.mp3");
        var result = Runner.FinishCapture(item, capture);
        Assert.Null(result.Error);
        Assert.Equal("question one", result.InputTranscript);
        Assert.Equal("Final answer: yes", result.Reply);
        Assert.Equal(4, result.AudioOutBytes);
        Assert.True(result.TtfbS is > 0);
        Assert.True(result.TurnS is > 0 && result.TurnS >= result.TtfbS);
        Assert.Equal("yes", result.Extracted);
        Assert.True(result.Correct);
        Assert.Equal("marker", result.Method);
    }

    [Fact]
    public async Task TwoInputItemsAfterAResponseMarkASplitTurn()
    {
        var socket = new FakeSocket();
        socket.Enqueue(
            new JsonObject { ["type"] = "input_audio_buffer.speech_started", ["item_id"] = "item-1" }.ToJsonString(),
            new JsonObject { ["type"] = "response.created" }.ToJsonString(),
            new JsonObject { ["type"] = "response.done", ["response"] = new JsonObject { ["status"] = "completed" } }.ToJsonString(),
            new JsonObject { ["type"] = "input_audio_buffer.speech_started", ["item_id"] = "item-2" }.ToJsonString());

        var capture = new Runner.TurnCapture();
        var audioSent = new AsyncEvent();
        audioSent.Set();
        await Runner.ConsumeAsync(socket, FastConfig(), capture, audioSent);
        Assert.Equal("split_turn (2 input items)", capture.Error);
    }

    [Fact]
    public async Task RepeatedItemIdIsSpeculativeReopenNotASplit()
    {
        var socket = new FakeSocket();
        socket.Enqueue(
            new JsonObject { ["type"] = "input_audio_buffer.speech_started", ["item_id"] = "item-1" }.ToJsonString(),
            new JsonObject { ["type"] = "input_audio_buffer.speech_started", ["item_id"] = "item-1" }.ToJsonString(),
            new JsonObject { ["type"] = "response.created" }.ToJsonString(),
            new JsonObject { ["type"] = "response.done", ["response"] = new JsonObject { ["status"] = "completed" } }.ToJsonString());

        var capture = new Runner.TurnCapture();
        var audioSent = new AsyncEvent();
        audioSent.Set();
        await Runner.ConsumeAsync(socket, FastConfig(), capture, audioSent);
        Assert.Null(capture.Error);
        Assert.NotNull(capture.Last);
    }

    [Fact]
    public async Task NonCompletedStatusBecomesAPrefixedError()
    {
        var socket = new FakeSocket();
        socket.Enqueue(
            new JsonObject { ["type"] = "response.created" }.ToJsonString(),
            new JsonObject { ["type"] = "response.done", ["response"] = new JsonObject { ["status"] = "cancelled" } }.ToJsonString());

        var capture = new Runner.TurnCapture();
        var audioSent = new AsyncEvent();
        audioSent.Set();
        await Runner.ConsumeAsync(socket, FastConfig(), capture, audioSent);
        Assert.Equal("response_cancelled", capture.Error);
    }

    [Fact]
    public async Task ErrorEventCarriesTheServerMessage()
    {
        var socket = new FakeSocket();
        socket.Enqueue(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["message"] = "boom" } }.ToJsonString());

        var capture = new Runner.TurnCapture();
        var audioSent = new AsyncEvent();
        audioSent.Set();
        await Runner.ConsumeAsync(socket, FastConfig(), capture, audioSent);
        Assert.Equal("boom", capture.Error);
    }

    [Fact]
    public async Task TimeoutWithoutAResponseMarksResponseTimeout()
    {
        var config = new Runner.RunnerConfig
        {
            Speed = 1000,
            ResponseTimeoutS = 0.3,
            GraceS = 0.1,
            ConnectTimeoutS = 2,
        };
        var socket = new FakeSocket(); // 无事件
        var capture = new Runner.TurnCapture();
        var audioSent = new AsyncEvent();
        audioSent.Set();
        await Runner.ConsumeAsync(socket, config, capture, audioSent);
        Assert.Equal("response_timeout", capture.Error);
    }

    [Fact]
    public async Task OpenSessionHandshakesAndAppliesTheUpdate()
    {
        var socket = new FakeSocket();
        socket.Enqueue(
            new JsonObject { ["type"] = "session.created" }.ToJsonString(),
            new JsonObject { ["type"] = "session.updated" }.ToJsonString());
        Uri? seenUrl = null;
        var ws = await Runner.OpenSessionAsync(FastConfig(), (url, _) =>
        {
            seenUrl = url;
            return socket;
        });

        Assert.Equal("ws://127.0.0.1:8765/v1/realtime", seenUrl!.ToString());
        Assert.Single(socket.Sent);
        Assert.Contains("session.update", socket.Sent[0]);
        Assert.Contains("Final answer", socket.Sent[0]);
        ws.Dispose();
    }

    [Fact]
    public async Task OpenSessionRetriesOnSessionLimitReached()
    {
        var rejected = new FakeSocket();
        rejected.Enqueue(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "session_limit_reached", ["message"] = "busy" } }.ToJsonString());
        var accepted = new FakeSocket();
        accepted.Enqueue(
            new JsonObject { ["type"] = "session.created" }.ToJsonString(),
            new JsonObject { ["type"] = "session.updated" }.ToJsonString());
        var queue = new ConcurrentQueue<FakeSocket>([rejected, accepted]);

        var ws = await Runner.OpenSessionAsync(FastConfig(), (_, _) => queue.TryDequeue(out var socket) ? socket : throw new InvalidOperationException("no more fake sockets"));
        Assert.Same(accepted, ws);
        ws.Dispose();
    }

    [Fact]
    public async Task OpenSessionRejectsOtherHandshakeErrorsImmediately()
    {
        var rejected = new FakeSocket();
        rejected.Enqueue(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "invalid_api_key", ["message"] = "bad key" } }.ToJsonString());
        var exc = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runner.OpenSessionAsync(FastConfig(), (_, _) => rejected));
        Assert.Equal("bad key", exc.Message);
    }

    [Fact]
    public async Task OpenSessionReportsARejectedConfiguration()
    {
        var socket = new FakeSocket();
        socket.Enqueue(
            new JsonObject { ["type"] = "session.created" }.ToJsonString(),
            new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["message"] = "no such voice" } }.ToJsonString());
        var exc = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runner.OpenSessionAsync(FastConfig(), (_, _) => socket));
        Assert.Contains("session configuration rejected", exc.Message);
    }

    [Fact]
    public async Task RunItemEndToEndWithAFakeServer()
    {
        var socket = new FakeSocket();
        socket.Enqueue(
            new JsonObject { ["type"] = "session.created" }.ToJsonString(),
            new JsonObject { ["type"] = "session.updated" }.ToJsonString(),
            new JsonObject { ["type"] = "input_audio_buffer.speech_started", ["item_id"] = "item-1" }.ToJsonString(),
            new JsonObject { ["type"] = "response.created" }.ToJsonString(),
            new JsonObject { ["type"] = "response.output_audio_transcript.delta", ["delta"] = "Final answer: " }.ToJsonString(),
            new JsonObject { ["type"] = "response.output_audio_transcript.delta", ["delta"] = "yes" }.ToJsonString(),
            new JsonObject { ["type"] = "response.output_audio.delta", ["delta"] = Convert.ToBase64String(new byte[4]) }.ToJsonString(),
            new JsonObject { ["type"] = "response.done", ["response"] = new JsonObject { ["status"] = "completed" } }.ToJsonString());

        var config = FastConfig();
        var item = new Dataset.EvalItem(7, "navigate", "Yes", "data/question_7.mp3");
        var pcm = new byte[config.ChunkBytes * 3]; // 3 个 20 ms 块
        var result = await Runner.RunItemAsync(config, item, pcm, (_, _) => socket);

        Assert.Null(result.Error);
        Assert.Equal("yes", result.Extracted);
        Assert.True(result.Correct);
        // 题目 3 块 + 尾部静音 2 块，全部是定长 append。
        var appends = socket.Sent.Where(static text => text.Contains("input_audio_buffer.append")).ToList();
        Assert.Equal(5, appends.Count);
        foreach (var append in appends)
        {
            var audio = ((JsonObject)JsonNode.Parse(append)!)["audio"]!.GetValue<string>();
            Assert.Equal(config.ChunkBytes, Convert.FromBase64String(audio).Length);
        }
    }
}
