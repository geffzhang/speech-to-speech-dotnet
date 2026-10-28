using SpeechToSpeech.Cli;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 播放缓冲语义（D035）。逐条对齐上游 PlaybackBuffer：
///
/// <list type="bullet">
/// <item>首包攒满 startup_buffer_ms 才开播，不足补静音；</item>
/// <item>finish() 放行已收完的短响应（不必攒满）；</item>
/// <item>一次 Read 拿不满就补零并回 idle（下次重新过门槛）；</item>
/// <item>append 后至少 150 ms 内 IsActive 为真（block_mic 判定窗口）；</item>
/// <item>clear 立即清空（打断路径：speech_started / cancelled / cleared）。</item>
/// </list>
/// </summary>
public sealed class PlaybackBufferTests
{
    private static byte[] Samples(int count, byte fill = 0xAB) => Enumerable.Repeat(fill, count).ToArray();

    [Fact]
    public void Below_startup_threshold_reads_silence()
    {
        // 16 kHz × 100 ms 门槛 = 3200 字节。
        var buffer = new PlaybackBuffer(16_000, startupBufferMs: 100);
        buffer.Append(Samples(1600)); // 50 ms，不足门槛

        var output = new byte[800];
        buffer.Read(output);
        Assert.All(output, b => Assert.Equal(0, b));
        Assert.Equal(1600, buffer.BufferedBytes); // 数据没被消费
    }

    [Fact]
    public void Reaching_threshold_releases_audio()
    {
        var buffer = new PlaybackBuffer(16_000, startupBufferMs: 100);
        buffer.Append(Samples(1600));
        buffer.Append(Samples(1600, fill: 0xCD)); // 凑满 100 ms

        var output = new byte[1600];
        buffer.Read(output);
        Assert.Equal(0xAB, output[0]);
        Assert.Equal(1600, buffer.BufferedBytes);

        buffer.Read(output);
        Assert.Equal(0xCD, output[0]);
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public void Finished_short_response_plays_before_threshold()
    {
        var buffer = new PlaybackBuffer(16_000, startupBufferMs: 100);
        buffer.Append(Samples(800)); // 25 ms，远低于门槛
        buffer.Finish();

        var output = new byte[400];
        buffer.Read(output);
        Assert.Equal(0xAB, output[0]);
    }

    [Fact]
    public void Short_read_returns_to_idle_and_requires_threshold_again()
    {
        // 上游 write 语义：一次拿不满 → playing=false，下次 Read 重新过门槛判定。
        var buffer = new PlaybackBuffer(16_000, startupBufferMs: 100);
        buffer.Append(Samples(3200)); // 正好满门槛
        var first = new byte[1600];
        buffer.Read(first);
        Assert.Equal(0xAB, first[0]);

        // 第二次读要 1600 字节但只剩 1600？正好拿满——用更大的读触发欠载。
        var big = new byte[2400];
        buffer.Read(big);
        // 前 1600 是真实数据，后 800 是补零，且回 idle。
        Assert.Equal(0xAB, big[0]);
        Assert.Equal(0, big[1600]);

        // 回到 idle 后数据已空，即使 finish 也读不出内容（全零）。
        var third = new byte[800];
        buffer.Read(third);
        Assert.All(third, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Append_keeps_active_for_at_least_150ms()
    {
        var buffer = new PlaybackBuffer(16_000, startupBufferMs: 0);
        buffer.Append(Samples(64));
        Assert.True(buffer.IsActive());
    }

    [Fact]
    public void IsActive_is_false_before_any_audio()
    {
        var buffer = new PlaybackBuffer(16_000, startupBufferMs: 0);
        Assert.False(buffer.IsActive());
    }

    [Fact]
    public void Clear_resets_everything()
    {
        var buffer = new PlaybackBuffer(16_000, startupBufferMs: 100);
        buffer.Append(Samples(3200));
        buffer.Clear();

        Assert.Equal(0, buffer.BufferedBytes);
        var output = new byte[800];
        buffer.Read(output);
        Assert.All(output, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Empty_append_is_ignored()
    {
        var buffer = new PlaybackBuffer(16_000, startupBufferMs: 0);
        buffer.Append(ReadOnlySpan<byte>.Empty);
        Assert.False(buffer.IsActive());
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public void Constructor_validates_arguments()
    {
        Assert.Throws<ArgumentException>(() => new PlaybackBuffer(0));
        Assert.Throws<ArgumentException>(() => new PlaybackBuffer(-1));
        Assert.Throws<ArgumentException>(() => new PlaybackBuffer(16_000, startupBufferMs: -1));
        Assert.Throws<ArgumentException>(() => new PlaybackBuffer(16_000, startupBufferMs: double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => new PlaybackBuffer(16_000, startupBufferMs: double.NaN));
    }

    [Fact]
    public void Startup_bytes_are_even_aligned_like_upstream()
    {
        // 16000 * 2 * 99.999 / 1000 = 3199.968 → int 3199 → 偶对齐 3198。
        var buffer = new PlaybackBuffer(16_000, startupBufferMs: 99.999);
        buffer.Append(Samples(3198));
        var output = new byte[1000];
        buffer.Read(output);
        Assert.Equal(0xAB, output[0]); // 已达门槛，开始播
    }
}
