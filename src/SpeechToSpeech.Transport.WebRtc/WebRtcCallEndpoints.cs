using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SIPSorcery.Net;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Realtime;
using SpeechToSpeech.Realtime.Protocol;

namespace SpeechToSpeech.Transport.WebRtc;

/// <summary>
/// WebRTC 会话槽位登记表（上游 pool + <c>_claim_unit</c> 的 WebRTC 侧对应物）。
///
/// 状态独立成类：claim/release/find 都是纯状态操作，可以在没有 HTTP 与
/// WebRTC 栈的环境下单测。上限对齐上游 <c>num_pipelines</c>（默认 1）；
/// claim 失败即上游的 503 <c>session_limit_reached</c>。
/// </summary>
public sealed class WebRtcCallRegistry
{
    private readonly int _limit;
    private readonly ConcurrentDictionary<string, WebRtcSession> _sessions = new(StringComparer.Ordinal);
    private int _active;

    public WebRtcCallRegistry(int limit)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        _limit = limit;
    }

    public int Limit => _limit;

    /// <summary>当前活动会话数。</summary>
    public int Active => Volatile.Read(ref _active);

    /// <summary>
    /// 占一个空闲槽位；满员返回 false（HTTP 层据此回 503）。
    /// </summary>
    public bool TryClaim()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _active);
            if (observed >= _limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _active, observed + 1, observed) == observed)
            {
                return true;
            }
        }
    }

    /// <summary>注册已协商的会话（key = 会话 id，DELETE 的 <c>call_id</c>）。</summary>
    public void Add(WebRtcSession session) => _sessions[session.SessionId] = session;

    /// <summary>
    /// 归还槽位（claim 之后、注册之前的失败路径用：此时还没有可关的会话对象）。
    /// </summary>
    public void ReleaseClaim() => Interlocked.Decrement(ref _active);

    /// <summary>释放：登记移除 + 槽位归还（幂等，关闭回调可能来自多个方向）。</summary>
    public void Release(WebRtcSession session)
    {
        if (_sessions.TryRemove(session.SessionId, out var removed) && ReferenceEquals(removed, session))
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>按 <c>call_id</c> 找未关闭的会话（上游跳过 <c>released_at is not None</c>）。</summary>
    public WebRtcSession? Find(string callId)
    {
        return _sessions.TryGetValue(callId, out var session) && !session.IsClosed ? session : null;
    }
}

