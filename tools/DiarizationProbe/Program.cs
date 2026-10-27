using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using SpeechToSpeech.Audio.Diarization;
using SpeechToSpeech.Backends.PyannoteDiarization;
using SpeechToSpeech.Backends.SpeakerEmbedding;

namespace SpeechToSpeech.Tools.DiarizationProbe;

/// <summary>
/// 说话人分离的<b>真实权重</b>冒烟。
///
/// <para>默认测试道里两个接缝都是双音替身（<c>DiarizationToneFixtures</c>），状态机行为可以在无模型无网络下
/// 复现——但也因此证明不了「真实 metadata 能被读对、真实输出宽度对得上 num_classes」。这个探针补那一段：
/// 载入 pyannote segmentation-3.0 的 ONNX，打印从 <c>custom_metadata_map</c> 读出的几何，跑几段确定性信号，
/// 再让流式状态机在<b>真实窗口几何</b>下走一遍。</para>
///
/// <para>它验证的不是准确率（那是模型的性质，不是本实现的性质），而是三件工程事实：
/// 元数据键名与 sherpa 导出一致、输出长度是 num_classes 的整数倍、同一窗口两次推理逐位相同。
/// 模型资产不入库（<c>models/</c> 被忽略），所以这个冒烟只能在有模型时跑——没有模型就明确失败，
/// 不静默跳过。</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string model = "models/pyannote-segmentation/model.onnx";
        int window = 0;
        int seconds = 6;
        bool metadataOnly = false;
        string? pcmPath = null;
        string? embeddingPath = null;
        int probsChannel = -1;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model":
                    model = args[++i];
                    break;
                case "--window":
                    window = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--seconds":
                    seconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--pcm":
                    pcmPath = args[++i];
                    break;
                case "--embedding":
                    embeddingPath = args[++i];
                    break;
                case "--probs":
                    probsChannel = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--metadata":
                    metadataOnly = true;
                    break;
                default:
                    Console.Error.WriteLine(
                        $"未知参数 '{args[i]}'\n用法: DiarizationProbe [--model <path>] [--window <samples>] " +
                        "[--seconds <n>] [--pcm <raw f32 16k mono>] [--embedding <path>] [--metadata]");
                    return 2;
            }
        }

        if (!File.Exists(model))
        {
            Console.Error.WriteLine(
                $"模型未就位：{model}\n" +
                "pyannote segmentation-3.0 的 sherpa 导出（5,992,913 字节，SHA-256 220ad67c…）" +
                "来源与下载地址见 porting/00-reference.md。");
            return 1;
        }

        if (metadataOnly)
        {
            Console.WriteLine(DumpMetadata(model).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        using var segmentation = new PyannoteOnnxMaskSegmentation(model, window);

        float[]? speech = pcmPath is null ? null : ReadPcm(pcmPath);

        var report = new JsonObject
        {
            ["model"] = model,
            ["geometry"] = new JsonObject
            {
                ["sample_rate"] = segmentation.SampleRate,
                ["window_samples"] = segmentation.WindowSamples,
                ["window_seconds"] = Round(segmentation.WindowSamples / (double)segmentation.SampleRate),
                ["frame_shift_samples"] = segmentation.FrameShiftSamples,
                ["frame_offset_samples"] = segmentation.FrameOffsetSamples,
                ["max_local_speakers"] = segmentation.MaxLocalSpeakers,
            },
        };

        // —— 窗口级观测：数字静音必须全零（可断言）；音调/噪声/真实语音各自点亮几个本地通道是观测 ——
        var windowSamples = segmentation.WindowSamples;
        var cases = new JsonObject
        {
            ["digital_silence"] = Describe(segmentation, new float[windowSamples]),
            ["tone_bursts"] = Describe(segmentation, ToneBursts(windowSamples, segmentation.SampleRate)),
            ["white_noise"] = Describe(segmentation, Noise(windowSamples, seed: 7)),
        };
        if (speech is not null) cases["speech"] = Describe(segmentation, Fit(speech, windowSamples));
        report["windows"] = cases;

        // —— 逐通道活动区间：把「模型到底认了几个人、边界在哪」摊开 ——
        // 声纹取自轮次的音频跨度，所以「A 和 B 被连成一个轮次」会让声纹变成两个人的混合，
        // 之后所有段都跟这个混合质心比、距离偏小，全部归到同一个 id。这张表就是排查它的入口。
        if (speech is not null)
        {
            var fitted = Fit(speech, windowSamples);
            report["tracks"] = TrackSpans(segmentation, fitted);

            // 逐帧概率：滞回（onset 0.5 / offset 0.35）到底是在哪断的，只有把曲线摊开才看得见。
            if (probsChannel >= 0)
            {
                var masks = segmentation.Analyze(fitted);
                var values = new JsonArray();
                for (int f = 0; f < masks.FrameCount; f++)
                    values.Add(Math.Round(masks.FrameProbabilities[f][probsChannel], 4));
                report["probs"] = new JsonObject
                {
                    ["channel"] = probsChannel,
                    ["frame_seconds"] = Round(segmentation.FrameShiftSamples / (double)segmentation.SampleRate),
                    ["values"] = values,
                };
            }
        }

        // —— 逐位确定性：同一窗口两次推理必须完全相同，否则「同音频同更新」不成立 ——
        var probe = Fit(speech ?? Noise(windowSamples, seed: 11), windowSamples);
        var a = segmentation.Analyze(probe);
        var b = segmentation.Analyze(probe);
        bool identical = a.FrameCount == b.FrameCount;
        for (int f = 0; identical && f < a.FrameCount; f++)
        {
            for (int s = 0; s < segmentation.MaxLocalSpeakers; s++)
            {
                if (a.FrameProbabilities[f][s] != b.FrameProbabilities[f][s]) identical = false;
            }
        }
        report["deterministic"] = identical;

        // —— 流式状态机在真实几何下走一遍 ——
        // 声纹：给了 --embedding 就用真实引擎，否则退回能量签名替身（只够证明「没崩、时钟没乱」）。
        ISpeakerEmbedding engine = embeddingPath is not null
            ? new OnnxSpeakerEmbedding(embeddingPath)
            : new EnergySignatureEmbedding();
        using var engineLifetime = engine as IDisposable;
        var embedding = new CountingEmbedding(engine);

        var config = new StreamingDiarizerConfig
        {
            SampleRate = segmentation.SampleRate,
            MaxBufferedSeconds = 20,
        };
        var diarizer = new StreamingDiarizer(segmentation, embedding, config);
        int chunk = Math.Max(1, segmentation.SampleRate / 10); // 100 ms
        var stream = speech ?? Dialogue(seconds * segmentation.SampleRate, segmentation.SampleRate);
        int segments = 0, merges = 0;

        // 同一段会被多次修订（按 Id upsert），所以按 Id 去重后再按说话人聚合——
        // 直接把每次推送加起来会把一段 3 秒的话记成 9 秒。
        var latest = new Dictionary<string, SpeakerSegmentUpdate>(StringComparer.Ordinal);
        for (int offset = 0; offset + chunk <= stream.Length; offset += chunk)
        {
            var update = diarizer.Push(stream.AsSpan(offset, chunk));
            segments += update.Segments.Count;
            merges += update.Merges.Count;
            foreach (var segment in update.Segments) latest[segment.Id] = segment;
        }
        var tail = diarizer.Flush();
        segments += tail.Segments.Count;
        foreach (var segment in tail.Segments) latest[segment.Id] = segment;

        report["stream"] = new JsonObject
        {
            ["audio_seconds"] = Round(stream.Length / (double)segmentation.SampleRate),
            ["window_samples"] = diarizer.WindowSamples,
            ["hop_samples"] = diarizer.HopSamples,
            ["lookahead_seconds"] = Round((diarizer.WindowSamples - diarizer.HopSamples) / 2.0 / segmentation.SampleRate),
            ["samples_seen"] = diarizer.SamplesSeen,
            ["buffered_samples"] = diarizer.BufferedSamples,
            ["embedding_calls"] = embedding.Calls,
            ["updates"] = segments,
            ["merges"] = merges,
            ["speakers"] = diarizer.SpeakerIds.Count,
        };

        report["embedding"] = new JsonObject
        {
            ["kind"] = engine is OnnxSpeakerEmbedding ? "onnx" : "energy-signature",
            ["dim"] = embedding.EmbeddingDim,
            ["framework"] = (engine as OnnxSpeakerEmbedding)?.Framework,
            ["calls"] = embedding.Calls,
        };

        // 每个说话人的落点：段数、总时长、起止。这才看得出「两个人在交替」而不是
        // 「一个人被切成了两半」——后者同样会给出 2 个 id，但两边的区间会互相咬住。
        var perSpeaker = latest.Values
            .Where(s => s.Speaker != SpeakerSegmentUpdate.UnknownSpeaker)
            .GroupBy(s => s.Speaker)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Segments = g.Count(),
                    Seconds = g.Sum(s => s.End - s.Start),
                    First = g.Min(s => s.Start),
                    Last = g.Max(s => s.End),
                });

        var detail = new JsonObject();
        foreach (var (speaker, stats) in perSpeaker)
        {
            detail[speaker.ToString(CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["segments"] = stats.Segments,
                ["seconds"] = Round(stats.Seconds),
                ["first_s"] = Round(stats.First),
                ["last_s"] = Round(stats.Last),
            };
        }
        report["speakers_detail"] = detail;
        report["unknown_segments"] = latest.Values.Count(s => s.Speaker == SpeakerSegmentUpdate.UnknownSpeaker);

        var timeline = new JsonArray();
        foreach (var segment in latest.Values.OrderBy(s => s.Start))
        {
            timeline.Add(new JsonObject
            {
                ["id"] = segment.Id,
                ["speaker"] = segment.Speaker,
                ["start_s"] = Round(segment.Start),
                ["end_s"] = Round(segment.End),
                ["final"] = segment.IsFinal,
                ["revision"] = segment.Revision,
                ["confidence"] = Round(segment.Confidence),
            });
        }
        report["segments"] = timeline;

        Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return identical ? 0 : 1;
    }

    /// <summary>
    /// 直接读 ONNX 的 <c>custom_metadata_map</c> 与输入/输出形状。几何全部来自这里——引擎从构造期就拒绝
    /// 「看起来像 pyannote 但缺键」的模型，所以排障第一步永远是先把真实数值打出来。
    /// </summary>
    private static JsonObject DumpMetadata(string model)
    {
        using var session = new InferenceSession(model);
        var meta = new JsonObject();
        foreach (var pair in session.ModelMetadata.CustomMetadataMap.OrderBy(p => p.Key, StringComparer.Ordinal))
            meta[pair.Key] = pair.Value;

        var inputs = new JsonArray();
        foreach (var pair in session.InputMetadata)
            inputs.Add(new JsonObject
            {
                ["name"] = pair.Key,
                ["element_type"] = pair.Value.ElementType.ToString(),
                ["dimensions"] = new JsonArray(pair.Value.Dimensions.Select(d => (JsonNode)d).ToArray()),
            });

        var outputs = new JsonArray();
        foreach (var pair in session.OutputMetadata)
            outputs.Add(new JsonObject
            {
                ["name"] = pair.Key,
                ["element_type"] = pair.Value.ElementType.ToString(),
                ["dimensions"] = new JsonArray(pair.Value.Dimensions.Select(d => (JsonNode)d).ToArray()),
            });

        return new JsonObject
        {
            ["model"] = model,
            ["custom_metadata"] = meta,
            ["inputs"] = inputs,
            ["outputs"] = outputs,
        };
    }

    /// <summary>一个窗口的观测：帧数、任一说话人活跃的帧、两个以上同时活跃（重叠）的帧、点亮了几个本地通道。</summary>
    private static JsonObject Describe(ISpeakerMaskSegmentation segmentation, float[] window)
    {
        var result = segmentation.Analyze(window);
        int active = 0, overlap = 0;
        double peak = 0;
        var lit = new bool[segmentation.MaxLocalSpeakers];
        for (int f = 0; f < result.FrameCount; f++)
        {
            var row = result.FrameProbabilities[f];
            int speakers = 0;
            for (int s = 0; s < row.Length; s++)
            {
                if (row[s] >= 0.5)
                {
                    speakers++;
                    lit[s] = true;
                }
                if (row[s] > peak) peak = row[s];
            }
            if (speakers > 0) active++;
            if (speakers > 1) overlap++;
        }

        return new JsonObject
        {
            ["frames"] = result.FrameCount,
            ["active_frames"] = active,
            ["overlap_frames"] = overlap,
            ["local_channels_lit"] = lit.Count(x => x),
            ["peak_probability"] = Round(peak),
        };
    }

    /// <summary>读一个裸 float32 小端、单声道 16 kHz 的 PCM 文件（冒烟素材由脚本转好，探针不做解码）。</summary>
    private static float[] ReadPcm(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));
        return samples;
    }

    /// <summary>把音频裁/补到正好一个窗口长（补的是数字静音，不影响已有的语音段）。</summary>
    private static float[] Fit(float[] audio, int samples)
    {
        var window = new float[samples];
        Array.Copy(audio, window, Math.Min(audio.Length, samples));
        return window;
    }

    /// <summary>交替的音调脉冲：逼出「有东西在响」的帧，用来把沉默与活动分开看。</summary>
    private static float[] ToneBursts(int samples, int sampleRate)
    {
        var audio = new float[samples];
        double burst = 0.4 * sampleRate;
        for (int i = 0; i < samples; i++)
        {
            bool on = (i / (int)burst) % 2 == 0;
            if (!on) continue;
            audio[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / sampleRate));
        }
        return audio;
    }

    /// <summary>白噪声：与音调一起构成「非静音但不一定是语音」的对照。</summary>
    private static float[] Noise(int samples, int seed)
    {
        var audio = new float[samples];
        var random = new Random(seed);
        for (int i = 0; i < samples; i++) audio[i] = (float)((random.NextDouble() * 2 - 1) * 0.2);
        return audio;
    }

    /// <summary>两段噪声之间夹一段静音的脚本化「对话」，只用来驱动状态机走完窗口与收尾路径。</summary>
    private static float[] Dialogue(int samples, int sampleRate)
    {
        var audio = Noise(samples, seed: 3);
        int blip = sampleRate; // 1 秒
        for (int i = 0; i < samples; i++)
        {
            bool silent = (i / blip) % 2 == 1;
            if (silent) audio[i] = 0f;
        }
        return audio;
    }

    private static double Round(double value) => Math.Round(value, 6);

    /// <summary>
    /// 每个本地通道在窗口里的活动区间（概率 ≥ 0.5 连续段）。时间相对窗口起点。
    /// </summary>
    private static JsonArray TrackSpans(ISpeakerMaskSegmentation segmentation, float[] window)
    {
        var result = segmentation.Analyze(window);
        double frameSeconds = segmentation.FrameShiftSamples / (double)segmentation.SampleRate;

        var channels = new JsonArray();
        for (int s = 0; s < segmentation.MaxLocalSpeakers; s++)
        {
            var spans = new JsonArray();
            bool active = false;
            int start = 0;
            for (int f = 0; f <= result.FrameCount; f++)
            {
                bool on = f < result.FrameCount && result.FrameProbabilities[f][s] >= 0.5;
                if (on && !active)
                {
                    active = true;
                    start = f;
                }
                else if (!on && active)
                {
                    active = false;
                    spans.Add(new JsonObject
                    {
                        ["start_s"] = Round(start * frameSeconds),
                        ["end_s"] = Round(f * frameSeconds),
                    });
                }
            }

            channels.Add(new JsonObject { ["channel"] = s, ["spans"] = spans });
        }

        return channels;
    }

    /// <summary>
    /// 只负责数调用次数的外壳：真实引擎与替身都套得上。
    /// 「每轮一次声纹」这条语义靠这个计数看——次数跟着轮次数走，而不是跟着窗口数走。
    /// </summary>
    private sealed class CountingEmbedding(ISpeakerEmbedding inner) : ISpeakerEmbedding
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public int EmbeddingDim => inner.EmbeddingDim;

        public float[] Embed(ReadOnlySpan<float> audio, int sampleRate)
        {
            Interlocked.Increment(ref _calls);
            return inner.Embed(audio, sampleRate);
        }
    }

    /// <summary>
    /// 冒烟用的声纹替身：把音频压成「对数能量 + 过零率」二维向量。判不出真人，但确定、有界、
    /// 且对不同响度的段给出不同向量——足够让原型比对与合并路径被真实走上一遍。
    /// </summary>
    private sealed class EnergySignatureEmbedding : ISpeakerEmbedding
    {
        private int _calls;

        public int EmbeddingDim => 2;

        public int Calls => Volatile.Read(ref _calls);

        public float[] Embed(ReadOnlySpan<float> audio, int sampleRate)
        {
            Interlocked.Increment(ref _calls);
            double energy = 0;
            int crossings = 0;
            for (int i = 0; i < audio.Length; i++)
            {
                energy += audio[i] * audio[i];
                if (i > 0 && (audio[i] >= 0) != (audio[i - 1] >= 0)) crossings++;
            }

            double rms = audio.Length > 0 ? Math.Sqrt(energy / audio.Length) : 0;
            double zcr = audio.Length > 1 ? crossings / (double)(audio.Length - 1) : 0;
            double magnitude = Math.Sqrt(rms * rms + zcr * zcr);
            if (magnitude <= 1e-9) return [0f, 0f];
            return [(float)(rms / magnitude), (float)(zcr / magnitude)];
        }
    }
}
