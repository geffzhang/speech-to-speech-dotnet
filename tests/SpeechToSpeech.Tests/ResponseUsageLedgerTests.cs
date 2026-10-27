using SpeechToSpeech.Realtime.Protocol;

namespace SpeechToSpeech.Tests;

/// <summary>
/// token 记账的不变量。
///
/// 这一层没法进差分语料：上游的记账埋在 <c>ConnState</c> 里，不注入状态就跑不起来。
/// 但它的三条规则每一条错了都只会表现为「数字不对」，没有任何异常、没有任何日志——
/// 所以只能靠用例把它们钉住。
/// </summary>
public sealed class ResponseUsageLedgerTests
{
    [Fact]
    public void AccruesToTheActiveResponse()
    {
        var ledger = new ResponseUsageLedger();

        ledger.Accrue("resp_a", 100, 20, activeResponseKey: "resp_a");
        ledger.Accrue("resp_a", 50, 5, activeResponseKey: "resp_a");

        Assert.Equal(new ResponseUsage(150, 25), ledger.Current);
        Assert.Equal(0, ledger.PendingCount);
    }

    [Fact]
    public void HoldsUsageForANonActiveResponseUntilItIsApplied()
    {
        // 投机轮次的用量会先到，而它可能永远不会成为当前响应。
        var ledger = new ResponseUsageLedger();

        ledger.Accrue("resp_speculative", 900, 300, activeResponseKey: "resp_live");
        ledger.Accrue("resp_live", 100, 20, activeResponseKey: "resp_live");

        // 挂起的那份不得混进当前响应——否则「被丢弃的那一轮」的账算到了活的那一轮头上
        Assert.Equal(new ResponseUsage(100, 20), ledger.Current);
        Assert.Equal(1, ledger.PendingCount);

        ledger.ApplyPending("resp_speculative");
        Assert.Equal(new ResponseUsage(1000, 320), ledger.Current);
        Assert.Equal(0, ledger.PendingCount);
    }

    [Fact]
    public void DiscardsUsageThatArrivesAfterTheResponseClosed()
    {
        var ledger = new ResponseUsageLedger();
        ledger.Accrue("resp_a", 100, 20, activeResponseKey: "resp_a");
        ledger.Close("resp_a");

        // 收口后迟到的用量：丢弃。落进下一轮会让下一轮凭空多出上百 token
        ledger.Accrue("resp_a", 100, 20, activeResponseKey: null);
        Assert.Equal(ResponseUsage.Empty, ledger.Current);
        Assert.Equal(0, ledger.PendingCount);
    }

    [Fact]
    public void ClosingZeroesSoTheNextResponseStartsClean()
    {
        var ledger = new ResponseUsageLedger();
        ledger.Accrue("resp_a", 100, 20, activeResponseKey: "resp_a");
        ledger.Close("resp_a");
        ledger.Accrue("resp_b", 7, 3, activeResponseKey: "resp_b");

        // 用量是每响应计的，不是累计值。归零漏了的话第二轮会带上第一轮的数字
        Assert.Equal(new ResponseUsage(7, 3), ledger.Current);
    }

    [Fact]
    public void UsageWithoutAResponseKeyCountsTowardTheCurrentResponse()
    {
        // 旧路径（内部触发、无 response_key）的用量仍要计入当前响应，不能悄悄丢
        var ledger = new ResponseUsageLedger();
        ledger.Accrue(null, 11, 4, activeResponseKey: "resp_a");

        Assert.Equal(new ResponseUsage(11, 4), ledger.Current);
    }

    [Fact]
    public void ClosingKeepsTheFirstHundredAndTwentyEightKeys()
    {
        // 无界增长会让长连接慢慢吃满内存；更早的 key 不会再被引用。
        var ledger = new ResponseUsageLedger();
        for (var index = 0; index < 200; index++)
        {
            ledger.Close($"resp_{index}");
        }

        Assert.True(ledger.IsClosed("resp_199"));
        Assert.False(ledger.IsClosed("resp_0"));
    }

    [Fact]
    public void PendingTableIsBounded()
    {
        // 被打断的投机轮次永远不会被结转。没有上限时，一次长会话里频繁打断就是一条慢性泄漏。
        var ledger = new ResponseUsageLedger();
        for (var index = 0; index < 200; index++)
        {
            ledger.Accrue($"resp_spec_{index}", 1, 1, activeResponseKey: "resp_live");
        }

        Assert.Equal(128, ledger.PendingCount);
    }

    [Fact]
    public void BeginResponseZeroesCountersButKeepsClosedKeys()
    {
        // 「已关闭 key」的记忆是用来挡上一轮迟到用量的。开新响应时清掉，等于把防护拆了。
        var ledger = new ResponseUsageLedger();
        ledger.Accrue("resp_a", 100, 20, activeResponseKey: "resp_a");
        ledger.Close("resp_a");
        ledger.BeginResponse();

        Assert.Equal(ResponseUsage.Empty, ledger.Current);
        Assert.True(ledger.IsClosed("resp_a"));

        ledger.Accrue("resp_a", 100, 20, activeResponseKey: "resp_b");
        Assert.Equal(ResponseUsage.Empty, ledger.Current);
        Assert.Equal(0, ledger.PendingCount);
    }

    [Fact]
    public void ResetClearsEverythingAcrossSessions()
    {
        var ledger = new ResponseUsageLedger();
        ledger.Accrue("resp_a", 100, 20, activeResponseKey: "resp_a");
        ledger.Accrue("resp_x", 5, 5, activeResponseKey: "resp_a");
        ledger.Close("resp_a");

        ledger.Reset();

        // 新会话不能继承上一会话的账，也不能继承「哪些 key 已关闭」的判断
        Assert.Equal(ResponseUsage.Empty, ledger.Current);
        Assert.Equal(0, ledger.PendingCount);
        Assert.False(ledger.IsClosed("resp_a"));
    }
}
