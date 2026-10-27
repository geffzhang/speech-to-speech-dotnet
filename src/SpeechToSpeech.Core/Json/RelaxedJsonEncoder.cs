using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace SpeechToSpeech.Core.Json;

/// <summary>
/// 与上游 <c>json.dumps(ensure_ascii=False, separators=(",", ":"))</c> 逐字节对齐的 JSON 编码器。
///
/// 为什么不能直接用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>：
/// 它会把 BMP 之外的字符（emoji、部分生僻字）转成代理对转义 <c>\uD83D\uDE00</c>，
/// 而上游原样输出。中文没暴露这个问题（BMP 内正常），所以只有含 emoji 的回复会出现字节差异，
/// 且每个 emoji 从 4 字节膨胀到 12 字节——音频与转写都在热路径上，这不是纯美观问题。
///
/// 转义规则照抄 Python 的 <c>json.dumps</c>：
/// 只转 <c>"</c> 与 <c>\</c>，外加 0x00–0x1F 控制字符（其中 <c>\b \f \n \r \t</c> 用短转义，
/// 其余用 <c>\u00xx</c>）。孤立的代理码元必须转义——原始输出会产出非法 UTF-8。
///
/// 放在 Core 而不是协议层：探针也要用它写出与产品同形的 JSON，两处各写一份必然漂。
/// </summary>
public sealed class RelaxedJsonEncoder : JavaScriptEncoder
{
    public static readonly RelaxedJsonEncoder Instance = new();

    /// <inheritdoc />
    public override int MaxOutputCharactersPerInputCharacter => 12;

    /// <inheritdoc />
    public override bool WillEncode(int unicodeScalar)
        => unicodeScalar is '"' or '\\' || unicodeScalar < 0x20 || IsLoneSurrogate(unicodeScalar);

    /// <inheritdoc />
    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (var index = 0; index < textLength; index++)
        {
            var ch = text[index];
            if (ch is '"' or '\\' || ch < 0x20)
            {
                return index;
            }

            if (char.IsHighSurrogate(ch))
            {
                // 合法的一对直接放行；孤立的高代理必须转义。
                if (index + 1 < textLength && char.IsLowSurrogate(text[index + 1]))
                {
                    index++;
                    continue;
                }

                return index;
            }

            if (char.IsLowSurrogate(ch))
            {
                return index;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public override unsafe bool TryEncodeUnicodeScalar(
        int unicodeScalar,
        char* buffer,
        int bufferLength,
        out int numberOfCharactersWritten)
    {
        if (!WillEncode(unicodeScalar))
        {
            // 原样写出，BMP 外按代理对拆两个 char。
            var raw = char.ConvertFromUtf32(unicodeScalar);
            if (bufferLength < raw.Length)
            {
                numberOfCharactersWritten = 0;
                return false;
            }

            for (var index = 0; index < raw.Length; index++)
            {
                buffer[index] = raw[index];
            }

            numberOfCharactersWritten = raw.Length;
            return true;
        }

        var escaped = Escape(unicodeScalar);
        if (bufferLength < escaped.Length)
        {
            numberOfCharactersWritten = 0;
            return false;
        }

        for (var index = 0; index < escaped.Length; index++)
        {
            buffer[index] = escaped[index];
        }

        numberOfCharactersWritten = escaped.Length;
        return true;
    }

    private static bool IsLoneSurrogate(int unicodeScalar)
        => unicodeScalar is >= 0xD800 and <= 0xDFFF;

    private static string Escape(int unicodeScalar) => unicodeScalar switch
    {
        '"' => "\\\"",
        '\\' => "\\\\",
        '\b' => "\\b",
        '\f' => "\\f",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        // 十六进制用小写：Python 的 json.dumps 输出 \u001f 而不是 \u001F，
        // 逐字节比对时大小写也算差异。
        _ => "\\u" + unicodeScalar.ToString("x4"),
    };
}

/// <summary>
/// 线路 JSON 的两套固定选项。
///
/// 只有「是否缩进」不同，编码器必须共用——编码差异是逐字节比对里最容易漏的一类，
/// 让产品与探针各持一份 options，迟早会有一处退回默认编码器。
/// </summary>
public static class WireJson
{
    /// <summary>线上传输：紧凑、无缩进（上游 <c>separators=(",", ":")</c>）。</summary>
    public static JsonSerializerOptions Compact { get; } = new()
    {
        WriteIndented = false,
        Encoder = RelaxedJsonEncoder.Instance,
    };

    /// <summary>语料基线文件：缩进 2 空格（上游探针 <c>json.dump(indent=2)</c>）。</summary>
    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true,
        Encoder = RelaxedJsonEncoder.Instance,
    };
}
