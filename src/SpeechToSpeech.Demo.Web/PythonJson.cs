using System.Globalization;
using System.Text;

namespace SpeechToSpeech.Demo.Web;

/// <summary>
/// Python <c>json</c> 模块语义的解析/序列化层，供 demo 服务端与 FastAPI 对拍。
///
/// 为什么不能直接用 System.Text.Json：
/// <list type="bullet">
///   <item>Python <c>json.loads</c> 接受 NaN/Infinity/-Infinity，STJ 默认不接受；</item>
///   <item>Python 区分 int/float（<c>1</c> 与 <c>1.0</c>），回显进 422 的 <c>input</c> 时字节不同；</item>
///   <item>大整数任意精度（<c>99999999999999999999</c>），STJ 会截断或改写；</item>
///   <item>浮点 repr 是 CPython <c>float.__repr__</c>（<c>1.0</c>→<c>1.0</c>、<c>1e16</c>→<c>1e+16</c>）；</item>
///   <item>JSON 解码错误的 msg/pos 必须与 <c>json.JSONDecodeError</c> 一致（进 <c>ctx.error</c>/<c>loc</c>）。</item>
/// </list>
/// </summary>
public static class PythonJson
{
    /// <summary>解析失败：msg 对应 <c>JSONDecodeError.msg</c>，pos 对应 <c>.pos</c>。</summary>
    public sealed class JsonError(string msg, int pos) : Exception(msg)
    {
        public string Msg { get; } = msg;
        public int Pos { get; } = pos;
    }

    /// <summary>Python 值的判别联合。</summary>
    public abstract record PyValue
    {
        /// <summary>按 Python <c>json.dumps</c> 规则写出（非 ASCII 不转义）。</summary>
        public abstract void Write(StringBuilder sb, bool allowNan);
    }

    public sealed record PyNull : PyValue
    {
        public static readonly PyNull Instance = new();
        public override void Write(StringBuilder sb, bool allowNan) => sb.Append("null");
    }

    public sealed record PyBool(bool Value) : PyValue
    {
        public override void Write(StringBuilder sb, bool allowNan) => sb.Append(Value ? "true" : "false");
    }

    /// <summary>任意精度整数：保留解析到的原始数字串（已规整 <c>-0</c>→<c>0</c>），序列化原样输出。</summary>
    public sealed record PyInt(string Digits) : PyValue
    {
        public override void Write(StringBuilder sb, bool allowNan) => sb.Append(Digits);
    }

    public sealed record PyFloat(double Value) : PyValue
    {
        public override void Write(StringBuilder sb, bool allowNan)
        {
            if (double.IsNaN(Value) || double.IsInfinity(Value))
            {
                if (!allowNan)
                {
                    throw new ArgumentException("Out of range float values are not JSON compliant");
                }

                sb.Append(double.IsNaN(Value) ? "NaN" : double.IsPositiveInfinity(Value) ? "Infinity" : "-Infinity");
                return;
            }

            sb.Append(PythonFloatRepr(Value));
        }
    }

    public sealed record PyStr(string Value) : PyValue
    {
        public override void Write(StringBuilder sb, bool allowNan)
        {
            sb.Append('"');
            foreach (var ch in Value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20)
                        {
                            sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(ch);
                        }

                        break;
                }
            }

