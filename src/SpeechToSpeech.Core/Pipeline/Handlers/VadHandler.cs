using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Runtime;

namespace SpeechToSpeech.Core.Pipeline.Handlers;

/// <summary>VAD 阶段的输入项：一段 PCM 加它所属的会话配置。</summary>
public sealed record VadInput(float[] Samples, int SampleRate, RuntimeConfig? RuntimeConfig = null);

/// <summary>
/// VAD 阶段。
///
/// 输入：解码后的 PCM（可带每连接的 <see cref="RuntimeConfig"/>）。
/// 输出：<see cref="VadAudio"/> 进入 STT 队列；<see cref="SpeechStartedEvent"/> /
/// <see cref="SpeechStoppedEvent"/> 进入文本事件队列——后者不经过 TTS，不必等合成。
/// </summary>
public sealed class VadHandler : BaseHandler<object, object>
{
    private readonly IVadBackend _backend;
    private readonly System.Collections.Concurrent.BlockingCollection<object> _textOutputQueue;
    private readonly CancelScope _cancelScope;
    private readonly SpeculativeTurnTracker _speculativeTurns;

    private readonly List<float> _pending = [];
    private RuntimeConfig? _runtimeConfig;
    private float[] _segment = [];
    private int _elapsedSamples;
    private int _segmentStartSample;
    private string? _turnId;
    private int? _turnRevision;
    private double? _speechEndAtSeconds;

    public VadHandler(
        CancellationToken stopping,
        System.Collections.Concurrent.BlockingCollection<object> queueIn,
        System.Collections.Concurrent.BlockingCollection<object> queueOut,
        System.Collections.Concurrent.BlockingCollection<object> textOutputQueue,
        IVadBackend backend,
        CancelScope cancelScope,
        SpeculativeTurnTracker speculativeTurns)
        : base(stopping, queueIn, queueOut)
    {
        _backend = backend;
        _textOutputQueue = textOutputQueue;
        _cancelScope = cancelScope;
        _speculativeTurns = speculativeTurns;
    }

    public override CancelScope CancelScope => _cancelScope;

    public override SpeculativeTurnTracker SpeculativeTurns => _speculativeTurns;

    protected override IEnumerable<object> Process(object input)
    {
        var (samples, sampleRate, config) = input switch
        {
            VadInput vad => (vad.Samples, vad.SampleRate, vad.RuntimeConfig),
            float[] floats => (floats, 16_000, (RuntimeConfig?)null),
            byte[] pcm => (AudioUtil.FromPcm16(pcm), 24_000, (RuntimeConfig?)null),
            _ => (Array.Empty<float>(), 16_000, (RuntimeConfig?)null),
        };

        if (config is not null)
        {
            _runtimeConfig = config;
        }

        if (samples.Length == 0)
        {
            yield break;
        }

        var mono16k = AudioUtil.Resample(samples, sampleRate, 16_000);
        _pending.AddRange(mono16k);

        while (_pending.Count >= AudioUtil.VadChunkSize)
        {
            var chunk = new float[AudioUtil.VadChunkSize];
            _pending.CopyTo(0, chunk, 0, AudioUtil.VadChunkSize);
            _pending.RemoveRange(0, AudioUtil.VadChunkSize);

            foreach (var frame in _backend.Push(chunk, _runtimeConfig ?? new RuntimeConfig()))
            {
                switch (frame.Kind)
                {
                    case VadFrameKind.SpeechStarted:
                        // 轮的标识由 tracker 派发，不再自造 GUID：会话游标必须只有一个写入口，
                        // 否则「谁更新」的判断会分裂成两套真相。
                        var (turnId, revision, _) = _speculativeTurns.SpeechStarted(frame.AudioStartMs);
                        _turnId = turnId;
                        _turnRevision = revision;
                        _segmentStartSample = _elapsedSamples;
                        _segment = [];
                        _speechEndAtSeconds = null;
                        _textOutputQueue.Add(new SpeechStartedEvent
                        {
                            AudioStartMs = frame.AudioStartMs,
                            TurnId = _turnId,
                            TurnRevision = _turnRevision,
                            InterruptResponse = _runtimeConfig?.InterruptResponseEnabled ?? true,
                        });
                        break;

                    case VadFrameKind.SpeechStopped:
                        {
                            var audio = frame.Audio ?? _segment;
                            _segment = [];
                            _speechEndAtSeconds = Monotonic.NowSeconds();
                            _textOutputQueue.Add(new SpeechStoppedEvent
                            {
                                DurationSeconds = frame.DurationSeconds,
                                AudioEndMs = frame.AudioEndMs,
                                TurnId = _turnId,
                                TurnRevision = _turnRevision,
                            });

                            if (audio.Length > 0)
                            {
                                yield return new VadAudio
                                {
                                    Audio = audio,
                                    SampleRate = 16_000,
                                    RuntimeConfig = _runtimeConfig,
                                    TurnId = _turnId,
                                    TurnRevision = _turnRevision,
                                    SpeechEndAtSeconds = _speechEndAtSeconds,
                                };
                            }

                            _turnId = null;
                            _turnRevision = null;
                            break;
                        }
                }
            }

            // 无论后端是否报告，都把落在语音段的样本留一份，作为后端不带音频时的兜底
            if (_turnId is not null)
            {
                _segment = [.. _segment, .. chunk];
            }

            _elapsedSamples += AudioUtil.VadChunkSize;
        }

        yield break;
    }

    protected override void OnSessionEnd()
    {
        _backend.ResetSession();
        _pending.Clear();
        _segment = [];
        _turnId = null;
        _turnRevision = null;
        _elapsedSamples = 0;
        _segmentStartSample = 0;
        _speechEndAtSeconds = null;
        _speculativeTurns.Reset();
    }

    protected override void Cleanup() => _backend.Dispose();
}
