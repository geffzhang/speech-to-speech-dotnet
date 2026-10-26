using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Text;

namespace SpeechToSpeech.Tests;

/// <summary>文本规范化与分句。</summary>
public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData("Hello world. How are you?", 2)]
    [InlineData("一句话。第二句话！第三句话？", 3)]
    [InlineData("No terminator here", 1)]
    [InlineData("", 0)]
    public void SentenceTokenize_SplitsOnTerminators(string text, int expected)
        => Assert.Equal(expected, TextNormalizer.SentenceTokenize(text).Count);

    [Fact]
    public void SentenceTokenize_DoesNotSplitCommonAbbreviations()
    {
        var sentences = TextNormalizer.SentenceTokenize("Dr. Smith arrived at 9 a.m. and left.");

        Assert.Single(sentences);
    }

    [Fact]
    public void SentenceTokenize_KeepsAdjacentTerminatorsTogether()
    {
        var sentences = TextNormalizer.SentenceTokenize("Really?! I cannot believe it.");

        Assert.Equal(2, sentences.Count);
        Assert.StartsWith("Really?!", sentences[0]);
    }

    /// <summary>
    /// 刻意与上游不同的地方（C005）。
    ///
    /// 上游用 nltk Punkt 的英文模型，差分语料实测它对中文标点**完全不切**
    /// （`corpus/text/cjk-terminator-boundary.expected.json` 里整段就是一个元素）。
    /// 后果是中文长回复要等全文生成完才能送 TTS，首段音频被拖到最后。
    /// 这里把 CJK 全角标点本身当作边界，是有意的改进，不是实现走样。
    /// 若哪天想改回上游行为，先改这条测试，再改 C005。
    /// </summary>
    [Fact]
    public void SentenceTokenize_TreatsCjkTerminatorsAsBoundaries_UnlikeUpstreamPunkt()
    {
        var sentences = TextNormalizer.SentenceTokenize("一句话。第二句话！第三句话？");

        Assert.Equal(["一句话。", "第二句话！", "第三句话？"], sentences);
    }

    [Fact]
    public void MarkdownCodeFence_IsNeverSplit()
    {
        var text = "Before code.\n```python\nprint('a. b. c.')\n```\nAfter code.";

        var sentences = TextNormalizer.SentenceTokenizePreservingMarkdownCode(text);

        Assert.Contains(sentences, s => s.Contains("print('a. b. c.')", StringComparison.Ordinal));
    }

    [Fact]
    public void UnclosedCodeFence_ReturnsWholeTextAsOneSentence()
    {
        var text = "Here is code:\n```\nunterminated()\nstill going. and more.";

        var sentences = TextNormalizer.SentenceTokenizePreservingMarkdownCode(text);

        Assert.Single(sentences);
    }

    [Fact]
    public void RemoveMarkdown_StripsDelimitersButKeepsText()
    {
        var cleaned = TextNormalizer.RemoveMarkdown("## Title\n- **bold** and _italic_");

        Assert.DoesNotContain("##", cleaned);
        Assert.DoesNotContain("**", cleaned);
        Assert.Contains("bold", cleaned);
        Assert.Contains("italic", cleaned);
    }

    [Fact]
    public void RemoveUnspeakable_DropsEmojiAndKeepsPunctuation()
    {
        var cleaned = TextNormalizer.RemoveUnspeakable("Hello 😀 world! 你好。");

        Assert.DoesNotContain("😀", cleaned);
        Assert.Contains("Hello", cleaned);
        Assert.Contains("你好", cleaned);
        Assert.Contains("!", cleaned);
    }

    [Fact]
    public void RemoveUnspeakable_MapsSmartQuotes()
    {
        var cleaned = TextNormalizer.RemoveUnspeakable("“quoted”");

        Assert.Equal("\"quoted\"", cleaned);
    }
}

/// <summary>句级缓冲：流式 token → 完整句子。</summary>
public sealed class SentenceAccumulatorTests
{
    [Fact]
    public void Push_HoldsBackIncompleteSentence()
    {
        var accumulator = new SentenceAccumulator();

        var first = accumulator.Push("Hello wor");
        var second = accumulator.Push("ld. Nex");

        Assert.Empty(first);
        Assert.Single(second);
        Assert.Equal("Hello world.", second[0]);
        Assert.Equal("Nex", accumulator.Pending);
    }

    [Fact]
    public void Flush_EmitsTrailingRemainder()
    {
        var accumulator = new SentenceAccumulator();
        accumulator.Push("no terminator");

        var flushed = accumulator.Flush();

        Assert.Single(flushed);
        Assert.Equal("no terminator", flushed[0]);
        Assert.Equal(string.Empty, accumulator.Pending);
    }

    [Fact]
    public void Push_EmitsMultipleSentencesFromOneDelta()
    {
        var accumulator = new SentenceAccumulator();

        var sentences = accumulator.Push("One. Two. Three.");

        Assert.Equal(3, sentences.Count);
    }
}

/// <summary>音频格式转换。</summary>
public sealed class AudioUtilTests
{
    [Fact]
    public void Resample_48kTo16k_ProducesOneThirdSamples()
    {
        var input = new float[48_000];

        var output = AudioUtil.Resample(input, 48_000, 16_000);

        Assert.Equal(16_000, output.Length);
    }

    [Fact]
    public void Resample_SameRate_ReturnsInputUnchanged()
    {
        var input = new float[] { 0.1f, 0.2f, 0.3f };

        Assert.Same(input, AudioUtil.Resample(input, 16_000, 16_000));
    }

    [Fact]
    public void Pcm16_RoundTripsWithinQuantizationError()
    {
        var samples = new[] { 0f, 0.5f, -0.5f, 1f, -1f };

        var roundTripped = AudioUtil.FromPcm16(AudioUtil.ToPcm16(samples));

        Assert.Equal(samples.Length, roundTripped.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.InRange(roundTripped[i], samples[i] - 0.001f, samples[i] + 0.001f);
        }
    }

    [Fact]
    public void Chunk_SplitsIntoFixedSizeBlocksWithShortTail()
    {
        var chunks = AudioUtil.Chunk(new float[1100], 512).ToList();

        Assert.Equal(3, chunks.Count);
        Assert.Equal(512, chunks[0].Length);
        Assert.Equal(76, chunks[2].Length);
    }

    [Fact]
    public void WavHeader_HasRiffWaveMarkers()
    {
        var wav = WavWriter.FromFloat32(new float[1600], 16_000);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(44 + 3200, wav.Length);
    }
}
