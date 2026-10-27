using System.Collections.Concurrent;
using System.Reflection;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Pipeline.Handlers;
using Xunit;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 转写通知器的出边：它只往文本队列发协议事件，**不**把转写塞进 LLM 队列。
///
/// 这条容易写反。直觉是「转写要交给模型」，于是 `yield return transcription`——但生成请求的
/// 发起者是 RealtimeService：它才知道会话配置（是否自动回复、逐响应覆盖、语言快照）。
/// 两边都发，真跑时每个服务端都会多出一条
/// 「LanguageModelHandler: unexpected input type Transcription」，而回复仍然只由另一条路径产生。
/// 单元测试不会因为多一条消息而失败，语料也测不到——这是真跑一轮才暴露的那类缺陷。
/// </summary>
public sealed class TranscriptionRoutingTests
{
    [Fact]
    public void FinalTranscription_EmitsEventButDoesNotFeedTheModel()
    {
        var textQueue = new BlockingCollection<object>(new ConcurrentQueue<object>(), 8);
        var (produced, events) = Run(new Transcription { Text = "你好", TurnId = "turn_1", TurnRevision = 0 }, textQueue);

        Assert.Empty(produced);
        var completed = Assert.Single(events.OfType<TranscriptionCompletedEvent>());
        Assert.Equal("你好", completed.Transcript);
    }

    [Fact]
    public void Failure_EmitsItemScopedEventAndStopsThere()
    {
        // 失败不是一个「该回答」的输入：喂给模型只会让它对着半句话编下去。
        var textQueue = new BlockingCollection<object>(new ConcurrentQueue<object>(), 8);
        var (produced, events) = Run(
            new TranscriptionFailure { Message = "模型没加载", TurnId = "turn_1", TurnRevision = 0 },
            textQueue);

        Assert.Empty(produced);
        Assert.Single(events.OfType<TranscriptionFailedEvent>());
    }

    [Fact]
    public void PartialTranscription_OnlyProducesDeltaEvents()
    {
        var textQueue = new BlockingCollection<object>(new ConcurrentQueue<object>(), 8);
        var (produced, events) = Run(
            new PartialTranscription { Text = "你 好 呀", TurnId = "turn_1", TurnRevision = 0 },
            textQueue);

        Assert.Empty(produced);
        // 扣住最新一个词：只剩「你 好」是可确认增长
        var delta = Assert.Single(events.OfType<PartialTranscriptionEvent>());
        Assert.Equal("你 好 ", delta.Delta);
    }

    private static (List<object> Upstream, List<object> Events) Run(
        PipelineMessage input,
        BlockingCollection<object> textQueue)
    {
        var handler = new TranscriptionNotifier(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            textQueue,
            snapshotsEnabled: false,
            new CancelScope(),
            new SpeculativeTurnTracker());

        var process = typeof(TranscriptionNotifier).GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TranscriptionNotifier.Process 不存在");

        var upstream = ((IEnumerable<object>)process.Invoke(handler, [input])!).ToList();
        var events = textQueue.ToList();
        return (upstream, events);
    }
}
