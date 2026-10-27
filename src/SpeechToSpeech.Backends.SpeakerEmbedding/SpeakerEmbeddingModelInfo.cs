using System.Globalization;

namespace SpeechToSpeech.Backends.SpeakerEmbedding;

/// <summary>
/// 声纹模型的超参，加载时从 ONNX 的 <c>custom_metadata_map</c> 读出来——一个也不硬编码，
/// 换模型（3D-Speaker 系 / WeSpeaker 系）不必改代码。
///
/// <para>sherpa-onnx 也是这么读的（<c>speaker-embedding-extractor-model.cc</c>），
/// 键名与它保持一致，这样可以从同一个模型库直接取权重。</para>
/// </summary>
internal sealed record SpeakerEmbeddingModelInfo(
    int SampleRate,
    int OutputDim,
    bool NormalizeSamples,
    string FeatureNormalizeType,
    string Framework)
{
    /// <summary>sherpa 认的两个框架；其余的导出没有共同的输入约定（T 的形状、fbank 口径都不保证）。</summary>
    private static readonly string[] SupportedFrameworks = ["wespeaker", "3d-speaker"];

    public static SpeakerEmbeddingModelInfo FromMetadata(IReadOnlyDictionary<string, string> meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        if (!meta.TryGetValue("framework", out string? framework) || string.IsNullOrWhiteSpace(framework))
        {
            throw new InvalidOperationException(
                "ONNX 模型 metadata 缺少 'framework'——这不像 wespeaker / 3d-speaker 的声纹导出" +
                "（需要 framework / sample_rate / output_dim / normalize_samples）。");
        }
        framework = framework.Trim();
        if (!SupportedFrameworks.Contains(framework, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"不支持的声纹框架 '{framework}'（期望 wespeaker 或 3d-speaker）。" +
                "别的导出没有共同的 fbank 口径，硬套只会得到一个「看起来很像我」的向量。");
        }

        int Get(string key)
        {
            if (!meta.TryGetValue(key, out string? raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                throw new InvalidOperationException($"ONNX 模型 metadata 缺少整数键 '{key}'。");
            }
            return value;
        }

        // normalize_samples = 0 的模型期望 [-32768, 32768] 量级的输入（kaldi 的老习惯），
        // 这时前端要先把 [-1, 1] 乘上去；= 1 才是现在的主流。
        bool normalizeSamples = !meta.TryGetValue("normalize_samples", out string? normalizeRaw)
            || normalizeRaw.Trim() is "1" or "true" or "True";

        string featureNormalize = meta.TryGetValue("feature_normalize_type", out string? raw2)
            ? raw2.Trim()
            : string.Empty;

        // 空 = 不做归约；global-mean = 逐维减时间均值。sherpa 只认这两种，别的（per-utterance 变体
        // 之类）在这里明确拒绝，而不是当作「大概差不多」跳过去。
        if (featureNormalize is not ("" or "global-mean"))
        {
            throw new InvalidOperationException(
                $"不支持的 feature_normalize_type '{featureNormalize}'（期望空或 global-mean）。");
        }

        return new SpeakerEmbeddingModelInfo(
            SampleRate: Get("sample_rate"),
            OutputDim: Get("output_dim"),
            NormalizeSamples: normalizeSamples,
            FeatureNormalizeType: featureNormalize,
            Framework: framework);
    }
}
