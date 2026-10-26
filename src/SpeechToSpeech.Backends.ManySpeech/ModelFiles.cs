using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Backends.ManySpeech;

/// <summary>
/// 模型文件探测。
///
/// ManySpeech 侧没有统一的模型目录约定，各示例用自己的命名（<c>model.int8.onnx</c>、
/// <c>encoder.int8.onnx</c>、<c>am.mvn</c>、<c>tokens.txt</c>…）。这里按优先级探测，
/// 找不到就抛出带完整路径的明确错误——绝不让后端带着空路径启动然后在第一句语音上失败。
/// </summary>
internal static class ModelFiles
{
    public static string Require(string? directory, string backend, params string[] patterns)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new BackendUnavailableException(backend, $"未指定模型目录（--{backend}_model_dir）");
        }

        if (!Directory.Exists(directory))
        {
            throw new BackendUnavailableException(backend, $"模型目录不存在：{directory}");
        }

        foreach (var pattern in patterns)
        {
            var match = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        throw new BackendUnavailableException(
            backend,
            $"在 {directory} 下找不到匹配 [{string.Join(", ", patterns)}] 的模型文件");
    }

    public static string? Optional(string? directory, params string[] patterns)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        foreach (var pattern in patterns)
        {
            var match = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}
