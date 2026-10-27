namespace SpeechToSpeech.Backends.SpeakerEmbedding;

/// <summary>
/// Kaldi 风格的 log-mel fbank 前端（纯 C#，无第三方依赖）。
///
/// <para>这个前端只有一件事要做：把 16 kHz 单声道波形变成声纹模型要的 <c>[帧, 80]</c> 特征。
/// 它的每个常量都被参照口径钉死——sherpa-onnx 的 <c>FeatureExtractorConfig</c> 默认值，
/// 底层是 kaldi-native-fbank。前端偏一点点不会报错，只会让「同一个人的两段话距离变大」，
/// 那是所有声纹阈值都不再灵的最难查的错法。</para>
///
/// <para>口径：25 ms 帧长 / 10 ms 帧移、povey 窗、预加重 0.97、逐帧去直流、<b>snip_edges=false</b>
/// （帧中心对齐、边界单边镜像）、512 点 FFT（round_to_power_of_two）、80 个 mel 三角滤波器
/// （20 Hz ~ Nyquist−400 Hz、自然对数 mel 轴）、log 前按 <c>FLT_EPSILON</c> 下限截断。
/// kaldi 的 <c>use_energy=false</c>，所以特征就是 80 维 log-mel，不含能量项。</para>
/// </summary>
internal sealed class KaldiFbank
{
    public const int SampleRate = 16000;
    public const int FeatureDim = 80;
    public const int FrameLength = 400;  // 25 ms
    public const int FrameShift = 160;   // 10 ms
    public const int PaddedLength = 512; // round_to_power_of_two(400)
    public const int NumFftBins = PaddedLength / 2;

    private const double Preemphasis = 0.97;
    private const double LowFrequency = 20.0;
    private const double HighFrequency = 7600.0; // Nyquist(8000) + (-400)

    /// <summary>
    /// kaldi 的 log 下限是 <c>std::numeric_limits&lt;float&gt;::epsilon()</c>，不是
    /// <see cref="float.Epsilon"/>——后者是反规格化最小值 1.4e-45，差了 38 个数量级。
    /// 数字静音的帧因此是 <c>log(1.1920929e-7) = -15.94</c>，而不是 -103。
    /// </summary>
    private const float LogFloor = 1.1920929e-7f;

    private readonly double[] _window;
    private readonly MelFilter[] _melBanks;
    private readonly double[] _cosTable;
    private readonly double[] _sinTable;

    public KaldiFbank()
    {
        _window = BuildPoveyWindow();
        _melBanks = BuildMelBanks();

        // 旋转因子整表预计算：递推（每次乘同一个因子）在 512 点上会累积到 1e-13 量级，
        // 而查表是精确的，也让「同一段音频两次算出同一个特征」成为理所当然的事。
        _cosTable = new double[PaddedLength];
        _sinTable = new double[PaddedLength];
        for (int k = 0; k < PaddedLength; k++)
        {
            double angle = -2.0 * Math.PI * k / PaddedLength;
            _cosTable[k] = Math.Cos(angle);
            _sinTable[k] = Math.Sin(angle);
        }
    }

    /// <summary>
    /// kaldi 的 <c>NumFrames(flush: true)</c>。<b>snip_edges=false</b> 时末帧会被反射补齐，
    /// 所以帧数是向上凑的 <c>(n + shift/2) / shift</c>，而不是 snip 口径的
    /// <c>(n − length) / shift + 1</c>——同样 3 秒音频前者 300 帧、后者 298 帧。
    /// </summary>
    public static int NumFrames(int sampleCount)
        => sampleCount <= 0 ? 0 : (sampleCount + FrameShift / 2) / FrameShift;

    /// <summary>
    /// 算完整的 fbank 矩阵，行主序 <c>[frameCount, FeatureDim]</c>。
    /// 只读共享实例：所有可变量都是局部数组，多连接并发调用互不影响。
    /// </summary>
    public float[] Compute(ReadOnlySpan<float> audio, out int frameCount)
    {
        frameCount = NumFrames(audio.Length);
        if (frameCount <= 0) return [];

        var features = new float[frameCount * FeatureDim];
        var re = new double[PaddedLength];
        var im = new double[PaddedLength];
        var frame = new double[FrameLength];

        for (int f = 0; f < frameCount; f++)
        {
            ExtractWindow(audio, f, frame);

            for (int i = 0; i < FrameLength; i++)
            {
                re[i] = frame[i];
                im[i] = 0;
            }
            for (int i = FrameLength; i < PaddedLength; i++)
            {
                re[i] = 0;
                im[i] = 0;
            }

            Fft(re, im);

            int offset = f * FeatureDim;
            for (int b = 0; b < FeatureDim; b++)
            {
                MelFilter filter = _melBanks[b];
                double energy = 0;
                for (int t = 0; t < filter.Indices.Length; t++)
                {
                    int k = filter.Indices[t];
                    energy += filter.Weights[t] * (re[k] * re[k] + im[k] * im[k]);
                }

                // kaldi 是先把 mel 能量落到 float 上、再 ApplyFloor(FLT_EPSILON)、再取 log。
                float energy32 = (float)energy;
                features[offset + b] = MathF.Log(MathF.Max(energy32, LogFloor));
            }
        }

        return features;
    }

