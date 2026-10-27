namespace SpeechToSpeech.Backends.SmartTurn;

/// <summary>
/// Whisper log-mel 前端，与上游 <c>WhisperFeatureExtractor(chunk_length=8)</c> 的 numpy 路径
/// 逐参数对齐（transformers 5.20.2 <c>feature_extraction_whisper.py</c> + <c>audio_utils.py</c>）：
///
/// <list type="bullet">
/// <item>周期 Hann 窗（n_fft=400）、hop 160、center 反射填充 200；</item>
/// <item>float64 STFT、功率谱（平方）、80 维 slaney/slaney mel 滤波器组（0–8000 Hz）；</item>
/// <item>mel 下限 1e-10、log10、丢最后一帧（800 帧）、全局 max-8 钳制、(x+4)/4、float32。</item>
/// </list>
///
/// 输出布局与模型输入一致：<c>[mel * 800 + frame]</c>，即 (1, 80, 800) 的展平。
/// </summary>
internal static class WhisperFrontend
{
    public const int SampleRate = 16000;
    public const int MaxSamples = 8 * SampleRate;
    public const int Frames = MaxSamples / 160;   // 800
    public const int Mels = 80;
    private const int Nfft = 400;
    private const int Hop = 160;
    private const int NumBins = Nfft / 2 + 1;     // 201

    private static readonly double[] Window = new double[Nfft];
    private static readonly double[] MelFilters = new double[NumBins * Mels];

    static WhisperFrontend()
    {
        // 周期 Hann：np.hanning(401)[:-1] == 0.5 - 0.5·cos(2πk/400)。
        for (var k = 0; k < Nfft; k++)
        {
            Window[k] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * k / Nfft);
        }

