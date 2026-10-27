using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 端点判定的时序决策层。
///
/// 这一层的价值在于「没有模型也能验证」：判定器只回答「说完了没有」，
/// 四种情形（没有判定器 / 说完了 / 没说完 / 判定器坏了）到两个毫秒数的映射是纯函数，
/// 与上游 <c>_smart_turn_timing_ms</c> 由 <c>corpus/smart-turn</c> 逐值比对。
/// 这里补上语料压不住的三件事：时钟、异常路径、窗口形状。
/// </summary>
public sealed class TurnEndTimingTests
{
    /// <summary>结论由构造参数定死的判定器，顺便记下它拿到的音频。</summary>
    private sealed class StubClassifier : ITurnEndClassifier
    {
        private readonly bool _complete;
        private readonly double _probability;
        private readonly bool _fail;

        public StubClassifier(bool complete = true, double probability = 0.9, bool fail = false)
        {
            _complete = complete;
            _probability = probability;
            _fail = fail;
        }

        public string Name => "stub";

        public float[]? LastAudio { get; private set; }

        public TurnEndPrediction Predict(ReadOnlySpan<float> audio, int sampleRate)
        {
            LastAudio = audio.ToArray();
            return _fail
                ? throw new InvalidOperationException("boom")
                : new TurnEndPrediction(_complete, _probability);
        }
    }

    /// <summary>步进时钟：第一次读返回起点，之后每次 +50ms。用来把「判定耗时」变成确定值。</summary>
    private sealed class StepClock
    {
        private int _reads;

        public double Now() => 1000.0 + (0.05 * _reads++);
    }

    private static TurnEndOptions Options(
        bool enabled = true,
        int speculativeReopenMs = 800,
        int maxWaitMs = 2000,
        int incompleteDelayMs = 600)
        => new()
        {
            Enabled = enabled,
            SpeculativeReopenMs = speculativeReopenMs,
            MaxWaitMs = maxWaitMs,
            IncompleteDelayMs = incompleteDelayMs,
        };

    [Fact]
    public void NoClassifier_ReportsDisabledAndFallsBackToSpeculativeGrace()
    {
        var decision = TurnEndTiming.Decide(null, [], 16_000, Options());

        Assert.Equal(TurnEndStatus.Disabled, decision.Status);
        Assert.Equal(800, decision.GraceMs);
        Assert.Equal(0, decision.ProcessingDelayMs);

        // 没有判定器就没有「判定耗时」可言。上游同样留 None，不是 0——
        // 0 会被下游当成「判定瞬间返回」，在时延分解里混进一个假样本。
        Assert.Null(decision.AnalysisSeconds);
    }

    [Fact]
    public void Complete_UsesSpeculativeGraceAndStartsProcessingImmediately()
    {
        var decision = TurnEndTiming.Decide(
            new StubClassifier(complete: true), [], 16_000, Options(), clock: new StepClock().Now);

        Assert.Equal(TurnEndStatus.Complete, decision.Status);
        Assert.Equal(800, decision.GraceMs);
        Assert.Equal(0, decision.ProcessingDelayMs);
        Assert.NotNull(decision.AnalysisSeconds);
    }

    [Fact]
    public void Incomplete_WidensGraceAndDelaysProcessing()
    {
        var decision = TurnEndTiming.Decide(
            new StubClassifier(complete: false), [], 16_000, Options(), clock: new StepClock().Now);

        Assert.Equal(TurnEndStatus.Incomplete, decision.Status);

        // 宽限从 800 拉到 2000：用户只是中途换气，输出不该在 800ms 后就发出去
        Assert.Equal(2000, decision.GraceMs);

        // 处理推迟 600ms：让用户接着说出的几个字在 STT/LLM 开工之前作废本轮修订
        Assert.Equal(600, decision.ProcessingDelayMs);
    }

