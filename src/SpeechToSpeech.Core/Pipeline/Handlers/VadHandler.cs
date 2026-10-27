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
    /// <summary>
    /// 流式转写 pre-speech 前缀上限（样本数 @16 kHz）。上游取 VAD 迭代器的
    /// <c>speech_pad_samples</c>（Silero 默认 30 ms）；本实现的 VAD 后端不暴露该值，
    /// 按上游 Silero 默认钉 30 ms。
    /// </summary>
    private const int PreSpeechPadSamples = 480;

    private readonly TurnEndOptions _turnEnd;
    private readonly ITurnEndClassifier? _turnEndClassifier;
    private readonly Action<string>? _log;
    private readonly TurnLatencyStore? _latency;
    private readonly IStreamingSttSink? _streamingSink;

    private readonly List<float> _pending = [];

    /// <summary>
    /// 流式转写的 pre-speech 前缀（上游 <c>_streaming_pre_speech</c>）：说话前的
    /// 若干块音频先攒着，开口确认后连同触发声的那块一起推给 sink——
    /// 转写服务不至于缺掉第一个字的起音。
    /// </summary>
    private readonly List<float> _streamingPreSpeech = [];

    /// <summary>
    /// 本轮到目前为止的**原始**音频。上游 <c>_speculative_raw_audio_prefix</c> 的等价物：
    /// 用户分两次说完同一句时，第二次的端点判定要看整句，而不是只有后半截。
    /// 只保留尾部 <see cref="TurnAudioWindow.MaxSamples"/> 个样本——判定窗口本来就只取尾部 8 秒，
    /// 这个截断对结果是恒等变换，却能把长轮次的内存钉在 512 KB。
    /// </summary>
    private readonly List<float> _turnAudio = [];

    private RuntimeConfig? _runtimeConfig;
    private float[] _segment = [];
    private int _elapsedSamples;
    private int _segmentStartSample;
    private string? _turnId;
    private int? _turnRevision;
    private double? _speechEndAtSeconds;

    /// <param name="turnEnd">端点判定与投机轮次配置（宽限、复开上限、Smart Turn 三项）。</param>
    /// <param name="turnEndClassifier">
    /// 端点判定器；null 表示不可用（本构建暂无 ONNX 实现）。为 null 时决策层报 <c>disabled</c>，
    /// 宽限退回 <see cref="TurnEndOptions.SpeculativeReopenMs"/>、处理延迟 0——与上游「显式关掉」同形。
    /// </param>
    /// <param name="log">诊断日志。</param>
    /// <param name="latency">
    /// 时延账。段定稿时往上面写 <c>vad_decision_s</c> 与 Smart Turn 四项；
    /// null 表示不记账（探针与单测通常不需要）。
    /// </param>
    /// <param name="streamingSink">
    /// 流式 STT 入口（openai-realtime / vllm-realtime 后端才有）。null 时 VAD 行为
    /// 与普通批式后端完全一致。
    /// </param>
    public VadHandler(
        CancellationToken stopping,
        System.Collections.Concurrent.BlockingCollection<object> queueIn,
        System.Collections.Concurrent.BlockingCollection<object> queueOut,
        System.Collections.Concurrent.BlockingCollection<object> textOutputQueue,
        IVadBackend backend,
        CancelScope cancelScope,
        SpeculativeTurnTracker speculativeTurns,
        TurnEndOptions? turnEnd = null,
        ITurnEndClassifier? turnEndClassifier = null,
        Action<string>? log = null,
        TurnLatencyStore? latency = null,
        IStreamingSttSink? streamingSink = null)
        : base(stopping, queueIn, queueOut)
    {
        _backend = backend;
        _textOutputQueue = textOutputQueue;
        _cancelScope = cancelScope;
        _speculativeTurns = speculativeTurns;
        _turnEnd = turnEnd ?? new TurnEndOptions();
        _turnEndClassifier = turnEndClassifier;
        _log = log;
        _latency = latency;
        _streamingSink = streamingSink;

        // 上游在 smart_turn 打开时校验这两项：MaxWait 必须为正（否则「没说完」反而比
        // 「说完了」压得更短，语义倒置），IncompleteDelay 不得为负。
        _turnEnd.Validate();

        // 上游取三者最大值：speculative_reopen_ms、unanswered_reopen_ms、
        // smart_turn_max_wait_ms（仅在 Smart Turn 启用时）。取 max 而不是覆盖，是因为复开窗口
        // 必须同时容纳宽限与判定延迟，否则宽限还没过就被判成「不能复开」。
        var unansweredReopenMs = Math.Max(0, _turnEnd.UnansweredReopenMs);
        if (_turnEnd.Enabled)
        {
            unansweredReopenMs = Math.Max(unansweredReopenMs, _turnEnd.MaxWaitMs);
        }

        _speculativeTurns.ConfigureReopen(
            Math.Max(Math.Max(0, _turnEnd.SpeculativeReopenMs), unansweredReopenMs));
    }

    public override CancelScope CancelScope => _cancelScope;

    public override SpeculativeTurnTracker SpeculativeTurns => _speculativeTurns;

    /// <summary>
    /// 最近一次端点判定的结论。协议层组装 <c>response.metadata</c> 时要用它
    /// （<c>smart_status</c> / <c>smart_turn_grace_s</c> / <c>smart_turn_processing_delay_s</c>，见 D016）。
    /// </summary>
    public TurnEndDecision? LastTurnEndDecision { get; private set; }

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

            // 收块时刻要在推入后端之前取：vad_decision_s 的锚点就是它。
            var chunkReceivedAt = Monotonic.NowSeconds();

            foreach (var frame in _backend.Push(chunk, _runtimeConfig ?? new RuntimeConfig()))
            {
                switch (frame.Kind)
                {
                    case VadFrameKind.SpeechStarted:
                        // 先开候选再确认：候选把输出压住（VAD 正在判定这段是不是续说），
                        // 紧接着的 SpeechStarted 走复开或开新轮二选一。顺序反过来就少了
                        // 那段"压住"，用户换气时的输出会抢在判定之前发出去。
                        _speculativeTurns.SpeechCandidateStarted(frame.AudioStartMs);

                        // 轮的标识由 tracker 派发，不再自造 GUID：会话游标必须只有一个写入口，
                        // 否则「谁更新」的判断会分裂成两套真相。
                        var (turnId, revision, reopened) = _speculativeTurns.SpeechStarted(frame.AudioStartMs);
                        _turnId = turnId;
                        _turnRevision = revision;
                        _segmentStartSample = _elapsedSamples;
                        _segment = [];

                        // 只有开新轮才清空累积音频。复开是同一轮的下一个修订（revision+1），
                        // 上一段的音频正是这一轮的一部分——清掉的话，第二次定稿就只看后半截，
                        // 端点判定会以为用户只说了三个字。上游同样只在「不是复开」时重置前缀。
                        if (!reopened)
                        {
                            _turnAudio.Clear();
                        }

                        _speechEndAtSeconds = null;

                        // 一行一段，不是调试残留：VAD 后端「一个段都不吐」时，管线会安静地
                        // 停在 speech_started 上——客户端只看到助手不再应答，服务端没有任何
                        // 报错。这条日志是唯一能不插桩就分辨「后端没切段」与「切了但没往下传」
                        // 的依据（C020/C023 都是靠它定位的）。
                        TranscriptLog.Info(
                            $"VadHandler: 开口 {frame.AudioStartMs}ms（turn={_turnId} rev={_turnRevision}"
                            + $"{(reopened ? " 复开" : string.Empty)}）");
                        _textOutputQueue.Add(new SpeechStartedEvent
                        {
                            AudioStartMs = frame.AudioStartMs,
                            TurnId = _turnId,
                            TurnRevision = _turnRevision,
                            // 复开与开新轮在协议层是不同的事：复开仍属同一条输入项，
                            // 下游据此决定是续写已有转写还是从头开始。
                            Reopened = reopened,
                            InterruptResponse = _runtimeConfig?.InterruptResponseEnabled ?? true,
                        });

                        // 上游 _notify_streaming_turn：开口确认后立刻标记流式会话的轮次，
                        // 之后的音频块才知道挂在哪一轮。
                        _streamingSink?.StartTurn(_turnId, _turnRevision);
                        break;

                    case VadFrameKind.SpeechStopped:
                        {
                            var audio = frame.Audio ?? _segment;
                            _segment = [];
                            _speechEndAtSeconds = Monotonic.NowSeconds();
                            TranscriptLog.Info(
                                $"VadHandler: 闭口 {frame.AudioStartMs}→{frame.AudioEndMs}ms"
                                + $"（turn={_turnId} rev={_turnRevision} 样本={audio.Length}"
                                + $"{(audio.Length == 0 ? " 空段丢弃" : string.Empty)}）");
                            _textOutputQueue.Add(new SpeechStoppedEvent
                            {
                                DurationSeconds = frame.DurationSeconds,
                                AudioEndMs = frame.AudioEndMs,
                                TurnId = _turnId,
                                TurnRevision = _turnRevision,
                            });

                            if (audio.Length > 0)
                            {
                                // 这段定稿了：先问端点判定器「说完了没有」，再据此记两个期限。
                                // 顺序不能反——宽限与处理延迟都是判定的结论，先记期限再判定
                                // 就等于把结论写死成配置值。
                                KeepTurnAudio(audio);
                                var decision = TurnEndTiming.Decide(
                                    _turnEndClassifier,
                                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_turnAudio),
                                    TurnAudioWindow.ModelSampleRate,
                                    _turnEnd,
                                    _log);
                                LastTurnEndDecision = decision;

                                // 时延账四项在这一刻成组写入（上游同一处）：判定结论给状态、
                                // 宽限、处理延迟；vad_decision_s 由「最后一个有声窗」反推。
                                // 段尾时刻不能用——段尾含闭合用的静音，会把延迟算成 0。
                                _latency?.GetOrCreateForTurn(_turnId, _turnRevision)?.RecordSegmentFinalized(
                                    VadDecisionTiming.Decide(
                                        chunkReceivedAt,
                                        Monotonic.NowSeconds(),
                                        (long)_elapsedSamples + AudioUtil.VadChunkSize,
                                        frame.SpeechEndSample,
                                        TurnAudioWindow.ModelSampleRate),
                                    decision);

                                // 宽限期内用户续说就回到同一轮（revision+1），而不是当成新的一句话；
                                // 判定「没说完」时处理也被推迟，让续说的那几个字在 STT/LLM 开工前作废本轮修订。
                                _speculativeTurns.SegmentFinalized(
                                    frame.AudioEndMs,
                                    outputHoldMs: decision.GraceMs,
                                    processingDelayMs: decision.ProcessingDelayMs);

                                // 上游 _commit_streaming_turn：先排队 provider 边界再往下 yield——
                                // 下一个麦克风块永远不可能超车这次提交。
                                _streamingSink?.CommitBoundary(_turnId, _turnRevision);

                                yield return new VadAudio
                                {
                                    Audio = audio,
                                    SampleRate = 16_000,
                                    RuntimeConfig = _runtimeConfig,
                                    // 定稿段带 `final`：STT 入口靠它区分「这一轮等 VAD 收口」
                                    // 与「同一次说话的中间快照」，据此决定要不要记 vad_settle_s。
                                    // 本实现没有 progressive 产出（D008），所以这里恒为 final。
                                    Mode = "final",
                                    TurnId = _turnId,
                                    TurnRevision = _turnRevision,
                                    SpeechEndAtSeconds = _speechEndAtSeconds,
                                };
                            }
                            else
                            {
                                // 空段（phantom 触发或长度不够）：这段放不出去，要让状态机
                                // 退回 SOFT_ENDED，否则下一句会被当成同一轮的续说。
                                _speculativeTurns.SegmentDiscarded();
                                _streamingSink?.DiscardUtterance();
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

            // 流式转写：说话中的每一块都直接喂 sink；静默块只攒 pre-speech 前缀。
            // 放在帧处理之后——SpeechStarted 已把本轮 turn 置位（触发块一起发出去），
            // SpeechStopped 已清位（闭合静音不再往线上送）。
            if (_streamingSink is not null)
            {
                StreamChunkToSink(chunk);
            }

            _elapsedSamples += AudioUtil.VadChunkSize;
        }

        yield break;
    }

    /// <summary>
    /// 把一块音频送进流式 sink（上游 <c>_stream_audio_chunk</c>）：活跃时先冲
    /// pre-speech 前缀再送块；静默时只滚动前缀缓冲（截到 pad 上限），不发空闲麦克风音频。
    /// </summary>
    private void StreamChunkToSink(float[] chunk)
    {
        if (_turnId is not null)
        {
            var toSend = chunk;
            if (_streamingPreSpeech.Count > 0)
            {
                toSend = [.. _streamingPreSpeech, .. chunk];
                _streamingPreSpeech.Clear();
            }

            try
            {
                _streamingSink!.AppendAudio(toSend);
            }
            catch (Exception ex)
            {
                TranscriptLog.Error("VadHandler: 流式 STT 音频块入队失败", ex);
            }
        }
        else
        {
            _streamingPreSpeech.AddRange(chunk);
            var excess = _streamingPreSpeech.Count - PreSpeechPadSamples;
            if (excess > 0)
            {
                _streamingPreSpeech.RemoveRange(0, excess);
            }
        }
    }

    /// <summary>
    /// 把这一段并入「本轮原始音频」，只留尾部一个判定窗口。
    ///
    /// 截断是恒等变换：<see cref="TurnAudioWindow.Prepare"/> 本来只取尾部 8 秒，
    /// 而尾部再截尾部仍是同一个尾部。不截的话，一口气说三分钟的用户会攒下
    /// 三分钟的 float（约 11 MB），换来的判定与截断后完全一样。
    /// </summary>
    private void KeepTurnAudio(float[] segment)
    {
        _turnAudio.AddRange(segment);
        var excess = _turnAudio.Count - TurnAudioWindow.MaxSamples;
        if (excess > 0)
        {
            _turnAudio.RemoveRange(0, excess);
        }
    }

    protected override void OnSessionEnd()
    {
        // 先收掉流式会话（上游同一位置：推进代际、断开远端），再复位本地状态。
        _streamingSink?.CancelSession();
        _backend.ResetSession();
        _pending.Clear();
        _streamingPreSpeech.Clear();
        _segment = [];
        _turnAudio.Clear();
        _turnId = null;
        _turnRevision = null;
        _elapsedSamples = 0;
        _segmentStartSample = 0;
        _speechEndAtSeconds = null;
        LastTurnEndDecision = null;
        _speculativeTurns.Reset();
    }

    protected override void Cleanup() => _backend.Dispose();
}
