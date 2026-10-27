namespace SpeechToSpeech.Backends.OpenLipSync.Inference;

/// <summary>
/// OVR LipSync 兼容的 15 口型帧（sil/PP/FF/TH/DD/kk/CH/SS/nn/RR/aa/E/ih/oh/ou）。
/// </summary>
public sealed class VisemeFrame
{
    public const int VisemeCount = 15;

    public static readonly string[] VisemeNames =
    [
        "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou",
    ];

    public int FrameNumber { get; set; }
    public int FrameDelay { get; set; }
    public float[] Visemes { get; set; } = new float[VisemeCount];
    public float LaughterScore { get; set; }

    public void Reset()
    {
        FrameNumber = 0;
        FrameDelay = 0;
        Array.Clear(Visemes, 0, Visemes.Length);
        LaughterScore = 0f;
    }
}