        BuildMelFilterBank();
    }

    /// <summary>测试入口：读一个 mel 滤波器系数（fixture 逐值对齐用）。</summary>
    internal static double MelFilter(int bin, int mel) => MelFilters[bin * Mels + mel];

    /// <summary>
    /// 波形零均值单位方差归一化——上游 <c>WhisperFeatureExtractor.__call__</c> 在
    /// <c>do_normalize=True</c>（smart_turn.py 恒传）时对**原始波形**做的变换：
    /// <c>(x - mean) / sqrt(var + 1e-7)</c>，方差是总体方差（除以 N）。
    /// 归一化发生在取窗/补零之后，统计的是这 128000 个样本全体。
    /// 原地修改并返回同一数组。
    /// </summary>
    public static float[] ZeroMeanUnitVar(float[] audio)
    {
        if (audio.Length == 0)
        {
            return audio;
        }

        double sum = 0.0;
        foreach (var value in audio)
        {
            sum += value;
        }

        var mean = sum / audio.Length;

        double squared = 0.0;
        foreach (var value in audio)
        {
            var centered = value - mean;
            squared += centered * centered;
        }

        var scale = 1.0 / Math.Sqrt(squared / audio.Length + 1e-7);
        for (var i = 0; i < audio.Length; i++)
        {
            audio[i] = (float)((audio[i] - mean) * scale);
        }

        return audio;
    }

    /// <summary>
    /// 计算 log-mel 特征。<paramref name="audio"/> 必须是恰好 128000 个样本
    /// （调用方负责取尾 8 秒、前部补零——上游 <c>SmartTurnAnalyzer._prepare_audio</c> 的口径）。
    /// </summary>
    public static float[] LogMel(ReadOnlySpan<float> audio)
    {
        if (audio.Length != MaxSamples)
        {
            throw new ArgumentException($"Smart Turn 前端需要恰好 {MaxSamples} 个样本，收到 {audio.Length}");
        }

        // center 反射填充 200。np.pad(mode="reflect")：镜像不重复边缘样本。
        var padded = new double[MaxSamples + 2 * (Nfft / 2)];
        for (var i = 0; i < MaxSamples; i++)
        {
            padded[(Nfft / 2) + i] = audio[i];
        }

        for (var i = 1; i <= Nfft / 2; i++)
        {
            padded[(Nfft / 2) - i] = padded[(Nfft / 2) + i];
            padded[(Nfft / 2) + MaxSamples - 1 + i] = padded[(Nfft / 2) + MaxSamples - 1 - i];
        }

        var numFrames = 1 + (padded.Length - Nfft) / Hop;   // 801
        var re = new double[Nfft];
        var im = new double[Nfft];
        var tmpRe = new double[Nfft];
        var tmpIm = new double[Nfft];
        var power = new double[numFrames * NumBins];

        for (var frame = 0; frame < numFrames; frame++)
        {
            Array.Fill(im, 0.0);
            var offset = frame * Hop;
            for (var k = 0; k < Nfft; k++)
            {
                re[k] = padded[offset + k] * Window[k];
            }

            Fft400.Forward(re, im, tmpRe, tmpIm);
            var base_ = frame * NumBins;
            for (var bin = 0; bin < NumBins; bin++)
            {
                power[base_ + bin] = re[bin] * re[bin] + im[bin] * im[bin];
            }
        }

        // mel：filters.T @ power，再 floor、log10。numpy 里谱是 (bins, frames)，
        // 这里按帧主序存储，直接对每个 (frame, mel) 求点积。
        var logSpec = new double[numFrames * Mels];
        var melMax = double.NegativeInfinity;
        for (var frame = 0; frame < numFrames; frame++)
        {
            var frameBase = frame * NumBins;
            var outBase = frame * Mels;
            for (var mel = 0; mel < Mels; mel++)
            {
                var sum = 0.0;
                for (var bin = 0; bin < NumBins; bin++)
                {
                    sum += MelFilters[bin * Mels + mel] * power[frameBase + bin];
                }

                var value = Math.Log10(Math.Max(1e-10, sum));
                logSpec[outBase + mel] = value;
                if (value > melMax)
                {
                    melMax = value;
                }
            }
        }

        // 丢最后一帧（上游 log_spec[:, :-1]，801 → 800），max-8 钳制，(x+4)/4，float32。
        var result = new float[Frames * Mels];
        var floor = melMax - 8.0;
        for (var frame = 0; frame < Frames; frame++)
        {
            for (var mel = 0; mel < Mels; mel++)
            {
                var value = Math.Max(logSpec[frame * Mels + mel], floor);
                result[mel * Frames + frame] = (float)((value + 4.0) / 4.0);
            }
        }

        return result;
    }

    /// <summary>
    /// transformers <c>mel_filter_bank(201, 80, 0, 8000, 16000, norm="slaney", mel_scale="slaney")</c>。
    /// 滤波器组是常量，float64 现算一次；与 Python 的差只在 double 舍入级。
    /// </summary>
    private static void BuildMelFilterBank()
    {
        Span<double> fftFreqs = stackalloc double[NumBins];
        for (var i = 0; i < NumBins; i++)
        {
            fftFreqs[i] = i * (8000.0 / (NumBins - 1));   // np.linspace(0, 8000, 201)
        }

        // slaney hz→mel：3·f/200，≥1000 Hz 后 15 + ln(f/1000)·(27/ln 6.4)。
        var logstepHtz = 27.0 / Math.Log(6.4);
        var melMax = 15.0 + Math.Log(8000.0 / 1000.0) * logstepHtz;

        Span<double> melFreqs = stackalloc double[Mels + 2];
        var melStep = melMax / (Mels + 1);
        for (var i = 0; i < Mels + 1; i++)
        {
            melFreqs[i] = i * melStep;
        }

        melFreqs[Mels + 1] = melMax;   // np.linspace 端点精确赋值

        // slaney mel→hz。
        Span<double> filterFreqs = stackalloc double[Mels + 2];
        var logstepMel = Math.Log(6.4) / 27.0;
        for (var i = 0; i < Mels + 2; i++)
        {
            var m = melFreqs[i];
            filterFreqs[i] = m < 15.0
                ? 200.0 * m / 3.0
                : 1000.0 * Math.Exp(logstepMel * (m - 15.0));
        }

        Span<double> filterDiff = stackalloc double[Mels + 1];
        for (var i = 0; i < Mels + 1; i++)
        {
            filterDiff[i] = filterFreqs[i + 1] - filterFreqs[i];
        }

        // 三角滤波器：down = -(center - f)/diff_left，up = (center+1 步 - f)/diff_right。
        for (var bin = 0; bin < NumBins; bin++)
        {
            var f = fftFreqs[bin];
            for (var mel = 0; mel < Mels; mel++)
            {
                var down = -(filterFreqs[mel] - f) / filterDiff[mel];
                var up = (filterFreqs[mel + 2] - f) / filterDiff[mel + 1];
                var value = Math.Max(0.0, Math.Min(down, up));
                MelFilters[bin * Mels + mel] = value * (2.0 / (filterFreqs[mel + 2] - filterFreqs[mel]));
            }
        }
    }
}
