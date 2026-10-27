using SpeechToSpeech.Backends.OpenLipSync;
using SpeechToSpeech.Backends.OpenLipSync.Inference;
using SpeechToSpeech.Backends.OpenLipSync.Inference.Audio;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Tests;

/// <summary>
/// OpenLipSync 口型引擎（D009 STV）的单测。移植自 BasisVR/Basis com.basis.openlipsync（MIT）。
///
/// 参照值由参照环境（CPython 3.13 + NumPy）按 Basis config 的 mel 前端几何逐项直算：
/// 同一确定性信号喂 C# 前端与 NumPy 参照（HTK mel、hann symmetric、10·log10(max(power,1e-10))），
/// dB 值互证。ONNX 端到端（流式不变性）由环境变量
/// <c>SPEECH_TO_SPEECH_VISEME_MODEL_DIR</c>（含 model.onnx + config.json）门控。
/// </summary>
public class OpenLipSyncTests
{
    // ── 参照信号：与 NumPy 参照脚本逐项一致 ──
    // s[n] = 0.5·sin(2π·220·n/SR) + 0.25·sin(2π·1800·n/SR + 0.3) + 0.1·sin(2π·440·n/SR + 1.1)
    private const int SampleRate = 16000;
    private const int NFft = 1024;
    private const int WindowLength = 400;
    private const int HopLength = 160;
    private const int NMels = 80;
    private const float FMin = 50.0f;
    private const float FMax = 8000.0f;

    private static float ReferenceSignal(int n) =>
        (0.5f * MathF.Sin(2f * MathF.PI * 220f * n / SampleRate))
        + (0.25f * MathF.Sin((2f * MathF.PI * 1800f * n / SampleRate) + 0.3f))
        + (0.1f * MathF.Sin((2f * MathF.PI * 440f * n / SampleRate) + 1.1f));

    /// <summary>NumPy rfft 参照：x[i]=sin(2π·3i/16)+0.5·cos(2π·i/16+0.7)，|X[k]|²。</summary>
    private static readonly double[] PowerSpectrum16Reference =
    [
        0.0, 16.0, 0.0, 64.0, 0.0, 0.0, 0.0, 0.0, 0.0,
    ];

    // NumPy mel 参照（dB）——按 Basis 前端的滑动窗语义逐项直算：
    // prev 初始为 240 个零；每 hop：window = prev + hop，prev = window[160..400]。
    // 即窗口 0 = [0×240, s(0..160)]，窗口 1 = [0×80, s(0..240)]，窗口 2 = s(0..400)。
    private static readonly double[] MelFrame0Bins0To5 =
    [
        17.998197, 19.467998, 21.61967, 23.400142, 25.460706, 25.945396,
    ];

    private static readonly double[] MelFrame1Bins0To5 =
    [
        9.683868, 10.60303, 12.758284, 18.305056, 29.670855, 34.690806,
    ];

    private static readonly double[] MelFrame2Bins40To45 =
    [
        19.714193, -11.682725, -24.206083, -32.333918, -38.676241, -43.58076,
    ];

    [Fact]
    public void ForwardPower_MatchesNumpyReference()
    {
        var x = new float[16];
        for (int i = 0; i < 16; i++)
        {
            x[i] = MathF.Sin(2f * MathF.PI * 3f * i / 16f)
                   + (0.5f * MathF.Cos((2f * MathF.PI * i / 16f) + 0.7f));
        }

        var fft = new FFTProcessor(16);
        var power = new float[9];
        fft.ForwardPower(x, power);

        for (int k = 0; k < 9; k++)
        {
            Assert.Equal(PowerSpectrum16Reference[k], power[k], precision: 3);
        }
    }

    [Fact]
    public void ForwardPower_SinePeakLandsOnItsBin()
    {
        // 64 点、恰好 4 个整周期的正弦：能量应全部落在 bin 4（与 bin 60 是同一峰的镜像，不输出）。
        var x = new float[64];
        for (int i = 0; i < 64; i++)
        {
            x[i] = MathF.Sin(2f * MathF.PI * 4f * i / 64f);
        }

        var power = new float[33];
        new FFTProcessor(64).ForwardPower(x, power);

        Assert.Equal(64f * 64f / 4f, power[4], precision: 2);
        Assert.True(power[0] < 1e-2);
        Assert.True(power[1] < 1e-2);
    }