    /// <summary>
    /// 逐维减时间均值（sherpa 的 <c>feature_normalize_type = "global-mean"</c>，
    /// 对应它用 Eigen 写的 <c>rowwise() - colwise().mean()</c>）。
    /// 注意是<b>每维一条时间均值</b>而不是全局一个数：它抹掉的是通道增益与录音条件，
    /// 不是说话人之间的相对差异。
    /// </summary>
    public static void SubtractGlobalMean(float[] features, int frameCount, int dim = FeatureDim)
    {
        if (frameCount <= 0) return;

        var mean = new double[dim];
        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * dim;
            for (int d = 0; d < dim; d++) mean[d] += features[offset + d];
        }
        for (int d = 0; d < dim; d++) mean[d] /= frameCount;

        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * dim;
            for (int d = 0; d < dim; d++) features[offset + d] -= (float)mean[d];
        }
    }

    /// <summary>
    /// 取一帧并做完所有逐帧预处理：反射取样本 → 去直流 → 预加重 → 加窗。
    /// 顺序不能换：kaldi 是在<b>去直流之后</b>做预加重的，先预加重会把直流分量放大成斜坡。
    /// </summary>
    private void ExtractWindow(ReadOnlySpan<float> audio, int frameIndex, double[] window)
    {
        // snip_edges=false 的帧起点：帧中心对齐到 frame*shift + shift/2，
        // 于是起点 = frame*shift + shift/2 − length/2，首帧为负（−120）。
        long start = (long)frameIndex * FrameShift + FrameShift / 2 - FrameLength / 2;
        int dim = audio.Length;

        for (int i = 0; i < FrameLength; i++)
        {
            long s = start + i;
            // kaldi 是<b>单边镜像</b>：−1→0、−2→1；dim→dim−1、dim+1→dim−2。
            // 不是把首样本重复一次（那样首帧的直流与能量都会偏）。
            while (s < 0 || s >= dim)
            {
                if (s < 0) s = -s - 1;
                else s = 2L * dim - 1 - s;
            }
            window[i] = audio[(int)s];
        }

        double mean = 0;
        for (int i = 0; i < FrameLength; i++) mean += window[i];
        mean /= FrameLength;
        for (int i = 0; i < FrameLength; i++) window[i] -= mean;

        // 从后往前走；首样本按「前一个样本等于它自己」处理，这是 kaldi 的口径
        // （差一个 x[0]*0.03 的偏移，逐位对拍时才看得见）。
        for (int i = FrameLength - 1; i > 0; i--) window[i] -= Preemphasis * window[i - 1];
        window[0] -= Preemphasis * window[0];

        for (int i = 0; i < FrameLength; i++) window[i] *= _window[i];
    }

    /// <summary>原地 radix-2 复数 FFT。输入实数放在 <paramref name="re"/>、虚部清零。</summary>
    private void Fft(double[] re, double[] im)
    {
        for (int i = 1, j = 0; i < PaddedLength; i++)
        {
            int bit = PaddedLength >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= PaddedLength; len <<= 1)
        {
            int half = len >> 1;
            int step = PaddedLength / len;
            for (int start = 0; start < PaddedLength; start += len)
            {
                for (int k = 0; k < half; k++)
                {
                    int tw = k * step;
                    double wr = _cosTable[tw], wi = _sinTable[tw];
                    int a = start + k, b = a + half;

                    double tr = re[b] * wr - im[b] * wi;
                    double ti = re[b] * wi + im[b] * wr;

                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }

    private static double[] BuildPoveyWindow()
    {
        var window = new double[FrameLength];
        double a = 2.0 * Math.PI / (FrameLength - 1);
        for (int i = 0; i < FrameLength; i++)
        {
            window[i] = Math.Pow(0.5 - 0.5 * Math.Cos(a * i), 0.85);
        }
        return window;
    }

    /// <summary>
    /// kaldi 的三角 mel 滤波器组：中心在 mel 轴上等间隔，宽度 <c>delta = (mel_high − mel_low)/(bins+1)</c>，
    /// 权重三角形不归一化（<c>htk_mode=false</c>、<c>norm</c> 不设）。用的是自然对数 mel 轴
    /// <c>1127·ln(1 + f/700)</c>——换成 HTK 的 log10 口径，所有 bin 中心都会挪位。
    /// </summary>
    private static MelFilter[] BuildMelBanks()
    {
        double melLow = MelScale(LowFrequency);
        double melHigh = MelScale(HighFrequency);
        double delta = (melHigh - melLow) / (FeatureDim + 1);
        double binWidth = (double)SampleRate / PaddedLength;

        var banks = new MelFilter[FeatureDim];
        for (int b = 0; b < FeatureDim; b++)
        {
            double left = melLow + b * delta;
            double center = melLow + (b + 1) * delta;
            double right = melLow + (b + 2) * delta;

            var indices = new List<int>(24);
            var weights = new List<float>(24);
            for (int k = 0; k < NumFftBins; k++)
            {
                double mel = MelScale(k * binWidth);
                if (mel <= left || mel >= right) continue;

                float weight = mel <= center
                    ? (float)((mel - left) / (center - left))
                    : (float)((right - mel) / (right - center));

                indices.Add(k);
                weights.Add(weight);
            }

            banks[b] = new MelFilter(indices.ToArray(), weights.ToArray());
        }

        return banks;
    }

    private static double MelScale(double frequency) => 1127.0 * Math.Log(1.0 + frequency / 700.0);

    private readonly record struct MelFilter(int[] Indices, float[] Weights);
}
