using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Realtime;
using SpeechToSpeech.Transport.WebRtc;

namespace SpeechToSpeech.Tests;

/// <summary>
/// D003-4 环回集成：真实 Kestrel + 真实 SIPSorcery SDP/ICE/DTLS/datachannel。
///
/// <list type="bullet">
/// <item>HTTP 错误面（上游 <c>webrtc_calls_endpoint</c> 逐字）：415 Content-Type、
/// 400 无效 offer、404 Unknown call、503 槽位满（JSON error 体）；</item>
/// <item>全链环回：offer → 201 + answer + Location → ICE 环回连接 → datachannel
/// 开启 → <c>session.created</c> 从服务端推送过来（上游 <c>_on_open</c> 口径）。</item>
/// </list>
/// 管线后端传空选项：连接的管线线程允许启动失败（fire-and-forget 只记日志），
/// 事件面不依赖模型——session.created 由 SendSessionCreatedAsync 直发。
/// </summary>
public sealed class WebRtcLoopbackTests
{
    private static readonly RealtimeServerOptions Options = new()
    {
        Host = "127.0.0.1",
        Port = 0,
        DefaultVoice = "ryan",
        DefaultInstructions = "test",
        WebRtcSessionSlots = 1,
    };

    private static BackendContext Backends() => new()
    {
        Vad = new VadOptions(),
        Stt = new SttOptions(),
        Llm = new LlmOptions(),
        Tts = new TtsOptions(),
    };

    private static async Task<(WebApplication App, string BaseUrl)> StartServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapWebRtcCalls(Options, Backends());
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses;
        var baseAddress = new Uri(addresses.First());
        return (app, $"http://{baseAddress.Authority}");
    }

    /// <summary>SIPSorcery 对端：recvonly 音频轨 + oai-events 数据频道，offer 就绪即返回。</summary>
    private static async Task<(RTCPeerConnection Pc, RTCDataChannel Channel)> MakeClientOfferAsync()
    {
        var pc = new RTCPeerConnection();
        // recvonly OPUS：与服务端 sendonly 轨（Setup 里的规格）可协商出交集——
        // PCMU 8000 与 OPUS 48k 无共同编码会 AudioIncompatible 拒 offer。
        // SDP 层即可协商，客户端不发音频就无需 Opus 编解码器实现。
        pc.addTrack(new MediaStreamTrack(
            new AudioFormat((int)AudioCodecsEnum.OPUS, "OPUS", 48_000, 2, "minptime=10;useinbandfec=1"),
            MediaStreamStatusEnum.RecvOnly));
        var channel = await pc.createDataChannel("oai-events");

        var offer = pc.createOffer(null);
        await pc.setLocalDescription(offer);
        await WaitUntilAsync(() => pc.iceGatheringState == RTCIceGatheringState.complete, TimeSpan.FromSeconds(5));
        return (pc, channel);
    }

    private static async Task<HttpResponseMessage> PostOfferAsync(
        HttpClient http, string baseUrl, RTCPeerConnection pc)
    {
        var content = new StringContent(pc.localDescription.sdp.ToString(), Encoding.UTF8, "application/sdp");
        return await http.PostAsync($"{baseUrl}/v1/realtime/calls", content);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met within timeout");
            }

            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Post_WithoutSdpContentType_Returns415()
    {
        var (app, baseUrl) = await StartServerAsync();
        try
        {
            using var http = new HttpClient();
            var response = await http.PostAsync(
                $"{baseUrl}/v1/realtime/calls",
                new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(System.Net.HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            Assert.Equal("Content-Type must be application/sdp", (await response.Content.ReadAsStringAsync()).Trim());
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Delete_UnknownCall_Returns404()
    {
        var (app, baseUrl) = await StartServerAsync();
        try
        {
            using var http = new HttpClient();
            var response = await http.DeleteAsync($"{baseUrl}/v1/realtime/calls/call_nope");

            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Unknown call", (await response.Content.ReadAsStringAsync()).Trim());
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Post_WithGarbageSdp_Returns400_AndReleasesSlot()
    {
        var (app, baseUrl) = await StartServerAsync();
        try
        {
            using var http = new HttpClient();
            var response = await http.PostAsync(
                $"{baseUrl}/v1/realtime/calls",
                new StringContent("this is not sdp", Encoding.UTF8, "application/sdp"));

            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Invalid SDP offer", (await response.Content.ReadAsStringAsync()).Trim());

            // 协商失败即释放：下一个 offer 仍能 claim 到唯一槽位（垃圾 offer 仍 400，
            // 而不是 503）——上游「negotiate 前的失败必须归还 unit」的口径。
            var second = await http.PostAsync(
                $"{baseUrl}/v1/realtime/calls",
                new StringContent("still not sdp", Encoding.UTF8, "application/sdp"));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, second.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Loopback_EstablishesDataChannel_AndReceivesSessionCreated()
    {
        var (app, baseUrl) = await StartServerAsync();
        try
        {
            var (pc, channel) = await MakeClientOfferAsync();
            var sessionCreated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.onmessage += (_, _, data) =>
            {
                var text = Encoding.UTF8.GetString(data);
                if (text.Contains("\"session.created\""))
                {
                    sessionCreated.TrySetResult(text);
                }
            };

            using var http = new HttpClient();
            var response = await PostOfferAsync(http, baseUrl, pc);
            Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("application/sdp", response.Content.Headers.ContentType?.MediaType);
            var location = response.Headers.Location?.OriginalString;
            Assert.Matches("^/v1/realtime/calls/", location);

            var answerSdp = await response.Content.ReadAsStringAsync();
            var result = pc.SetRemoteDescription(SdpType.answer, SDP.ParseSDPDescription(answerSdp));
            Assert.Equal(SetDescriptionResultEnum.OK, result);

            // ICE 环回（同进程 host candidates）+ DTLS + SCTP 握手。
            await WaitUntilAsync(
                () => pc.connectionState == RTCPeerConnectionState.connected,
                TimeSpan.FromSeconds(20));

            await WaitUntilAsync(() => channel.IsOpened, TimeSpan.FromSeconds(10));

            // 服务端 datachannel 开启 → _on_open → session.created 推过来。
            var received = await sessionCreated.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("\"type\":\"session.created\"", received);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Loopback_SecondOfferWhileSessionActive_Returns503SessionLimitReached()
    {
        var (app, baseUrl) = await StartServerAsync();
        try
        {
            var (pc, _) = await MakeClientOfferAsync();
            using var http = new HttpClient();
            var first = await PostOfferAsync(http, baseUrl, pc);
            Assert.Equal(System.Net.HttpStatusCode.Created, first.StatusCode);

            // 第一个会话存活期间，第二个 offer 被唯一槽位拒绝。
            var (pc2, _) = await MakeClientOfferAsync();
            var second = await PostOfferAsync(http, baseUrl, pc2);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, second.StatusCode);

            var body = await second.Content.ReadAsStringAsync();
            Assert.Equal("application/json", second.Content.Headers.ContentType?.MediaType);
            using var json = System.Text.Json.JsonDocument.Parse(body);
            Assert.Equal("error", json.RootElement.GetProperty("type").GetString());
            Assert.Equal("session_limit_reached", json.RootElement.GetProperty("error").GetProperty("type").GetString());
            Assert.Contains("session slots are in use", json.RootElement.GetProperty("error").GetProperty("message").GetString());
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
