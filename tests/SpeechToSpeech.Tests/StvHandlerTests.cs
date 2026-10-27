using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SpeechToSpeech.Backends.OpenLipSync;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Pipeline.Handlers;
using SpeechToSpeech.Realtime.Protocol;

namespace SpeechToSpeech.Tests;

/// <summary>
/// D032：口型级接线。
///
/// 这一级本身不做推理，它负责**什么时候算、算完怎么发**——上游把这三条写死在
/// <c>Wav2Vec2STVHandler</c> 里，每条都对应一种只有接上客户端才看得见的错：
/// 窗口没攒满就推理（口型早到、与音频错位）、偏移不累加（第二段起口型全挤在开头）、
/// 响应的尾巴没冲出来（最后半秒没口型）。
/// </summary>
public sealed class StvHandlerTests
{
    private const int SampleRate = 24_000;      // 本构建 TTS 的输出率（上游是 16k 管线率）
    private const int WindowSamples = 12_000;   // 0.5 秒
    private const int BlockSamples = 768;       // 512 @16k 的等价时长（32 ms）

    // ── 测试脚手架 ────────────────────────────────────────────────────

    /// <summary>假提取器：记录每次调用看到的样本数，按固定套路返回口型区间。</summary>
    private sealed class FakeExtractor : IVisemeExtractor
    {
        public int SampleRate => StvHandlerTests.SampleRate;

        public List<int> Calls { get; } = [];

        public List<Viseme> Cues { get; set; } = [new Viseme(4, 0.0, 0.2)];

        public bool Throw { get; set; }

        public int DisposeCount { get; private set; }

