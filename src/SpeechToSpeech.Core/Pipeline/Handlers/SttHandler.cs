using System.Collections.Concurrent;
using System.Text;
using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Core.Pipeline.Handlers;

/// <summary>
/// STT 阶段。
///
/// 输入 <see cref="VadAudio"/>，输出 <see cref="PartialTranscription"/> 与
/// <see cref="Transcription"/> / <see cref="TranscriptionFailure"/>。
/// 一条音频段的失败必须编码为 <see cref="TranscriptionFailure"/> 而不是异常——
/// 上层才有机会向客户端解释"这次没听清"，而不是整条管线静默卡住。
/// </summary>
public sealed class SttHandler : BaseHandler<VadAudio, PipelineMessage>
{
    private readonly ISttBackend _backend;
    private readonly CancelScope _cancelScope;
    private readonly SpeculativeTurnTracker _speculativeTurns;

    public SttHandler(
        CancellationToken stopping,
        BlockingCollection<object> queueIn,
        BlockingCollection<object> queueOut,
        ISttBackend backend,
        CancelScope cancelScope,
        SpeculativeTurnTracker speculativeTurns)
        : base(stopping, queueIn, queueOut)
    {
        _backend = backend;
        _cancelScope = cancelScope;
        _speculativeTurns = speculativeTurns;
    }

    public override CancelScope CancelScope => _cancelScope;

    public override SpeculativeTurnTracker SpeculativeTurns => _speculativeTurns;

    protected override IEnumerable<PipelineMessage> Process(VadAudio input)
    {
        if (_speculativeTurns.Gate(input.TurnId, input.TurnRevision).Action == TurnGateAction.Drop)
        {
            yield break;
        }

        List<PipelineMessage> messages;
        try
        {
            messages = _backend.Transcribe(input, Stopping).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TranscriptLog.Error($"{GetType().Name}: transcription failed", ex);
            messages =
            [
                new TranscriptionFailure
                {
                    Message = ex.Message,
                    TurnId = input.TurnId,
                    TurnRevision = input.TurnRevision,
                    SpeechStoppedAtSeconds = input.SpeechEndAtSeconds,
                },
            ];
        }

        foreach (var message in messages)
        {
            switch (message)
            {
                case PartialTranscription partial:
                    TranscriptLog.Transcript("user(partial)", partial.Text);
                    yield return partial;
                    break;

                case Transcription final:
                    TranscriptLog.Transcript("user", final.Text);
                    if (input.TurnId is not null && input.TurnRevision is not null)
                    {
                        // 提交走 gate(commit:true) 而不是直接记账：已陈旧的轮次不该被提交，
                        // 否则它会和当前轮一起被当成「已回答」（上游同义）。
                        _speculativeTurns.Gate(input.TurnId, input.TurnRevision.Value, commit: true);
                    }

                    yield return new Transcription
                    {
                        Text = final.Text,
                        LanguageCode = final.LanguageCode,
                        SpeakerAttribution = final.SpeakerAttribution,
                        TurnId = input.TurnId,
                        TurnRevision = input.TurnRevision,
                        SpeechStoppedAtSeconds = input.SpeechEndAtSeconds,
                    };
                    break;

                default:
                    yield return message;
                    break;
            }
        }
    }

    protected override void Cleanup() => _backend.Dispose();
}

/// <summary>
/// 把 STT 的内部消息转成路由事件。
///
/// 转写增量必须是"只增不回"的：协议没有撤回事件，所以已发出的词不能被新的假设改写。
/// 上游的做法是<b>扣住最新一个匹配词</b>，只把超出已提交前缀的确认增长发到线上，
/// 不稳定的首字母大小写与边缘标点留给最终转写。这里移植的就是这条规则。
/// </summary>
public sealed class TranscriptionNotifier : BaseHandler<PipelineMessage, object>
{
    private readonly BlockingCollection<object> _textOutputQueue;
    private readonly bool _snapshotsEnabled;
    private readonly CancelScope _cancelScope;
    private readonly SpeculativeTurnTracker _speculativeTurns;
    private readonly Dictionary<string, string> _committed = new(StringComparer.Ordinal);

