using System.Collections.Concurrent;
using System.Reflection;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Pipeline.Handlers;
using Xunit;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 取消闸门的判据：**按代际**丢弃，不是「取消过就全丢」。
///
/// 这个区别看着像细节，实际是要么全对要么全错：打断之后 <see cref="CancelScope.Discarding"/>
/// 会一直亮着（打断路径不走 <see cref="CancelScope.ResponseDone"/>），只看它的话，
/// 用户打断后说的下一句话的回复会被整段静默吞掉——客户端收到 created、等到 done，
/// 中间一个字都没有，而服务端日志一切正常、时延账也照常落数。
///
/// 上游专门为此把判断抽成 `_generation_is_discardable`，注释里点明了「discarding 残留会
/// 吞掉新一轮的转写」。这里的用例逐条锁住那两个条件。
/// </summary>
public sealed class CancelScopeDiscardTests
{
    [Fact]
    public void FreshScope_DiscardsNothing()
    {
        var scope = new CancelScope();

        Assert.False(scope.IsDiscardable(scope.Generation));
        Assert.False(scope.IsDiscardable(null));
    }

    [Fact]
    public void AfterCancel_SupersededGenerationIsDiscarded()
    {
        var scope = new CancelScope();
        var live = scope.Generation;

        scope.Cancel();

        Assert.True(scope.IsDiscardable(live));
        Assert.True(scope.Discarding);
    }

    [Fact]
    public void AfterCancel_CurrentGenerationStillPasses()
    {
        var scope = new CancelScope();
        scope.Cancel();

        // 打断之后开的新响应捕获的是新代际，它的内容属于「用户现在正在听的这段」。
        // 这里判错，就是「打断一次之后助手再也不说话」。
        Assert.False(scope.IsDiscardable(scope.Generation));
    }

    [Fact]
    public void AfterCancel_UntaggedOutputIsDiscarded()
    {
        var scope = new CancelScope();
        scope.Cancel();

        // 没有代际标记的输出无法证明自己属于新响应，只能按旧输出处理。
        Assert.True(scope.IsDiscardable(null));
    }

    [Fact]
    public void ResponseDone_ClearsTheDiscardWindow()
    {
        var scope = new CancelScope();
        scope.Cancel();
        var live = scope.Generation;

        scope.ResponseDone();

        Assert.False(scope.Discarding);
        Assert.False(scope.IsDiscardable(null));
        Assert.False(scope.IsDiscardable(live));
    }

    [Fact]
    public void StaleGeneration_StaysDiscardedEvenAfterResponseDone()
    {
        var scope = new CancelScope();
        var superseded = scope.Generation;
        scope.Cancel();

        // 清掉丢弃窗口不等于旧代际的东西可以复活：它属于已经被打断的那一轮。
        scope.ResponseDone();
        Assert.True(scope.IsDiscardable(superseded));
    }

    [Fact]
    public void LateResponseDone_FromAnOlderGenerationIsIgnored()
    {
        var scope = new CancelScope();
        var stale = scope.Generation;
        scope.Cancel();
        scope.Cancel();                 // 第二次取消把丢弃窗口推进到更新的代际

        // 带更早代际的完成通知回来时不得清掉当前的丢弃保护：
        // 它是被打断那一轮的收尸通知，与现在这一轮无关。
        scope.ResponseDone(stale);

        Assert.True(scope.Discarding);
        Assert.True(scope.IsDiscardable(null));
    }

    [Fact]
    public void GenerationWrapsAroundWithoutCollision()
    {
        var scope = new CancelScope();

        // 32 位回绕是上游的口径（`(self._gen + 1) & 0xFFFFFFFF`）。
        // 回绕本身不可观测，可观测的是「回绕之后 IsStale 仍然只对上一代成立」。
        var before = scope.Generation;
        for (var i = 0; i < 3; i++)
        {
            scope.Cancel();
            Assert.True(scope.IsStale(before));
            before = scope.Generation;
        }

        Assert.Equal(3, scope.Generation);
    }

    /// <summary>
    /// 闸门用在 TTS 阶段的效果：打断之后，**新一轮**的文本仍然要合成。
    ///
    /// 这条不进差分语料的原因很实际：它不改变任何计算结果，只决定「合不合成」。
    /// 语料里两侧的字节永远一致，缺陷表现为用户听不到声音而日志全绿。
    /// </summary>
    [Fact]
    public void TtsHandler_AfterInterrupt_StillSynthesisesTheNewGeneration()
    {
        var backend = new CountingTts();
        var scope = new CancelScope();
        scope.Cancel();                 // 打断：discarding 亮着，且不会在这次响应里被清掉。

        var produced = Synthesize(backend, scope, new TtsInput
        {
            Text = "这句话必须念出来",
            CancelGeneration = scope.Generation,
        });

        Assert.Equal(1, backend.Calls);
        Assert.NotEmpty(produced);
    }

    [Fact]
    public void TtsHandler_AfterInterrupt_SkipsTheSupersededGeneration()
    {
        var backend = new CountingTts();
        var scope = new CancelScope();
        var superseded = scope.Generation;
        scope.Cancel();

        var produced = Synthesize(backend, scope, new TtsInput
        {
            Text = "这句属于被打断的那一轮",
            CancelGeneration = superseded,
        });

        Assert.Equal(0, backend.Calls);
        Assert.Empty(produced);
    }

    private static List<object> Synthesize(CountingTts backend, CancelScope scope, TtsInput input)
    {
        var handler = new TtsHandler(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            backend,
            scope,
            new SpeculativeTurnTracker(),
            new TurnLatencyStore());

        var process = typeof(TtsHandler).GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TtsHandler.Process 不存在");

        return [.. (IEnumerable<object>)process.Invoke(handler, [input])!];
    }

    private sealed class CountingTts : ITtsBackend
    {
        public int Calls { get; private set; }

        public string Name => "counting";

        public int SampleRate => 24_000;

        public IEnumerable<byte[]> Synthesize(TtsRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            yield return [0, 0];
        }

        public void Dispose()
        {
        }
    }
}
