using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Realtime;

/// <summary>
/// Realtime 服务端宿主。
///
/// 只暴露一条路径：<c>GET /v1/realtime</c>（WebSocket 升级）。
/// 上游把 WebRTC 作为第二条传输并列；C# 首期只有 WebSocket，见差异账 D-03。
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
            await using var connection = new RealtimeConnection(socket, _options, _backends);
            await connection.RunAsync();
        });

        app.MapGet("/healthz", () => Results.Text("ok", "text/plain"));

        return app;
    }
}