/// <summary>
/// OpenAI GA Realtime「calls」HTTP 端点（上游 <c>webrtc_calls_endpoint</c> /
/// <c>webrtc_hangup_endpoint</c> 的对齐实现）。
///
/// <list type="bullet">
/// <item><c>POST /v1/realtime/calls</c>：客户端以 <c>application/sdp</c> 提交 offer，
/// 返回 201 + SDP answer + <c>Location: /v1/realtime/calls/{id}</c>。音频走 WebRTC
/// 媒体轨，事件走 <c>oai-events</c> 数据频道（与 WebSocket 同一协议）。</item>
/// <item><c>DELETE /v1/realtime/calls/{id}</c>：挂断，200；未知 id 404 "Unknown call"。</item>
/// </list>
/// 错误面逐字对齐上游：415（Content-Type）、503（槽位满，JSON error 事件体）、
/// 500（claim/setup 失败）、400（SDP 协商失败）。上游的 501（webrtc extra 未安装）
/// 在 C# 里不可达——传输是 NuGet 硬依赖，不做死分支。
/// </summary>
public static class WebRtcCallEndpoints
{
    public static void MapWebRtcCalls(
        this WebApplication app,
        RealtimeServerOptions options,
        BackendContext backends)
    {
        var registry = new WebRtcCallRegistry(options.WebRtcSessionSlots);

        app.MapPost("/v1/realtime/calls", async (HttpContext context) =>
        {
            // 上游是子串包含（"application/sdp" not in content-type）——charset 等
            // 参数不构成拒绝；全等匹配会把正常客户端全拒掉。
            if (!context.Request.ContentType?.Contains("application/sdp", StringComparison.OrdinalIgnoreCase) ?? true)
            {
                await WriteTextAsync(context, "Content-Type must be application/sdp",
                    StatusCodes.Status415UnsupportedMediaType).ConfigureAwait(false);
                return;
            }

            string offerSdp;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
            {
                offerSdp = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            }

            if (!registry.TryClaim())
            {
                TranscriptLog.Info($"rejected WebRTC offer: all {registry.Limit} session slots are in use");
                var body = ServerEvent.Serialize(ServerEvent.TypeError(
                    ErrorCodes.SessionLimitReached,
                    $"All {registry.Limit} session slots are in use. Disconnect an existing client first."));
                await WriteTextAsync(context, body, StatusCodes.Status503ServiceUnavailable,
                    "application/json").ConfigureAwait(false);
                return;
            }

            WebRtcSession? session = null;
            RealtimeConnection? connection = null;
            try
            {
                // 回调闭包引用后赋值的局部变量：datachannel 事件只会在 negotiate
                // 返回（浏览器拿到 answer）之后触发，那时两者都已非空。
                var configuration = WebRtcSession.ConfigurationFromEnvironment();
                var pc = configuration is null ? new RTCPeerConnection() : new RTCPeerConnection(configuration);
                session = new WebRtcSession(
                    pc,
                    onClientEvent: raw => connection!.DispatchClientEventAsync(raw),
                    onAudio: pcm => connection!.AppendInboundAudio(pcm),
                    onOpen: () => connection!.SendSessionCreatedAsync(),
                    onClosed: () =>
                    {
                        registry.Release(session!);
                        connection?.NotifyTransportClosed();
                        TranscriptLog.Info($"WebRTC client {session!.SessionId} disconnected");
                    });
                connection = new RealtimeConnection(session, options, backends);
                session.SessionId = connection.SessionId;
                session.Setup();
                registry.Add(session);
            }
            catch (Exception ex)
            {
                TranscriptLog.Error("WebRTC session setup failed", ex);
                if (session is not null)
                {
                    // close 触发 onClosed → 槽位在回调里归还。
                    await session.CloseAsync().ConfigureAwait(false);
                }
                else
                {
                    registry.ReleaseClaim();
                }

                await WriteTextAsync(context, "WebRTC session setup failed",
                    StatusCodes.Status500InternalServerError).ConfigureAwait(false);
            }

            string answerSdp;
            var negotiated = session!;
            try
            {
                answerSdp = await negotiated.NegotiateAsync(offerSdp).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TranscriptLog.Error("WebRTC negotiation failed", ex);
                await negotiated.CloseAsync().ConfigureAwait(false);
                await WriteTextAsync(context, "Invalid SDP offer",
                    StatusCodes.Status400BadRequest).ConfigureAwait(false);
                return;
            }

            // 管线与发送循环现在就启动（RunAsync 同步段跑完 StartPipeline + SendLoop
            // 启动），之后 datachannel 一开，session.created 经回调发出。
            var activeSession = session;
            var activeConnection = connection;
            _ = Task.Run(async () =>
            {
                try
                {
                    await activeConnection!.RunAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    TranscriptLog.Error("WebRTC connection ended with an error", ex);
                }
            });

            TranscriptLog.Info($"WebRTC SDP answer returned (session {activeSession!.SessionId})");
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.ContentType = "application/sdp";
            context.Response.Headers.Location = $"/v1/realtime/calls/{activeSession.SessionId}";
            await context.Response.WriteAsync(answerSdp, context.RequestAborted).ConfigureAwait(false);
        });

        app.MapDelete("/v1/realtime/calls/{callId}", async (HttpContext context, string callId) =>
        {
            var session = registry.Find(callId);
            if (session is null)
            {
                await WriteTextAsync(context, "Unknown call", StatusCodes.Status404NotFound).ConfigureAwait(false);
                return;
            }

            TranscriptLog.Info($"WebRTC call {callId} hung up via DELETE");
            // close 触发 onClosed 回调，槽位与登记表在那里释放（幂等）。
            await session.CloseAsync().ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status200OK;
        });
    }

    private static async Task WriteTextAsync(
        HttpContext context, string content, int status, string mediaType = "text/plain")
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = mediaType;
        await context.Response.WriteAsync(content, context.RequestAborted).ConfigureAwait(false);
    }
}
