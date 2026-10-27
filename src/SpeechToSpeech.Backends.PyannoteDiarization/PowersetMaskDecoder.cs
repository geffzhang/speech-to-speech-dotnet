namespace SpeechToSpeech.Backends.PyannoteDiarization;

/// <summary>
/// pyannote segmentation-3.0 输出的纯解码（不碰 ONNX，也不碰 DSP 之外的东西），所以能用合成 logits 单测。
///
/// <para>这里只有<b>帧级、逐说话人</b>那一种解码——流式分离需要的形式。批量「整段切语音区间」的解码
/// （Hamming 加权重叠相加、onset/offset 二值化、帧→秒）不在本文件：本移植没有批量分离路径，
/// 把用不到的代码搬过来只会给未来的读者制造两个真相。</para>
/// </summary>
internal static class PowersetMaskDecoder
{
    /// <summary>
    /// powerset → 逐说话人的映射：第 0 行是静音（全零），随后是单人行，再是双人行——正是 pyannote
    /// powerset 头输出的顺序。用来标记哪些类别意味着「有语音」，以及每个类别激活哪些本地说话人。
    /// </summary>
    public static float[][] BuildPowersetMapping(int numClasses, int numSpeakers, int powersetMaxClasses)
    {
        var mapping = new float[numClasses][];
        for (int c = 0; c < numClasses; c++) mapping[c] = new float[numSpeakers];

        int k = 1; // 第 0 行留成全零（静音）
        for (int i = 1; i <= powersetMaxClasses; i++)
        {
            if (i == 1)
            {
                for (int j = 0; j < numSpeakers; j++) { mapping[k][j] = 1; k++; }
            }
            else if (i == 2)
            {
                for (int j = 0; j < numSpeakers; j++)
                    for (int m = j + 1; m < numSpeakers; m++) { mapping[k][j] = 1; mapping[k][m] = 1; k++; }
            }
            else
            {
                throw new NotSupportedException($"不支持 powerset_max_classes > 2（收到 {powersetMaxClasses}）。");
            }
        }
        return mapping;
    }

    /// <summary>某个类别索引「有语音」，当且仅当它的 powerset 行激活了任何说话人（即不是静音行）。</summary>
    public static bool[] SpeechClasses(float[][] mapping)
    {
        var speech = new bool[mapping.Length];
        for (int c = 0; c < mapping.Length; c++)
            foreach (var v in mapping[c]) if (v > 0) { speech[c] = true; break; }
        return speech;
    }

    /// <summary>
    /// <b>说话人级</b>解码：对帧的 powerset logits 做 softmax，再边缘化到本地说话人——
    /// <c>p[s] = Σ softmax[c]</c>，对所有激活 <c>s</c> 的类别求和。
    ///
    /// <para>这就是「不要过早压平 powerset」在实现上的意思。压成语音/静音会丢掉三样东西：
    /// (a) <i>哪个</i>本地说话人——换人变成一整段；(b) <i>第二个</i>活跃说话人——重叠消失；
    /// (c) 概率本身——编排层再也分不清一帧是笃定还是勉强。边缘化把三样都留着：
    /// 一帧上两个非零项就是两个人在说，而数值正是轮次策略要的可信度。</para>
    ///
    /// <para>注意边缘和故意不为 1：双人类别会给它的两个成员各贡献一份，因为两个通道被当成互相独立的轨道读——
    /// 这正是「同帧两个非零项＝重叠」得以成立的前提。</para>
    /// </summary>
    /// <param name="frameLogits">一帧的原始 powerset logits（宽 <c>num_classes</c>）。</param>
    /// <param name="mapping">来自 <see cref="BuildPowersetMapping"/> 的映射。</param>
    /// <param name="destination">接收每个本地说话人的一个概率；先清零。</param>
    public static void FrameSpeakerProbabilities(
        ReadOnlySpan<float> frameLogits, float[][] mapping, Span<float> destination)
    {
        if (frameLogits.Length != mapping.Length)
            throw new ArgumentException(
                $"帧有 {frameLogits.Length} 个 logit，但 powerset 映射有 {mapping.Length} 个类别。");
        int speakers = mapping.Length == 0 ? 0 : mapping[0].Length;
        if (destination.Length != speakers)
            throw new ArgumentException(
                $"目标必须容纳 {speakers} 个本地说话人；收到 {destination.Length}。");

        destination.Clear();
        if (speakers == 0) return;

        // 数值稳定的 softmax（减最大值），在 powerset 类别上做。
        float max = frameLogits[0];
        for (int c = 1; c < frameLogits.Length; c++) if (frameLogits[c] > max) max = frameLogits[c];

        Span<double> probabilities = frameLogits.Length <= 64
            ? stackalloc double[frameLogits.Length]
            : new double[frameLogits.Length];
        double sum = 0;
        for (int c = 0; c < frameLogits.Length; c++)
        {
            double e = Math.Exp(frameLogits[c] - max);
            probabilities[c] = e;
            sum += e;
        }
        if (sum <= 0) return;

        for (int c = 0; c < frameLogits.Length; c++)
        {
            double p = probabilities[c] / sum;
            var row = mapping[c];
            for (int s = 0; s < row.Length; s++)
                if (row[s] > 0) destination[s] += (float)p;
        }
    }
}
