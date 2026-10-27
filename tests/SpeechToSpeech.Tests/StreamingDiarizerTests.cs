using SpeechToSpeech.Audio.Diarization;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 流式说话人分离（D009）在一段脚本化双人对话上的行为：跨窗口的稳定 id、重叠发言被保留、
/// 增量状态与「音频怎么切块」无关、缓冲有界、以及未定→定稿的修订协议。
/// 不用模型、不用网络——分割与声纹两个接缝都是音调替身。
/// </summary>
public sealed class StreamingDiarizerTests
{
    private const int SampleRate = DiarizationTone.SampleRate;
    private const int WindowSamples = 3_200; // 200 ms
    private const int HopSamples = 640;      // 40 ms ⇒ 80 ms 前瞻

    private static StreamingDiarizerConfig Config(params (string Key, object Value)[] overrides)
    {
        var config = new StreamingDiarizerConfig
        {
            SampleRate = SampleRate,
            WindowSamples = WindowSamples,
            HopSamples = HopSamples,
            Onset = 0.5,
            MinSilenceDuration = 0.04,
            MinSpeechDuration = 0.06,
            MinEmbeddingDuration = 0.08,
            MatchThreshold = 0.45,
            NewSpeakerThreshold = 0.62,
            MinNewSpeakerEvidence = 0.15,
            MinTurnConfidence = 0.5,
            ContinuityWindow = 0.3,
            MaxMergeGap = 0.1,
            MaxSpeakers = 10,
            RevisionWindow = 5,
            MergeThreshold = 0.3,
            MergeEvidence = 3,
            MaxBufferedSeconds = 8,
        };

        foreach (var (key, value) in overrides)
        {
            config = key switch
            {
                nameof(StreamingDiarizerConfig.MinEmbeddingDuration) => config with { MinEmbeddingDuration = (double)value },
                nameof(StreamingDiarizerConfig.MaxSpeakers) => config with { MaxSpeakers = (int)value },
                nameof(StreamingDiarizerConfig.MaxBufferedSeconds) => config with { MaxBufferedSeconds = (double)value },
                nameof(StreamingDiarizerConfig.ContinuityWindow) => config with { ContinuityWindow = (double)value },
                nameof(StreamingDiarizerConfig.MinNewSpeakerEvidence) => config with { MinNewSpeakerEvidence = (double)value },
                nameof(StreamingDiarizerConfig.Onset) => config with { Onset = (double)value },
                _ => throw new ArgumentOutOfRangeException(nameof(overrides), key, "不支持的覆盖项。"),
            };
        }

        return config;
    }

    private static (StreamingDiarizer Diarizer, ToneEmbedding Embedding) Create(StreamingDiarizerConfig? config = null)
    {
        var embedding = new ToneEmbedding();
        var diarizer = new StreamingDiarizer(new ToneMaskSegmentation(WindowSamples), embedding, config ?? Config());
        return (diarizer, embedding);
    }

    // 按 20 ms 一块推入（管线就是这么喂的），每次更新都折进消费方状态。
    private static SegmentTable Run(StreamingDiarizer diarizer, float[] audio, int chunkSamples = 320)
    {
        var table = new SegmentTable();
        for (int offset = 0; offset < audio.Length; offset += chunkSamples)
            table.Apply(diarizer.Push(audio.AsSpan(offset, Math.Min(chunkSamples, audio.Length - offset))));
        table.Apply(diarizer.Flush());
        return table;
    }

    [Fact]
    public void Alternating_speakers_keep_stable_ids_across_windows()
    {
        var (diarizer, _) = Create();
        var audio = DiarizationTone.Build(3.2,
            (0.0, 1.0, 0.5, 0.0),   // 说话人 A
            (1.2, 2.2, 0.0, 0.5),   // 说话人 B
            (2.4, 3.2, 0.5, 0.0));  // A 又说

        var table = Run(diarizer, audio);
        var finals = table.Final;

        Assert.Equal(3, finals.Count);
        Assert.All(finals, s => Assert.True(s.Confidence > 0.5, "每一轮都够长，应该能被描述"));

        Assert.Equal(0, finals[0].Speaker);
        Assert.Equal(1, finals[1].Speaker);
        Assert.Equal(0, finals[2].Speaker); // id 认的是声音，不是轮次
        Assert.Equal(new[] { 0, 1 }, diarizer.SpeakerIds);

        // 边界落在脚本上一个前瞻 hop 之内。
        Assert.Equal(0.0, finals[0].Start, 1);
        Assert.Equal(1.0, finals[0].End, 1);
        Assert.Equal(1.2, finals[1].Start, 1);
        Assert.Equal(2.2, finals[1].End, 1);
        Assert.Equal(2.4, finals[2].Start, 1);
    }