            sb.Append('"');
        }
    }

    public sealed record PyList : PyValue, System.Collections.IEnumerable
    {
        public List<PyValue> Items { get; }

        public PyList() => Items = new();

        public PyList(List<PyValue> items) => Items = items;

        public void Add(PyValue item) => Items.Add(item);

        public System.Collections.IEnumerator GetEnumerator() => Items.GetEnumerator();

        public override void Write(StringBuilder sb, bool allowNan)
        {
            sb.Append('[');
            for (var i = 0; i < Items.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                Items[i].Write(sb, allowNan);
            }

            sb.Append(']');
        }
    }

    /// <summary>对象：按插入序输出；重复 key 后者胜（与 Python dict 一致）。</summary>
    public sealed record PyDict : PyValue, System.Collections.IEnumerable
    {
        public List<KeyValuePair<string, PyValue>> Items { get; }

        public PyDict() => Items = new();

        public PyDict(List<KeyValuePair<string, PyValue>> items) => Items = items;

        public void Add(KeyValuePair<string, PyValue> item) => Items.Add(item);

        public System.Collections.IEnumerator GetEnumerator() => Items.GetEnumerator();

        public override void Write(StringBuilder sb, bool allowNan)
        {
            sb.Append('{');
            for (var i = 0; i < Items.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                new PyStr(Items[i].Key).Write(sb, allowNan);
                sb.Append(':');
                Items[i].Value.Write(sb, allowNan);
            }

            sb.Append('}');
        }
    }

    // ── 序列化 ────────────────────────────────────────────────────────────────

    /// <summary>等价 <c>json.dumps(value, separators=(",", ":"), ensure_ascii=False, allow_nan=...)</c>。</summary>
    public static string Serialize(PyValue value, bool allowNan = false)
    {
        var sb = new StringBuilder();
        value.Write(sb, allowNan);
        return sb.ToString();
    }

    /// <summary>
    /// CPython <c>float.__repr__</c> / <c>json.dumps</c> 的浮点格式。
    ///
    /// 取最短往返数字串，规整为 (digits, decpt)——数值 = int(digits) × 10^(decpt-len(digits))；
    /// 小数点指数 decpt 落在 [-4, 16] 用定点（整数值补 <c>.0</c>），否则科学计数 <c>e±XX</c>（指数至少两位）。
    /// </summary>
    public static string PythonFloatRepr(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        if (value == 0.0)
        {
            // Python 区分 0.0 与 -0.0
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        var negative = value < 0;
        var raw = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var (digits, decpt) = ParseRoundTrip(raw);
        return FormatPython(digits, decpt, negative);
    }

    /// <summary>把 .NET 往返数字串解析为 (有效数字, 小数点位置)。</summary>
    private static (string Digits, int Decpt) ParseRoundTrip(string raw)
    {
        // raw 形如 1.5 / 1E+16 / 0.0001 / 123 / 1E-05（不变文化，E 可能大写）
        var s = raw;
        var expNet = 0;
        var eIndex = s.IndexOfAny('E', 'e');
        if (eIndex >= 0)
        {
            expNet = int.Parse(s[(eIndex + 1)..], CultureInfo.InvariantCulture);
            s = s[..eIndex];
        }

        string intPart;
        string fracPart;
        var dot = s.IndexOf('.');
        if (dot >= 0)
        {
            intPart = s[..dot];
            fracPart = s[(dot + 1)..];
        }
        else
        {
            intPart = s;
            fracPart = "";
        }

        var rawDigits = intPart + fracPart;
        var scale = expNet - fracPart.Length; // 值 = int(rawDigits) × 10^scale

        // 去前导零不改 int 值 → scale 不动；去尾零要补 scale（int("100")→int("1") 少乘 10^2）。
        var digits = rawDigits.TrimStart('0');
        if (digits.Length == 0)
        {
            return ("0", 1);
        }

        var trailing = digits.Length - digits.TrimEnd('0').Length;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }

        scale += trailing;

        // 值 = int(digits) × 10^scale，且值 = int(digits) × 10^(decpt-len(digits))
        var decpt = scale + digits.Length;
        return (digits, decpt);
    }

    private static string FormatPython(string digits, int decpt, bool negative)
    {
        var sign = negative ? "-" : "";
        if (decpt <= -4 || decpt > 16)
        {
            var exp = decpt - 1;
            var expStr = Math.Abs(exp).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
            var sb = new StringBuilder(sign);
            sb.Append(digits[0]);
            if (digits.Length > 1)
            {
                sb.Append('.').Append(digits.AsSpan(1));
            }

            sb.Append('e').Append(exp < 0 ? '-' : '+').Append(expStr);
            return sb.ToString();
        }

        if (decpt <= 0)
        {
            return sign + "0." + new string('0', -decpt) + digits;
        }

        if (decpt >= digits.Length)
        {
            return sign + digits + new string('0', decpt - digits.Length) + ".0";
        }

        return sign + digits[..decpt] + "." + digits[decpt..];
    }

    // ── 解析 ──────────────────────────────────────────────────────────────────

    /// <summary>等价 <c>json.loads(text)</c>。错误抛 <see cref="JsonError"/>，msg/pos 与 CPython 一致。</summary>
    public static PyValue Parse(string text)
    {
        var p = new Parser(text);
        var v = p.ParseValue();
        p.SkipWhitespace();
        if (!p.AtEnd)
        {
            throw new JsonError("Extra data", p.Index);
        }

        return v;
    }

    private sealed class Parser(string text)
    {
        private readonly string _t = text;
        public int Index;

        public bool AtEnd => Index >= _t.Length;

        public void SkipWhitespace()
        {
            while (Index < _t.Length && _t[Index] is ' ' or '\t' or '\n' or '\r')
            {
                Index++;
            }
        }

        private char Peek() => _t[Index];

        public PyValue ParseValue()
        {
            SkipWhitespace();
            if (AtEnd)
            {
                throw new JsonError("Expecting value", Index);
            }

            return Peek() switch
            {
                '{' => ParseObject(),
                '[' => ParseArray(),
                '"' => new PyStr(ParseString()),
                't' => ParseKeyword("true", new PyBool(true)),
                'f' => ParseKeyword("false", new PyBool(false)),
                'n' => ParseKeyword("null", PyNull.Instance),
                'N' => ParseKeyword("NaN", new PyFloat(double.NaN)),
                'I' => ParseKeyword("Infinity", new PyFloat(double.PositiveInfinity)),
                '-' => ParseNumberOrNegInfinity(),
                >= '0' and <= '9' => ParseNumber(),
                _ => throw new JsonError("Expecting value", Index),
            };
        }

        private PyValue ParseKeyword(string word, PyValue value)
        {
            if (Index + word.Length > _t.Length || !string.CompareOrdinal(_t, Index, word, 0, word.Length).Equals(0))
            {
                throw new JsonError("Expecting value", Index);
            }

            Index += word.Length;
            return value;
        }

        private PyValue ParseNumberOrNegInfinity()
        {
            if (Index + 9 <= _t.Length
                && string.CompareOrdinal(_t, Index, "-Infinity", 0, 9).Equals(0))
            {
                Index += 9;
                return new PyFloat(double.NegativeInfinity);
            }

            return ParseNumber();
        }

        private PyValue ParseNumber()
        {
            var start = Index;
            if (!AtEnd && Peek() == '-')
            {
                Index++;
            }

            if (AtEnd || Peek() < '0' || Peek() > '9')
            {
                throw new JsonError("Expecting value", start);
            }

            if (Peek() == '0')
            {
                Index++;
                if (!AtEnd && Peek() >= '0' && Peek() <= '9')
                {
                    // 前导零：Python 只收下 0，多余数字留给上层报 delimiter 错
                    return MakeNumber(_t[start..Index], isFloat: false);
                }
            }
            else
            {
                while (!AtEnd && Peek() >= '0' && Peek() <= '9')
                {
                    Index++;
                }
            }

            var isFloat = false;
            if (!AtEnd && Peek() == '.')
            {
                isFloat = true;
                Index++;
                if (AtEnd || Peek() < '0' || Peek() > '9')
                {
                    throw new JsonError("Expecting value", Index);
                }

                while (!AtEnd && Peek() >= '0' && Peek() <= '9')
                {
                    Index++;
                }
            }

            if (!AtEnd && (Peek() == 'e' || Peek() == 'E'))
            {
                isFloat = true;
                Index++;
                if (!AtEnd && (Peek() == '+' || Peek() == '-'))
                {
                    Index++;
                }

                if (AtEnd || Peek() < '0' || Peek() > '9')
                {
                    throw new JsonError("Expecting value", Index);
                }

                while (!AtEnd && Peek() >= '0' && Peek() <= '9')
                {
                    Index++;
                }
            }

            return MakeNumber(_t[start..Index], isFloat);
        }

        private static PyValue MakeNumber(string text, bool isFloat)
        {
            if (isFloat)
            {
                return new PyFloat(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
            }

            // Python 的 int 没有 -0
            return new PyInt(text == "-0" ? "0" : text);
        }

        private PyValue ParseArray()
        {
            Index++; // '['
            var items = new List<PyValue>();
            SkipWhitespace();
            if (!AtEnd && Peek() == ']')
            {
                Index++;
                return new PyList(items);
            }

            while (true)
            {
                items.Add(ParseValue());
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new JsonError("Expecting ',' delimiter", Index);
                }

                if (Peek() == ',')
                {
                    var commaPos = Index;
                    Index++;
                    SkipWhitespace();
                    if (!AtEnd && Peek() == ']')
                    {
                        throw new JsonError("Illegal trailing comma before end of array", commaPos);
                    }

                    continue;
                }

                if (Peek() == ']')
                {
                    Index++;
                    return new PyList(items);
                }

                throw new JsonError("Expecting ',' delimiter", Index);
            }
        }

        private PyValue ParseObject()
        {
            Index++; // '{'
            var items = new List<KeyValuePair<string, PyValue>>();
            SkipWhitespace();
            if (!AtEnd && Peek() == '}')
            {
                Index++;
                return new PyDict(items);
            }

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            while (true)
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new JsonError("Expecting property name enclosed in double quotes", Index);
                }

                if (Peek() != '"')
                {
                    throw new JsonError("Expecting property name enclosed in double quotes", Index);
                }

                var key = ParseString();
                SkipWhitespace();
                if (AtEnd || Peek() != ':')
                {
                    throw new JsonError("Expecting ':' delimiter", Index);
                }

                Index++;
                var val = ParseValue();
                if (seen.TryGetValue(key, out var idx))
                {
                    items[idx] = new KeyValuePair<string, PyValue>(key, val);
                }
                else
                {
                    seen[key] = items.Count;
                    items.Add(new KeyValuePair<string, PyValue>(key, val));
                }

                SkipWhitespace();
                if (AtEnd)
                {
                    throw new JsonError("Expecting ',' delimiter", Index);
                }

                if (Peek() == ',')
                {
                    var commaPos = Index;
                    Index++;
                    SkipWhitespace();
                    if (!AtEnd && Peek() == '}')
                    {
                        throw new JsonError("Illegal trailing comma before end of object", commaPos);
                    }

                    continue;
                }

                if (Peek() == '}')
                {
                    Index++;
                    return new PyDict(items);
                }

                throw new JsonError("Expecting ',' delimiter", Index);
            }
        }

        private string ParseString()
        {
            var start = Index;
            Index++; // '"'
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new JsonError("Unterminated string starting at", start);
                }

                var c = Peek();
                if (c == '"')
                {
                    Index++;
                    return sb.ToString();
                }

                if (c == '\\')
                {
                    var escapePos = Index;
                    Index++;
                    if (AtEnd)
                    {
                        throw new JsonError("Invalid \\escape", escapePos);
                    }

                    var e = Peek();
                    Index++;
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Index + 4 > _t.Length)
                            {
                                // pos 指向 'u'（CPython 同款：backslash+1）
                                throw new JsonError("Invalid \\uXXXX escape", escapePos + 1);
                            }

                            for (var k = 0; k < 4; k++)
                            {
                                if (!Uri.IsHexDigit(_t[Index + k]))
                                {
                                    throw new JsonError("Invalid \\uXXXX escape", escapePos + 1);
                                }
                            }

                            sb.Append((char)ushort.Parse(_t.AsSpan(Index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Index += 4;
                            break;
                        default:
                            throw new JsonError("Invalid \\escape", escapePos);
                    }

                    continue;
                }

                if (c < 0x20)
                {
                    throw new JsonError("Invalid control character at", Index);
                }

                sb.Append(c);
                Index++;
            }
        }
    }
}
