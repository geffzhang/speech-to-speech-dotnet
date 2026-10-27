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
    /// <summary>
    /// 闸门等待到这个量级才值得说一声（上游 <c>_GATE_WAIT_REPORT_S</c>）。
    ///
    /// 同一个阈值管两件事：打日志，以及决定要不要记 <c>vad_settle_s</c>。
    /// 分成两个常量没有依据——上游就是同一个数。
    /// </summary>
    private const double GateWaitReportSeconds = 0.05;

    private readonly ISttBackend _backend;
    private readonly CancelScope _cancelScope;
    private readonly SpeculativeTurnTracker _speculativeTurns;
    private readonly TurnLatencyStore? _latency;

    public SttHandler(
        CancellationToken stopping,
        BlockingCollection<object> queueIn,
        BlockingCollection<object> queueOut,
        ISttBackend backend,
        CancelScope cancelScope,
        SpeculativeTurnTracker speculativeTurns,
        TurnLatencyStore? latency = null)
        : base(stopping, queueIn, queueOut)
    {
        _backend = backend;
        _cancelScope = cancelScope;
        _speculativeTurns = speculativeTurns;
        _latency = latency;
    }

    public override CancelScope CancelScope => _cancelScope;

    public override SpeculativeTurnTracker SpeculativeTurns => _speculativeTurns;

    protected override IEnumerable<PipelineMessage> Process(VadAudio input)
    {
        // 入口闸门要真的等，不能只看一眼。上游在 STT 入口按 processing_deadline 阻塞：
        // 端点判定说「没说完」时，用户接着说的那几个字应该在转写开工**之前**把这个修订作废——
        // 只看一眼等于让 STT 白跑一次随后被丢弃的识别，处理延迟就成了纯装饰。
        //
        // holdForGrace 为 false：宽限只压输出，不压输入。把输入也压住的话，
        // 用户续说时新旧两段音频会互相等，谁也不先出转写。
        var processingDeadline = _speculativeTurns.ProcessingDeadline(input.TurnId, input.TurnRevision);
        var gateStartedAt = Monotonic.NowSeconds();
        var accepted = _speculativeTurns.WaitForGate(
            input.TurnId,
            input.TurnRevision,
            holdForGrace: false,
            holdUntil: processingDeadline);
        var gateWaitSeconds = Monotonic.NowSeconds() - gateStartedAt;

        if (gateWaitSeconds >= GateWaitReportSeconds)
        {
            // 与上游同一行口径：等久了要留痕，否则「这一轮怎么这么慢」只能靠猜。
            TranscriptLog.Info(
                $"{GetType().Name}: STT input gate waited {gateWaitSeconds:F3}s"
                + $" for turn={input.TurnId} rev={input.TurnRevision} mode={input.Mode ?? "<none>"}"
                + $" latest={accepted} age={Monotonic.NowSeconds() - input.CreatedAtSeconds:F3}s"
                + $" queue={QueueIn.Count}");
        }

        // `vad_settle_s` 记的是**整段输入闸门的等待**，只在两个条件同时成立时才有意义：
        // 这一修订仍然是最新的（被作废的账马上会被摘掉），且它是定稿段（progressive 只是
        // 同一次说话的中间快照，它的等待不属于「这一轮等 VAD 收口」）。
        // 别把它和 hold_s 混起来：那个是输出侧宽限的区间并集。
        if (accepted && input.IsFinal && gateWaitSeconds >= GateWaitReportSeconds)
        {
            _latency?.GetOrCreateForTurn(input.TurnId, input.TurnRevision)
                ?.RecordVadSettle(gateWaitSeconds);
        }

        if (!accepted)
        {
            // 这一修订被后来的话作废了，不会有响应来认领它的待定账目。
            // 不摘掉的话，待定槽位会一直挂着一条没人结算的记录（上游同一处置）。
            _latency?.DiscardPendingTurn(input.TurnId, input.TurnRevision);
            yield break;
        }

        List<PipelineMessage> messages;
        var startedAt = Monotonic.NowSeconds();
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

        // 只量转写本身：闸门等待与排队都不该算进 stt_s，否则「模型慢」和「管线堵」分不开。
        // 挂在待定槽位上而不是直接写响应：此刻响应键通常还不存在，就地把测量丢掉
        // 会让 stt_s 永久为 null（这是上游 record_final_stt 存在的全部理由）。
        _latency?.GetOrCreateForTurn(input.TurnId, input.TurnRevision)
            ?.RecordStt(Monotonic.NowSeconds() - startedAt);

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

                    // **不**往下游（LLM 队列）转发最终转写。
                    //
                    // 生成请求由 RealtimeService 在消费这条 completed 事件时创建——它才知道
                    // 会话配置（是否自动回复、逐响应覆盖）。这里再塞一份会让 LM 队列里多出
                    // 一条类型不对的消息：真跑时表现为每个服务端警告
                    // 「unexpected input type Transcription」，而回复仍然由另一条路径产生。
                    // 上游 `TranscriptionNotifier` 在 Transcription 分支里只发事件、不返回。
                    break;
                }

            case TranscriptionFailure failure:
                _textOutputQueue.Add(new TranscriptionFailedEvent
                {
                    Message = failure.Message,
                    TurnId = failure.TurnId,
                    TurnRevision = failure.TurnRevision,
                });

                // 同理不转发：失败也不是一个「该回答」的输入。
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
