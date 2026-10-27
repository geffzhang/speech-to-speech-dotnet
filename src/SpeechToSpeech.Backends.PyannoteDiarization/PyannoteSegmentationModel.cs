using System.Globalization;

namespace SpeechToSpeech.Backends.PyannoteDiarization;

/// <summary>
/// pyannote segmentation-3.0 的超参，加载时从 ONNX 的 <c>custom_metadata_map</c> 读出来
/// ——一个也不硬编码，重新导出的模型自带它的几何。
///
/// 流式分离要用的只有窗口/感受野几何与 powerset 布局：窗口长度对帧步长、帧中心偏移、
/// 每帧的本地说话人通道数，全在这里。
/// </summary>
internal sealed record PyannoteSegmentationModel(
    int SampleRate,
    int WindowSize,
    int WindowShift,
    int ReceptiveFieldSize,
    int ReceptiveFieldShift,
    int NumSpeakers,
    int PowersetMaxClasses,
    int NumClasses)
{
    /// <summary>从 ONNX 会话的 custom metadata 读几何；不像 pyannote 导出时明确报错。</summary>
    public static PyannoteSegmentationModel FromMetadata(IReadOnlyDictionary<string, string> meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        int Get(string key)
        {
            if (!meta.TryGetValue(key, out var raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidOperationException(
                    $"ONNX 模型 metadata 缺少整数键 '{key}'——这不像 pyannote segmentation-3.0 的导出" +
                    "（需要 window_size / sample_rate / receptive_field_* / num_speakers / powerset_max_classes / num_classes）。");
            }
            return value;
        }

        int windowSize = Get("window_size");
        return new PyannoteSegmentationModel(
            SampleRate: Get("sample_rate"),
            WindowSize: windowSize,
            WindowShift: (int)(0.1 * windowSize), // 参照实现：跳长取 10% 窗口
            ReceptiveFieldSize: Get("receptive_field_size"),
            ReceptiveFieldShift: Get("receptive_field_shift"),
            NumSpeakers: Get("num_speakers"),
            PowersetMaxClasses: Get("powerset_max_classes"),
            NumClasses: Get("num_classes"));
    }
}
