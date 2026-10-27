using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Tools.SmartTurnProbe;

/// <summary>
/// 端点判定层的本实现探针。
///
/// 读与 <c>tools/refprobe/smart_turn_probe.py</c> 相同的输入，调**产品入口**
/// （<see cref="TurnEndTiming.Decide"/> / <see cref="TurnAudioWindow.Prepare"/>），
/// 不自己重写一份决策，否则产品改了、探针没改，diff 永远假绿。
///
/// 两条归一化约定（见 <c>corpus/smart-turn/README.md</c>）：
/// - 状态输出上游同名字符串（<c>disabled</c> / <c>failed</c> / <c>complete</c> / <c>incomplete</c>）
/// - 窗口样本输出 float32 位模式（uint32）而不是十进制：浮点的最短表示是
///   两侧运行时各自的策略，比它等于允许漂移
/// </summary>
internal static class Program
{
    /// <summary>脚本化判定器：结论由语料指定，不做任何推理。</summary>
    private sealed class ScriptedClassifier : ITurnEndClassifier
    {
        private readonly bool _complete;
        private readonly double _probability;
        private readonly bool _fail;

        public ScriptedClassifier(bool complete, double probability, bool fail)
        {
            _complete = complete;
            _probability = probability;
            _fail = fail;
        }

        public string Name => "scripted";

        public TurnEndPrediction Predict(ReadOnlySpan<float> audio, int sampleRate)
            => _fail
                ? throw new InvalidOperationException("stub inference failure")
                : new TurnEndPrediction(_complete, _probability);
    }

    private static int Main()
    {
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin, new UTF8Encoding(false));
        var raw = reader.ReadToEnd();

        var document = JsonNode.Parse(raw)!.AsObject();
        var payload = document["payload"] as JsonObject ?? document;
        var mode = Str(payload, "mode") ?? "timing";

        var result = mode switch
        {
            "timing" => RunTiming(payload),
            "window" => RunWindow(payload),
            _ => throw new ArgumentException($"未知 mode: {mode}"),
        };

        Console.Out.Write(result.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        Console.Out.Write('\n');
        return 0;
    }

    private static JsonObject RunTiming(JsonObject payload)
    {
        var config = payload["config"] as JsonObject ?? [];
        var classifierKind = Str(payload, "classifier") ?? "disabled";
        var threshold = OptDouble(config, "smart_turn_threshold") ?? 0.5;
        var probability = OptDouble(payload, "probability") ?? 0.0;

        var options = new TurnEndOptions
        {
            Enabled = classifierKind != "disabled",
            SpeculativeReopenMs = OptInt(config, "speculative_reopen_ms") ?? 800,
            MaxWaitMs = OptInt(config, "smart_turn_max_wait_ms") ?? 2000,
            IncompleteDelayMs = OptInt(config, "smart_turn_incomplete_delay_ms") ?? 600,
            Threshold = threshold,
        };

        ITurnEndClassifier? classifier = classifierKind switch
        {
            "disabled" => null,
            "failed" => new ScriptedClassifier(complete: false, probability, fail: true),
            _ => new ScriptedClassifier(probability > threshold, probability, fail: false),
        };

        // 判定器不看音频内容，喂一段静音即可：与上游探针喂的那段一致。
        var audio = new float[16_000];
        var decision = TurnEndTiming.Decide(classifier, audio, TurnAudioWindow.ModelSampleRate, options);

        return new JsonObject
        {
            ["grace_ms"] = decision.GraceMs,
            ["processing_delay_ms"] = decision.ProcessingDelayMs,
            ["status"] = decision.Status,
            ["analysis_seconds_is_null"] = decision.AnalysisSeconds is null,
        };
    }

    private static JsonObject RunWindow(JsonObject payload)
    {
        var samples = OptInt(payload, "samples") ?? 0;
        var pattern = Str(payload, "pattern") ?? "ramp";
        var sampleRate = OptInt(payload, "sample_rate") ?? TurnAudioWindow.ModelSampleRate;

        var audio = GenerateAudio(samples, pattern);
        var window = TurnAudioWindow.Prepare(audio, sampleRate);

        return new JsonObject
        {
            ["length"] = window.Length,
            ["leading_zeros"] = LeadingZeros(window),
            ["trailing_zeros"] = TrailingZeros(window),
            ["head_bits"] = BitArray(window, takeHead: true, 4),
            ["tail_bits"] = BitArray(window, takeHead: false, 4),
            ["bits_hash"] = BitsHash(window),
        };
    }

    /// <summary>
    /// 与 Python 侧同一套整数规则（<c>((i*37) % 1000) / 1000.0</c>）。
    /// 只用到整数运算和一次 double→float 转换，IEEE 转换是确定性的，两侧逐位相同。
    /// </summary>
    private static float[] GenerateAudio(int samples, string pattern)
    {
        var audio = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            audio[i] = pattern == "signed"
                ? (float)(((i * 37L) % 2001 - 1000) / 1000.0)
                : (float)(((i * 37L) % 1000) / 1000.0);
        }

        return audio;
    }

    private static int LeadingZeros(float[] window)
    {
        for (var i = 0; i < window.Length; i++)
        {
            if (window[i] != 0f)
            {
                return i;
            }
        }

        return window.Length;
    }

    private static int TrailingZeros(float[] window)
    {
        for (var i = window.Length - 1; i >= 0; i--)
        {
            if (window[i] != 0f)
            {
                return window.Length - 1 - i;
            }
        }

        return window.Length;
    }

    private static JsonArray BitArray(float[] window, bool takeHead, int count)
    {
        var array = new JsonArray();
        for (var i = 0; i < count && i < window.Length; i++)
        {
            var index = takeHead ? i : window.Length - count + i;
            array.Add((long)BitConverter.SingleToUInt32Bits(window[index]));
        }

        return array;
    }

    /// <summary>FNV-1a 64 over little-endian float32 bit patterns（与参照侧同算法）。</summary>
    private static string BitsHash(float[] window)
    {
        var hash = 0xCBF29CE484222325UL;
        foreach (var sample in window)
        {
            var bits = BitConverter.SingleToUInt32Bits(sample);
            for (var shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(bits >> shift);
                hash *= 0x100000001B3UL;
            }
        }

        return "0x" + hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static string? Str(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? OptInt(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : null;

    private static double? OptDouble(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<double>(out var parsed) ? parsed : null;
}
