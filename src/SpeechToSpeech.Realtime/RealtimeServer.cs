using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Realtime.Protocol;
using System.Net.WebSockets;
using System.Text;

namespace SpeechToSpeech.Realtime;

/// <summary>
/// Realtime 服务端宿主。
///
/// 只暴露一条路径：<c>GET /v1/realtime</c>（WebSocket 升级）。
/// 上游把 WebRTC 作为第二条传输并列；C# 首期只有 WebSocket，见差异账 D003。
/// </summary>
public sealed class RealtimeServer
{
    private readonly RealtimeServerOptions _options;
    private readonly BackendContext _backends;

    public RealtimeServer(RealtimeServerOptions options, BackendContext backends)
    {
        _options = options;
        _backends = backends;
    }

    public string Endpoint => $"ws://{_options.Host}:{_options.Port}/v1/realtime";

    /// <summary>构建并启动宿主；返回的 <see cref="WebApplication"/> 需由调用方 Run。</summary>
    public WebApplication Build()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (_options.Verbose)
        {
            builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        }

        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.Listen(
                _options.Host is "0.0.0.0" or "::" ? System.Net.IPAddress.Any : System.Net.IPAddress.Parse(_options.Host),
                _options.Port);
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        app.Map("/v1/realtime", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("expected a WebSocket upgrade request");
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();

            // 建连接会加载后端模型。路径写错、模型缺失都在这一步炸——不接住的话
            // Kestrel 直接把 socket 关掉，客户端只看到「连上了又被断开」，服务端一行日志都没有。
            // 上游也是把异常记下来（logging 默认到 stderr），再把原因告诉客户端。
            try
            {
                await using var transport = new Transports.WebSocketTransport(socket);
                await using var connection = new RealtimeConnection(transport, _options, _backends);
                await connection.RunAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TranscriptLog.Error("realtime connection ended with an error", ex);
                await TrySendConnectionErrorAsync(socket, ex.Message);
            }
        });

        app.MapGet("/healthz", () => Results.Text("ok", "text/plain"));

        return app;
    }

    /// <summary>
    /// 尽力把一条 <c>error</c> 事件送到客户端再关闭。
    ///
    /// 「尽力」是字面意思：连接可能已经半关，发送本身会抛——那种情况下正确的做法是放弃，
    /// 而不是让清理路径再抛一次把原始异常盖掉。
    /// </summary>
    private static async Task TrySendConnectionErrorAsync(WebSocket socket, string message)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            var payload = Encoding.UTF8.GetBytes(ServerEvent.Serialize(
                ServerEvent.Error(ErrorCodes.InternalError, message)));
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TranscriptLog.Error("failed to report the connection error to the client", ex);
        }
    }
}
