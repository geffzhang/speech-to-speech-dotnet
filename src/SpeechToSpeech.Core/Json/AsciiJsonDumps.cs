using System.Text;

namespace SpeechToSpeech.Core.Json;

/// <summary>
/// 逐字节对齐 Python <c>json.dumps(obj, separators=(",", ":"))</c>（<c>ensure_ascii</c> 保持默认的 True）。
///
/// 只服务一类载荷：写进 <c>response.metadata</c> 的那几个字符串。它们不是普通 JSON 字段，
/// 而是**客户端会按值比对的字符串**——元数据里存的就是这段文本，差一个字节客户端就对不上，
/// 哪怕解出来的对象一模一样。
///
/// 为什么不用产品的线路编码器：线路编码要与 <c>send_json(ensure_ascii=False)</c> 一致，
/// 中文原样输出；而 <c>json.dumps</c> 默认把非 ASCII 转成 <c>\uXXXX</c>。两者口径相反，
/// 共用一套只会让其中一边错。
/// </summary>
public static class AsciiJsonDumps
{
    /// <summary><c>json.dumps(value, ensure_ascii=True)</c>：一个字符串字面量（含引号）。</summary>
    public static string String(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    // 0x7F（DEL）也要转义：Python 的界是「可打印 ASCII」而不是「非控制字符」。
                    // 非 BMP 字符在这里天然被拆成两个代理码元，各自转义正好是 Python 的
                    // \ud83d\ude00 形态，不需要额外判别。
                    if (character < 0x20 || character >= 0x7F)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>
    /// <c>json.dumps(list_of_str, separators=(",", ":"))</c>。
    ///
    /// 顺序由调用方给定，本方法<b>不排序</b>：排序口径（码点序还是区域相关序）是业务决定，
    /// 藏在渲染里会让「谁该先出现」变得不可见。
    /// </summary>
    public static string StringArray(IEnumerable<string> orderedValues)
        => "[" + string.Join(",", orderedValues.Select(String)) + "]";
}
