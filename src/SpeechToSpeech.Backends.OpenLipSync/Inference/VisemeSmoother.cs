namespace SpeechToSpeech.Backends.OpenLipSync.Inference;

/// <summary>
/// 口型概率的时间平滑器（双极点 + 趋势外推，消除单极点的群延迟）。
/// 移植自 BasisVR/Basis com.basis.openlipsync（MIT）。
///
/// 普通单极点 y[n]=a·y[n-1]+(1-a)·x[n] 的群延迟是 a/(1-a) 帧：a=0.7、100 Hz hop 时
/// 每个口型滞后 23 ms。跑两遍极点再沿差值外推（2·y1 - y2）对斜坡输入零延迟，
/// 噪声抑制略降（0.62x vs 0.42x）——嘴上的「错位感」主要来自滞后，划算。
/// </summary>
public sealed class VisemeSmoother
{
    private readonly float[] _stage1;
    private readonly float[] _stage2;
    private readonly float[] _output;
    private float _alpha;

    public VisemeSmoother(int visemeCount, float alpha = 0.7f)
    {
        if (visemeCount <= 0)
        {
            visemeCount = 1;
        }

        _stage1 = new float[visemeCount];
        _stage2 = new float[visemeCount];
        _output = new float[visemeCount];
        _alpha = Sanitize(alpha);
        Reset();
    }

    /// <summary>滞后补偿后的权重。分配一次、原地更新，调用方可缓存引用。</summary>
    public float[] Output => _output;

    /// <summary>保留给上一帧的权重，0..0.99。0 = 直通，双极点塌缩为输入本身。</summary>
    public float Alpha
    {
        get => _alpha;
        set => _alpha = Sanitize(value);
    }

    private static float Sanitize(float alpha)
    {
        if (float.IsNaN(alpha))
        {
            return 0f;
        }

        return alpha < 0f ? 0f : alpha > 0.99f ? 0.99f : alpha;
    }

    /// <summary>推进两个极点一帧并发布 <see cref="Output"/>。</summary>
    public void Step(float[] probabilities)
    {
        int n = _output.Length;
        if (probabilities.Length < n)
        {
            n = probabilities.Length;
        }

        float a = _alpha;
        float inv = 1f - a;

        for (int i = 0; i < n; i++)
        {
            float s1 = (_stage1[i] * a) + (probabilities[i] * inv);
            float s2 = (_stage2[i] * a) + (s1 * inv);
            _stage1[i] = s1;
            _stage2[i] = s2;

            // s1 加一步自身趋势。硬起音上的过冲是超前项的全部意义，
            // 但不能跑出概率空间——这些值直接驱动 blendshape。
            float d = s1 + s1 - s2;
            _output[i] = d < 0f ? 0f : d > 1f ? 1f : d;
        }
    }

    /// <summary>回到闭嘴静息位。两极点同值播种，重置后首帧不带幻影趋势。</summary>
    public void Reset()
    {
        Array.Clear(_stage1, 0, _stage1.Length);
        Array.Clear(_stage2, 0, _stage2.Length);
        Array.Clear(_output, 0, _output.Length);

        // 索引 0 是 "sil"：从静音开始，而不是满零。
        _stage1[0] = 1f;
        _stage2[0] = 1f;
        _output[0] = 1f;
    }
}
