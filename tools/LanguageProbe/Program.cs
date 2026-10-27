using System.Text;
using System.Text.Json.Nodes;
using SpeechToSpeech.Backends.Lingua;
using SpeechToSpeech.Core.Json;
using SpeechToSpeech.Core.Text;

// 本实现的助手语言探针。与 tools/refprobe/lang_probe.py 输出同形，用于差分语料比对。
//
// 输入：{"texts": ["第一片", "第二片", ...]}（按模型输出顺序）
// 输出：{"detected": "en" | null, "windows": ["观察第 1 片后的窗口", ...]}
//
// 为什么比窗口而不只比结论：结论只有「判出来 / 没判出来」两种，
// 而「窗口怎么累积、什么时候停止累积」错了，结论可能碰巧一样，
// 到长回复或中途换语言时才暴露。窗口是那条规则的直接投影。
//
// 编码自己管：Console.In 在 Windows 按 OEM 代码页解码，中日韩文本会乱码。

var stdin = Console.OpenStandardInput();
string raw;
using (var reader = new StreamReader(stdin, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
{
    raw = reader.ReadToEnd();
}

if (string.IsNullOrWhiteSpace(raw))
{
    Console.Error.WriteLine("stdin 为空，期望 {\"texts\": [\"...\"]}");
    return 2;
}

string[] texts;
try
{
    var payload = JsonNode.Parse(raw)!;
    texts = [.. payload["texts"]!.AsArray().Select(node => node!.GetValue<string>())];
}
catch (Exception ex)
{
    Console.Error.WriteLine($"入参解析失败：{ex.Message}");
    return 2;
}

using var detector = new LinguaLanguageDetector();
var probe = new AssistantLanguageProbe(detector);

var windows = new JsonArray();
foreach (var text in texts)
{
    probe.Observe(text);
    windows.Add(JsonValue.Create(probe.Window));
}

var result = new JsonObject
{
    ["detected"] = probe.Detected is null ? null : JsonValue.Create(probe.Detected),
    ["windows"] = windows,
};

// 与产品共用一个编码器：探针输出的字节要和上游 json.dump(ensure_ascii=False) 对齐，
// 自己另配一份 options 就会在 emoji 上分叉（产品修好了、探针还是转义）。
var json = result.ToJsonString(WireJson.Indented) + "\n";
using var stdout = Console.OpenStandardOutput();
var bytes = new UTF8Encoding(false).GetBytes(json);
stdout.Write(bytes, 0, bytes.Length);
stdout.Flush();
return 0;