    public TranscriptionNotifier(
        CancellationToken stopping,
        BlockingCollection<object> queueIn,
        BlockingCollection<object> queueOut,
        BlockingCollection<object> textOutputQueue,
        bool snapshotsEnabled,
        CancelScope cancelScope,
        SpeculativeTurnTracker speculativeTurns)
        : base(stopping, queueIn, queueOut)
    {
        _textOutputQueue = textOutputQueue;
        _snapshotsEnabled = snapshotsEnabled;
        _cancelScope = cancelScope;
        _speculativeTurns = speculativeTurns;
    }

    public override CancelScope CancelScope => _cancelScope;

    public override SpeculativeTurnTracker SpeculativeTurns => _speculativeTurns;

    protected override IEnumerable<object> Process(PipelineMessage input)
    {
        switch (input)
        {
            case PartialTranscription partial:
                {
                    var key = partial.TurnId ?? "default";
                    var delta = ConfirmIncrement(key, partial.Text);
                    if (delta.Length > 0)
                    {
                        _textOutputQueue.Add(new PartialTranscriptionEvent
                        {
                            Delta = delta,
                            TurnId = partial.TurnId,
                            TurnRevision = partial.TurnRevision,
                        });
                    }

                    if (_snapshotsEnabled)
                    {
                        _textOutputQueue.Add(new TranscriptionSnapshotEvent
                        {
                            Snapshot = partial.Text,
                            TurnId = partial.TurnId,
                            TurnRevision = partial.TurnRevision,
                        });
                    }

                    yield break;
                }

            case Transcription final:
                {
                    var key = final.TurnId ?? "default";
                    _committed.Remove(key);
                    _textOutputQueue.Add(new TranscriptionCompletedEvent
                    {
                        Transcript = final.Text,
                        SpeakerAttribution = final.SpeakerAttribution,
                        LanguageCode = final.LanguageCode,
                        TurnId = final.TurnId,
                        TurnRevision = final.TurnRevision,
                        SpeechStoppedAtSeconds = final.SpeechStoppedAtSeconds,
                    });

                    // 仍要流向 LLM 队列，供生成阶段使用
                    yield return final;
                    break;
                }

            case TranscriptionFailure failure:
                _textOutputQueue.Add(new TranscriptionFailedEvent
                {
                    Message = failure.Message,
                    TurnId = failure.TurnId,
                    TurnRevision = failure.TurnRevision,
                });
                yield return failure;
                break;

            default:
                yield return input;
                break;
        }
    }

    /// <summary>
    /// 计算可以安全追加的增量：扣住最新一个词，只发已确认增长的部分。
    /// </summary>
    private string ConfirmIncrement(string key, string hypothesis)
    {
        _committed.TryGetValue(key, out var committed);
        committed ??= string.Empty;

        if (committed.Length > 0 && !hypothesis.StartsWith(committed, StringComparison.Ordinal))
        {
            // 新的假设改写了已发出的词：本帧整段扣住，等后续假设重新延伸已提交前缀
            return string.Empty;
        }

        var words = hypothesis.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
        {
            // 只有一个词时无法判断它是否还会被改写，先不发
            return string.Empty;
        }

        var confirmed = string.Join(' ', words[..^1]);
        if (confirmed.Length <= committed.Length)
        {
            return string.Empty;
        }

        var increment = confirmed[committed.Length..].TrimStart();
        _committed[key] = confirmed;
        return increment.Length == 0 ? string.Empty : increment + " ";
    }

    protected override void OnSessionEnd() => _committed.Clear();
}

/// <summary>
/// 可替换的投机转写快照（<c>speech_to_speech.input_audio_transcription.snapshot</c>）。
///
/// 标准 delta 只能追加；当本地流式模型改写前面的词时，只有这个快照事件能如实反映现状。
/// 它由客户端在 <c>session.update</c> 的 <c>extensions</c> 里显式订阅。
/// </summary>
public sealed class TranscriptionSnapshotEvent : PipelineEvent
{
    public override string EventType => "input_audio_transcription_snapshot";

    public required string Snapshot { get; init; }
}