        public IReadOnlyList<Viseme> Extract(ReadOnlySpan<byte> pcm16)
        {
            Calls.Add(pcm16.Length / 2);
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            return Cues.Select(cue => new Viseme(cue.VisemeId, cue.StartSeconds, cue.EndSeconds)).ToList();
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class Harness : IDisposable
    {
        public BlockingCollection<object> In { get; } = new(new ConcurrentQueue<object>());

        public BlockingCollection<object> Out { get; } = new(new ConcurrentQueue<object>());

        public CancellationTokenSource Cts { get; } = new();

        public Task Thread { get; }

        public StvHandler Handler { get; }

        public Harness(FakeExtractor extractor, CancelScope scope)
        {
            Handler = new StvHandler(Cts.Token, In, Out, extractor, scope, SampleRate);
            Thread = Task.Run(Handler.Run);
        }

        public List<AudioOutput> Audio() => Out.OfType<AudioOutput>().ToList();

        public void Dispose()
        {
            try
            {
                In.Add(PipelineSentinels.PipelineEnd);
                Thread.Wait(TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException)
            {
            }

            In.Dispose();
            Out.Dispose();
            Cts.Dispose();
        }
    }

    private static byte[] Pcm(int samples, short value = 1000)
    {
        var bytes = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        return bytes;
    }

    private static AudioOutput Chunk(byte[] pcm, string responseKey, int? generation = null)
        => new() { Audio = pcm, ResponseKey = responseKey, CancelGeneration = generation };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("等待条件超时");
    }

    // ── 整窗缓冲 ──────────────────────────────────────────────────────

    [Fact]
    public async Task WindowBuffersUntilHalfSecond_ThenInfersOnce()
    {
        var extractor = new FakeExtractor();
        using var harness = new Harness(extractor, new CancelScope());

        // 0.4 秒：还不够一个窗口——既不能推理，也不能把音频卡住不发？上游是真的卡住，
        // 所以这里断言「没有输出」，这正是 enable_visemes 带来的那 0.5 秒时延。
        harness.In.Add(Chunk(Pcm(9_600), "resp_a"));
        await Task.Delay(150);

        Assert.Empty(harness.Audio());
        Assert.Empty(extractor.Calls);

        // 再给 0.1 秒凑满 0.5 秒 → 一次推理，窗口音频原样（按 32ms 重分块）发出。
        harness.In.Add(Chunk(Pcm(2_400), "resp_a"));
        await WaitUntilAsync(() => harness.Audio().Count == 16, TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { WindowSamples }, extractor.Calls);
        var blocks = harness.Audio();
        Assert.Equal(WindowSamples * 2, blocks.Sum(block => block.Audio.Length));
        Assert.All(blocks.Take(15), block => Assert.Equal(BlockSamples * 2, block.Audio.Length));
        Assert.Equal(480 * 2, blocks[^1].Audio.Length); // 12000 / 768 的余数
        Assert.All(blocks, block => Assert.Equal("resp_a", block.ResponseKey));
    }

    [Fact]
    public async Task VisemesRideOnFirstBlockOnly()
    {
        var extractor = new FakeExtractor
        {
            Cues = [new Viseme(21, 0.0, 0.12), new Viseme(2, 0.12, 0.5)],
        };
        using var harness = new Harness(extractor, new CancelScope());

        harness.In.Add(Chunk(Pcm(WindowSamples), "resp_a"));
        await WaitUntilAsync(() => harness.Audio().Count == 16, TimeSpan.FromSeconds(5));

        var blocks = harness.Audio();
        Assert.Equal(2, blocks[0].Visemes.Count);
        Assert.Equal(21, blocks[0].Visemes[0].VisemeId);
        Assert.All(blocks.Skip(1), block => Assert.Empty(block.Visemes));
    }

    // ── 偏移累加 ──────────────────────────────────────────────────────

    [Fact]
    public async Task SecondWindowOffsetsCuesByAccumulatedSamples()
    {
        var extractor = new FakeExtractor { Cues = [new Viseme(4, 0.0, 0.2)] };
        using var harness = new Harness(extractor, new CancelScope());

        harness.In.Add(Chunk(Pcm(WindowSamples), "resp_a"));
        await WaitUntilAsync(() => harness.Audio().Count == 16, TimeSpan.FromSeconds(5));

        harness.In.Add(Chunk(Pcm(WindowSamples), "resp_a"));
        await WaitUntilAsync(() => harness.Audio().Count == 32, TimeSpan.FromSeconds(5));

        var blocks = harness.Audio();
        Assert.Equal(0.0, blocks[0].Visemes[0].StartSeconds, 6);
        Assert.Equal(0.2, blocks[0].Visemes[0].EndSeconds, 6);
        // 第二个窗口：同一段相对区间 + 已推理的 0.5 秒。
        Assert.Equal(0.5, blocks[16].Visemes[0].StartSeconds, 6);
        Assert.Equal(0.7, blocks[16].Visemes[0].EndSeconds, 6);
    }

    // ── 响应收口 ──────────────────────────────────────────────────────

    [Fact]
    public async Task ResponseDoneFlushesTailThenPassesMarkerThrough()
    {
        var extractor = new FakeExtractor();
        using var harness = new Harness(extractor, new CancelScope());

        // 只有 0.2 秒：不满一窗，靠响应结束把它冲出来。
        // 上下文（response_key + 代际）必须与终态事件一致，否则上游同样不冲——代际本来就不同轮。
        harness.In.Add(Chunk(Pcm(4_800), "resp_a", 0));
        var end = new EndOfResponse { ResponseKey = "resp_a", CancelGeneration = 0 };
        harness.In.Add(end);

        // 4800 样本 / 768 = 6.25 → 7 块（6 整块 + 1 块 384 样本）+ 1 条终态事件。
        await WaitUntilAsync(() => harness.Out.Count == 8, TimeSpan.FromSeconds(5));

        var drained = harness.Out.ToList();
        Assert.Equal(8, drained.Count);
        Assert.Same(end, drained[^1]);

        var blocks = drained.OfType<AudioOutput>().ToList();
        Assert.Equal(7, blocks.Count);
        Assert.Equal(4_800 * 2, blocks.Sum(block => block.Audio.Length));
        Assert.Equal(new[] { 4_800 }, extractor.Calls);
        Assert.Single(blocks[0].Visemes);
    }

    [Fact]
    public async Task CleanupOnlyResponseDone_DoesNotFlushTail()
    {
        var extractor = new FakeExtractor();
        using var harness = new Harness(extractor, new CancelScope());

        harness.In.Add(Chunk(Pcm(4_800), "resp_a", 0));
        var end = new EndOfResponse { ResponseKey = "resp_a", CancelGeneration = 0, CleanupOnly = true };
        harness.In.Add(end);

        await WaitUntilAsync(() => harness.Out.Count == 1, TimeSpan.FromSeconds(5));

        Assert.Empty(harness.Audio());
        Assert.Empty(extractor.Calls);
        Assert.Same(end, harness.Out.First());
    }

    [Fact]
    public async Task ContextSwitchFlushesPreviousWindowWithItsOwnKey()
    {
        var extractor = new FakeExtractor();
        using var harness = new Harness(extractor, new CancelScope());

        harness.In.Add(Chunk(Pcm(2_400), "resp_a"));
        harness.In.Add(Chunk(Pcm(WindowSamples), "resp_b"));

        // 2400 样本 → 4 块；resp_b 的整窗 → 16 块。
        await WaitUntilAsync(() => harness.Audio().Count == 20, TimeSpan.FromSeconds(5));

        var blocks = harness.Audio();
        // 前 2400 样本属于 resp_a：2400/768 = 3.125 → 4 块，全部带 resp_a。
        Assert.Equal(4, blocks.Count(block => block.ResponseKey == "resp_a"));
        Assert.All(blocks.Where(block => block.ResponseKey == "resp_a"), block =>
            Assert.InRange(block.Audio.Length, 2, BlockSamples * 2));
        Assert.Equal(2, extractor.Calls.Count);
        Assert.Equal(2_400, extractor.Calls[0]);
        Assert.Equal(WindowSamples, extractor.Calls[1]);
    }

    // ── 取消与异常 ────────────────────────────────────────────────────

    [Fact]
    public async Task StaleGenerationDropsWindowWithoutEmitting()
    {
        var scope = new CancelScope();
        int generation = scope.Generation;
        scope.Cancel();

        var extractor = new FakeExtractor();
        using var harness = new Harness(extractor, scope);

        harness.In.Add(Chunk(Pcm(WindowSamples), "resp_a", generation));
        await Task.Delay(150);

        Assert.Empty(harness.Audio());
        Assert.Empty(extractor.Calls);
    }

    [Fact]
    public async Task ExtractorFailureKeepsAssistantAudio()
    {
        var extractor = new FakeExtractor { Throw = true };
        using var harness = new Harness(extractor, new CancelScope());

        harness.In.Add(Chunk(Pcm(WindowSamples), "resp_a"));
        await WaitUntilAsync(() => harness.Audio().Count == 16, TimeSpan.FromSeconds(5));

        var blocks = harness.Audio();
        Assert.Equal(WindowSamples * 2, blocks.Sum(block => block.Audio.Length));
        Assert.All(blocks, block => Assert.Empty(block.Visemes));
    }

    [Fact]
    public async Task OddByteCountIsDroppedWithoutKillingTheStage()
    {
        var extractor = new FakeExtractor();
        using var harness = new Harness(extractor, new CancelScope());

        harness.In.Add(new byte[1001]); // 半个样本
        harness.In.Add(Chunk(Pcm(WindowSamples), "resp_a"));

        await WaitUntilAsync(() => harness.Audio().Count == 16, TimeSpan.FromSeconds(5));

        // 坏块被基类捕获记日志，后面的正常块照样处理——线程不能因为一条脏数据死掉。
        Assert.True(harness.Thread.IsCompleted || !harness.Thread.IsFaulted);
        Assert.Equal(new[] { WindowSamples }, extractor.Calls);
    }

    [Fact]
    public async Task SessionEndResetsSampleOffset()
    {
        var extractor = new FakeExtractor();
        using var harness = new Harness(extractor, new CancelScope());

        harness.In.Add(Chunk(Pcm(WindowSamples), "resp_a"));
        await WaitUntilAsync(() => harness.Audio().Count == 16, TimeSpan.FromSeconds(5));

        harness.In.Add(PipelineControl.SessionEnd);
        await WaitUntilAsync(() => harness.Out.Count == 17, TimeSpan.FromSeconds(5));

        harness.In.Add(Chunk(Pcm(WindowSamples), "resp_b"));
        await WaitUntilAsync(() => harness.Audio().Count == 32, TimeSpan.FromSeconds(5));

        var blocks = harness.Audio();
        // 新会话的时间线从 0 重新开始，不接着上一会话的 0.5 秒。
        Assert.Equal(0.0, blocks[16].Visemes[0].StartSeconds, 6);
    }

    // ── 线上协议 ──────────────────────────────────────────────────────

    [Fact]
    public void VisemesEventCarriesUpstreamShape()
    {
        var payload = ServerEvent.OutputAudioVisemes(
            "resp_1", "item_1", [new Viseme(21, 0.0, 0.12), new Viseme(2, 0.12, 0.5)], outputIndex: 2);

        Assert.Equal("speech_to_speech.output_audio.visemes", payload["type"]!.GetValue<string>());
        Assert.Equal("resp_1", payload["response_id"]!.GetValue<string>());
        Assert.Equal("item_1", payload["item_id"]!.GetValue<string>());
        Assert.Equal(2, payload["output_index"]!.GetValue<int>());
        Assert.Equal(0, payload["content_index"]!.GetValue<int>());
        Assert.StartsWith("event_", payload["event_id"]!.GetValue<string>());

        var visemes = payload["visemes"]!.AsArray();
        Assert.Equal(2, visemes.Count);
        Assert.Equal(21, visemes[0]!["viseme"]!.GetValue<int>());
        Assert.Equal(0.0, visemes[0]!["start_s"]!.GetValue<double>());
        Assert.Equal(0.12, visemes[0]!["end_s"]!.GetValue<double>(), 6);
        Assert.Equal(2, visemes[1]!["viseme"]!.GetValue<int>());

        // 序列化后键名必须仍是 snake_case —— 客户端按 upsteam 字段名解析。
        string json = ServerEvent.Serialize(payload);
        Assert.Contains("\"start_s\"", json, StringComparison.Ordinal);
        Assert.Contains("\"end_s\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"StartSeconds\"", json, StringComparison.Ordinal);
    }

    // ── 引擎接线（模型门控） ──────────────────────────────────────────

    [Fact]
    public void ExtractorCoversWholeWindowOnSilence()
    {
        string? modelDir = Environment.GetEnvironmentVariable("SPEECH_TO_SPEECH_VISEME_MODEL_DIR");
        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir))
        {
            return; // 模型资产不入库，未设置时不跑（与 OpenLipSyncTests 同口径）
        }

