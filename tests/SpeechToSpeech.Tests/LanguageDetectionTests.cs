using System.Collections.Concurrent;
using System.Reflection;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Pipeline.Handlers;
using SpeechToSpeech.Core.Runtime;
using SpeechToSpeech.Core.Text;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 语言检测的决策层与响应级语言解析。
///
/// 决策层那部分已由 `corpus/lang` 的 15 条差分语料与上游逐值比对过，这里锁的是语料覆盖不到的
/// 边界组合（阈值两侧、置信度差两侧、CJK 区间端点），以及解析器的「一轮只解析一次」。
///
/// 解析层没法做差分：上游把它埋在 <c>LMOutputProcessor.process</c> 这个长方法里，
/// 与 TTS 入队、工具调用侧信道混在一处，抽不出可比对的接缝。所以按分支逐条落成用例。
/// </summary>
public sealed class LanguageDetectionTests
{
    /// <summary>按预设返回候选的假检测器，用来把阈值卡在边界上。</summary>
    private sealed class ScriptedDetector(params LanguageCandidate[] candidates) : ILanguageDetector
    {
        public string? LastText { get; private set; }

        public IReadOnlyList<LanguageCandidate> Rank(string text)
        {
            LastText = text;
            return candidates;
        }
    }

    [Fact]
    public void DetectFromText_WithoutDetector_ReturnsNull()
    {
        // 没有检测器时必须放弃而不是猜。猜出来的语言会被拿去选音色，
        // 错的那一半听起来比沉默更糟，而且没有任何信号说明这是猜的。
        Assert.Null(LanguageDetection.DetectFromText("A long enough English sentence.", detector: null));
    }

    [Fact]
    public void DetectFromText_BelowMinimumLength_ReturnsNull()
    {
        var detector = new ScriptedDetector(new LanguageCandidate("en", 1.0));

        // 19 个字符，差一个都不行。
        Assert.Null(LanguageDetection.DetectFromText("abcdefghijklmnopqrs", detector));
        Assert.Equal("en", LanguageDetection.DetectFromText("abcdefghijklmnopqrst", detector));
    }

    [Fact]
    public void DetectFromText_ShortCjk_LowersThresholdOnlyWhenAllowed()
    {
        var detector = new ScriptedDetector(new LanguageCandidate("zh", 1.0));

        // 「好的，马上查。」6 个码点，远低于 20。
        Assert.Null(LanguageDetection.DetectFromText("好的，马上查。", detector, allowShortCjk: false));
        Assert.Equal("zh", LanguageDetection.DetectFromText("好的，马上查。", detector, allowShortCjk: true));
    }

    [Fact]
    public void DetectFromText_ConfidenceGap_FollowsThreshold()
    {
        // 用 0.75 / 0.5（二进制可精确表示）避免浮点噪声干扰边界判断：
        // 0.6 - 0.5 在双精度下是 0.09999999999999998，会「莫名其妙」掉到阈值下面，
        // 上游 Python 也是同样的浮点，所以那不是缺陷而是必须一致的行为——正因如此才要另找干净的边界值。
        var exactlyAtThreshold = new ScriptedDetector(
            new LanguageCandidate("en", 0.75),
            new LanguageCandidate("nl", 0.50));
        Assert.Equal("en", LanguageDetection.DetectFromText(
            "A sentence long enough to detect.", exactlyAtThreshold, minimumConfidenceGap: 0.25));

        var justUnder = new ScriptedDetector(
            new LanguageCandidate("en", 0.7499),
            new LanguageCandidate("nl", 0.50));
        Assert.Null(LanguageDetection.DetectFromText(
            "A sentence long enough to detect.", justUnder, minimumConfidenceGap: 0.25));
    }

    [Fact]
    public void DetectFromText_NoCandidates_ReturnsNull()
    {
        Assert.Null(LanguageDetection.DetectFromText("A sentence long enough to detect.", new ScriptedDetector()));
    }

    [Fact]
    public void DetectFromText_NormalizesBokmalToNorwegian()
    {
        var detector = new ScriptedDetector(new LanguageCandidate("nb", 1.0));

        // 检测器分得出 Bokmål(nb) 与 Nynorsk(nn)，下游 TTS 只认 no。
        Assert.Equal("no", LanguageDetection.DetectFromText(
            "Jeg kan hjelpe deg med å finne svaret.", detector));
        Assert.Equal("no", LanguageDetection.NormalizeCode("nb"));
        Assert.Equal("nn", LanguageDetection.NormalizeCode("nn"));
    }