    [Fact]
    public void Overlapping_speech_keeps_both_segments()
    {
        // Onset 压到两个等幅声音各得 0.5 的下面：两条通道都必须保持开启，这正是本用例要的
        // （默认 0.5 会让重叠区恰好压在刀刃上）。
        var (diarizer, _) = Create(Config((nameof(StreamingDiarizerConfig.Onset), 0.4)));
        var audio = DiarizationTone.Build(2.2,
            (0.0, 1.5, 0.5, 0.0),   // A 独说，然后是 A + B
            (1.0, 2.0, 0.0, 0.5));  // B 在 A 还在说的时候开口

        var table = Run(diarizer, audio);
        var finals = table.Final;

        Assert.Equal(2, finals.Count);
        Assert.Equal(new[] { 0, 1 }, finals.Select(s => s.Speaker).OrderBy(x => x).ToArray());

        // 两个轮次真的重叠：压成语音/静音只会得到一整块合并区域。
        double overlapStart = Math.Max(finals[0].Start, finals[1].Start);
        double overlapEnd = Math.Min(finals[0].End, finals[1].End);
        Assert.True(overlapEnd - overlapStart >= 0.4, $"期望持续重叠，实际 [{overlapStart}, {overlapEnd}]");
    }

    [Fact]
    public void Chunking_does_not_change_the_result()
    {
        var audio = DiarizationTone.Build(2.6,
            (0.0, 0.9, 0.5, 0.0),
            (1.1, 1.9, 0.0, 0.5),
            (2.1, 2.6, 0.5, 0.0));

        var (chunked, _) = Create();
        var (whole, _) = Create();

        var fromChunks = Run(chunked, audio, chunkSamples: 320);        // 20 ms
        var fromWhole = Run(whole, audio, chunkSamples: audio.Length);  // 一次推完

        Assert.Equal(Describe(fromChunks.Final), Describe(fromWhole.Final));
    }

    [Fact]
    public void Audio_buffer_stays_bounded_on_a_long_stream()
    {
        var (diarizer, _) = Create(Config((nameof(StreamingDiarizerConfig.MaxBufferedSeconds), 2.0)));
        var audio = DiarizationTone.Build(12.0, (0.0, 12.0, 0.5, 0.0));

        for (int offset = 0; offset < audio.Length; offset += 1_600)
            diarizer.Push(audio.AsSpan(offset, Math.Min(1_600, audio.Length - offset)));

        // 12 秒的会话跑了 2 秒的缓冲：保留的音频有界，时间轴没有。
        Assert.True(diarizer.BufferedSamples <= 2 * SampleRate, $"缓冲涨到 {diarizer.BufferedSamples} 个样本");
        Assert.Equal(audio.Length, diarizer.SamplesSeen);
    }

    [Fact]
    public void Flush_commits_the_tail_that_would_still_be_provisional()
    {
        var (diarizer, _) = Create();
        var audio = DiarizationTone.Build(1.0, (0.0, 1.0, 0.5, 0.0));

        var table = new SegmentTable();
        for (int offset = 0; offset < audio.Length; offset += 320)
            table.Apply(diarizer.Push(audio.AsSpan(offset, Math.Min(320, audio.Length - offset))));

        var beforeFlush = table.All;
        Assert.Contains(beforeFlush, s => !s.IsFinal); // 尾巴还没定

        table.Apply(diarizer.Flush());
        Assert.All(table.All, s => Assert.True(s.IsFinal, "flush 必须提交每一个进行中的轮次"));
    }

    [Fact]
    public void A_reported_gap_keeps_the_absolute_timeline()
    {
        var (diarizer, _) = Create();
        var first = DiarizationTone.Build(0.8, (0.0, 0.8, 0.5, 0.0));
        var second = DiarizationTone.Build(0.8, (0.0, 0.8, 0.0, 0.5));

        var table = new SegmentTable();
        foreach (var sample in first.Chunk(320)) table.Apply(diarizer.Push(sample));
        table.Apply(diarizer.NotifyGap(SampleRate)); // 上游丢了一秒音频
        foreach (var sample in second.Chunk(320)) table.Apply(diarizer.Push(sample));
        table.Apply(diarizer.Flush());

        Assert.Equal(first.Length + SampleRate + second.Length, diarizer.SamplesSeen);

        var secondTurn = table.Final.Single(s => s.Speaker == 1);
        // 缺口是时间轴上的，不是缓冲里的静音：第二段在它之后开始，而不是从零开始。
        Assert.InRange(secondTurn.Start, 1.75, 1.95);
    }

