using System.Collections.Concurrent;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 处理线程的循环语义。
///
/// 上游把这些规则写在一个 200 行的基类里，看起来像样板代码，实际每条都对应一次线上事故：
/// 哨兵没发就退出 → 下游永久阻塞；异常冒出循环 → 整条管线静默死亡；
/// 会话结束当成停止 → 换个会话就得重启进程。
/// </summary>
public sealed class BaseHandlerTests
{
    private sealed class RecordingHandler : BaseHandler<string, string>
    {
        public RecordingHandler(
            CancellationToken stopping,
            BlockingCollection<object> queueIn,
            BlockingCollection<object> queueOut,
            CancelScope? cancelScope = null)
            : base(stopping, queueIn, queueOut)
        {
            CancelScope = cancelScope;
        }

        public override CancelScope? CancelScope { get; }

        public List<string> Processed { get; } = [];

        public int SessionEndCount { get; private set; }

        public bool CleanedUp { get; private set; }

        public Func<string, IEnumerable<string>>? Behavior { get; set; }

        protected override IEnumerable<string> Process(string input)
        {
            Processed.Add(input);
            if (Behavior is not null)
            {
                return Behavior(input);
            }

            return [$"out:{input}"];
        }

        protected override void OnSessionEnd() => SessionEndCount++;

        protected override void Cleanup() => CleanedUp = true;
    }

    private static (RecordingHandler Handler, BlockingCollection<object> In, BlockingCollection<object> Out,
        CancellationTokenSource Cts, Task Thread) Start(CancelScope? scope = null)
    {
        var queueIn = new BlockingCollection<object>(new ConcurrentQueue<object>());
        var queueOut = new BlockingCollection<object>(new ConcurrentQueue<object>());
        var cts = new CancellationTokenSource();
        var handler = new RecordingHandler(cts.Token, queueIn, queueOut, scope);
        var thread = Task.Run(handler.Run);
        return (handler, queueIn, queueOut, cts, thread);
    }

    [Fact]
    public async Task PipelineEndSentinel_StopsThread_AndEmitsSentinelDownstream()
    {
        var (handler, queueIn, queueOut, cts, thread) = Start();

        queueIn.Add("hello");
        queueIn.Add(PipelineSentinels.PipelineEnd);
        await thread.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(handler.CleanedUp);
        Assert.Equal(1, queueOut.Count(o => ReferenceEquals(o, PipelineSentinels.PipelineEnd)));

        cts.Dispose();
    }

    [Fact]
    public async Task SessionEnd_IsForwarded_AndDoesNotStopTheThread()
    {
        var (handler, queueIn, queueOut, cts, thread) = Start();

        queueIn.Add(PipelineControl.SessionEnd);
        queueIn.Add("after-session-end");
        queueIn.Add(PipelineSentinels.PipelineEnd);
        await thread.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, handler.SessionEndCount);
        Assert.Contains("after-session-end", handler.Processed);
        Assert.Contains(queueOut, item => item is PipelineControlMessage);

        cts.Dispose();
    }

    [Fact]
    public async Task ExceptionInProcess_IsContainedAndNextItemStillRuns()
    {
        var (handler, queueIn, _, cts, thread) = Start();
        handler.Behavior = input => input == "boom"
            ? throw new InvalidOperationException("boom")
            : [$"out:{input}"];

        queueIn.Add("boom");
        queueIn.Add("fine");
        queueIn.Add(PipelineSentinels.PipelineEnd);
        await thread.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["boom", "fine"], handler.Processed);

        cts.Dispose();
    }

    [Fact]
    public async Task StaleGenerationInput_IsDroppedSilently()
    {
        var scope = new CancelScope();
        var (handler, queueIn, _, cts, thread) = Start(scope);

        scope.Cancel(); // 代际 1；下面这条带着旧代际 0
        queueIn.Add(new LlmResponseChunk
        {
            Parts = [new AssistantTextPart { Text = "stale" }],
            CancelGeneration = 0,
            ResponseKey = "resp-1",
        }.Normalize());
        queueIn.Add(PipelineSentinels.PipelineEnd);
        await thread.WaitAsync(TimeSpan.FromSeconds(5));

        // 输入在 should_process_input 阶段就被丢掉，不会进入 Process
        Assert.Empty(handler.Processed);

        cts.Dispose();
    }

    [Fact]
    public async Task UnknownControlKindIsSkipped_WithoutStopping()
    {
        var (handler, queueIn, _, cts, thread) = Start();

        queueIn.Add(new PipelineControlMessage((ControlKind)999));
        queueIn.Add("still-alive");
        queueIn.Add(PipelineSentinels.PipelineEnd);
        await thread.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("still-alive", handler.Processed);

        cts.Dispose();
    }
}

/// <summary>未注册工厂时的后端选择行为。</summary>
public sealed class BackendRegistryTests
{
    [Fact]
    public void AllUpstreamBackendNamesAreRegistered()
    {
        Assert.Equal(15, SpeechToSpeech.Core.Backends.BackendRegistry.SttBackends.Count);
        Assert.Equal(5, SpeechToSpeech.Core.Backends.BackendRegistry.LlmBackends.Count);
        Assert.Equal(12, SpeechToSpeech.Core.Backends.BackendRegistry.TtsBackends.Count);
    }

    [Fact]
    public void UnknownBackendName_ListsEveryLegalValue()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SpeechToSpeech.Core.Backends.BackendRegistry.Find(
                SpeechToSpeech.Core.Backends.BackendRegistry.SttBackends,
                "not-a-backend",
                SpeechToSpeech.Core.Backends.BackendKind.Stt));

        Assert.Contains("whisper", ex.Message);
        Assert.Contains("paraformer", ex.Message);
        Assert.Contains("可选值", ex.Message);
    }

    [Fact]
    public void UnimplementedBackend_ReportsReasonInsteadOfSilentlyDegrading()
    {
        var context = new SpeechToSpeech.Core.Backends.BackendContext
        {
            Vad = new SpeechToSpeech.Core.Backends.VadOptions(),
            Stt = new SpeechToSpeech.Core.Backends.SttOptions(),
            Llm = new SpeechToSpeech.Core.Backends.LlmOptions(),
            Tts = new SpeechToSpeech.Core.Backends.TtsOptions(),
        };

        var ex = Assert.Throws<SpeechToSpeech.Core.Backends.BackendUnavailableException>(() =>
            SpeechToSpeech.Core.Backends.BackendRegistry.CreateStt("parakeet-unified", context));

        Assert.Equal("parakeet-unified", ex.BackendName);
        Assert.Contains("NeMo", ex.Reason);
    }
}