    [Theory]
    [InlineData("汉", true)]        // U+6C49，Han
    [InlineData("あ", true)]        // U+3042，Hiragana 起点
    [InlineData("カ", true)]        // U+30AB，Katakana
    [InlineData("한", true)]        // U+D55C，Hangul
    [InlineData("é", false)]
    [InlineData("😀", false)]       // BMP 之外，不属于三个 CJK 区间
    public void ContainsCjk_CoversOnlyTheThreeRanges(string text, bool expected)
        => Assert.Equal(expected, LanguageDetection.ContainsCjk(text));

    [Fact]
    public void AccumulateWindow_JoinsWithSpaceAndTrims()
    {
        // 模型分片常不带尾空格，直接相接会把两个词粘成一个，所以拼接时补一个空格。
        Assert.Equal("Sure, I can", AssistantLanguageProbe.AccumulateWindow("Sure,", "I can"));

        // 但 strip 只吃首尾：两片各自带空格时中间会出现两个空格。上游同样如此，
        // 这里不顺手「修正」——检测器的分词对连续空白的处理两边一致，改了反而不等价。
        Assert.Equal("Sure,  I can", AssistantLanguageProbe.AccumulateWindow("Sure,", " I can"));
        Assert.Equal("hello", AssistantLanguageProbe.AccumulateWindow("", "  hello  "));
    }

    [Fact]
    public void AccumulateWindow_KeepsOnlyTheTail()
    {
        var text = new string('a', 300);
        var window = AssistantLanguageProbe.AccumulateWindow("", text);

        // 丢头保尾：结尾通常才是最完整的句子。
        Assert.Equal(LanguageDetection.AssistantProbeLimit, window.Length);
        Assert.Equal(new string('a', 256), window);
    }

    [Fact]
    public void AssistantLanguageProbe_StopsObservingAfterDetection()
    {
        var detector = new ScriptedDetector(new LanguageCandidate("en", 1.0));
        var probe = new AssistantLanguageProbe(detector);

        probe.Observe("First chunk that is long enough to be detected.");
        var windowAfterFirst = probe.Window;

        probe.Observe("Ещё немного текста на русском языке.");

        // 判出就定死：一轮回复中途换语言会让 TTS 前后音色不一致。
        Assert.Equal("en", probe.Detected);
        Assert.Equal(windowAfterFirst, probe.Window);
    }

    [Fact]
    public void AssistantLanguageProbe_Reset_ClearsWindowAndVerdict()
    {
        var detector = new ScriptedDetector(new LanguageCandidate("en", 1.0));
        var probe = new AssistantLanguageProbe(detector);
        probe.Observe("First chunk that is long enough to be detected.");

        probe.Reset();

        Assert.Null(probe.Detected);
        Assert.Equal("", probe.Window);
    }

    [Fact]
    public void AssistantLanguageProbe_WithoutDetector_StillAccumulatesWindow()
    {
        var probe = new AssistantLanguageProbe();

        probe.Observe("Some assistant text.");

        // 窗口照常累积，只是判不出来——「有没有检测器」不该改变「什么时候观察」。
        Assert.Null(probe.Detected);
        Assert.Equal("Some assistant text.", probe.Window);
    }

    [Fact]
    public void ResponseLanguageResolver_ResolvesOnlyOncePerResponse()
    {
        var resolver = new ResponseLanguageResolver();

        var first = resolver.Resolve("zh", null, "en", null, null, detectEnabled: false);
        var second = resolver.Resolve("zh", null, "ja", null, null, detectEnabled: false);

        // 后续分片即使带了不同配置也不改结论：同一段回复不能换音色。
        Assert.Equal("en", first.TtsLanguageCode);
        Assert.Equal("en", second.TtsLanguageCode);
    }

    [Fact]
    public void ResponseLanguageResolver_Auto_UsesDetectedThenLastAssistantLanguage()
    {
        var withDetection = new ResponseLanguageResolver().Resolve(
            chunkLanguageCode: "en",
            chunkSelectedLanguage: "auto",
            configSelectedLanguage: null,
            configLastAssistantLanguage: "fr",
            detected: "de",
            detectEnabled: true);

        // 本轮判出的优先于上一轮助手语言。
        Assert.Equal("de", withDetection.TtsLanguageCode);

        var withoutThisRound = new ResponseLanguageResolver().Resolve(
            "en", "auto", null, "fr", detected: null, detectEnabled: true);

        // 判不出就沿用上一轮——这是连续同语言对话不换音色的原因。
        Assert.Equal("fr", withoutThisRound.TtsLanguageCode);
    }

