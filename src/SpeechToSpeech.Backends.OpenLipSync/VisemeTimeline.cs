using SpeechToSpeech.Backends.OpenLipSync.Inference;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Backends.OpenLipSync;

/// <summary>
/// OpenLipSync 的 OVR 15 口型流 → 上游 wire 契约（Microsoft SAPI 0–21 口型区间）的转换层。
///
/// 上游 <c>speech_to_speech.output_audio.visemes</c> 事件的载荷是
/// <c>{viseme: int 0-21, start_s, end_s}</c> 区间列表，由 wav2vec2 音素路线产出；
/// 本引擎是逐帧（100 Hz）概率流，转换为「argmax → 相邻同 id 合并成区间」。
/// 引擎与口型集的差异登记在差异账 D009。
/// </summary>
public static class VisemeTimeline
{
    /// <summary>
    /// OVR 15 口型 → Microsoft SAPI 21 口型的标准映射。
    ///
    /// OVR 集合是 MS 集合的粗化（例如 DD/nn 同落 19=D/T/N）——这是音系学上的真实对应，
    /// 不是可逆映射。索引是 OVR id（<see cref="VisemeFrame.VisemeNames"/> 顺序），
    /// 值是 MS id。MS 集的语义：0=sil 2=aa/ao 3=ow 4=eh/er 6=ih/y 7=uw 13=r 15=s/z
    /// 16=sh/ch/jh 17=th/dh 18=f/v 19=d/t/n 20=k/g/ng 21=p/b/m。
    /// </summary>
    public static readonly int[] OvrToMicrosoft =
    [
        0,  // sil  → 0  (sil)
        21, // PP   → 21 (p/b/m)
        18, // FF   → 18 (f/v)
        17, // TH   → 17 (th/dh)
        19, // DD   → 19 (d/t/n)
        20, // kk   → 20 (k/g/ng)
        16, // CH   → 16 (sh/ch/jh/zh)
        15, // SS   → 15 (s/z)
        19, // nn   → 19 (d/t/n)
        13, // RR   → 13 (r)
        2,  // aa   → 2  (aa/ao)
        4,  // E    → 4  (eh/er/ey)
        6,  // ih   → 6  (ih/y)
        3,  // oh   → 3  (ow)
        7,  // ou   → 7  (uw)
    ];

    /// <summary>
    /// 把一帧口型概率转成 Microsoft 口型 id（argmax）。全零概率回落到 sil。
    /// </summary>
    public static int FrameToMicrosoftViseme(ReadOnlySpan<float> probabilities)
    {
        int best = 0;
        float max = -1f;
        for (int i = 0; i < probabilities.Length && i < OvrToMicrosoft.Length; i++)
        {
            if (probabilities[i] > max)
            {
                max = probabilities[i];
                best = i;
            }
        }

        return OvrToMicrosoft[best];
    }

    /// <summary>
    /// 把逐帧（每帧 <paramref name="frameDurationSeconds"/>，等间隔）的概率序列转成
    /// 上游形状的口型区间列表：argmax 取 id，相邻同 id 合并，按 start 排序输出。
    ///
    /// 概率帧来自 <see cref="VisemeSmoother.Output"/>（平滑后的 15 维权重，每帧一次）。
    /// </summary>
    public static List<Viseme> BuildIntervals(
        IReadOnlyList<float[]> probabilityFrames,
        double frameDurationSeconds,
        double offsetSeconds = 0.0)
    {
        var cues = new List<Viseme>();
        if (probabilityFrames.Count == 0 || frameDurationSeconds <= 0)
        {
            return cues;
        }

        int currentViseme = -1;
        double currentStart = 0;
        double currentEnd = 0;

        for (int frame = 0; frame < probabilityFrames.Count; frame++)
        {
            int viseme = FrameToMicrosoftViseme(probabilityFrames[frame]);
            double start = offsetSeconds + (frame * frameDurationSeconds);
            double end = start + frameDurationSeconds;

            if (viseme == currentViseme && start <= currentEnd + 1e-12)
            {
                // 相邻同口型：延长当前区间（容忍浮点缝合误差）。
                currentEnd = end;
                continue;
            }

            if (currentViseme >= 0)
            {
                cues.Add(new Viseme(currentViseme, Math.Round(currentStart, 6), Math.Round(currentEnd, 6)));
            }

            currentViseme = viseme;
            currentStart = start;
            currentEnd = end;
        }

        if (currentViseme >= 0)
        {
            cues.Add(new Viseme(currentViseme, Math.Round(currentStart, 6), Math.Round(currentEnd, 6)));
        }

        return cues;
    }
}