    [Fact]
    public void A_short_turn_borrows_the_previous_speaker_instead_of_inventing_one()
    {
        var (diarizer, _) = Create(Config((nameof(StreamingDiarizerConfig.MinEmbeddingDuration), 0.2)));
        var audio = DiarizationTone.Build(2.1,
            (0.0, 1.5, 0.5, 0.0),   // A
            (1.7, 1.85, 0.0, 0.5)); // B 的 150 ms——够长算语音，太短描述不出声音

        var table = Run(diarizer, audio);

        Assert.Equal(new[] { 0 }, diarizer.SpeakerIds); // 150 ms 造不出新说话人
        var blip = table.Final.Last();
        Assert.Equal(0, blip.Speaker);
        Assert.True(blip.Confidence <= 0.3, "借来的标签不能声称高可信度");
    }

    [Fact]
    public void A_thin_turn_with_no_history_is_unknown()
    {
        var (diarizer, _) = Create(Config((nameof(StreamingDiarizerConfig.MinEmbeddingDuration), 0.2)));
        var audio = DiarizationTone.Build(0.9, (0.1, 0.25, 0.0, 0.5)); // 150 ms 的一下，前面什么都没有

        var table = Run(diarizer, audio);

        Assert.Empty(diarizer.SpeakerIds);
        var segment = Assert.Single(table.Final);
        Assert.Equal(SpeakerSegmentUpdate.UnknownSpeaker, segment.Speaker);
        Assert.Equal(0, segment.Confidence);
    }

    [Fact]
    public void The_speaker_cap_attaches_instead_of_creating()
    {
        var (diarizer, _) = Create(Config(
            (nameof(StreamingDiarizerConfig.MaxSpeakers), 1),
            (nameof(StreamingDiarizerConfig.MinNewSpeakerEvidence), 0.15)));

        var audio = DiarizationTone.Build(2.2, (0.0, 1.0, 0.5, 0.0), (1.2, 2.2, 0.0, 0.5));
        var table = Run(diarizer, audio);

        Assert.Single(diarizer.SpeakerIds);
        Assert.All(table.Final, s => Assert.Equal(0, s.Speaker));
    }

    [Fact]
    public void The_same_audio_produces_the_same_updates()
    {
        var audio = DiarizationTone.Build(2.0, (0.0, 1.0, 0.5, 0.0), (1.2, 2.0, 0.0, 0.5));

        var first = Run(Create().Diarizer, audio);
        var second = Run(Create().Diarizer, audio);

        Assert.Equal(Describe(first.Final), Describe(second.Final));
    }

    [Fact]
    public void Revisions_are_monotonic_per_segment_and_final_comes_last()
    {
        var (diarizer, _) = Create();
        var audio = DiarizationTone.Build(1.6, (0.0, 1.6, 0.5, 0.0));

        var seen = new Dictionary<string, List<SpeakerSegmentUpdate>>();
        for (int offset = 0; offset < audio.Length; offset += 320)
        {
            var update = diarizer.Push(audio.AsSpan(offset, Math.Min(320, audio.Length - offset)));
            foreach (var segment in update.Segments)
            {
                if (!seen.TryGetValue(segment.Id, out var list)) seen[segment.Id] = list = [];
                list.Add(segment);
            }
        }
        foreach (var segment in diarizer.Flush().Segments)
        {
            if (!seen.TryGetValue(segment.Id, out var list)) seen[segment.Id] = list = [];
            list.Add(segment);
        }

        Assert.NotEmpty(seen);
        foreach (var (id, revisions) in seen)
        {
            Assert.Equal(Enumerable.Range(0, revisions.Count), revisions.Select(r => r.Revision));
            Assert.All(revisions.SkipLast(1), r => Assert.False(r.IsFinal, $"{id} 在最后一次修订之前就 final 了"));
            Assert.True(revisions[^1].IsFinal);
            Assert.All(revisions, r => Assert.Equal(revisions[0].Start, r.Start, 6));
        }
    }

