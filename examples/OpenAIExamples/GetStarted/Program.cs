//-----------------------------------------------------------------------------
// Filename: Program.cs
//
// Description: A self-contained SIPSorcery WebRTC client that talks to the
// speech-to-speech .NET server's OpenAI Realtime-compatible API (serve mode).
//
// Adapted from sipsorcery's OpenAIExamples/GetStarted. The upstream example
// hardcodes the OpenAI base URL (https://api.openai.com/v1/realtime/) inside
// OpenAIRestClientFactory, so it can only be pointed at our local server by
// injecting a custom IHttpClientFactory through the WebRTCEndPoint DI
// constructor (as done below).
//
// Usage:
//   # start serve first (see docs/api-guide.md §1.2)
//   dotnet run
//
//   Optional env overrides:
//   SPEECH_TO_SPEECH_REALTIME_URL  REST base, default http://127.0.0.1:8765/v1/realtime/
//   S2S_VOICE                      RealtimeVoicesEnum name, default marin
//   S2S_PROMPT                     first response prompt, default "Say Hi!"
//
// NOTE: No echo cancellation in this demo; use a headset so the server's VAD
// barge-in does not hear its own output.
//
// License: BSD 3-Clause, see LICENSE.
//-----------------------------------------------------------------------------

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using SIPSorcery.OpenAI.Realtime;
using SIPSorcery.OpenAI.Realtime.Models;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;

namespace S2sOpenAiExample;

class Program
{
    static async Task Main()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var loggerFactory = new SerilogLoggerFactory(Log.Logger);
        SIPSorcery.LogFactory.Set(loggerFactory);

        // Our server ignores the bearer token, so any non-empty value works
        // (the upstream client only needs it to set the Authorization header).
        var restBase = Environment.GetEnvironmentVariable("SPEECH_TO_SPEECH_REALTIME_URL")
            ?? "http://127.0.0.1:8765/v1/realtime/";

        Log.Information("Connecting to speech-to-speech server at {Base}", restBase);

        var factory = new LocalHttpClientFactory(restBase);
        var restClient = new WebRTCRestClient(factory);

        var webrtcEndPoint = new WebRTCEndPoint(
            loggerFactory.CreateLogger<WebRTCEndPoint>(),
            loggerFactory.CreateLogger<DataChannelMessenger>(),
            restClient);

        // Send/receive audio directly from Windows audio devices.
        var audioEncoder = new AudioEncoder(AudioCommonlyUsedFormats.OpusWebRTC);
        var windowsAudioEp = new WindowsAudioEndPoint(audioEncoder);
        webrtcEndPoint.ConnectAudioEndPoint(windowsAudioEp);

        var voice = ParseVoice(Environment.GetEnvironmentVariable("S2S_VOICE")) ?? RealtimeVoicesEnum.marin;
        var prompt = Environment.GetEnvironmentVariable("S2S_PROMPT") ?? "Say Hi!";

        webrtcEndPoint.OnPeerConnectionConnected += () =>
        {
            Log.Information("WebRTC peer connection established.");

            // Optional session tweak: pick a voice and give the model a short instruction.
            var sessionUpdate = webrtcEndPoint.DataChannelMessenger.SendSessionUpdate(voice, "Keep it short.");
            if (sessionUpdate.IsLeft)
            {
                Log.Error("Failed to send session update: {Err}", sessionUpdate.LeftToList().First());
            }

            // Kick off the first response.
            var createResult = webrtcEndPoint.DataChannelMessenger.SendResponseCreate(voice, prompt);
            if (createResult.IsLeft)
            {
                Log.Error("Failed to send response create: {Err}", createResult.LeftToList().First());
            }
        };

        webrtcEndPoint.OnDataChannelMessage += (dc, message) =>
        {
            var log = message switch
            {
                // The library wraps any event type it does not model as
                // RealtimeUnknown (e.g. our transcription.delta), which we ignore.
                RealtimeServerEventSessionUpdated s => $"Session updated: {s.ToJson()}",
                RealtimeServerEventConversationItemInputAudioTranscriptionCompleted t =>
                    $"ME (transcript): {t.Transcript?.Trim()}",
                RealtimeServerEventResponseAudioTranscriptDone d => $"AI (transcript): {d.Transcript?.Trim()}",
                _ => string.Empty
            };

            if (log != string.Empty)
            {
                Log.Information(log);
            }
        };

        webrtcEndPoint.OnPeerConnectionFailed += () => Log.Error("WebRTC peer connection failed.");
        webrtcEndPoint.OnPeerConnectionClosed += () => Log.Information("WebRTC peer connection closed.");

        var negotiateResult = await webrtcEndPoint.StartConnect();
        if (negotiateResult.IsLeft)
        {
            Log.Error("Failed to negotiate WebRTC connection: {Err}",
                negotiateResult.LeftToList().First());
            return;
        }

        Console.WriteLine("Connected. Press Ctrl+C to exit.");
        var exitTcs = new TaskCompletionSource<object?>();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            exitTcs.TrySetResult(null);
        };
        await exitTcs.Task;

        webrtcEndPoint.Dispose();
    }

    private static RealtimeVoicesEnum? ParseVoice(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Enum.TryParse<RealtimeVoicesEnum>(name, true, out var v) ? v : null;
    }

    /// <summary>
    /// Points the OpenAI Realtime REST client at our local server instead of
    /// OpenAI. The upstream OpenAIRestClientFactory hardcodes the OpenAI base
    /// URL; this factory is what lets us redirect it.
    /// </summary>
    private sealed class LocalHttpClientFactory : IHttpClientFactory
    {
        private readonly string _baseUrl;
        private readonly Lazy<HttpClient> _client;

        public LocalHttpClientFactory(string baseUrl)
        {
            _baseUrl = baseUrl;
            _client = new Lazy<HttpClient>(CreateConfiguredClient);
        }

        public HttpClient CreateClient(string name) => _client.Value;

        private HttpClient CreateConfiguredClient()
        {
            var client = new HttpClient();
            client.BaseAddress = new Uri(_baseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
            // Bearer is required by the upstream client factory pattern; our
            // server ignores it. Any non-empty value works for auth-free local use.
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", "local-app-key");
            return client;
        }
    }
}