        using var extractor = OpenLipSyncVisemeExtractor.Load(modelDir, SampleRate);
        Assert.Equal(SampleRate, extractor.SampleRate);

        var cues = extractor.Extract(Pcm(WindowSamples, value: 0));

        // 区间必须铺满整窗：逐帧 argmax + 相邻合并的产物天然连续，
        // 上游「没有音素就不发口型」的稀疏语义相反——这处差异登记在 C033。
        Assert.NotEmpty(cues);
        Assert.Equal(0.0, cues[0].StartSeconds, 6);
        // 尾部少的 10 ms 是 48 tap 重采样的延迟线（24000→16000 时留 23 个样本），
        // 与 OpenLipSyncTests 里 1:1 重采样的尾滞后同源。
        Assert.InRange(cues[^1].EndSeconds, 0.48, 0.5);
        for (int i = 1; i < cues.Count; i++)
        {
            Assert.Equal(cues[i - 1].EndSeconds, cues[i].StartSeconds, 6);
        }

        // 纯静音下占主导的应是 sil(0)，出现其它口型说明前端或映射跑偏了。
        Assert.Contains(cues, cue => cue.VisemeId == 0);
    }

    [Fact]
    public void CreateViseme_ReturnsNullWhenDisabled()
    {
        var context = new BackendContext
        {
            Vad = new VadOptions(),
            Stt = new SttOptions(),
            Llm = new LlmOptions(),
            Tts = new TtsOptions(),
        };

        Assert.Null(BackendRegistry.CreateViseme(context));
    }

    [Fact]
    public void CreateViseme_RequiresRegisteredFactoryOnceEnabled()
    {
        var context = new BackendContext
        {
            Vad = new VadOptions(),
            Stt = new SttOptions(),
            Llm = new LlmOptions(),
            Tts = new TtsOptions(),
            Stv = new StvOptions { Enabled = true },
        };

        // 该环境里没有任何地方注册过口型工厂（组合根才注册）：启用即报错，不能静默降级。
        Assert.Throws<BackendUnavailableException>(() => BackendRegistry.CreateViseme(context));
    }
}
