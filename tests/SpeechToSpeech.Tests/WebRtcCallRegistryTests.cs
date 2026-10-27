using System.Text.Json;
using SpeechToSpeech.Realtime.Protocol;
using SpeechToSpeech.Transport.WebRtc;

namespace SpeechToSpeech.Tests;

/// <summary>
/// D003-3：WebRTC calls HTTP 端点的可测状态与错误面。
///
/// 槽位登记表是纯状态机（对应上游 pool + <c>_claim_unit</c>）；503 的 JSON 体
/// 是上游 <c>build_error_event(..., "session_limit_reached")</c> 的同形事件。
/// 415/404/500/400 的文本面在 HTTP 层逐字对齐，由 D003-4 的端到端环回验证。
/// </summary>
public sealed class WebRtcCallRegistryTests
{
    private static WebRtcSession NewSession(string id)
    {
        var session = new WebRtcSession(
            new SIPSorcery.Net.RTCPeerConnection(),
            onClientEvent: _ => Task.CompletedTask,
            onAudio: _ => { },
            onOpen: () => Task.CompletedTask,
            onClosed: () => { })
        {
            SessionId = id,
        };
        return session;
    }

    [Fact]
    public void TryClaim_AcceptsUpToLimit_ThenRejects()
    {
        var registry = new WebRtcCallRegistry(2);

        Assert.True(registry.TryClaim());
        Assert.True(registry.TryClaim());
        Assert.False(registry.TryClaim());
        Assert.Equal(2, registry.Active);
    }

    [Fact]
    public void TryClaim_FailsImmediately_AtLimitOne()
    {
        var registry = new WebRtcCallRegistry(1);

        Assert.True(registry.TryClaim());
        Assert.False(registry.TryClaim());
    }

    [Fact]
    public void Release_ReturnsSlot_AndIsIdempotent()
    {
        var registry = new WebRtcCallRegistry(1);
        Assert.True(registry.TryClaim());

        var session = NewSession("call_aaa");
        registry.Add(session);

        registry.Release(session);
        Assert.Equal(0, registry.Active);

        // 重复释放（关闭回调可能来自多个方向）：登记已移除，计数不再递减。
        registry.Release(session);
        Assert.Equal(0, registry.Active);

        Assert.True(registry.TryClaim());
    }

    [Fact]
    public void Release_UnknownSession_DoesNotTouchCounter()
    {
        var registry = new WebRtcCallRegistry(1);
        Assert.True(registry.TryClaim());

        // 未注册的会话对象不递减——否则另一个会话的释放会凭空多出一个槽位。
        registry.Release(NewSession("call_unknown"));
        Assert.Equal(1, registry.Active);
    }

    [Fact]
    public void ReleaseClaim_UndoClaimBeforeRegistration()
    {
        var registry = new WebRtcCallRegistry(1);
        Assert.True(registry.TryClaim());
        registry.ReleaseClaim();
        Assert.True(registry.TryClaim());
    }

    [Fact]
    public void Find_ReturnsActiveSession_SkipsClosed()
    {
        var registry = new WebRtcCallRegistry(1);
        Assert.True(registry.TryClaim());

        var live = NewSession("call_live");
        var closed = NewSession("call_closed");
        registry.Add(live);
        registry.Add(closed);

        Assert.Same(live, registry.Find("call_live"));
        Assert.Null(registry.Find("call_missing"));
    }

    [Fact]
    public async Task Find_AfterCloseAsync_SkipsReleasedSession()
    {
        var registry = new WebRtcCallRegistry(1);
        Assert.True(registry.TryClaim());

        var session = NewSession("call_bye");
        registry.Add(session);
        Assert.False(session.IsClosed);

        await session.CloseAsync();

        Assert.True(session.IsClosed);
        Assert.Null(registry.Find("call_bye"));
    }

    [Fact]
    public void Registry_RejectsNonPositiveLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebRtcCallRegistry(0));
    }

    [Fact]
    public void SessionLimitBody_MatchesUpstreamErrorEventShape()
    {
        // 上游：build_error_event("All {len(pool)} session slots are in use. "
        //   "Disconnect an existing client first.", error_type="session_limit_reached")
        // 形状 = make_error 口径：error.type 直接放错误类型、无 code 字段。
        var payload = ServerEvent.Serialize(ServerEvent.TypeError(
            ErrorCodes.SessionLimitReached,
            "All 1 session slots are in use. Disconnect an existing client first."));

        var json = JsonDocument.Parse(payload).RootElement;
        Assert.Equal("error", json.GetProperty("type").GetString());
        Assert.StartsWith("event_", json.GetProperty("event_id").GetString());
        var error = json.GetProperty("error");
        Assert.Equal("session_limit_reached", error.GetProperty("type").GetString());
        Assert.Equal(
            "All 1 session slots are in use. Disconnect an existing client first.",
            error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("code", out _));
    }
}
