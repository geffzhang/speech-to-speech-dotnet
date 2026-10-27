using System.Collections.Concurrent;
using System.Reflection;
using SpeechToSpeech.Audio.Diarization;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Pipeline.Handlers;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 归因接线（D009）：时间线上的段 → 转写的 speaker 归属。
///
/// <para>这是「谁说的这句话」的全部实现，落点在 STT 这一级——转写和它的音频区间只在这一处同时在场
/// （上游同名逻辑在 <c>base_stt_handler</c> 里读 <c>speaker_pending</c>，位置相同）。上游另有一条
/// 「分离器晚于转写」的待定路径；本实现的分离级在 VAD 之前、时钟连续，所以这里只需要区间 join
/// 加一条 continuity 借用。</para>
/// </summary>
public sealed class SpeakerAttributionWiringTests
{
    private const string Prefix = "speaker_";

    private static Transcription Transcribe(
        SpeakerTimeline? timeline,
        double? start,
        double? end,
        string prefix = Prefix,
        SpeakerAttribution? carried = null)
    {
        var handler = new SttHandler(
            CancellationToken.None,
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new BlockingCollection<object>(new ConcurrentQueue<object>(), 8),
            new FixedStt(),
            new CancelScope(),
            new SpeculativeTurnTracker(),
            latency: null,
            speakerTimeline: timeline,
            speakerIdPrefix: prefix);

        var process = typeof(SttHandler).GetMethod(
            "Process",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SttHandler.Process 不存在");

        var produced = ((IEnumerable<PipelineMessage>)process.Invoke(handler, [
            new VadAudio
            {
                Audio = [0.1f, 0.2f],
                SampleRate = 16_000,
                AudioStartSeconds = start,
                AudioEndSeconds = end,
                SpeakerAttribution = carried,
            },
        ])!).ToList();

        return produced.OfType<Transcription>().Single();
    }

    private static SpeakerTimeline Timeline(params SpeakerSegmentUpdate[] segments)
    {
        var timeline = new SpeakerTimeline();
        timeline.Apply(new DiarizerUpdate(segments, []));
        return timeline;
    }

    private static SpeakerSegmentUpdate Segment(double start, double end, int speaker, double confidence = 0.9) =>
        new($"seg_{start}_{speaker}", speaker, start, end, IsFinal: true, Revision: 1, Confidence: confidence);

    [Fact]
    public void A_span_inside_a_committed_segment_gets_the_speaker_prefix()
    {
        var timeline = Timeline(Segment(0.0, 2.0, speaker: 0), Segment(2.0, 4.0, speaker: 1));

        var transcription = Transcribe(timeline, 0.4, 1.6);

        Assert.NotNull(transcription.SpeakerAttribution);
        Assert.Equal("speaker_0", transcription.SpeakerAttribution.SpeakerId);
        Assert.Equal(0.9, transcription.SpeakerAttribution.Confidence, 6);
    }

    // 区间和第二个人重叠更多时归第二个人：归因是「重叠最多者胜」，不是「第一个碰到的人」。
    [Fact]
    public void The_segment_covering_most_of_the_span_wins()
    {
        var timeline = Timeline(Segment(0.0, 0.6, speaker: 0), Segment(0.6, 3.0, speaker: 1));

        Assert.Equal("speaker_1", Transcribe(timeline, 0.2, 2.0).SpeakerAttribution?.SpeakerId);
    }

    [Fact]
    public void The_prefix_is_honoured_where_the_label_is_built()
    {
        var timeline = Timeline(Segment(0.0, 2.0, speaker: 3));

        Assert.Equal("spk-3", Transcribe(timeline, 0.5, 1.5, prefix: "spk-").SpeakerAttribution?.SpeakerId);
    }

    // 没有段覆盖这段音频、也没有紧邻的前一位说话人：宁可没有标签，也不要编一个。
    [Fact]
    public void A_span_no_segment_covers_stays_unlabelled()
    {
        var timeline = Timeline(Segment(5.0, 6.0, speaker: 0));

        var transcription = Transcribe(timeline, 0.5, 1.5);

        Assert.Null(transcription.SpeakerAttribution);
        // 时间轴区间本身照旧带下去：缺的是「谁」，不是「什么时候」。
        Assert.Equal(0.5, transcription.StartSeconds);
        Assert.Equal(1.5, transcription.EndSeconds);
    }

    // 没有时间轴区间就无从归因（拿不到打点时不能猜），但转写照发。
    [Fact]
    public void A_transcript_without_an_audio_span_is_not_attributed()
    {
        var timeline = Timeline(Segment(0.0, 2.0, speaker: 0));

        var transcription = Transcribe(timeline, start: null, end: null);

        Assert.Null(transcription.SpeakerAttribution);
        Assert.Null(transcription.StartSeconds);
        Assert.Equal("hello", transcription.Text);
    }

    // 后端自己已经给出归属（例如上游那种异步分离把标签挂回音频上）时不被时间线覆盖：
    // 后到的推断不能盖掉先到的结论。
    [Fact]
    public void An_attribution_carried_by_the_audio_is_not_overwritten()
    {
        var timeline = Timeline(Segment(0.0, 2.0, speaker: 0));

        var transcription = Transcribe(
            timeline, 0.5, 1.5, carried: new SpeakerAttribution("speaker_9") { Confidence = 0.5 });

        Assert.Equal("speaker_9", transcription.SpeakerAttribution?.SpeakerId);
        Assert.Equal(0.5, transcription.SpeakerAttribution?.Confidence);
    }

    private sealed class FixedStt : ISttBackend
    {
        public string Name => "fixed";

        public IEnumerable<PipelineMessage> Transcribe(VadAudio audio, CancellationToken cancellationToken)
        {
            yield return new Transcription
            {
                Text = "hello",
                TurnId = audio.TurnId,
                TurnRevision = audio.TurnRevision,
                // 真后端会把音频上已有的归属透传下来（上游就是让分离结果挂在音频上跟过来）。
                SpeakerAttribution = audio.SpeakerAttribution,
            };
        }

        public void Dispose()
        {
        }
    }
}
