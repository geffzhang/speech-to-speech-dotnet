using SpeechToSpeech.Backends.SpeakerEmbedding;

namespace SpeechToSpeech.Tests;

/// <summary>
/// Kaldi 风格 fbank 前端的单测。
///
/// <para>参照值是 <c>kaldi-native-fbank</c>（sherpa-onnx 的前端就是它）用<b>同样的参数</b>算出来的：
/// 25 ms / 10 ms、povey 窗、preemph 0.97、逐帧去直流、snip_edges=false、512 点 FFT、
/// 80 mel bins（20~7600 Hz）、log 前按 FLT_EPSILON 截断。参数错一个（比如 HTK 的 log10 mel 轴、
/// 或者 snip_edges=true）这些数字就对不上，而且是对不上一大片——这正是要用定点值而不是
/// 「看起来合理」来断言的原因。</para>
/// </summary>
public class KaldiFbankTests
{
    private const int SampleRate = 16000;

    /// <summary>
    /// 确定性合成信号：两个正弦 + 一点整数伪噪声。用整数运算生成噪声项，保证 C# 与 Python
    /// 逐样本一致（浮点三角函数只差 1 ulp，落在 log 之后可以忽略）。
    /// </summary>
    private static float[] Synthetic(int samples = 8000)
    {
        var audio = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            double tone = 0.30 * Math.Sin(2 * Math.PI * 300 * n / SampleRate)
                        + 0.20 * Math.Sin(2 * Math.PI * 1200 * n / SampleRate);
            double noise = 0.01 * (n * 37 % 200 - 100) / 100.0;
            audio[n] = (float)(tone + noise);
        }
        return audio;
    }

    [Fact]
    public void Frames_follow_the_unsnipped_formula()
    {
        // snip_edges=false：帧数是 (n + shift/2) / shift，向上凑——末帧靠边界镜像补齐。
        // snip_edges=true 的口径会是 (n − length)/shift + 1 = 48，差两帧。
        Assert.Equal(50, KaldiFbank.NumFrames(8000));
        Assert.Equal(300, KaldiFbank.NumFrames(48000));

        var fbank = new KaldiFbank();
        fbank.Compute(Synthetic(8000), out int frames);
        Assert.Equal(50, frames);
    }

    [Fact]
    public void Digital_silence_sits_on_the_float_epsilon_floor()
    {
        // 数字静音的能量低于下限，被截到 FLT_EPSILON 再取 log：-15.942385。
        // 用 float.Epsilon（1.4e-45）会得到 -103，两者差 87——一个很容易写错、
        // 又不会抛任何异常的地方。
        var fbank = new KaldiFbank();
        var features = fbank.Compute(new float[8000], out int frames);

        Assert.Equal(50, frames);
        float expected = MathF.Log(1.1920929e-7f);
        for (int i = 0; i < features.Length; i++)
        {
            Assert.Equal(expected, features[i], 5);
        }
    }

    [Fact]
    public void Matches_kaldi_native_fbank_on_a_synthetic_signal()
    {
        var fbank = new KaldiFbank();
        var features = fbank.Compute(Synthetic(), out int frames);
        Assert.Equal(50, frames);

        // (帧, 维, 参照值)。挑的是低频低能量 bin 与 300 Hz / 1200 Hz 两个峰附近——
        // 前者抓 floor 与 mel 下限，后者抓 mel 中心位置。
        (int Frame, int Dim, float Expected)[] points =
        [
            (0, 0, -4.720361f), (0, 1, -3.969726f), (0, 2, -3.682324f),
            (0, 5, -2.116085f), (0, 10, 1.905899f), (0, 20, -5.841107f),
            (0, 40, -1.061691f), (0, 60, -3.046238f), (0, 79, -3.585163f),
            (12, 0, -10.022510f), (12, 1, -12.340033f), (12, 2, -9.426878f),
            (12, 10, 2.453266f), (12, 20, -8.754594f),
            (25, 0, -10.035416f), (25, 1, -13.763446f), (25, 10, 2.453478f),
            (25, 31, 4.795456f), (25, 20, -9.022339f),
            (49, 0, -4.313043f), (49, 1, -3.219500f), (49, 10, 1.826297f),
            (49, 79, -4.002945f),
        ];

        foreach (var (frame, dim, expected) in points)
        {
            float actual = features[frame * KaldiFbank.FeatureDim + dim];
            Assert.True(
                MathF.Abs(actual - expected) < 5e-4f,
                $"帧 {frame} 维 {dim}：参照 {expected:F6}，实际 {actual:F6}");
        }

        // 1200 Hz 的峰落在第 31 维（300 Hz 的峰在第 10 维）：mel 轴若用错口径，
        // 两个峰会同时挪位，这条断言会立刻报出来。
        Assert.Equal(31, ArgMax(features, 25));

        // 全局统计量：整块矩阵一起对，抓的是「个别数值对、整体趋势偏」这种更隐蔽的错法。
        double sum = 0;
        float min = float.MaxValue, max = float.MinValue;
        foreach (float value in features)
        {
            sum += value;
            if (value < min) min = value;
            if (value > max) max = value;
        }
        Assert.True(Math.Abs(sum - -21891.097656) < 0.5, $"矩阵和 {sum:F3}");
        Assert.True(MathF.Abs(min - -15.354465f) < 5e-4f, $"最小值 {min:F6}");
        Assert.True(MathF.Abs(max - 4.795532f) < 5e-4f, $"最大值 {max:F6}");
    }

    [Fact]
    public void Subtracting_the_global_mean_centres_every_dimension()
    {
        var fbank = new KaldiFbank();
        var features = fbank.Compute(Synthetic(), out int frames);

        KaldiFbank.SubtractGlobalMean(features, frames);

        for (int d = 0; d < KaldiFbank.FeatureDim; d++)
        {
            double mean = 0;
            for (int f = 0; f < frames; f++) mean += features[f * KaldiFbank.FeatureDim + d];
            mean /= frames;
            Assert.True(Math.Abs(mean) < 1e-4, $"维 {d} 归约后均值 {mean:E3}");
        }
    }

    [Fact]
    public void Is_deterministic_across_calls_and_instances()
    {
        var audio = Synthetic();

        var first = new KaldiFbank().Compute(audio, out _);
        var second = new KaldiFbank().Compute(audio, out _);

        Assert.Equal(first.Length, second.Length);
        for (int i = 0; i < first.Length; i++)
        {
            Assert.Equal(first[i], second[i]);
        }
    }

    private static int ArgMax(float[] features, int frame)
    {
        int best = 0;
        for (int d = 1; d < KaldiFbank.FeatureDim; d++)
        {
            if (features[frame * KaldiFbank.FeatureDim + d] > features[frame * KaldiFbank.FeatureDim + best])
                best = d;
        }
        return best;
    }
}
