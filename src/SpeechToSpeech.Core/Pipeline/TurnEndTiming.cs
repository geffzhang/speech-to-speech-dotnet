using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Core.Pipeline;

/// <summary>端点判定状态。字符串值与上游 <c>_last_smart_turn_status</c> 逐字相同。</summary>
public static class TurnEndStatus
{
    /// <summary>没有判定器（未启用，或本构建没有可用的分类器）。</summary>
    public const string Disabled = "disabled";

    /// <summary>判定器抛了（模型坏了、音频形状不对……）。</summary>
    public const string Failed = "failed";

    /// <summary>判定说完了。</summary>
    public const string Complete = "complete";

    /// <summary>判定没说完。</summary>
    public const string Incomplete = "incomplete";
}

/// <summary>
/// 一次端点判定的结论：输出压多久（<see cref="GraceMs"/>）、处理推迟多久
/// （<see cref="ProcessingDelayMs"/>），以及遥测三件套。对应上游
/// <c>_smart_turn_timing_ms</c> 的返回值加上 <c>_last_smart_turn_status</c> /
/// <c>_last_smart_turn_analysis_s</c>。
/// </summary>
/// <param name="AnalysisSeconds">判定耗时；<see cref="TurnEndStatus.Disabled"/> 时为 null（上游同样不记）。</param>
public readonly record struct TurnEndDecision(
    int GraceMs,
    int ProcessingDelayMs,
    string Status,
    double? AnalysisSeconds);

/// <summary>
/// 端点判定的时序决策。
///
/// 这一层之所以单独存在：上游把判定器与「拿到结论后怎么用」写在一个方法里，
/// 于是没有模型就无法验证后者。拆开之后，判定器只回答「说完了没有」，
/// 四种结论（有/无判定器、成功、失败）到两个毫秒数的映射是纯函数，
/// 可以拿脚本化判定器做差分语料（<c>corpus/smart-turn</c>）。
///
/// 四条分支的含义不是随便定的：
/// - <b>没有判定器</b> → 退回普通投机窗口 <see cref="TurnEndOptions.SpeculativeReopenMs"/>，不推迟处理。
/// - <b>判定器抛异常</b> → 同上。临时故障不该让回复凭空多等两秒。
/// - <b>说完了</b> → 普通窗口，不推迟。
/// - <b>没说完</b> → 宽限拉到 <see cref="TurnEndOptions.MaxWaitMs"/>，并把处理推迟
///   <see cref="TurnEndOptions.IncompleteDelayMs"/>（上限仍是 MaxWait）。推迟的意义是：
///   用户接着说的那几个字能在 STT/LLM 开工之前把这个修订作废，省掉一次白跑。
/// </summary>
public static class TurnEndTiming
{
    /// <summary>
    /// 决定本轮输出的宽限与处理延迟。
    /// </summary>
    /// <param name="classifier">判定器；null 表示不可用。</param>
    /// <param name="audio">本轮到目前为止的原始音频。</param>
    /// <param name="sampleRate">采样率（Hz）。</param>
    /// <param name="options">配置。</param>
    /// <param name="log">失败日志；null 不记。</param>
    /// <param name="clock">单调时钟（秒），用于测量判定耗时；null 用真实时钟。</param>
    public static TurnEndDecision Decide(
        ITurnEndClassifier? classifier,
        ReadOnlySpan<float> audio,
        int sampleRate,
        TurnEndOptions options,
        Action<string>? log = null,
        Func<double>? clock = null)
    {
        var speculativeReopenMs = options.SpeculativeReopenMs;
        if (classifier is null)
        {
            return new TurnEndDecision(speculativeReopenMs, 0, TurnEndStatus.Disabled, null);
        }

        var now = clock ?? Monotonic.NowSeconds;
        var startedAt = now();

        TurnEndPrediction prediction;
        try
        {
            prediction = classifier.Predict(audio, sampleRate);
        }
        catch (Exception ex)
        {
            // 上游同样宽泛捕获：临时故障退回普通投机窗口，而不是让回复多等 MaxWait。
            log?.Invoke($"端点判定失败（{classifier.Name}），退回默认投机宽限：{ex.Message}");
            return new TurnEndDecision(
                speculativeReopenMs,
                0,
                TurnEndStatus.Failed,
                Math.Max(0, now() - startedAt));
        }

        var analysisSeconds = Math.Max(0, now() - startedAt);

        // 非有限概率说明模型输出坏了。上游在判定器内部就抛了，这里的防御是为了
        // 「实现方忘了抛」时行为仍与上游一致，而不是静默当成「没说完」压下两秒。
        if (!double.IsFinite(prediction.Probability))
        {
            log?.Invoke($"端点判定返回非有限概率（{classifier.Name}）：{prediction.Probability}");
            return new TurnEndDecision(speculativeReopenMs, 0, TurnEndStatus.Failed, analysisSeconds);
        }

        if (prediction.Complete)
        {
            return new TurnEndDecision(speculativeReopenMs, 0, TurnEndStatus.Complete, analysisSeconds);
        }

        // 上游这里不做任何钳制：非法组合由构造期校验挡住（MaxWait 必须为正、
        // IncompleteDelay 不得为负）。纯函数照抄语义，不发明第二套兜底——
        // 两处各兜一次，兜的值不同时没人说得清哪个才是「对」的。
        var maxWaitMs = options.MaxWaitMs;
        var processingDelayMs = Math.Min(options.IncompleteDelayMs, maxWaitMs);
        return new TurnEndDecision(maxWaitMs, processingDelayMs, TurnEndStatus.Incomplete, analysisSeconds);
    }
}

