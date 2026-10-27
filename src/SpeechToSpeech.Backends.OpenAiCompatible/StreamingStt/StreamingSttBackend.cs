using System.Collections.Concurrent;
using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Runtime;

namespace SpeechToSpeech.Backends.OpenAiCompatible.StreamingStt;

/// <summary>
/// 上游 <c>StatefulStreamingSTTHandler</c> 的 C# 对应物：把增量 PCM 入口
/// （<see cref="IStreamingSttSink"/>，由 VAD 调）与定稿路径（<see cref="ISttBackend"/>，
/// 由 SttHandler 调）接到同一条远端 WebSocket 会话上。
///
/// 部分转写与定稿共用 worker 的产出：<c>delta</c> 事件即时发
/// <see cref="PartialTranscription"/>，<c>completed</c> 填充提交票据。
/// </summary>
public sealed class StreamingSttBackend : IStreamAwareSttBackend, IStreamingSttSink
{
    /// <summary>
    /// pre-speech 前缀缓冲上限（样本数 @16 kHz）。上游取 VAD 迭代器的
    /// <c>speech_pad_samples</c>（Silero 默认 30 ms）；本实现的 VAD 后端不暴露该值，
    /// 按上游 Silero 默认钉 30 ms。
    /// </summary>
    private const int PreSpeechPadSamples = 480;

    private readonly IRealtimeTranscriptionProtocol _protocol;
    private readonly string _endpointUrl;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly double _connectTimeout;
    private readonly double _finalTimeout;
    private readonly int _sampleRate;
    private readonly StreamingConnectFactory? _connectFactory;

    private readonly object _attachLock = new();
    private StreamingSttSession? _session;
    private StreamingResampler? _resampler;
    private SpeculativeTurnTracker? _tracker;

    /// <param name="connectFactory">
    /// 连接工厂；null 用产品 WebSocket（上游 setup 的 <c>connect_factory</c>，测试注入 fake）。
    /// </param>
    public StreamingSttBackend(
        IRealtimeTranscriptionProtocolFactory protocolFactory,
        SttOptions options,
        StreamingConnectFactory? connectFactory = null)
    {
        // 默认值逐项取自上游 openai_realtime_stt_arguments / vllm_realtime_stt_arguments。
        var isVllm = protocolFactory.Name == "vllm-realtime";
        var baseUrl = options.BaseUrl
            ?? (isVllm ? "ws://localhost:8000/v1" : OpenAiRealtimeProtocol.DefaultBaseUrl);
        var model = string.IsNullOrWhiteSpace(options.StreamingModel)
            ? isVllm ? "Qwen/Qwen3-ASR-1.7B" : "gpt-live-transcribe"
            : options.StreamingModel;
        _sampleRate = options.SampleRate;
        _connectTimeout = options.ConnectTimeoutSeconds;
        _finalTimeout = options.FinalTimeoutSeconds;
        _connectFactory = connectFactory;

        var setup = StreamingSttSetup.Create(
            protocolFactory, baseUrl, model, _sampleRate,
            _connectTimeout, _finalTimeout, options.Language,
            includeModelQuery: !isVllm,
            apiKey: options.ApiKey);
        _protocol = setup.Protocol;
        _endpointUrl = setup.EndpointUrl;
        _headers = setup.UseApiKey
            ? new Dictionary<string, string> { ["Authorization"] = $"Bearer {options.ApiKey}" }
            : new Dictionary<string, string>();
    }

    public string Name => _protocol.Name;

    // ── 按连接挂载 ────────────────────────────────────────────────────

    public IStreamingSttSink? Attach(
        BlockingCollection<object> queueOut,
        SpeculativeTurnTracker speculativeTurns,
        CancellationToken stopped)
    {
        lock (_attachLock)
        {
            if (_session is not null)
            {
                return this; // 幂等：同一连接重复装配直接复用。
            }

            _tracker = speculativeTurns;
            _session = new StreamingSttSession(
                _protocol, _endpointUrl, _headers,
                _connectTimeout, _finalTimeout,
                _connectFactory ?? ClientWebSocketStreamingSocket.DefaultFactory,
                queueOut, stopped,
                speculativeTurns: speculativeTurns,
                log: null);
            _resampler = _sampleRate != 16_000 ? new StreamingResampler(16_000, _sampleRate) : null;
            return this;
        }
    }

    // ── IStreamingSttSink（VAD 侧） ───────────────────────────────────

    public void StartTurn(string? turnId, int? turnRevision) => _session?.StartTurn(turnId, turnRevision);

    public void AppendAudio(float[] samples)
    {
        var session = _session;
        if (session is null || samples.Length == 0)
        {
            return;
        }

        var pcm = _resampler is not null
            ? AudioUtil.ToPcm16(_resampler.Push(samples))
            : AudioUtil.ToPcm16(samples);
        session.AppendAudio(pcm);
    }

    public void CommitBoundary(string? turnId, int? turnRevision)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        if (_resampler is not null)
        {
            var tail = _resampler.FinishUtterance();
            if (tail.Length > 0)
            {
                session.AppendAudio(AudioUtil.ToPcm16(tail));
            }
        }

        session.BeginCommit(turnId, turnRevision);
    }

    public void DiscardUtterance()
    {
        _resampler?.Reset();
        _session?.DiscardUtterance();
    }

    public void CancelSession()
    {
        _resampler?.Reset();
        _session?.CancelSession();
    }

    // ── ISttBackend（SttHandler 定稿路径） ────────────────────────────

    public IEnumerable<PipelineMessage> Transcribe(VadAudio audio, CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null || audio.Mode == "progressive")
        {
            yield break;
        }

        // VAD 接线正常时票据已在飞；这里兜底处理未经 VAD 的直连路径。
        if (!session.HasPendingCommit(audio.TurnId, audio.TurnRevision))
        {
            CommitBoundary(audio.TurnId, audio.TurnRevision);
        }

        var generation = session.Generation;
        var commit = session.Commit(audio.TurnId, audio.TurnRevision);
        if (commit is null || generation != session.Generation)
        {
            yield break;
        }

        if (commit.Error is not null)
        {
            TranscriptLog.Info(
                $"{Name} STT failed turn={audio.TurnId} rev={audio.TurnRevision}: {commit.Error}");
            yield return new TranscriptionFailure
            {
                Message = commit.Error,
                TurnId = audio.TurnId,
                TurnRevision = audio.TurnRevision,
                SpeechStoppedAtSeconds = audio.SpeechEndAtSeconds,
            };
            yield break;
        }

        if (_tracker is not null && !_tracker.IsLatest(audio.TurnId, audio.TurnRevision))
        {
            yield break;
        }

        yield return new Transcription
        {
            Text = commit.Result ?? string.Empty,
            LanguageCode = commit.Language,
            TurnId = audio.TurnId,
            TurnRevision = audio.TurnRevision,
            SpeechStoppedAtSeconds = audio.SpeechEndAtSeconds,
        };
    }

    public void Dispose() => _session?.Dispose();
}
