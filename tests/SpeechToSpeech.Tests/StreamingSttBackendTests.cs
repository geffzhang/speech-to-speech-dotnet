using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SpeechToSpeech.Backends.OpenAiCompatible.StreamingStt;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using Xunit;

namespace SpeechToSpeech.Tests;

/// <summary>
/// <see cref="StreamingSttBackend"/> 的接线测试：Attach → sink → Transcribe 全链
/// （fake socket 在进程内扮演转写服务）。
/// </summary>
public class StreamingSttBackendTests
{
    private sealed class FakeSocket : IStreamingSocket
    {
        public ConcurrentQueue<string> Inbox { get; } = new();
        public ConcurrentQueue<string> Sent { get; } = new();

        public Action<FakeSocket, JsonObject>? OnSend { get; set; }

        public void Send(string message)
        {
            Sent.Enqueue(message);
            OnSend?.Invoke(this, JsonNode.Parse(message)!.AsObject());
        }

        public string? Receive(TimeSpan timeout)
        {
            if (Inbox.TryDequeue(out var message))
            {
                return message;
            }

            Thread.Sleep(5);
            return null;
        }

        public void Dispose()
        {
        }

        public IEnumerable<JsonObject> SentEvents() => Sent.Select(m => JsonNode.Parse(m)!.AsObject());
    }

    private static (FakeSocket Socket, StreamingConnectFactory Factory) MakeFake(Action<FakeSocket, JsonObject>? onSend)
    {
        var socket = new FakeSocket();
        StreamingConnectFactory factory = (_, _, _) =>
        {
            socket.Inbox.Enqueue(new JsonObject { ["type"] = "session.created" }.ToJsonString());
            return socket;
        };
        socket.OnSend = (s, evt) =>
        {
            if ((string?)evt["type"] == "session.update")
            {
                s.Inbox.Enqueue(new JsonObject { ["type"] = "session.updated" }.ToJsonString());
            }

            onSend?.Invoke(s, evt);
        };
        return (socket, factory);
    }

    private static StreamingSttBackend MakeBackend(StreamingConnectFactory factory, int sampleRate = 16_000)
    {
        var backend = new StreamingSttBackend(new OpenAiRealtimeProtocolFactory(), new SttOptions
        {
            BaseUrl = "ws://fake/realtime",
            StreamingModel = "whisper-1",
            SampleRate = sampleRate,
        }, factory);
        Assert.NotNull(backend.Attach(new BlockingCollection<object>(), new SpeculativeTurnTracker(),
            CancellationToken.None));
        return backend;
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"timed out waiting for {what}");
    }

    private static VadAudio Vad(float[] audio) => new()
    {
        Audio = audio,
        SampleRate = 16_000,
        Mode = "final",
        TurnId = null,
        TurnRevision = null,
    };

    [Fact]
    public void Sink_AppendAudio_ReachesWireAsBase64Pcm16()
    {
        var (socket, factory) = MakeFake(null);
        using var backend = MakeBackend(factory);

        backend.AppendAudio([0.1f, -0.1f, 0.5f]);

        WaitFor(() => socket.SentEvents().Any(e => (string?)e["type"] == "input_audio_buffer.append"), "append");
        var append = socket.SentEvents().First(e => (string?)e["type"] == "input_audio_buffer.append");
        var expected = Convert.ToBase64String(SpeechToSpeech.Core.Audio.AudioUtil.ToPcm16([0.1f, -0.1f, 0.5f]));
        Assert.Equal(expected, (string?)append["audio"]);
    }

    [Fact]
    public void Transcribe_CompletesWithResult()
    {
        var (socket, factory) = MakeFake((s, evt) =>
        {
            switch ((string?)evt["type"])
            {
                case "input_audio_buffer.append":
                    s.Inbox.Enqueue(new JsonObject
                    {
                        ["type"] = "input_audio_buffer.committed",
                        ["item_id"] = "item_1",
                    }.ToJsonString());
                    break;
                case "input_audio_buffer.commit":
                    s.Inbox.Enqueue(new JsonObject
                    {
                        ["type"] = "conversation.item.input_audio_transcription.completed",
                        ["item_id"] = "item_1",
                        ["content_index"] = 0,
                        ["transcript"] = "整句转写",
                        ["languages"] = new JsonArray("zh"),
                    }.ToJsonString());
                    break;
            }
        });
        using var backend = MakeBackend(factory);

        backend.StartTurn(null, null);
        backend.AppendAudio([0.1f, 0.2f]);
        backend.CommitBoundary(null, null);

        var messages = backend.Transcribe(Vad([0.1f]), CancellationToken.None).ToList();

        var final = Assert.IsType<Transcription>(messages.Single());
        Assert.Equal("整句转写", final.Text);
        Assert.Equal("zh", final.LanguageCode);
    }

    [Fact]
    public void Transcribe_RemoteError_YieldsFailureNotException()
    {
        var (socket, factory) = MakeFake((s, evt) =>
        {
            if ((string?)evt["type"] == "input_audio_buffer.append")
            {
                s.Inbox.Enqueue(new JsonObject
                {
                    ["type"] = "error",
                    ["error"] = new JsonObject { ["message"] = "quota exceeded" },
                }.ToJsonString());
            }
        });
        using var backend = MakeBackend(factory);

        backend.StartTurn(null, null);
        backend.AppendAudio([0.1f]);
        backend.CommitBoundary(null, null);

        var messages = backend.Transcribe(Vad([0.1f]), CancellationToken.None).ToList();

        var failure = Assert.IsType<TranscriptionFailure>(messages.Single());
        Assert.Equal("remote streaming transcription failed", failure.Message);
    }

    [Fact]
    public void Transcribe_ProgressiveMode_IsSkipped()
    {
        var (socket, factory) = MakeFake(null);
        using var backend = MakeBackend(factory);

        var messages = backend.Transcribe(new VadAudio
        {
            Audio = [0.1f],
            SampleRate = 16_000,
            Mode = "progressive",
        }, CancellationToken.None).ToList();

        Assert.Empty(messages);
        Assert.Empty(socket.Sent);
    }

    [Fact]
    public void ResamplerIsWiredWhenSampleRateDiffers()
    {
        // 24 kHz 目标率（hosted OpenAI）：sink 的 16 kHz 音频应被放大后上线。
        var (socket, factory) = MakeFake(null);
        using var backend = MakeBackend(factory, sampleRate: 24_000);

        backend.AppendAudio(new float[1_600]); // 100 ms @16k

        WaitFor(() => socket.SentEvents().Any(e => (string?)e["type"] == "input_audio_buffer.append"), "append");
        var append = socket.SentEvents().First(e => (string?)e["type"] == "input_audio_buffer.append");
        var decoded = SpeechToSpeech.Core.Audio.AudioUtil.FromPcm16(
            Convert.FromBase64String((string)append["audio"]!));
        // 1 600 个 16 kHz 样本 ≈ 2 400 个 24 kHz 样本（保守产出 + 少量在途）。
        Assert.InRange(decoded.Length, 2_300, 2_450);
    }
}
