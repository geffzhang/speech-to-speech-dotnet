namespace SpeechToSpeech.Core.Backends;

/// <summary>
/// 端点判定器的一次预测。对应上游 <c>SmartTurnResult</c>（少了 <c>inference_ms</c>——
/// 那是测量值，由调用方按自己的时钟记，不该由被测量者自报）。
/// </summary>
/// <param name="Complete">这段话听起来说完了。</param>
/// <param name="Probability">模型给出的「说完」概率，仅用于日志与遥测。</param>
public readonly record struct TurnEndPrediction(bool Complete, double Probability);

/// <summary>
/// Smart Turn 端点判定器接缝。
///
/// 上游把分类器直接 new 在 <c>VADHandler.__init__</c> 里，导致「有没有分类器」这件事
/// 与「怎么用分类器的结论」缠在一起，没有模型就没法测决策层。这里拆成两截：
/// 判定器只回答「说完了没有」，时序决策（宽限多久、处理推迟多久）在
/// <see cref="Pipeline.TurnEndTiming"/> 里，纯函数、可差分。
///
/// 实现方如果要加载 ONNX 模型，**没有模型时抛 <see cref="BackendUnavailableException"/>**，
/// 不要返回「判不出」——那会让决策层走成「说完了」，静默改变行为。
/// </summary>
public interface ITurnEndClassifier
{
    /// <summary>后端名，用于日志与 <c>list-backends</c>。</summary>
    string Name { get; }

    /// <summary>
    /// 判定一段（已升采样到 16 kHz 的）单声道语音是否构成完整轮次。
    ///
    /// <paramref name="audio"/> 是**本轮到目前为止**的全部原始音频（不是最后一段），
    /// 上游 <c>_combined_raw_turn_audio</c> 就是这个语义：用户分两次说同一句，
    /// 第二次的判定要看整句而不只是后半截。截断到模型窗口（8 秒，取尾部）是实现方的事。
    /// </summary>
    /// <exception cref="BackendUnavailableException">模型/运行时缺失。</exception>
    TurnEndPrediction Predict(ReadOnlySpan<float> audio, int sampleRate);
}

/// <summary>
/// 端点判定与投机轮次的配置。对应上游 <c>VadArguments</c> 里 <c>speculative_reopen_ms</c> /
/// <c>unanswered_reopen_ms</c> / <c>smart_turn*</c> 六项。
/// </summary>
public sealed class TurnEndOptions
{
    /// <summary>
    /// 对应 <c>--smart_turn</c>，上游默认开。
    ///
    /// **注意**：打开不等于「有判定器」。模型缺失时注入的判定器为 null
    /// （CLI 会打启动告警并走 <c>disabled</c> 分支），决策层退回
    /// <see cref="SpeculativeReopenMs"/>——与上游「用户显式关掉」在行为上相同，
    /// 所以启动时会打印一行显式告警。
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>说完了之后输出压多久（毫秒）。上游 <c>speculative_reopen_ms</c>，默认 800。</summary>
    public int SpeculativeReopenMs { get; init; } = 800;

    /// <summary>未应答轮次的可复开上限（毫秒）。上游 <c>unanswered_reopen_ms</c>，默认 7000。</summary>
    public int UnansweredReopenMs { get; init; } = 7000;

    /// <summary>判定「没说完」时的宽限（毫秒）。上游 <c>smart_turn_max_wait_ms</c>，默认 2000，必须 &gt; 0。</summary>
    public int MaxWaitMs { get; init; } = 2000;

    /// <summary>判定「没说完」时把 STT/LLM 推迟多久（毫秒）。上游 <c>smart_turn_incomplete_delay_ms</c>，默认 600。</summary>
    public int IncompleteDelayMs { get; init; } = 600;

    /// <summary>模型路径；null 表示由判定器自行下载/定位。</summary>
    public string? ModelPath { get; init; }

    /// <summary>说完的判定阈值（<c>probability &gt; threshold</c>）。上游 <c>smart_turn_threshold</c>，默认 0.5。</summary>
    public double Threshold { get; init; } = 0.5;

    /// <summary>推理线程数。上游 <c>smart_turn_cpu_count</c>，默认 1。</summary>
    public int CpuCount { get; init; } = 1;

    /// <summary>
    /// 校验组合合法性。上游在 <c>VADHandler.__init__</c> 里做，对应到本实现的启动路径
    /// （<c>OptionsBuilder</c>）与 <see cref="Pipeline.Handlers.VadHandler"/> 构造期各调一次——
    /// 两处都调是刻意的：命令行能配错，直接构造 handler 的宿主程序也能配错。
    /// </summary>
    /// <remarks>
    /// <c>MaxWaitMs</c> 为 0 或负会让「没说完」的宽限比「说完了」还短，语义倒置；
    /// <c>IncompleteDelayMs</c> 为负则会让处理期限早于定稿时刻。
    /// </remarks>
    public void Validate()
    {
        if (Enabled && MaxWaitMs <= 0)
        {
            throw new ArgumentException(
                $"smart_turn_max_wait_ms 必须大于 0，当前为 {MaxWaitMs}；" +
                "0 或负值会让「判定没说完」的宽限比「判定说完了」还短");
        }

        if (Enabled && IncompleteDelayMs < 0)
        {
            throw new ArgumentException(
                $"smart_turn_incomplete_delay_ms 不得为负，当前为 {IncompleteDelayMs}");
        }

        if (SpeculativeReopenMs < 0)
        {
            throw new ArgumentException(
                $"speculative_reopen_ms 不得为负，当前为 {SpeculativeReopenMs}");
        }

        if (UnansweredReopenMs < 0)
        {
            throw new ArgumentException(
                $"unanswered_reopen_ms 不得为负，当前为 {UnansweredReopenMs}");
        }
    }
}