    [Fact]
    public void ResponseLanguageResolver_ExplicitSelection_PrefersDetectionWhenEnabled()
    {
        var enabled = new ResponseLanguageResolver().Resolve("en", "ja", null, null, detected: "ko", detectEnabled: true);
        Assert.Equal("ko", enabled.TtsLanguageCode);

        var disabled = new ResponseLanguageResolver().Resolve("en", "ja", null, null, detected: "ko", detectEnabled: false);
        // 检测关掉时显式选择说了算，判出的语言不参与。
        Assert.Equal("ja", disabled.TtsLanguageCode);
    }

    [Fact]
    public void ResponseLanguageResolver_DetectDisabled_ReportsChunkLanguage()
    {
        var resolution = new ResponseLanguageResolver().Resolve(
            "zh", null, null, null, "zh", detectEnabled: false);

        Assert.Equal("zh", resolution.LanguageCode);
        // 没有选择时合成语言跟随文本语言；助手语言字段只在启用检测且有选择缺口时才有值。
        Assert.Equal("zh", resolution.TtsLanguageCode);
        Assert.Null(resolution.ResponseAssistantLanguageCode);
    }

    [Fact]
    public void ResponseLanguageResolver_Reset_StartsOver()
    {
        var resolver = new ResponseLanguageResolver();
        resolver.Resolve("en", null, "en", null, null, detectEnabled: false);

        resolver.Reset();

        var next = resolver.Resolve("ja", null, "ja", null, null, detectEnabled: false);
        Assert.Equal("ja", next.TtsLanguageCode);
        Assert.Equal("ja", resolver.Selected);
    }

    [Fact]
    public void TtsInput_UsesResolvedLanguageVerbatim()
    {
        var resolved = new TtsLanguageResolution("de", "auto", "de", "de");

        var input = TtsInput.Create("hello", language: resolved);

        // 给了已解析结果就不再重算：否则第一片按 auto 解析、第二片按配置重算，同一轮会分叉。
        Assert.Equal("de", input.LanguageCode);
        Assert.Equal("auto", input.SelectedLanguage);
        Assert.Equal("de", input.TtsLanguageCode);
        Assert.Equal("de", input.ResponseAssistantLanguageCode);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void LmOutputProcessor_OnlySynthesizesWhenResponseWantsAudio(bool textOnly, int expected)
    {
        var processor = new LmOutputProcessor(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 32),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 32),
            new CancelScope(),
            new SpeculativeTurnTracker(),
            new AssistantLanguageProbe(),
            detectLlmOutputLanguage: false);

        var chunk = new LlmResponseChunk
        {
            Parts = [new AssistantTextPart { Text = "Hello there, this is a spoken reply." }],
            RuntimeConfig = new RuntimeConfig(),
            Response = textOnly
                ? new RealtimeResponseCreateParams { OutputModalities = ["text"] }
                : new RealtimeResponseCreateParams(),
        };

        var process = typeof(LmOutputProcessor).GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LmOutputProcessor.Process 不存在");

        var spoken = ((IEnumerable<object>) process.Invoke(processor, [chunk])!).OfType<TtsInput>().Count();

        // 纯文本响应不该白跑一次合成模型：上游同一处判断既挡合成，也挡语言观察与解析。
        Assert.Equal(expected, spoken);
    }

    [Fact]
    public void LmOutputProcessor_CarriesSpeechStoppedAtIntoTtsInput()
    {
        // e2e_s 的起点是估计的语音结束时刻。这条链在 LmOutputProcessor 处断过一次：
        // chunk 带着值、TtsInput.Create 却没把它填进去，metadata 里 e2e_s 恒 null，
        // 语料全绿——因为假后端从不检查这个字段。这里锁「值真的到了末端」。
        var processor = new LmOutputProcessor(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 32),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 32),
            new CancelScope(),
            new SpeculativeTurnTracker(),
            new AssistantLanguageProbe(),
            detectLlmOutputLanguage: false);

        var chunk = new LlmResponseChunk
        {
            Parts = [new AssistantTextPart { Text = "好的。" }],
            RuntimeConfig = new RuntimeConfig(),
            Response = new RealtimeResponseCreateParams(),
            SpeechStoppedAtSeconds = 12.5,
        };

        var process = typeof(LmOutputProcessor).GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LmOutputProcessor.Process 不存在");

        var ttsInput = ((IEnumerable<object>) process.Invoke(processor, [chunk])!)
            .OfType<TtsInput>()
            .Single();

        Assert.Equal(12.5, ttsInput.SpeechStoppedAtSeconds);
    }
}