    /// <summary>顺序喂 hop 0..hopIndex，返回第 <paramref name="hopIndex"/> 个 mel 帧（Basis 滑动语义）。</summary>
    private static float[] RunMelFrame(int hopIndex)
    {
        var config = new AudioProcessingConfig
        {
            SampleRate = SampleRate,
            HopLengthSamples = HopLength,
            WindowLengthSamples = WindowLength,
            NFft = NFft,
            NMels = NMels,
            FMin = FMin,
            FMax = FMax,
        };
        using var mel = new MelSpectrogramProcessor(config);

        float[] frame = Array.Empty<float>();
        for (int k = 0; k <= hopIndex; k++)
        {
            var hop = new float[HopLength];
            for (int i = 0; i < HopLength; i++)
            {
                hop[i] = ReferenceSignal((k * HopLength) + i);
            }

            frame = mel.ProcessHop(hop);
        }

        return frame;
    }

    [Fact]
    public void MelFrontEnd_MatchesNumpyReferenceFrame0()
    {
        var frame = RunMelFrame(0);
        for (int m = 0; m < 6; m++)
        {
            Assert.Equal(MelFrame0Bins0To5[m], frame[m], 1);
        }
    }

    [Fact]
    public void MelFrontEnd_MatchesNumpyReferenceFrame1()
    {
        var frame = RunMelFrame(1);
        for (int m = 0; m < 6; m++)
        {
            Assert.Equal(MelFrame1Bins0To5[m], frame[m], 1);
        }
    }

