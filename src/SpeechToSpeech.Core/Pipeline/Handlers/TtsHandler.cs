using System.Collections.Concurrent;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Text;

namespace SpeechToSpeech.Core.Pipeline.Handlers;

/// <summary>
/// TTS 阶段。
///
/// 输入 <see cref="TtsInput"/> 与 <see cref="EndOfResponse"/>，输出 int16 PCM 字节块
/// （或带代际标记的 <see cref="AudioOutput"/>）。
///
/// 分句在 handler 做而不是在后端做：这样"一段文本 → 多段可并行/流式合成"的策略只此一处，
/// 换后端不会改变句级时序。合成失败不吞掉终止事件——否则客户端会永远等这段音频。
/// </summary>
public sealed class TtsHandler : BaseHandler<object, object>
{
    private readonly ITtsBackend _backend;
    private readonly CancelScope _cancelScope;
    private readonly SpeculativeTurnTracker _speculativeTurns;
    private readonly TurnLatencyStore _latency;

    public TtsHandler(
        CancellationToken stopping,
        BlockingCollection<object> queueIn,
        BlockingCollection<object> queueOut,
        ITtsBackend backend,
        CancelScope cancelScope,
        SpeculativeTurnTracker speculativeTurns,
        TurnLatencyStore latency)
        : base(stopping, queueIn, queueOut)
    {
        _backend = backend;
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
            case EndOfResponse end:
                // 终止事件必须原样穿过 TTS 阶段，否则发送循环等不到响应结束
                yield return end;
                yield break;

            case TtsInput tts:
                {
                    foreach (var sentence in SplitForSynthesis(tts.Text))
                    {
                        if (_cancelScope.Discarding || Stopping.IsCancellationRequested)
                        {
                            yield break;
                        }

                        var request = new TtsRequest
                        {
                            Text = sentence,
                            Language = tts.TtsLanguageCode,
                            Speaker = tts.RuntimeConfig?.Voice,
                            Instruct = null,
                        };

                        IEnumerator<byte[]>? enumerator = null;
                        try
                        {
                            enumerator = _backend.Synthesize(request, Stopping).GetEnumerator();
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            TranscriptLog.Error($"{GetType().Name}: TTS unavailable", ex);
                            yield break;
                        }

                        using (enumerator)
                        {
                            while (true)
                            {
                                byte[] chunk;
                                try
                                {
                                    if (!enumerator.MoveNext())
                                    {
                                        break;
                                    }

                                    chunk = enumerator.Current;
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    TranscriptLog.Error($"{GetType().Name}: synthesis failed", ex);
                                    break;
                                }

                                if (chunk.Length == 0)
                                {
                                    continue;
                                }

                                yield return chunk;
                            }
                        }
                    }

                    yield break;
                }

            default:
                yield break;
        }
    }

    /// <summary>
    /// 按句切开长文本再逐句合成。
    ///
    /// 目的只有一个：让首段音频尽早出去。整段一次性合成会把首字延迟拉到与全文长度成正比，
    /// 这在语音代理里是致命的——用户会以为对方没听见。
    /// </summary>
    public static IEnumerable<string> SplitForSynthesis(string text)
    {
        var sentences = TextNormalizer.SentenceTokenizePreservingMarkdownCode(text);
        if (sentences.Count == 0)
        {
            var trimmed = text.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }

            yield break;
        }

        foreach (var sentence in sentences)
        {
            var trimmed = sentence.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    protected override void Cleanup() => _backend.Dispose();
}