/// <summary>
/// 判定器的输入窗口。对应上游 <c>SmartTurnAnalyzer._prepare_audio</c> 的 16 kHz 路径。
///
/// 单列出来的理由与决策层相同：模型预处理里最容易错的是「取头还是取尾」和「短了往哪边补」，
/// 这两点不看数据看不出来——说得久的用户，前两秒的犹豫会被保留而结尾被丢掉，判定就反了。
/// 上游取**尾部** 8 秒（<c>audio[-max_samples:]</c>），短了在**前面**补零。
/// </summary>
public static class TurnAudioWindow
{
    /// <summary>上游 <c>MODEL_SAMPLE_RATE</c>。</summary>
    public const int ModelSampleRate = 16_000;

    /// <summary>上游 <c>MAX_AUDIO_SECONDS</c>。</summary>
    public const int MaxAudioSeconds = 8;

    /// <summary>窗口样本数（128,000）。</summary>
    public const int MaxSamples = MaxAudioSeconds * ModelSampleRate;

    /// <summary>
    /// 把任意长度的单声道音频整理成模型要的定长窗口。
    /// </summary>
    /// <param name="audio">原始音频。非 16 kHz 时先重采样（上游走 <c>resample_poly</c>，
    /// 本实现走自带的 <see cref="AudioUtil.Resample"/>，两者不逐样本相同——但 VAD 阶段
    /// 送到这里的音频恒为 16 kHz，这条路只在外部直接调用时才走到）。</param>
    public static float[] Prepare(ReadOnlySpan<float> audio, int sampleRate = ModelSampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "采样率必须为正");
        }

        var samples = sampleRate == ModelSampleRate
            ? audio.ToArray()
            : AudioUtil.Resample(audio.ToArray(), sampleRate, ModelSampleRate);

        if (samples.Length == MaxSamples)
        {
            return samples;
        }

        var window = new float[MaxSamples];
        if (samples.Length > MaxSamples)
        {
            // 取尾部：判定「说完了没有」看的是最后那句话的收束，不是开头的犹豫。
            samples.AsSpan(samples.Length - MaxSamples).CopyTo(window);
            return window;
        }

        // 短了往前面补零：模型按「右对齐到当下」训练，左补才是同一个时间轴。
        samples.AsSpan().CopyTo(window.AsSpan(MaxSamples - samples.Length));
        return window;
    }
}
