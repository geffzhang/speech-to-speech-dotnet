using System.Text.Json.Serialization;

namespace SpeechToSpeech.Backends.OpenLipSync.Inference.Audio;

/// <summary>mel 前端几何参数（来自模型 config 的 audio 段）。</summary>
public sealed class AudioProcessingConfig
{
    public int SampleRate { get; init; }
    public int HopLengthSamples { get; init; }
    public int WindowLengthSamples { get; init; }
    public int NFft { get; init; }
    public int NMels { get; init; }
    public float FMin { get; init; }
    public float FMax { get; init; }
    public float Fps { get; init; }

    public static AudioProcessingConfig FromModelConfig(ModelConfig modelConfig)
    {
        var audio = modelConfig.Audio ?? throw new ArgumentException("Model config missing audio section");

        // 优先取模型训练时的逐样本几何；从整毫秒推导有损（有 off-by-one 风险）。
        int hop = audio.HopLengthSamples > 0
            ? audio.HopLengthSamples
            : audio.HopLengthMs > 0
                ? audio.SampleRate * audio.HopLengthMs / 1000
                : audio.SampleRate / 100;

        int win = audio.WindowLengthSamples > 0
            ? audio.WindowLengthSamples
            : audio.WindowLengthMs > 0
                ? audio.SampleRate * audio.WindowLengthMs / 1000
                : (int)(audio.SampleRate * 0.025f);

        return new AudioProcessingConfig
        {
            SampleRate = audio.SampleRate,
            HopLengthSamples = hop,
            WindowLengthSamples = win,
            NFft = audio.NFft,
            NMels = audio.NMels,
            FMin = audio.Fmin,
            FMax = audio.Fmax,
            Fps = audio.Fps > 0f ? audio.Fps : (float)audio.SampleRate / hop,
        };
    }
}

/// <summary>模型 config 的根（config.json）。</summary>
public sealed class ModelConfig
{
    [JsonPropertyName("model")]
    public ModelInfo? Model { get; init; }

    [JsonPropertyName("audio")]
    public AudioInfo? Audio { get; init; }

    [JsonPropertyName("training")]
    public TrainingInfo? Training { get; init; }
}

public sealed class ModelInfo
{
    [JsonPropertyName("num_visemes")]
    public int NumVisemes { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("viseme_names")]
    public string[] VisemeNames { get; init; } = [];

    /// <summary>true = 每个 viseme 独立 sigmoid 权重（多标签，共发音）；false = softmax 竞争。</summary>
    [JsonPropertyName("multi_label")]
    public bool? MultiLabel { get; init; }

    /// <summary>模型可前瞻的帧数（标签位移实现，2 帧 = 20 ms 延迟）。</summary>
    [JsonPropertyName("lookahead_frames")]
    public int LookaheadFrames { get; init; }

    /// <summary>"baked_into_graph" 表示 ONNX 图内部做 CMVN，C# 必须喂原始 dB mel。</summary>
    [JsonPropertyName("normalization")]
    public string Normalization { get; init; } = "";
}

public sealed class AudioInfo
{
    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; init; }

    [JsonPropertyName("n_mels")]
    public int NMels { get; init; }

    [JsonPropertyName("fmin")]
    public float Fmin { get; init; }

    [JsonPropertyName("fmax")]
    public float Fmax { get; init; }

    [JsonPropertyName("n_fft")]
    public int NFft { get; init; }

    [JsonPropertyName("fps")]
    public float Fps { get; init; }

    // 逐样本几何——优先于 *_ms 字段。
    [JsonPropertyName("window_length_samples")]
    public int WindowLengthSamples { get; init; }

    [JsonPropertyName("hop_length_samples")]
    public int HopLengthSamples { get; init; }

    [JsonPropertyName("window_length_ms")]
    public int WindowLengthMs { get; init; }

    [JsonPropertyName("hop_length_ms")]
    public int HopLengthMs { get; init; }

    [JsonPropertyName("normalization")]
    public string Normalization { get; init; } = "";
}

public sealed class TrainingInfo
{
    [JsonPropertyName("multi_label")]
    public bool MultiLabel { get; init; }
}
