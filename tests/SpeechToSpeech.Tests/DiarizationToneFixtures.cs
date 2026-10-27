using SpeechToSpeech.Audio.Diarization;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 流式分离测试用的合成双声：A 是 200 Hz 音，B 是 400 Hz 音。假分割引擎与假声纹引擎都把这两个音读回来，
/// 于是一对测试替身表现得像一对真模型（同一份音频、同一个真值）——不需要模型、不需要下载、不需要网络。
/// </summary>
internal static class DiarizationTone
{
    public const int SampleRate = 16_000;
    public const int FrameSamples = 160; // 10 ms —— 模型的帧网格
    public const int ToneABin = 2;       // 160 点变换下的 200 Hz
    public const int ToneBBin = 4;       // 400 Hz

    /// <summary>合成 <paramref name="seconds"/> 秒音频：每段在它的区间里叠加两个音的幅度。</summary>
    public static float[] Build(double seconds, params (double Start, double End, double A, double B)[] parts)
    {
        var audio = new float[(int)Math.Round(seconds * SampleRate)];
        foreach (var (start, end, ampA, ampB) in parts)
        {
            int from = Math.Clamp((int)Math.Round(start * SampleRate), 0, audio.Length);
            int to = Math.Clamp((int)Math.Round(end * SampleRate), from, audio.Length);
            for (int i = from; i < to; i++)
            {
                double t = i / (double)SampleRate;
                audio[i] += (float)(ampA * Math.Sin(2 * Math.PI * 200 * t) + ampB * Math.Sin(2 * Math.PI * 400 * t));
            }
        }
        return audio;
    }

    /// <summary>一帧在 <paramref name="bin"/> 上的 Goertzel 功率（整数 bin ⇒ 两个音正交）。</summary>
    public static double Power(ReadOnlySpan<float> frame, int bin)
    {
        double w = 2 * Math.PI * bin / frame.Length;
        double coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        foreach (float sample in frame)
        {
            double s0 = sample + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return Math.Max(0, s1 * s1 + s2 * s2 - coeff * s1 * s2);
    }

    /// <summary>逐帧归一化音能量；静音帧全零。</summary>
    public static void FrameProbabilities(ReadOnlySpan<float> frame, Span<float> destination)
    {
        double a = Power(frame, ToneABin);
        double b = Power(frame, ToneBBin);
        double total = a + b;
        if (total <= 1e-6) { destination.Clear(); return; }
        destination[0] = (float)(a / total);
        destination[1] = (float)(b / total);
    }
}

/// <summary>
/// 分割模型的替身：逐帧报告它听到多少 200 Hz 与多少 400 Hz。一帧两个非零通道就是重叠发言。
/// </summary>
internal sealed class ToneMaskSegmentation : ISpeakerMaskSegmentation
{
    private readonly int _frameShift;
    private readonly int _frameOffset;

    public ToneMaskSegmentation(int windowSamples, int frameShift = DiarizationTone.FrameSamples, int frameOffset = 0)
    {
        WindowSamples = windowSamples;
        _frameShift = frameShift;
        _frameOffset = frameOffset;
    }

    public int SampleRate => DiarizationTone.SampleRate;
    public int WindowSamples { get; }
    public int FrameShiftSamples => _frameShift;
    public int FrameOffsetSamples => _frameOffset;
    public int MaxLocalSpeakers => 2;

    public SpeakerMaskWindow Analyze(ReadOnlySpan<float> window)
    {
        if (window.Length != WindowSamples)
            throw new ArgumentException($"期望 {WindowSamples} 个样本；收到 {window.Length}。");

        int frames = window.Length / FrameShiftSamples;
        var rows = new float[frames][];
        for (int f = 0; f < frames; f++)
        {
            var row = new float[MaxLocalSpeakers];
            DiarizationTone.FrameProbabilities(window.Slice(f * FrameShiftSamples, FrameShiftSamples), row);
            rows[f] = row;
        }
        return new SpeakerMaskWindow(rows);
    }
}

/// <summary>
/// 声纹模型的替身：这段音频的平均音色，只统计「一个声音占优」的帧
/// （混合重叠的声纹两个人都描述不了，真模型也不会这么干）。
/// </summary>
internal sealed class ToneEmbedding : ISpeakerEmbedding
{
    public int EmbeddingDim => 2;

    /// <summary>取过多少次声纹——用来断言声纹开销随轮次而不是随音频增长。</summary>
    public int Calls { get; private set; }

    public float[] Embed(ReadOnlySpan<float> audio, int sampleRate)
    {
        Calls++;
        var dominant = new double[2];
        var all = new double[2];
        var row = new float[2];
        int dominantFrames = 0;

        for (int offset = 0; offset + DiarizationTone.FrameSamples <= audio.Length; offset += DiarizationTone.FrameSamples)
        {
            DiarizationTone.FrameProbabilities(audio.Slice(offset, DiarizationTone.FrameSamples), row);
            all[0] += row[0];
            all[1] += row[1];
            if (Math.Max(row[0], row[1]) >= 0.7)
            {
                dominant[0] += row[0];
                dominant[1] += row[1];
                dominantFrames++;
            }
        }

        var vector = dominantFrames > 0 ? dominant : all;
        double norm = Math.Sqrt(vector[0] * vector[0] + vector[1] * vector[1]);
        return norm <= 0 ? [0f, 0f] : [(float)(vector[0] / norm), (float)(vector[1] / norm)];
    }
}

/// <summary>按消费方的方式把 upsert 流折成当前状态。</summary>
internal sealed class SegmentTable
{
    private readonly Dictionary<string, SpeakerSegmentUpdate> _byId = [];
    private readonly List<SpeakerMergeInfo> _merges = [];

    public void Apply(DiarizerUpdate update)
    {
        foreach (var segment in update.Segments) _byId[segment.Id] = segment;
        _merges.AddRange(update.Merges);
    }

    public IReadOnlyList<SpeakerSegmentUpdate> All =>
        _byId.Values.OrderBy(s => s.Start).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();

    public IReadOnlyList<SpeakerSegmentUpdate> Final => All.Where(s => s.IsFinal).ToList();

    public IReadOnlyList<SpeakerMergeInfo> Merges => _merges;

    public SpeakerSegmentUpdate? Covering(double seconds) =>
        All.FirstOrDefault(s => seconds >= s.Start && seconds < s.End);
}
