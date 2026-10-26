using System.Collections.Concurrent;
using System.Diagnostics;

namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// 管线零件的基类。每个零件有自己的输入与输出队列。
///
/// 与上游 <c>baseHandler.BaseHandler</c> 逐条对应：
/// <list type="bullet">
/// <item>放入输入队列的对象由 <see cref="Process"/> 处理，产出项写入输出队列；</item>
/// <item><see cref="PipelineSentinels.PipelineEnd"/> 是避免队列死锁的终止哨兵；</item>
/// <item><see cref="ControlKind.SessionEnd"/> 是软控制消息，重置每会话状态但不终止线程；</item>
/// <item>处理阶段的异常只记录到日志，绝不让线程退出；</item>
/// <item>线程退出时一定会 <see cref="Cleanup"/> 并向输出队列写入终止哨兵。</item>
/// </list>
/// </summary>
public abstract class BaseHandler<TIn, TOut> : IDisposable
    where TIn : class
{
    /// <summary>队列等待超时；与上游 0.1s 一致，用于周期性检查停止信号。</summary>
    private static readonly TimeSpan QueuePollTimeout = TimeSpan.FromMilliseconds(100);

    private readonly List<double> _times = [];
    private int _disposed;

    protected BaseHandler(
        CancellationToken stopping,
        BlockingCollection<object> queueIn,
        BlockingCollection<object> queueOut)
    {
        Stopping = stopping;
        QueueIn = queueIn;
        QueueOut = queueOut;
        try
        {
            Setup();
        }
        catch
        {
            try
            {
                Cleanup();
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine($"Handler cleanup failed after setup failure: {cleanupEx.Message}");
            }

            throw;
        }
    }

    /// <summary>停止信号。等价上游 <c>stop_event</c>。</summary>
    protected CancellationToken Stopping { get; }

    protected BlockingCollection<object> QueueIn { get; }

    protected BlockingCollection<object> QueueOut { get; }

    /// <summary>在管线中的序号，仅用于日志上下文。</summary>
    public int? PipelineIndex { get; set; }

    /// <summary>取消信号。需要感知取消的 handler 覆写该属性。</summary>
    public virtual CancelScope? CancelScope => null;

    /// <summary>投机轮次跟踪器。需要感知投机代际的 handler 覆写该属性。</summary>
    public virtual SpeculativeTurnTracker? SpeculativeTurns => null;

    /// <summary>零件初始化：加载模型、打开设备等。</summary>
    protected virtual void Setup()
    {
    }

    /// <summary>处理一个输入项，产出零到多个输出项。</summary>
    protected abstract IEnumerable<TOut> Process(TIn input);

    /// <summary>
    /// 是否处理该输入。
    ///
    /// 两条丢弃规则与上游一致：
    /// 1) 带 prefetch 事务的 TTS 输入，若未被 <c>response.create</c> 认领，则丢弃；
    /// 2) 取消代际已过期的输入，除 <see cref="EndOfResponse"/> 与 <see cref="TokenUsageEvent"/> 外一律丢弃。
    /// </summary>
    protected virtual bool ShouldProcessInput(TIn item)
    {
        if (item is TtsInput { PrefetchTransaction: { } transaction })
        {
            // 隐藏的跟进 LM 工作可以与原 TTS 重叠，但合成必须在标准 response.create 认领后才开始。
            // 丢弃时会释放这个串行 worker，而不进入 TTS 后端。
            while (!Stopping.IsCancellationRequested && !transaction.WaitUntilResolved(TimeSpan.FromMilliseconds(50)))
            {
            }

            if (!transaction.Claimed)
            {
                return false;
            }
        }

        var cancelScope = CancelScope;
        var cancelGeneration = TryGetCancelGeneration(item);
        if (cancelScope is not null
            && cancelGeneration is not null
            && item is not EndOfResponse
            && item is not TokenUsageEvent
            && cancelScope.IsStale(cancelGeneration.Value))
        {
            return false;
        }

        return true;
    }

    /// <summary>是否发出该输出。</summary>
    protected virtual bool ShouldEmitOutput(TOut output) => true;

    /// <summary>发出前的钩子。</summary>
    protected virtual void BeforeEmitOutput(TOut output)
    {
    }

    /// <summary>
    /// 把输出打包为队列项。
    ///
    /// 上游在这里把裸 <c>bytes</c>/<c>ndarray</c> 音频按源输入的代际标记包成 <see cref="AudioOutput"/>，
    /// 否则发送循环无法分辨"哪段音频属于哪次响应"。
    /// </summary>
    protected virtual object OutputForQueue(TOut output, TIn sourceInput)
    {
        var cancelGeneration = TryGetCancelGeneration(sourceInput);
        var responseKey = TryGetResponseKey(sourceInput);
        var cleanupOnly = TryGetCleanupOnly(sourceInput);

        if ((cancelGeneration is not null || responseKey is not null || cleanupOnly)
            && output is byte[] audio)
        {
            return new AudioOutput
            {
                Audio = audio,
                CancelGeneration = cancelGeneration,
                ResponseKey = responseKey,
                CleanupOnly = cleanupOnly,
            };
        }

        return output!;
    }

    /// <summary>转发有序事件，允许下游阶段先冲刷音频。</summary>
    protected virtual IEnumerable<object> ProcessPipelineEvent(PipelineEvent pipelineEvent)
    {
        yield return pipelineEvent;
    }

    /// <summary>线程主循环。退出时保证清理并写入终止哨兵。</summary>
    public void Run()
    {
        try
        {
            RunLoop();
        }
        finally
        {
            try
            {
                Cleanup();
            }
            finally
            {
                QueueOut.Add(PipelineSentinels.PipelineEnd);
            }
        }
    }

    private void RunLoop()
    {
        EndOfResponse? pendingHistory = null;

        while (!Stopping.IsCancellationRequested)
        {
            object item;
            try
            {
                if (!QueueIn.TryTake(out item!, QueuePollTimeout))
                {
                    continue;
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (PipelineControl.IsControlMessage(item, ControlKind.SessionEnd))
            {
                pendingHistory = null;
                try
                {
                    OnSessionEnd();
                }
                catch (Exception ex)
                {
                    TranscriptLog.Error($"{GetType().Name}: Error in OnSessionEnd()", ex);
                }

                QueueOut.Add(item);
                continue;
            }

            if (ReferenceEquals(item, PipelineSentinels.PipelineEnd))
            {
                // 标识停止，避免队列死锁
                break;
            }

            if (item is PipelineControlMessage control)
            {
                Console.Error.WriteLine($"{GetType().Name}: unexpected control message kind: {control.Kind}");
                continue;
            }

            if (item is not TIn typedItem)
            {
                Console.Error.WriteLine(
                    $"{GetType().Name}: unexpected input type {item.GetType().Name}, expected {typeof(TIn).Name}");
                continue;
            }

            if (item is GenerateResponseRequest request && pendingHistory is not null)
            {
                var history = pendingHistory.History;
                var cancelScope = CancelScope;
                var tracker = SpeculativeTurns;

                // 只有服务对上一次历史做出裁决后，才读取下一个请求。
                // 逻辑完成绕开慢速 TTS。
                while (history is not null && ReferenceEquals(history.Chat, request.RuntimeConfig.Chat))
                {
                    if (Stopping.IsCancellationRequested)
                    {
                        break;
                    }

                    if (cancelScope is not null
                        && pendingHistory.CancelGeneration is not null
                        && cancelScope.IsStale(pendingHistory.CancelGeneration.Value))
                    {
                        break;
                    }

                    if (tracker is not null
                        && !tracker.IsLatest(pendingHistory.TurnId, pendingHistory.TurnRevision))
                    {
                        break;
                    }

                    if (history.WaitUntilResolved(TimeSpan.FromMilliseconds(50)))
                    {
                        break;
                    }
                }

                pendingHistory = null;
                if (Stopping.IsCancellationRequested)
                {
                    break;
                }
            }

            if (!ShouldProcessInput(typedItem))
            {
                continue;
            }

            // 响应事件与对应音频共用 TTS 队列；在此转发可保留模型原始的文本/工具/音频顺序。
            if (item is PipelineEvent pipelineEvent)
            {
                foreach (var output in ProcessPipelineEvent(pipelineEvent))
                {
                    QueueOut.Add(output);
                }

                continue;
            }

            var startTime = Stopwatch.GetTimestamp();
            try
            {
                foreach (var output in Process(typedItem))
                {
                    if (!ShouldEmitOutput(output))
                    {
                        startTime = Stopwatch.GetTimestamp();
                        continue;
                    }

                    var elapsed = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
                    _times.Add(elapsed);
                    if (ShouldLogTiming(output))
                    {
                        Console.Error.WriteLine($"{GetType().Name}: {LastTime:F3} s");
                    }

                    BeforeEmitOutput(output);
                    var queuedOutput = OutputForQueue(output, typedItem);
                    QueueOut.Add(queuedOutput);

                    if (typedItem is GenerateResponseRequest && queuedOutput is EndOfResponse end)
                    {
                        pendingHistory = end;
                    }

                    startTime = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception ex)
            {
                TranscriptLog.Error($"{GetType().Name}: Error in Process()", ex);
            }
        }
    }

    /// <summary>最近一次处理耗时（秒）。</summary>
    public double LastTime => _times.Count == 0 ? 0 : _times[^1];

    /// <summary>低于该阈值不打印耗时日志。</summary>
    protected virtual double MinTimeToDebug => 0.001;

    protected virtual bool ShouldLogTiming(TOut output) => LastTime > MinTimeToDebug;

    /// <summary>清理资源。</summary>
    protected virtual void Cleanup()
    {
    }

    /// <summary>会话结束钩子：重置每会话状态。</summary>
    protected virtual void OnSessionEnd()
    {
    }

    // ── 反射式取代际标记 ──────────────────────────────────────────────
    //
    // 上游用 getattr(item, "cancel_generation", None) 统一取值，因为这些字段分布在
    // 多个消息类型上且都是可选的。C# 里用 pattern matching 显式列出，行为等价且可静态检查。

    private static int? TryGetCancelGeneration(TIn item) => item switch
    {
        LlmResponseChunk chunk => chunk.CancelGeneration,
        TokenUsage usage => usage.CancelGeneration,
        EndOfResponse end => end.CancelGeneration,
        TtsInput input => input.CancelGeneration,
        AudioOutput audio => audio.CancelGeneration,
        AssistantOutputEvent evt => evt.CancelGeneration,
        AssistantToolCallReadyEvent evt => evt.CancelGeneration,
        TokenUsageEvent evt => evt.CancelGeneration,
        AssistantResponseDoneEvent evt => evt.CancelGeneration,
        ResponseGenerationDoneEvent evt => evt.CancelGeneration,
        ResponseFailedEvent evt => evt.CancelGeneration,
        _ => null,
    };

    private static string? TryGetResponseKey(TIn item) => item switch
    {
        LlmResponseChunk chunk => chunk.ResponseKey,
        TokenUsage usage => usage.ResponseKey,
        EndOfResponse end => end.ResponseKey,
        TtsInput input => input.ResponseKey,
        AudioOutput audio => audio.ResponseKey,
        AssistantOutputEvent evt => evt.ResponseKey,
        AssistantToolCallReadyEvent evt => evt.ResponseKey,
        TokenUsageEvent evt => evt.ResponseKey,
        AssistantResponseDoneEvent evt => evt.ResponseKey,
        ResponseGenerationDoneEvent evt => evt.ResponseKey,
        ResponseFailedEvent evt => evt.ResponseKey,
        _ => null,
    };

    private static bool TryGetCleanupOnly(TIn item) => item switch
    {
        EndOfResponse end => end.CleanupOnly,
        AudioOutput audio => audio.CleanupOnly,
        _ => false,
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Cleanup();
        }
        catch (Exception ex)
        {
            TranscriptLog.Error($"{GetType().Name}: Error in Dispose()", ex);
        }

        GC.SuppressFinalize(this);
    }
}