    [Fact]
    public void IncompleteDelayAboveMaxWait_IsClampedToMaxWait()
    {
        var decision = TurnEndTiming.Decide(
            new StubClassifier(complete: false),
            [],
            16_000,
            Options(maxWaitMs: 500, incompleteDelayMs: 900));

        // 不钳制的话，判定给自己发的期限会比它自己的宽限还晚
        Assert.Equal(500, decision.GraceMs);
        Assert.Equal(500, decision.ProcessingDelayMs);
    }

    [Fact]
    public void ClassifierFailure_FallsBackWithoutDelayingAndKeepsTiming()
    {
        var decision = TurnEndTiming.Decide(
            new StubClassifier(fail: true), [], 16_000, Options(), clock: new StepClock().Now);

        // 临时故障退回普通投机窗口：不该因为分类器坏了就让回复多等两秒
        Assert.Equal(TurnEndStatus.Failed, decision.Status);
        Assert.Equal(800, decision.GraceMs);
        Assert.Equal(0, decision.ProcessingDelayMs);

        // 但耗时仍要记：故障有多慢本身就是要观测的东西
        Assert.Equal(0.05, decision.AnalysisSeconds!.Value, 6);
    }

    [Fact]
    public void NonFiniteProbability_IsTreatedAsFailureNotAsIncomplete()
    {
        // 判定器忘了抛异常、直接返回 NaN 时，不能静默当成「没说完」——那会压住两秒
        var decision = TurnEndTiming.Decide(
            new StubClassifier(complete: false, probability: double.NaN), [], 16_000, Options());

        Assert.Equal(TurnEndStatus.Failed, decision.Status);
        Assert.Equal(800, decision.GraceMs);
        Assert.Equal(0, decision.ProcessingDelayMs);
    }

    [Fact]
    public void Decide_PassesWholeTurnAudioToClassifier()
    {
        var classifier = new StubClassifier();
        var audio = new float[TurnAudioWindow.MaxSamples];

        TurnEndTiming.Decide(classifier, audio, 16_000, Options());

        Assert.Equal(TurnAudioWindow.MaxSamples, classifier.LastAudio!.Length);
    }

    [Fact]
    public void Prepare_ShortAudioPadsAtTheFront()
    {
        var window = TurnAudioWindow.Prepare([0.25f, 0.5f, 0.75f]);

        Assert.Equal(TurnAudioWindow.MaxSamples, window.Length);

        // 往前面补零：右对齐到「刚刚说完」。往后面补会把整句话挪到时间轴开头，
        // 模型看到的就不是「刚刚说完」而是「很久以前说过」。
        Assert.Equal(0f, window[0]);
        Assert.Equal(0f, window[^4]);
        Assert.Equal(0.25f, window[^3]);
        Assert.Equal(0.5f, window[^2]);
        Assert.Equal(0.75f, window[^1]);
    }

    [Fact]
    public void Prepare_LongAudioKeepsTheTail()
    {
        var audio = new float[TurnAudioWindow.MaxSamples + 5];
        for (var i = 0; i < audio.Length; i++)
        {
            audio[i] = i;
        }

        var window = TurnAudioWindow.Prepare(audio);

        // 取尾部：判定看的是最后那句话的收束，不是开头的犹豫
        Assert.Equal(TurnAudioWindow.MaxSamples, window.Length);
        Assert.Equal(5f, window[0]);
        Assert.Equal(audio.Length - 1, window[^1]);
    }

    [Fact]
    public void Prepare_ExactWindowIsReturnedUnchanged()
    {
        var audio = new float[TurnAudioWindow.MaxSamples];
        audio[0] = 1f;
        audio[^1] = 2f;

        var window = TurnAudioWindow.Prepare(audio);

        Assert.Equal(1f, window[0]);
        Assert.Equal(2f, window[^1]);
    }

    [Fact]
    public void Prepare_EmptyAudioBecomesAllZeroWindow()
    {
        // phantom 触发后定稿的空段会走到这里，不能抛异常也不能返回空数组
        var window = TurnAudioWindow.Prepare([]);

        Assert.Equal(TurnAudioWindow.MaxSamples, window.Length);
        Assert.All(window, sample => Assert.Equal(0f, sample));
    }
}