    [Fact]
    public void An_embedding_is_taken_per_turn_not_per_window()
    {
        var (diarizer, embedding) = Create();
        var audio = DiarizationTone.Build(2.0, (0.0, 2.0, 0.5, 0.0));

        Run(diarizer, audio);

        // 40 个窗口跑过这段音频（2 s / 40 ms 跳长），但声纹只在轮次的两次描述上取：
        // 一次在够长时、一次在收尾。开销随轮次，不随窗口数。
        Assert.InRange(embedding.Calls, 1, 3);
    }

    [Fact]
    public void Geometry_that_cannot_decide_its_own_region_is_rejected()
    {
        var segmentation = new ToneMaskSegmentation(WindowSamples);
        // 跳长超过窗口的三分之一：窗口看不清它要决定的区域。
        var bad = Config() with { HopSamples = WindowSamples / 2 };
        Assert.Throws<ArgumentException>(() => new StreamingDiarizer(segmentation, new ToneEmbedding(), bad));

        // 跳长让 own 区落不到帧网格上。
        var offGrid = Config() with { HopSamples = 480 };
        Assert.Throws<ArgumentException>(() => new StreamingDiarizer(segmentation, new ToneEmbedding(), offGrid));
    }

    [Fact]
    public void A_rate_mismatch_is_rejected_at_construction()
    {
        var bad = Config() with { SampleRate = 8_000 };
        Assert.Throws<ArgumentException>(
            () => new StreamingDiarizer(new ToneMaskSegmentation(WindowSamples), new ToneEmbedding(), bad));
    }

    // pyannote segmentation-3.0 的 sherpa 导出：window_size=160000 而 receptive_field_shift=270，两者不成整数倍。
    // 照抄 metadata 的窗口会被几何校验拒掉，所以默认几何从帧网格反推：592 帧 = 159840 样本（9.99 秒）。
    [Fact]
    public void The_real_models_off_grid_window_is_snapped_onto_the_frame_grid()
    {
        var model = new ToneMaskSegmentation(160_000, frameShift: 270, frameOffset: 495);
        var config = new StreamingDiarizerConfig { SampleRate = SampleRate, MaxBufferedSeconds = 20 };
        var diarizer = new StreamingDiarizer(model, new ToneEmbedding(), config);

        Assert.Equal(159_840, diarizer.WindowSamples);
        Assert.Equal(0, diarizer.WindowSamples % 270);
        Assert.Equal(0, diarizer.HopSamples % 270);
        // own 区起点仍落在帧网格上：这是「窗口与跳长的帧数必须同奇偶」的来源。
        Assert.Equal(0, (diarizer.WindowSamples - diarizer.HopSamples) / 2 % 270);
        // 默认跳长仍是窗口的十分之一量级，不是被奇偶凑成别的东西。
        Assert.InRange(diarizer.HopSamples, 159_840 / 12, 159_840 / 9);
    }

    // 显式给的窗口不进网格：那是调用方的配置错误，静默改写只会把它藏起来。
    [Fact]
    public void An_explicit_off_grid_window_is_rejected_rather_than_snapped()
    {
        var model = new ToneMaskSegmentation(160_000, frameShift: 270, frameOffset: 495);
        var config = new StreamingDiarizerConfig
        {
            SampleRate = SampleRate,
            WindowSamples = 160_000, // 正是 metadata 的原始值：引擎能接受，状态机不接受
            MaxBufferedSeconds = 20,
        };
        Assert.Throws<ArgumentException>(() => new StreamingDiarizer(model, new ToneEmbedding(), config));
    }

    // 一段话的声纹只能来自它自己的音频。
    //
    // 内部取音缓冲按需增长、之后复用；把整块缓冲交给 CopyTo 时，它会按缓冲的**当前长度**复制，
    // 于是先来一段 6 秒、再来一段 0.6 秒时，短的那次会连着上一段的残留一起送进声纹。
    // 后果不是崩溃，而是所有向量都朝「整条音轨的平均」收敛——谁跟谁都像，整场只剩一个说话人。
    [Fact]
    public void An_embedding_sees_only_its_own_turn()
    {
        var (diarizer, embedding) = Create();

        var audio = DiarizationTone.Build(7.0,
            (0.0, 6.0, 0.5, 0.0),  // 长段：把取音缓冲撑到 6 秒
            (6.3, 6.9, 0.0, 0.5)); // 短段：只有 0.6 秒

        Run(diarizer, audio);

        var seconds = embedding.Lengths.Select(n => n / (double)SampleRate).ToArray();
        Assert.True(
            seconds.Any(s => s <= 1.2),
            "没有任何一次声纹只取到短段（夹带了上一段的音频）：" +
            string.Join(", ", seconds.Select(s => $"{s:F2}s")));
    }