    [Fact]
    public void MelFrontEnd_MatchesNumpyReferenceLowEnergyBins()
    {
        // bin 40–45 跨能量峰与近底噪区——低能量 bin 对 float32 累加误差最敏感。
        var frame = RunMelFrame(2);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(MelFrame2Bins40To45[i], frame[40 + i], 1);
        }
    }

    [Fact]
    public void MelFrontEnd_SilenceIsFloorDb()
    {
        var config = new AudioProcessingConfig
        {
            SampleRate = SampleRate,
            HopLengthSamples = HopLength,
            WindowLengthSamples = WindowLength,
            NFft = NFft,
            NMels = NMels,
            FMin = FMin,
            FMax = FMax,
        };
        using var mel = new MelSpectrogramProcessor(config);
        var frame = mel.ProcessHop(stackalloc float[HopLength]);

        // 10·log10(1e-10) = -100
        Assert.All(frame, v => Assert.Equal(-100.0f, v, 4));
    }

    [Fact]
    public void AudioProcessingConfig_PrefersSampleExactGeometry()
    {
        var fromSamples = AudioProcessingConfig.FromModelConfig(new ModelConfig
        {
            Audio = new AudioInfo
            {
                SampleRate = 16000,
                HopLengthSamples = 160,
                WindowLengthSamples = 400,
                NFft = 1024,
                NMels = 80,
                Fmin = 50f,
                Fmax = 8000f,
            },
        });
        Assert.Equal(160, fromSamples.HopLengthSamples);
        Assert.Equal(400, fromSamples.WindowLengthSamples);

        // 没有逐样本几何时从毫秒推导（hop 25ms@16k=400、win 40ms=640）。
        var fromMs = AudioProcessingConfig.FromModelConfig(new ModelConfig
        {
            Audio = new AudioInfo
            {
                SampleRate = 16000,
                HopLengthMs = 25,
                WindowLengthMs = 40,
                NFft = 512,
                NMels = 64,
                Fmin = 50f,
                Fmax = 8000f,
            },
        });
        Assert.Equal(400, fromMs.HopLengthSamples);
        Assert.Equal(640, fromMs.WindowLengthSamples);
    }

    // ── 重采样 ──

    [Fact]
    public void Resampler_IdentityRateReproducesInput()
    {
        // 48 tap 滤波的尾部有 halfTaps-1 = 23 个样本留在延迟线里——流式语义的固有滞后。
        // 注意 1:1 率下滤波器是 0.45·fs 低通且有半样本线性相位延迟（23.5 tap），
        // 不是恒等直通——本测试锁的是「块边界不变性」与频率保持这两个流式关键性质。
        const int tailLag = 23;

        var resampler = new AudioResampler(16000, 16000);
        var input = new float[1600];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = MathF.Sin(2f * MathF.PI * 220f * i / 16000f);
        }

        // 一次整块。
        var whole = resampler.Resample(input).ToArray();
        Assert.Equal(input.Length - tailLag, whole.Length);

        // 分块喂（任意块边界）：输出必须与整块一致（位级稳定，无接缝）。
        resampler.Reset();
        var streamed = new List<float>();
        var rng = new Random(7);
        var offset = 0;
        while (offset < input.Length)
        {
            int count = Math.Min(1 + rng.Next(300), input.Length - offset);
            streamed.AddRange(resampler.Resample(input.AsSpan(offset, count)).ToArray());
            offset += count;
        }

        Assert.Equal(whole.Length, streamed.Count);
        for (int i = 0; i < whole.Length; i++)
        {
            Assert.Equal(whole[i], streamed[i], 5);
        }

        // 频率保持：220 Hz 正弦经 1:1「重采样」后过零率不变。
        static int ZeroCrossings(float[] s)
        {
            int crossings = 0;
            for (int i = 1; i < s.Length; i++)
            {
                if ((s[i - 1] < 0f) != (s[i] < 0f))
                {
                    crossings++;
                }
            }

            return crossings;
        }

        int inputCrossings = ZeroCrossings(input);
        Assert.InRange(ZeroCrossings(whole), inputCrossings - 2, inputCrossings + 2);
    }

    [Fact]
    public void Resampler_DownsampleConvergesToExactLength()
    {
        var resampler = new AudioResampler(48000, 16000);
        var rng = new Random(11);
        long totalOut = 0;
        long totalIn = 0;
        for (int block = 0; block < 50; block++)
        {
            var blockIn = new float[480];
            for (int i = 0; i < blockIn.Length; i++)
            {
                blockIn[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            }

            totalOut += resampler.Resample(blockIn).Length;
            totalIn += blockIn.Length;
        }

        // 3:1 整数率：稳态产出率精确 1/3（尾部 ≤ 半窗/3 的样本留在延迟线里）。
        Assert.InRange(totalOut, (totalIn / 3) - 10, totalIn / 3);
        Assert.True(Math.Abs(totalOut - (totalIn / 3.0)) / (totalIn / 3.0) < 0.002);
    }

    // ── 平滑器 ──

    [Fact]
    public void Smoother_ConvergesToConstantInputWithoutBias()
    {
        var smoother = new VisemeSmoother(15, 0.3f);
        var input = new float[15];
        input[10] = 0.8f;

        for (int i = 0; i < 200; i++)
        {
            smoother.Step(input);
        }

        Assert.Equal(0.8f, smoother.Output[10], 3);
        Assert.True(smoother.Output[0] < 0.01f);
    }

    [Fact]
    public void Smoother_ResetReturnsToSilence()
    {
        var smoother = new VisemeSmoother(15, 0.3f);
        smoother.Step(new float[15]);
        smoother.Reset();

        // 静息位 = sil（索引 0）权重 1，其余 0。
        Assert.Equal(1f, smoother.Output[0], 5);
        Assert.All(smoother.Output.Skip(1), v => Assert.Equal(0f, v, 5));
    }

    [Fact]
    public void Smoother_AlphaIsClamped()
    {
        var smoother = new VisemeSmoother(15, alpha: 1.5f);
        Assert.Equal(0.99f, smoother.Alpha, 5);
        smoother.Alpha = -3f;
        Assert.Equal(0f, smoother.Alpha, 5);
        smoother.Alpha = float.NaN;
        Assert.Equal(0f, smoother.Alpha, 5);
    }

    // ── 激活函数 ──

    [Fact]
    public void Activation_MultiLabelSigmoidIsPerViseme()
    {
        var destination = new float[3];
        OpenLipSyncBackend.ApplyActivation([0f, 2f, -2f], destination, 3, multiLabel: true);

        Assert.Equal(0.5f, destination[0], 5);
        Assert.Equal(1f / (1f + MathF.Exp(-2f)), destination[1], 5);
        Assert.Equal(1f / (1f + MathF.Exp(2f)), destination[2], 5);
    }

    [Fact]
    public void Activation_SoftmaxSumsToOne()
    {
        var destination = new float[3];
        OpenLipSyncBackend.ApplyActivation([0.3f, 1.7f, -0.9f], destination, 3, multiLabel: false);

        Assert.Equal(1f, destination[0] + destination[1] + destination[2], 5);
        Assert.Equal(1, destination.ToList().IndexOf(destination.Max()));
    }

    // ── OVR → Microsoft 映射与区间构建 ──

    [Fact]
    public void OvrToMicrosoftMapping_IsLockedToThePublishedTable()
    {
        // OVR 15 口型（sil/PP/FF/TH/DD/kk/CH/SS/nn/RR/aa/E/ih/oh/ou）→ MS SAPI 21。
        int[] expected = [0, 21, 18, 17, 19, 20, 16, 15, 19, 13, 2, 4, 6, 3, 7];
        Assert.Equal(expected, VisemeTimeline.OvrToMicrosoft);
        Assert.Equal(VisemeFrame.VisemeCount, VisemeTimeline.OvrToMicrosoft.Length);
        Assert.Equal(VisemeFrame.VisemeCount, VisemeFrame.VisemeNames.Length);
    }

    [Fact]
    public void FrameToViseme_TakesArgmaxAndMaps()
    {
        var probs = new float[15];
        probs[1] = 0.7f; // PP
        probs[10] = 0.2f; // aa
        Assert.Equal(21, VisemeTimeline.FrameToMicrosoftViseme(probs));

        var silence = new float[15];
        Assert.Equal(0, VisemeTimeline.FrameToMicrosoftViseme(silence)); // 全零 → sil
    }

    [Fact]
    public void BuildIntervals_MergesAdjacentSameVisemeFrames()
    {
        // 帧 0-2: PP(21)，帧 3-5: FF(18)，帧 6: sil(0)。帧长 10ms。
        var frames = new List<float[]>();
        float[] OneHot(int index)
        {
            var p = new float[15];
            p[index] = 1f;
            return p;
        }

        frames.Add(OneHot(1));
        frames.Add(OneHot(1));
        frames.Add(OneHot(1));
        frames.Add(OneHot(2));
        frames.Add(OneHot(2));
        frames.Add(OneHot(2));
        frames.Add(OneHot(0));

        var cues = VisemeTimeline.BuildIntervals(frames, 0.01);

        Assert.Equal(3, cues.Count);
        Assert.Equal(21, cues[0].VisemeId);
        Assert.Equal(0.0, cues[0].StartSeconds, 6);
        Assert.Equal(0.03, cues[0].EndSeconds, 6);
        Assert.Equal(18, cues[1].VisemeId);
        Assert.Equal(0.03, cues[1].StartSeconds, 6);
        Assert.Equal(0.06, cues[1].EndSeconds, 6);
        Assert.Equal(0, cues[2].VisemeId);
        Assert.Equal(0.06, cues[2].StartSeconds, 6);
        Assert.Equal(0.07, cues[2].EndSeconds, 6);
    }

    [Fact]
    public void BuildIntervals_AppliesOffsetAndHandlesEmpty()
    {
        Assert.Empty(VisemeTimeline.BuildIntervals([], 0.01));

        var frames = new List<float[]> { SingleProb(3) };
        var cues = VisemeTimeline.BuildIntervals(frames, 0.01, offsetSeconds: 1.25);
        Assert.Single(cues);
        Assert.Equal(17, cues[0].VisemeId); // OVR 3 = TH → MS 17
        Assert.Equal(1.25, cues[0].StartSeconds, 6);
        Assert.Equal(1.26, cues[0].EndSeconds, 6);
    }

    private static float[] SingleProb(int index)
    {
        var p = new float[15];
        p[index] = 1f;
        return p;
    }

    // ── ONNX 端到端（模型就位时）──

    private static string? ResolveModelDir()
    {
        var dir = Environment.GetEnvironmentVariable("SPEECH_TO_SPEECH_VISEME_MODEL_DIR");
        if (dir is not null && Directory.Exists(dir))
        {
            return dir;
        }

        // 仓库布局兜底（本地模型目录，不入库）。
        var local = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "viseme");
        return Directory.Exists(local) ? Path.GetFullPath(local) : null;
    }

    [Fact]
    public void StreamingInvariance_BlockBoundariesDoNotChangeVisemeFrames()
    {
        var modelDir = ResolveModelDir();
        if (modelDir is null)
        {
            // 模型资产不入库；设 SPEECH_TO_SPEECH_VISEME_MODEL_DIR 指向含
            // model.onnx + config.json 的目录即可启用端到端互证。
            return;
        }

        var modelBytes = File.ReadAllBytes(Path.Combine(modelDir, "model.onnx"));
        var configJson = File.ReadAllText(Path.Combine(modelDir, "config.json"));

        var backend = new OpenLipSyncBackend();
        Assert.True(backend.InitializeFromBytes(SampleRate, modelBytes, configJson), backend.LastError);
        try
        {
            Assert.True(backend.IsStreaming, "随包模型应为流式图（cache_* 输入）");

            // 2 秒的确定性语音样信号（多正弦混合，覆盖口型频段）。
            const int totalSamples = 2 * SampleRate;
            var signal = new float[totalSamples];
            for (int n = 0; n < totalSamples; n++)
            {
                signal[n] = 0.4f * MathF.Sin(2f * MathF.PI * 180f * n / SampleRate)
                            + (0.3f * MathF.Sin((2f * MathF.PI * 900f * n / SampleRate) + 0.5f))
                            + (0.2f * MathF.Sin((2f * MathF.PI * 2500f * n / SampleRate) + 1.7f));
            }

            // 整段 vs 任意块边界：每帧概率必须逐位一致（TCN 缓存语义的流式不变性）。
            var whole = RunCollectingFrames(backend, signal, [totalSamples]);
            var odd = RunCollectingFrames(backend, signal, BlockSequence(totalSamples, [777, 1234, 9973, 160, 160, 160]));
            Assert.True(whole.Count > 100, $"应有数百个 mel 帧，实际 {whole.Count}");
            Assert.Equal(whole.Count, odd.Count);
            for (int f = 0; f < whole.Count; f++)
            {
                for (int v = 0; v < 15; v++)
                {
                    Assert.Equal(whole[f][v], odd[f][v], 5);
                }
            }

            // 信号结束后的静音段也要一致（缓存衰减路径）。
            var tail = new float[SampleRate / 2];
            var wholeTail = RunCollectingFrames(backend, tail, [tail.Length]);
            var oddTail = RunCollectingFrames(backend, tail, BlockSequence(tail.Length, [311, 1024, 4096]));
            Assert.Equal(wholeTail.Count, oddTail.Count);
            for (int f = 0; f < wholeTail.Count; f++)
            {
                for (int v = 0; v < 15; v++)
                {
                    Assert.Equal(wholeTail[f][v], oddTail[f][v], 5);
                }
            }
        }
        finally
        {
            backend.Dispose();
        }
    }

    /// <summary>把块尺寸序列循环扩展到恰好覆盖 totalSamples。</summary>
    private static int[] BlockSequence(int totalSamples, int[] sizes)
    {
        var blocks = new List<int>();
        int i = 0;
        int remaining = totalSamples;
        while (remaining > 0)
        {
            int size = Math.Min(sizes[i % sizes.Length], remaining);
            blocks.Add(size);
            remaining -= size;
            i++;
        }

        return blocks.ToArray();
    }

    /// <summary>按块喂信号，收集每个 mel 帧的平滑后概率快照（100 Hz）。</summary>
    private static List<float[]> RunCollectingFrames(OpenLipSyncBackend backend, float[] signal, int[] blockSizes)
    {
        long ctx = backend.CreateContext();
        try
        {
            var frames = new List<float[]>();
            void OnFrame(float[] snapshot) => frames.Add(snapshot);
            backend.VisemeFrameComputed += OnFrame;
            try
            {
                var frame = new VisemeFrame();
                int offset = 0;
                foreach (int size in blockSizes)
                {
                    int count = Math.Min(size, signal.Length - offset);
                    if (count <= 0)
                    {
                        break;
                    }

                    backend.ProcessFrame(ctx, signal.AsSpan(offset, count), ref frame);
                    offset += count;
                }
            }
            finally
            {
                backend.VisemeFrameComputed -= OnFrame;
            }

            return frames;
        }
        finally
        {
            backend.DestroyContext(ctx);
        }
    }

    [Fact]
    public void ConfigFromBasisModel_YieldsThePublishedGeometry()
    {
        // Basis 随包 config.json 的 audio 段（几何参数写死锁定，防漂移）。
        const string configJson = """
            {"audio":{"sample_rate":16000,"n_fft":1024,"window_length_samples":400,"hop_length_samples":160,
            "n_mels":80,"fmin":50.0,"fmax":8000.0,"fps":100.0,"normalization":"none - the model does it"},
            "model":{"num_visemes":15,"multi_label":true,"normalization":"baked_into_graph"}}
            """;
        var config = System.Text.Json.JsonSerializer.Deserialize<ModelConfig>(
            configJson,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = false });
        var audio = AudioProcessingConfig.FromModelConfig(config!);

        Assert.Equal(16000, audio.SampleRate);
        Assert.Equal(160, audio.HopLengthSamples);
        Assert.Equal(400, audio.WindowLengthSamples);
        Assert.Equal(1024, audio.NFft);
        Assert.Equal(80, audio.NMels);
        Assert.Equal(50f, audio.FMin);
        Assert.Equal(8000f, audio.FMax);
        Assert.Equal(100f, audio.Fps);
    }
}
