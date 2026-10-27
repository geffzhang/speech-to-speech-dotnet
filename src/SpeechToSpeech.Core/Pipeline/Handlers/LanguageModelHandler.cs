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
        var outOfBand = response?.IsOutOfBand ?? false;

        // 带外响应的输入三口径（上游 build_active_chat）：input 未携带读会话副本、
        // input=[] 用空聊天、input=[...] 用这些项做一次性种子。种子在这里校验，
        // 失败走 response.failed——上游同一处捕 ChatItemError 后 yield EndOfResponse。
        if (outOfBand && input.InputItems is not null)
        {
            var seeded = new List<ChatMessage>();
            foreach (var item in input.InputItems)
            {
                if (!ChatItemParser.TryParse(item, out var message, out var itemError))
                {
                    yield return new EndOfResponse
                    {
                        Error = itemError,
                        ResponseKey = input.ResponseKey,
                        CancelGeneration = generation,
                    };
                    yield break;
                }

                seeded.Add(message!);
            }

            history = seeded;
        }

        // 带内生成前检查会话是否在等工具结果（上游 active_chat.has_pending_tool_calls）：
        // 悬空的函数调用开新一轮生成，模型会把没应答的调用当成已应答。
        if (!outOfBand && config.Chat.HasPendingToolCalls())
        {
            yield return new EndOfResponse
            {
                Error = "Cannot generate a response while function call outputs are pending.",
                ResponseKey = input.ResponseKey,
                TurnId = input.TurnId,
                TurnRevision = input.TurnRevision,
                CancelGeneration = generation,
            };
            yield break;
        }

        // 上游口径：response.instructions 优先，response 为 None 或没带 instructions
        // 时回落 runtime_config.session.instructions（C027：语音轮的请求不带 response，
        // 少了这一步回落，session.update 配的指令在语音对话里从来没到过模型）。
        var instructions = response?.ResolveInstructions(config) ?? config.Instructions;

        // 上游没有「空会话」守卫：_apply_instructions 只要有 instructions 就把
        // system 提示拼进聊天，模型对纯 system 上下文照常生成（新连接还没说话
        // 就 response.create 是合法用法）。只有连 system 提示都拼不出来——
        // 既无 instructions、也无音频、历史又为空——才真的没有可生成的上下文。
        if (history.Count == 0 && input.Audio is null && string.IsNullOrWhiteSpace(instructions))
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
            Instructions = instructions,
            Tools = config.Tools,
            MaxOutputTokens = response?.ResolveMaxOutputTokens(config),
            Temperature = response?.ResolveTemperature(config),
            Audio = input.Audio,
            AudioSampleRate = input.AudioSampleRate,
        };

        var produced = false;
        string? error = null;
        var incomplete = ResponseIncompleteReason.None;

        // llm_s 覆盖整段生成：从组装请求、发出去，到把 provider 的输出吃完。
        // 失败的请求也要记——「模型报错」和「模型很慢」都是要看得见的。
        var generationStartedAt = Monotonic.NowSeconds();

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
            // 「哪些工具结果真的到了模型」在这里定格：取的是**组装请求时用的那份历史**，
            // 生成期间客户端再追加的条目不在这份快照里，也就没到过模型。
            // 是否真的报给客户端由下游按 succeeded 决定（上游同一处分两段判断）。
            InputToolCallIds = [.. ChatSession.ToolOutputCallIds(history)],
        };

        // 用 response_key 而不是 turn_id 记账：同一轮里工具调用与它的口头回答是两条
        // 独立的响应，按轮次记会把两者的 llm_s 挤进同一个格子。
        // 不按「有没有 turn_id」筛：客户端独立创建的响应照样要有 llm_s，它只是没有 e2e。
        _latency.GetResponse(input.ResponseKey)?.RecordLlm(Monotonic.NowSeconds() - generationStartedAt);

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
