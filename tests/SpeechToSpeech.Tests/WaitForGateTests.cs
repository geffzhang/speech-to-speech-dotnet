using System.Diagnostics;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 阻塞式放行（<see cref="SpeculativeTurnTracker.WaitForGate"/>）。
///
/// 纯状态迁移由 <c>corpus/turns</c> 的 13 条差分语料与上游逐值比对，
/// 这里只锁语料压不住的那部分：**等待的人怎么被叫醒**。三件事缺一不可——
/// 被丢弃的工作要立刻返回 false 而不是白等到期限、
/// 被接受的工作要在状态变化时立刻醒、等 grace 的时长要能报出来。
///
/// 这些用例会用真实时钟（影子线程 + 短宽限），所以断言都留了大余量：
/// 目标是抓"永不返回"和"提前返回"，不是抓毫秒级偏差。
/// </summary>
public sealed class WaitForGateTests
{
    [Fact]
    public void NoTurnLabel_ReturnsImmediately()
    {
        var tracker = new SpeculativeTurnTracker();

        // 无轮次标签的工作（旧客户端、内部触发）不该参与状态机
        Assert.True(tracker.WaitForGate(null, null));
        Assert.True(tracker.WaitForGate("turn_1", null));
    }

    [Fact]
    public void CommittedTurn_IsNeverHeld()
    {
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();
        Assert.True(tracker.WaitForGate(turnId, revision, commit: true));

        // 已放行的工作再压住，回复会卡在半截、音频流永不关闭
        Assert.True(tracker.WaitForGate(turnId, revision));
    }

    [Fact]
    public void SupersededRevision_ReturnsFalseWithoutWaiting()
    {
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();

        // 开下一轮会让上一轮的修订变成陈旧引用：该立刻判定为丢弃，
        // 不能因为"没有状态变化"就一直睡在期限上
        tracker.StartTurn();

        var stopwatch = Stopwatch.StartNew();
        Assert.False(tracker.WaitForGate(turnId, revision));
        Assert.True(stopwatch.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public void HeldByReopenGrace_WakesWhenGraceExpires()
    {
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();
        tracker.SegmentFinalized(audioMs: 1000, outputHoldMs: 250);

        var stopwatch = Stopwatch.StartNew();
        Assert.True(tracker.WaitForGate(turnId, revision));

        // 必须真的等过一段（不能把 HOLD 当 ACCEPT 直接放行），
        // 且必须在宽限结束后回来（不能永远睡下去）
        Assert.True(stopwatch.ElapsedMilliseconds >= 150, $"等了 {stopwatch.ElapsedMilliseconds}ms，宽限未生效");
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"等了 {stopwatch.ElapsedMilliseconds}ms，宽限未释放");
    }

    [Fact]
    public void HeldByReopenGrace_WakesOnNewTurnDiscountingIt()
    {
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();
        tracker.SegmentFinalized(audioMs: 1000, outputHoldMs: 30_000);

        var released = false;
        var worker = new Thread(() => released = tracker.WaitForGate(turnId, revision));
        worker.Start();
        Thread.Sleep(150);

        // 用户已经说下一句了：旧修订被取代，等待者必须被叫醒并且拿到 false
        tracker.StartTurn();

        Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "状态变化没有唤醒等待者");
        Assert.False(released);
    }

    [Fact]
    public void WaitObserver_ReportsGraceWaitWithTurnLabel()
    {
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();
        tracker.SegmentFinalized(audioMs: 1000, outputHoldMs: 200);

        (string TurnId, int Revision, double Started, double Ended)? observed = null;
        tracker.WaitObserver = (id, rev, started, ended) => observed = (id, rev, started, ended);

        Assert.True(tracker.WaitForGate(turnId, revision));

        // 观察者只统计"等 grace"这一类等待：它服务于打断延迟指标，
        // 把等 VAD 判定的时间混进来会把两件事算成一件
        Assert.NotNull(observed);
        Assert.Equal(turnId, observed!.Value.TurnId);
        Assert.Equal(revision, observed.Value.Revision);
        Assert.True(observed.Value.Ended >= observed.Value.Started);
    }

    [Fact]
    public void WaitObserver_NotFiredWhenNothingWasHeld()
    {
        var tracker = new SpeculativeTurnTracker();
        var (turnId, revision) = tracker.StartTurn();

        var fired = false;
        tracker.WaitObserver = (_, _, _, _) => fired = true;

        Assert.True(tracker.WaitForGate(turnId, revision));
        Assert.False(fired);
    }
}
