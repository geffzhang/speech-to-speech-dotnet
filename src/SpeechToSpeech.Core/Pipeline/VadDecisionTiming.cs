namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// <c>vad_decision_s</c>：从「用户真的停止说话」到「我们下结论说这段结束了」。
///
/// 这个量不能直接用「段定稿时刻减段尾音频位置」——段尾包含了用来闭合这段的尾部静音，
/// 那样算出来的延迟会把静音长度整个抹掉，看起来像「判定零延迟」。上游的做法是从
/// <b>最后一个有声窗</b>反推时间轴：
///
/// <code>
/// speech_end_at = 收到这一块音频的时刻 - (已缓冲样本 - 最后一个有声窗的结束样本) / 采样率
/// vad_decision_s = max(0, 下结论的时刻 - speech_end_at)
/// </code>
///
/// 拆开看是两段：
/// <list type="bullet">
/// <item>减掉的那一项是<b>尾部静音</b>——音频已经收到，只是当时还不知道用户说完了；</item>
/// <item>剩下的部分是<b>从收块到下结论</b>的处理耗时。</item>
/// </list>
/// 上游注释说「This excludes upstream queue/network delay」：音频在收块之前排队等了多久，
/// 会同时推后「收到时刻」和估计的「语音结束时刻」，两者相减就抵掉了。
/// 换句话说，这个数只量 VAD 自己的确认窗口，不量上游排队。
/// </summary>
public static class VadDecisionTiming
{
    /// <summary>
    /// 算一次判定延迟。任何一项缺失都返回 null——上游同样让这一格空着，
    /// 而不是记 0（0 会在时延分布里混进假样本）。
    /// </summary>
    /// <param name="chunkReceivedAtSeconds">这一块音频被收到的单调时刻。</param>
    /// <param name="decidedAtSeconds">下结论的单调时刻。</param>
    /// <param name="totalSamples">截至下结论时已缓冲的累计样本数（含当前这块）。</param>
    /// <param name="speechEndSample">最后一个有声窗的结束样本位。</param>
    /// <param name="sampleRate">采样率（Hz）。</param>
    public static double? Decide(
        double? chunkReceivedAtSeconds,
        double decidedAtSeconds,
        long totalSamples,
        int? speechEndSample,
        int sampleRate)
    {
        if (chunkReceivedAtSeconds is null || speechEndSample is null || sampleRate <= 0)
        {
            return null;
        }

        // 段内样本可能多于已缓冲数（后端给了未来的位点），钳到 0 而不是让时间倒流。
        var trailingSamples = Math.Max(0L, totalSamples - speechEndSample.Value);
        var speechEndedAtSeconds = chunkReceivedAtSeconds.Value - (double)trailingSamples / sampleRate;
        return Math.Max(0.0, decidedAtSeconds - speechEndedAtSeconds);
    }
}
