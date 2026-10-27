using System.Collections.Concurrent;
using SpeechToSpeech.Core.Runtime;
using SpeechToSpeech.Core.Text;

namespace SpeechToSpeech.Core.Pipeline.Handlers;

/// <summary>
/// 拦截 LLM 输出，把"事件"与"待合成文本"放进同一条有序队列。
///
/// 输入：<see cref="LlmResponseChunk"/>、<see cref="TokenUsage"/>、<see cref="EndOfResponse"/>。
/// 输出：<see cref="AssistantOutputEvent"/>、<see cref="TokenUsageEvent"/>、
/// <see cref="TtsInput"/>、<see cref="EndOfResponse"/>——顺序即模型原始顺序。
///
/// 工具调用走侧信道（<see cref="AssistantToolCallReadyEvent"/>）提前下发：
/// 客户端不必等前置语音合成完就能开始执行函数，但音频收尾顺序仍由主队列保证。
/// </summary>
public sealed class LmOutputProcessor : BaseHandler<object, object>
{
    private readonly BlockingCollection<object> _textOutputQueue;
    private readonly CancelScope _cancelScope;
    private readonly SpeculativeTurnTracker _speculativeTurns;
    private readonly AssistantLanguageProbe _languageProbe;
    private readonly bool _detectLlmOutputLanguage;

    private string? _responseKey;
    private int _outputSequence;

    /// <summary>本轮的语言解析状态（一轮只解析一次）。</summary>
    private readonly ResponseLanguageResolver _language = new();

    /// <summary>最后一个送进 TTS 的配置：收尾时要把本轮语言写回它，供下一轮兜底。</summary>
    private RuntimeConfig? _ttsRuntimeConfig;

    public LmOutputProcessor(
        CancellationToken stopping,
        BlockingCollection<object> queueIn,
        BlockingCollection<object> queueOut,
        BlockingCollection<object> textOutputQueue,
        CancelScope cancelScope,
        SpeculativeTurnTracker speculativeTurns,
        AssistantLanguageProbe? languageProbe = null,
        bool detectLlmOutputLanguage = false)
        : base(stopping, queueIn, queueOut)
    {
        _textOutputQueue = textOutputQueue;
        _cancelScope = cancelScope;
        _speculativeTurns = speculativeTurns;
        // 没有检测器时仍保留探针：窗口照常累积，只是判不出结果。
        // 这样「什么时候调检测」这件事与「有没有接上检测器」解耦，不会悄悄少跑一段逻辑。
        _languageProbe = languageProbe ?? new AssistantLanguageProbe();
        _detectLlmOutputLanguage = detectLlmOutputLanguage;
    }

    public override CancelScope CancelScope => _cancelScope;

    public override SpeculativeTurnTracker SpeculativeTurns => _speculativeTurns;

