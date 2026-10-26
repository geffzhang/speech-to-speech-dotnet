using System.Collections.Concurrent;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Runtime;

namespace SpeechToSpeech.Core.Pipeline.Handlers;

/// <summary>
/// LLM 阶段。
///
/// 输入 <see cref="GenerateResponseRequest"/>，输出 <see cref="LlmResponseChunk"/>、
/// <see cref="TokenUsage"/>，并以 <see cref="EndOfResponse"/> 收尾。
///
/// 一条红线：生成失败必须是<b>数据</b>而不是异常。半截输出绝不能当成成功——
/// 那会让下游把一部分回答念出去，然后卡在一个永远等不到终止的响应里。
/// </summary>
public sealed class LanguageModelHandler : BaseHandler<GenerateResponseRequest, PipelineMessage>
{
    private readonly ILanguageModelBackend _backend;
    private readonly CancelScope _cancelScope;
    private readonly SpeculativeTurnTracker _speculativeTurns;
    private readonly TurnLatencyStore _latency;

    public LanguageModelHandler(
        CancellationToken stopping,
        BlockingCollection<object> queueIn,
        BlockingCollection<object> queueOut,
        ILanguageModelBackend backend,
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

    protected override IEnumerable<PipelineMessage> Process(GenerateResponseRequest input)
    {
        var generation = _cancelScope.Generation;
        var config = input.RuntimeConfig;
        var response = input.Response;

        var history = config.Chat.Snapshot();
        if (history.Count == 0 && input.Audio is null)
        {
            yield return new EndOfResponse
            {
                Error = "no conversation context to respond to",
                ResponseKey = input.ResponseKey,
                TurnId = input.TurnId,
                TurnRevision = input.TurnRevision,
                CancelGeneration = generation,
            };
            yield break;
        }

        var request = new LlmRequest
        {
            History = history,
            Instructions = response?.ResolveInstructions(config),
            Tools = config.Tools,
            MaxOutputTokens = response?.ResolveMaxOutputTokens(config),
            Temperature = response?.ResolveTemperature(config),
            Audio = input.Audio,
            AudioSampleRate = input.AudioSampleRate,
        };

        var produced = false;
        string? error = null;
        var incomplete = ResponseIncompleteReason.None;

        IEnumerable<LlmResponseChunk> stream;
        try
        {
            stream = StreamingGuard(_backend.GenerateAsync(request, Stopping), input, generation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TranscriptLog.Error($"{GetType().Name}: generation failed", ex);
            error = ex.Message;
            stream = [];
        }

        foreach (var chunk in stream)
        {
            if (_cancelScope.IsStale(generation))
            {
                // 取消后不再向下游投递任何内容，但仍要以终止事件收尾，避免下游永久等待
                error ??= "cancelled";
                break;
            }

            produced = true;
            yield return chunk;
        }

        if (!produced && error is null)
        {
            error = "language model produced no output";
        }

        var end = new EndOfResponse
        {
            Error = error,
            Status = incomplete == ResponseIncompleteReason.None ? ResponseStatus.Completed : ResponseStatus.Incomplete,
            Reason = incomplete,
            ResponseKey = input.ResponseKey,
            TurnId = input.TurnId,
            TurnRevision = input.TurnRevision,
            CancelGeneration = generation,
        };

        if (input.TurnId is not null)
        {
            _latency.MarkResponseDone(input.TurnId);
        }

        yield return end;
    }

    /// <summary>
    /// 把异步流展开为同步序列，并把异常收敛成一条错误。
    ///
    /// 这里必须用阻塞枚举：BaseHandler 的处理线程本身就是专职线程，
    /// 同步模型让"事件按完成顺序"这件事不必再引入一层调度。
    /// </summary>
    private static IEnumerable<LlmResponseChunk> StreamingGuard(
        IAsyncEnumerable<LlmResponseChunk> source,
        GenerateResponseRequest input,
        int generation)
    {
        var enumerator = source.GetAsyncEnumerator(CancellationToken.None);
        try
        {
            while (true)
            {
                LlmResponseChunk current;
                try
                {
                    if (!enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                    {
                        break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    TranscriptLog.Error("language model stream failed", ex);
                    break;
                }

                yield return new LlmResponseChunk
                {
                    History = null,
                    Parts = current.Parts,
                    Text = current.Text,
                    LanguageCode = current.LanguageCode,
                    SelectedLanguage = current.SelectedLanguage ?? input.SelectedLanguage,
                    RuntimeConfig = current.RuntimeConfig ?? input.RuntimeConfig,
                    Response = current.Response ?? input.Response,
                    TurnId = current.TurnId ?? input.TurnId,
                    TurnRevision = current.TurnRevision ?? input.TurnRevision,
                    SpeechStoppedAtSeconds = current.SpeechStoppedAtSeconds ?? input.SpeechStoppedAtSeconds,
                    CancelGeneration = generation,
                    ResponseKey = current.ResponseKey ?? input.ResponseKey,
                    PrefetchTransaction = current.PrefetchTransaction ?? input.PrefetchTransaction,
                }.Normalize();
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    protected override void Cleanup() => _backend.Dispose();
}
