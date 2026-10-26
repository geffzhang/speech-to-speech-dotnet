using System.Collections.Concurrent;
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
    private readonly TurnLatencyStore _latency;

    private string? _responseKey;
    private readonly List<string> _toolCallIds = [];
    private int _outputSequence;

    public LmOutputProcessor(
        CancellationToken stopping,
        BlockingCollection<object> queueIn,
        BlockingCollection<object> queueOut,
        BlockingCollection<object> textOutputQueue,
        CancelScope cancelScope,
        SpeculativeTurnTracker speculativeTurns,
        TurnLatencyStore latency)
        : base(stopping, queueIn, queueOut)
    {
        _textOutputQueue = textOutputQueue;
        _cancelScope = cancelScope;
        _speculativeTurns = speculativeTurns;
        _latency = latency;
    }

    public override CancelScope CancelScope => _cancelScope;

    public override SpeculativeTurnTracker SpeculativeTurns => _speculativeTurns;

    protected override IEnumerable<object> Process(object input)
    {
        switch (input)
        {
            case LlmResponseChunk chunk:
                {
                    var key = chunk.ResponseKey ?? _responseKey ?? Guid.NewGuid().ToString("N");
                    _responseKey = key;
                    var normalized = chunk.Normalize();

                    if (_latency is not null && chunk.TurnId is not null)
                    {
                        _latency.MarkFirstAudio(chunk.TurnId);
                    }

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
                                _toolCallIds.Add(toolPart.Tool.CallId);
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
                                    if (speakable.Length > 0)
                                    {
                                        yield return TtsInput.Create(
                                            speakable,
                                            chunk.RuntimeConfig,
                                            chunk.LanguageCode,
                                            chunk.SelectedLanguage,
                                            key,
                                            chunk.CancelGeneration,
                                            chunk.PrefetchTransaction,
                                            chunk.TurnId,
                                            chunk.TurnRevision);
                                    }

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
                    var doneStatus = end.Error is null ? end.Status : ResponseStatus.Completed;

                    yield return new AssistantResponseDoneEvent
                    {
                        Status = doneStatus,
                        Reason = end.Reason,
                        ResponseKey = key,
                        InputToolCallIds = [.. _toolCallIds],
                        CancelGeneration = end.CancelGeneration,
                        TurnId = end.TurnId,
                        TurnRevision = end.TurnRevision,
                    };

                    yield return end;
                    _responseKey = null;
                    _toolCallIds.Clear();
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

    protected override void OnSessionEnd()
    {
        _responseKey = null;
        _toolCallIds.Clear();
        _outputSequence = 0;
    }
}
