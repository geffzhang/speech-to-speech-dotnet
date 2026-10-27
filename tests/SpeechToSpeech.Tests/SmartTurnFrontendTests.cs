using System.Text.Json;
using SpeechToSpeech.Backends.SmartTurn;
using Xunit;

namespace SpeechToSpeech.Tests;

/// <summary>
/// Smart Turn 前端与参照实现（transformers <c>WhisperFeatureExtractor</c> 的 numpy 路径 +
/// numpy <c>fft</c>）的逐值对齐测试。
///
/// fixture（<c>SmartTurnFrontendFixture.json</c>）由参照实现录制，来源写在该文件
/// <c>source</c> 字段里；录制脚本用公式化合成音频（不经 PRNG），C# 侧按同一公式
/// 复刻输入。基线只能由参照实现 record，不能手写——这条与 <c>corpus/</c> 同一纪律。
/// </summary>
public sealed class SmartTurnFrontendTests
{
    private static readonly Lazy<JsonElement> Fixture = new(() =>
        JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "SmartTurnFrontendFixture.json"))).RootElement.Clone());

    /// <summary>fixture 里的合成音频公式，与录制脚本逐项一致。</summary>
    private static float[] SyntheticAudio()
    {
        var audio = new float[WhisperFrontend.MaxSamples];
        for (var i = 0; i < audio.Length; i++)
        {
            var t = i / 16000.0;
            audio[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 180.0 * t)
                               + 0.10 * Math.Sin(2 * Math.PI * 1400.0 * t + 0.7)
                               + 0.05 * Math.Sin(2 * Math.PI * 3600.0 * t + 1.3));
        }

        return audio;
    }

    [Fact]
    public void Fft400_MatchesNumpyOnFixtureBins()
    {
        // 输入公式与 fixture 的 fft_re_formula / fft_im_formula 一致。
        var re = new double[Fft400.Size];
        var im = new double[Fft400.Size];
        for (var j = 0; j < Fft400.Size; j++)
        {
            re[j] = Math.Cos(0.7 * j) + (j % 7);
            im[j] = Math.Sin(0.3 * j) - (j % 5);
        }

        Fft400.Forward(re, im);

        var fftRef = Fixture.Value.GetProperty("fft_ref");
        foreach (var property in fftRef.EnumerateObject())
        {
            var bin = int.Parse(property.Name);
            var expectedReal = property.Value[0].GetDouble();
            var expectedImag = property.Value[1].GetDouble();
            var scale = Math.Max(1.0, Math.Abs(expectedReal) + Math.Abs(expectedImag));

            Assert.True(
                Math.Abs(re[bin] - expectedReal) < 1e-8 * scale,
                $"FFT bin {bin} 实部：{re[bin]} != {expectedReal}");
            Assert.True(
                Math.Abs(im[bin] - expectedImag) < 1e-8 * scale,
                $"FFT bin {bin} 虚部：{im[bin]} != {expectedImag}");
        }
    }

    [Fact]
    public void MelFilterBank_MatchesTransformersFixtureSpots()
    {
        var melSpots = Fixture.Value.GetProperty("mel_spots");
        foreach (var property in melSpots.EnumerateObject())
        {
            var parts = property.Name.Split(',');
            var expected = property.Value.GetDouble();

            Assert.True(
                Math.Abs(WhisperFrontend.MelFilter(int.Parse(parts[0]), int.Parse(parts[1])) - expected) < 1e-12,
                $"mel 滤波器 [{property.Name}]：{WhisperFrontend.MelFilter(int.Parse(parts[0]), int.Parse(parts[1]))} != {expected}");
        }
    }

    [Fact]
    public void LogMel_MatchesTransformersFixtureSpots()
    {
        // fixture 由 fx(..., do_normalize=True) 录制——波形先做零均值单位方差归一化。
        var features = WhisperFrontend.LogMel(WhisperFrontend.ZeroMeanUnitVar(SyntheticAudio()));
        var spots = Fixture.Value.GetProperty("feature_spots");
        foreach (var property in spots.EnumerateObject())
        {
            var parts = property.Name.Split(',');
            var expected = property.Value.GetDouble();
            // 布局 [mel * Frames + frame]。
            var actual = features[int.Parse(parts[0]) * WhisperFrontend.Frames + int.Parse(parts[1])];

            Assert.True(
                Math.Abs(actual - expected) < 1e-5,
                $"特征 [{property.Name}]：{actual} != {expected}");
        }
    }

    [Fact]
    public void LogMel_OutputRangeIsNormalized()
    {
        var features = WhisperFrontend.LogMel(WhisperFrontend.ZeroMeanUnitVar(SyntheticAudio()));

        Assert.Equal(Fixture.Value.GetProperty("feature_max").GetDouble(), features.Max(), 4);
        Assert.Equal(Fixture.Value.GetProperty("feature_min").GetDouble(), features.Min(), 4);
    }

    /// <summary>
    /// 端到端对齐：同一份合成音频，本实现的完整链路（前端 + ONNX 推理）与参照链路的
    /// 概率差应在 1e-4 内。模型缺失时跳过断言（判定器在缺模型时走 disabled，测试同理不能依赖模型）。
    /// </summary>
    [Fact]
    public void Classifier_MatchesReferenceProbabilityOnSyntheticAudio()
    {
        // 模型定位顺序：S2S_SMART_TURN_MODEL 环境变量 → 输出目录 models/smart-turn/。
        // 都没有就跳过断言：判定器在缺模型时走 disabled，测试同理不能依赖模型。
        var modelPath = Environment.GetEnvironmentVariable("S2S_SMART_TURN_MODEL");
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            modelPath = Path.Combine(
                AppContext.BaseDirectory, "models", "smart-turn", SmartTurnOnnxClassifier.DefaultModelFileName);
        }

        if (!File.Exists(modelPath))
        {
            return;
        }

        var classifier = new SmartTurnOnnxClassifier(modelPath);
        var prediction = classifier.Predict(SyntheticAudio(), 16000);
        var expected = Fixture.Value.GetProperty("reference_probability").GetDouble();

        Assert.True(
            Math.Abs(prediction.Probability - expected) < 1e-4,
            $"概率 {prediction.Probability} 与参照 {expected} 偏差超过 1e-4");
        Assert.Equal(expected > 0.5, prediction.Complete);
    }
}