    // —— 下面三条是真实权重暴露出来的形态，音调替身造不出来 ——
    //
    // 稳定的音调过阈让每个轮次的起点逐位不变、通道号也从不换手，于是「按起点帧 + 通道号认亲」的实现
    // 在测试里全绿。换成 pyannote 的真实输出就散架：18.7 秒的素材算出 29.1 秒的段，时间线上全是
    // 互相咬住、谁也不延长谁的碎片。跨窗口唯一稳的判据是**时间重叠**——这三条把它钉住。

    // 同一段语音的起点在相邻窗口之间抖几帧（模型在 0.5 附近的穿越点本来就不稳）。
    // 抖动序列取 ±2 帧跨度 3 步：够让「起点帧相差不超过 1 帧」的认亲在一句话里断两次。
    [Fact]
    public void A_drifting_onset_stays_one_segment()
    {
        const int runStart = 30, runEnd = 150; // 0.30–1.50 秒
        var audio = DiarizationTone.Build(1.8, (0.0, 1.8, 0.5, 0.0));
        var mask = new ScriptedMaskSegmentation(WindowSamples, window =>
        {
            int windowStart = window * (HopSamples / DiarizationTone.FrameSamples);
            int drift = window * 3 % 5 - 2;
            return [(0, runStart + drift - windowStart, runEnd - windowStart)];
        });

        var table = Run(new StreamingDiarizer(mask, new ToneEmbedding(), Config()), audio);

        var segment = Assert.Single(table.Final);
        Assert.InRange(segment.Start, 0.20, 0.40);
        Assert.InRange(segment.End, 1.48, 1.55);
    }

    // 本地通道编号只在窗口内有效：同一个人这一窗在通道 0、下一窗在通道 1（另一个人不在场时轨道被排到前面）。
    // 真实权重的常态是这两件事一起发生——起点也抖、通道也换，所以这里一起造。
    [Fact]
    public void A_voice_that_switches_local_channel_stays_one_segment()
    {
        const int runStart = 30, runEnd = 150;
        var audio = DiarizationTone.Build(1.8, (0.0, 1.8, 0.5, 0.0));
        var mask = new ScriptedMaskSegmentation(WindowSamples, window =>
        {
            int windowStart = window * (HopSamples / DiarizationTone.FrameSamples);
            int drift = window * 3 % 5 - 2;
            return [(window % 2, runStart + drift - windowStart, runEnd - windowStart)];
        });

        var table = Run(new StreamingDiarizer(mask, new ToneEmbedding(), Config()), audio);

        var segment = Assert.Single(table.Final);
        Assert.InRange(segment.Start, 0.20, 0.40);
        Assert.InRange(segment.End, 1.48, 1.55);
    }

    // 模型有时会在一段两秒的轨道中间给另一条通道亮两三帧：那不是第二个人，是噪声。
    [Fact]
    public void A_two_frame_blip_on_the_other_channel_is_not_a_speaker()
    {
        const int runStart = 30, runEnd = 150;
        var audio = DiarizationTone.Build(1.8, (0.0, 1.8, 0.5, 0.0));
        var mask = new ScriptedMaskSegmentation(WindowSamples, window =>
        {
            int windowStart = window * (HopSamples / DiarizationTone.FrameSamples);
            var runs = new List<(int, int, int)> { (0, runStart - windowStart, runEnd - windowStart) };
            if (window == 17) runs.Add((1, 80 - windowStart, 82 - windowStart)); // 长轨道中段的 20 ms 亮点
            return runs;
        });

        var table = Run(new StreamingDiarizer(mask, new ToneEmbedding(), Config()), audio);

        var segment = Assert.Single(table.Final);
        Assert.Equal(0, segment.Speaker);
        Assert.InRange(segment.End, 1.48, 1.55);
    }

    private static string[] Describe(IReadOnlyList<SpeakerSegmentUpdate> segments) =>
        segments.Select(s => $"{s.Id}|{s.Speaker}|{s.Start:F3}|{s.End:F3}|{s.IsFinal}").ToArray();
}
