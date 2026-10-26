using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Text;

// 本实现的文本层探针。与 tools/refprobe/text_probe.py 输出同形，用于差分语料比对。
//
// 读数约定：stdin 收 {"text": "..."}，stdout 回 {"unspeakable", "markdown", "sentences"}。
// 单独做成一个工程而不是塞进主 CLI，是为了让这个"测试脚手架"不混进产品入口。
//
// 编码必须自己管：Windows 上 Console.In 按 OEM 代码页（中文机器是 GBK）解码，
// 直接 ReadToEnd 会把 UTF-8 的中日韩文本读成乱码，随后 JSON 解析就报
// "Expected end of string"。所以 stdin/stdout 都绕开 Console，走原始流 + UTF-8。

var stdin = Console.OpenStandardInput();
string raw;
using (var reader = new StreamReader(stdin, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
{
    raw = reader.ReadToEnd();
}

if (string.IsNullOrWhiteSpace(raw))
{
    Console.Error.WriteLine("stdin 为空，期望 {\"text\": \"...\"}");
    return 2;
}

string text;
bool split;
try
{
    var payload = JsonNode.Parse(raw)!;
    text = payload["text"]!.GetValue<string>();
    // split=false 时只出两个纯函数视图。CJK 文本用它——切分本身是已登记的差异（C005），
    // 那部分由单元测试表征，这里只锁 remove_unspeakable / remove_markdown 的逐字节等价。
    split = payload["split"] is null || payload["split"]!.GetValue<bool>();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"入参解析失败：{ex.Message}");
    return 2;
}

var result = new JsonObject
{
    ["unspeakable"] = TextNormalizer.RemoveUnspeakable(text),
    ["markdown"] = TextNormalizer.RemoveMarkdown(text),
};

if (split)
{
    var sentences = new JsonArray();
    foreach (var sentence in TextNormalizer.SentenceTokenizePreservingMarkdownCode(text))
    {
        sentences.Add(JsonValue.Create(sentence));
    }

    result["sentences"] = sentences;
}

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    // 保留中文字面量，让基线文件能直接读；默认编码器会把非 ASCII 转 \uXXXX
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

var json = result.ToJsonString(options) + "\n";
using var stdout = Console.OpenStandardOutput();
var bytes = new UTF8Encoding(false).GetBytes(json);
stdout.Write(bytes, 0, bytes.Length);
stdout.Flush();
return 0;