    protected override IEnumerable<object> Process(object input)
    {
        switch (input)
        {
            case LlmResponseChunk chunk:
                {
                    // 投机宽限真正生效的地方就在这一句。上游对每一片输出都阻塞等闸门：
                    // 用户可能只是换气，宽限内续说会让这个修订作废——那就一个字都不该发出去。
                    // 少了这一步，状态机算得再准也只是"记了个期限"，陈旧轮次的回复照样抢答。
                    if (!_speculativeTurns.WaitForGate(chunk.TurnId, chunk.TurnRevision))
                    {
                        TranscriptLog.Info($"丢弃陈旧轮次的模型输出（turn={chunk.TurnId} rev={chunk.TurnRevision}）");
                        yield break;
                    }

                    var key = chunk.ResponseKey ?? _responseKey ?? Guid.NewGuid().ToString("N");
                    _responseKey = key;
                    var normalized = chunk.Normalize();

                    var outputEvent = new AssistantOutputEvent
                    {
                        Parts = normalized.Parts,
                        Text = normalized.Text,
                        Tools = normalized.Tools,
                        CancelGeneration = chunk.CancelGeneration,
                        ResponseKey = key,
                        OutputSequence = ++_outputSequence,
                        TurnId = chunk.TurnId,
                        TurnRevision = chunk.TurnRevision,
                    };

                    // 事件先于音频入队：客户端先拿到文本，再收到对应的声音
                    yield return outputEvent;

                    foreach (var part in normalized.Parts)
                    {
                        switch (part)
                        {
                            case AssistantToolCallPart toolPart:
                                _textOutputQueue.Add(new AssistantToolCallReadyEvent
                                {
                                    Part = toolPart,
                                    OutputSequence = outputEvent.OutputSequence!.Value,
                                    CancelGeneration = chunk.CancelGeneration,
                                    ResponseKey = key,
                                    TurnId = chunk.TurnId,
                                    TurnRevision = chunk.TurnRevision,
                                });
                                break;

                            case AssistantTextPart textPart:
                                {
                                    var speakable = PrepareForSpeech(textPart.Text);
                                    if (speakable.Length == 0)
                                    {
                                        break;
                                    }

                                    // 纯文本响应不合成：上游同一处判断（response_wants_audio）既挡住合成，
                                    // 也挡住语言观察与解析。不挡的话，一轮不该出声音的回复会白跑一次模型。
                                    if (chunk.Response is { WantsAudio: false })
                                    {
                                        break;
                                    }

                                    var config = chunk.RuntimeConfig;

                                    // 上游条件是「启用检测 或 有配置」——看起来多余，实际不是：
                                    // 即使检测没开，观察结果也会写回 last_assistant_language 供下一轮兜底。
                                    if (_detectLlmOutputLanguage || config is not null)
                                    {
                                        _languageProbe.Observe(textPart.Text);
                                    }

                                    _ttsRuntimeConfig = config;

                                    var resolved = _language.Resolve(
                                        chunk.LanguageCode,
                                        chunk.SelectedLanguage,
                                        config?.SelectedLanguage,
                                        config?.LastAssistantLanguage,
                                        _languageProbe.Detected,
                                        _detectLlmOutputLanguage);

                                    yield return TtsInput.Create(
                                        speakable,
                                        config,
                                        chunk.LanguageCode,
                                        chunk.SelectedLanguage,
                                        key,
                                        chunk.CancelGeneration,
                                        chunk.PrefetchTransaction,
                                        chunk.TurnId,
                                        chunk.TurnRevision,
                                        chunk.Response,
                                        resolved,
                                        chunk.SpeechStoppedAtSeconds);

                                    break;
                                }
                        }
                    }

                    yield break;
                }

            case TokenUsage usage:
                yield return new TokenUsageEvent
                {
                    InputTokens = usage.InputTokens,
                    OutputTokens = usage.OutputTokens,
                    CancelGeneration = usage.CancelGeneration,
                    ResponseKey = usage.ResponseKey ?? _responseKey,
                    TurnId = usage.TurnId,
                    TurnRevision = usage.TurnRevision,
                };
                yield break;

            case EndOfResponse end:
                {
                    var key = end.ResponseKey ?? _responseKey;

                    // 收尾同样要过闸门。丢掉它而不回话会让客户端永远等一个不会来的
                    // response.done——所以照常收口，但把这一轮标记成「不进历史」。
                    var accepted = _speculativeTurns.WaitForGate(end.TurnId, end.TurnRevision);
                    if (!accepted)
                    {
                        TranscriptLog.Info($"陈旧轮次的收尾（turn={end.TurnId} rev={end.TurnRevision}）：不进历史");
                    }

                    var succeeded = accepted && end.Error is null && end.Status == ResponseStatus.Completed;
                    var doneStatus = end.Error is null ? end.Status : ResponseStatus.Completed;

                    // 成功的这一轮把判出的助手语言写回配置，供下一轮「选择为 auto/未指定」时兜底。
                    // 失败或取消不写——那半句可能根本没说完，拿它当语言样本会把下一轮带偏。
                    if (succeeded && _languageProbe.Detected is { } detected && _ttsRuntimeConfig is not null)
                    {
                        _ttsRuntimeConfig.LastAssistantLanguage = detected;
                    }

                    yield return new AssistantResponseDoneEvent
                    {
                        Status = doneStatus,
                        Reason = end.Reason,
                        ResponseKey = key,
                        // 只有真算数的这一轮才上报工具输入：被作废的修订、失败与不完整的响应
                        // 都不该告诉客户端「这些结果已经进过模型」——那会让它跳过重投。
                        InputToolCallIds = succeeded ? [.. end.InputToolCallIds] : [],
                        // 被作废的轮次不进历史：它的文本没发出去，写进聊天记录
                        // 等于让模型以为它说过一句用户从没听到的话。
                        Succeeded = accepted,
                        CancelGeneration = end.CancelGeneration,
                        TurnId = end.TurnId,
                        TurnRevision = end.TurnRevision,
                    };

                    // 丢弃收尾时不再把原事件递下去：下游会把它当作下游管线的正常结束。
                    if (accepted)
                    {
                        yield return end;
                    }

                    ResetResponseState();
                    yield break;
                }

            default:
                yield return input;
                yield break;
        }
    }

    /// <summary>
    /// 把一段助手文本变成可朗读的纯文本。
    ///
    /// 顺序有意为之：先剥 Markdown 结构（需要完整文本），再滤掉不可朗读字符（可逐段安全执行）。
    /// 反过来做会把 <c>**bold**</c> 的星号先抹平，导致结构识别失效。
    /// </summary>
    public static string PrepareForSpeech(string text)
    {
        var withoutMarkdown = TextNormalizer.RemoveMarkdown(text);
        var speakable = TextNormalizer.RemoveUnspeakable(withoutMarkdown);
        return speakable.Trim();
    }

    /// <summary>
    /// 一轮结束后的状态归零。上游 <c>_reset_response</c> 的等价物。
    ///
    /// 语言相关的三项必须一起清：探针窗口、已判语言、解析结果。
    /// 少清任何一项，下一轮都会继承上一轮的语言——表现为「换了语言提问，回复还是旧语言的音色」。
    /// </summary>
    private void ResetResponseState()
    {
        _responseKey = null;
        _ttsRuntimeConfig = null;
        _languageProbe.Reset();
        _language.Reset();
    }

    protected override void OnSessionEnd()
    {
        _outputSequence = 0;
        ResetResponseState();
    }
}
