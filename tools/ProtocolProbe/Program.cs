using System.Text;
using System.Text.Json.Nodes;
using SpeechToSpeech.Realtime.Protocol;

// 协议形状层探针：喂一个事件构造请求，输出本实现产出的事件 JSON。
//
// 与 tools/refprobe/protocol_probe.py 同形，两侧输出用同一条归一化规则
// （event_id 抹成 "<event_id>"），这样 diff_corpus 能直接对比。
//
// 未实现的事件一律以退出码 3 报出，不伪造输出——缺口必须是可见的。

Console.OutputEncoding = new UTF8Encoding(false);

string raw;
using (var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
{
    raw = await reader.ReadToEndAsync().ConfigureAwait(false);
}

if (string.IsNullOrWhiteSpace(raw))
{
    Console.Error.WriteLine("输入为空");
    return 2;
}

JsonNode? request;
try
{
    request = JsonNode.Parse(raw);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"入参不是合法 JSON：{ex.Message}");
    return 2;
}

var wire = request?["event"]?.GetValue<string>();
if (string.IsNullOrEmpty(wire))
{
    Console.Error.WriteLine("入参缺少 'event'");
    return 2;
}

var fields = request?["args"] as JsonObject ?? new JsonObject();
var missing = new List<string>();

string Text(string name) => Track(name, () => Str(fields, name));
int Number(string name) => Track(name, () => Int(fields, name));

string ErrorText(string name) => Track($"error.{name}", () => Str(ErrorObject(fields), name));

string? MaybeText(string name) => OptStr(fields, name);
string? MaybeErrorText(string name) => fields["error"] is JsonObject e ? OptStr(e, name) : null;

// response.done 的入参与上游模型同形：整个 response 对象，不是散字段。
// 平铺写法（response_id / status 直接放 args 顶层）留作兼容。
JsonObject? ResponseObject() => fields["response"] as JsonObject;

string RespText(string name)
{
    if (ResponseObject() is { } r)
    {
        return Track($"response.{name}", () => Str(r, name));
    }

    // 平铺兼容写法：response_id / status
    var flat = name.EndsWith("_id", StringComparison.Ordinal) ? $"response_{name}" : name;
    return Track(flat, () => Str(fields, flat));
}

// status_details 可能挂在 response 里（上游同形），也可能平铺
JsonObject? StatusDetails() =>
    (ResponseObject()?["status_details"] as JsonObject) ?? (fields["status_details"] as JsonObject);

string? DetailText(string name) => StatusDetails() is { } d ? OptStr(d, name) : null;
string? DetailErrorText(string name) => StatusDetails()?["error"] is JsonObject e ? OptStr(e, name) : null;

// transcription.completed 的 usage 只有 duration 一形态（上游不上报 token 口径）
double UsageSeconds() => fields["usage"]?["seconds"]?.GetValue<double>() ?? 0;

// role 同样有两种给法：平铺或放在 item 里
string RoleValue() => fields["role"] is { } node
    ? node.GetValue<string>()
    : Track("item.role", () => Str(fields["item"] as JsonObject ?? throw new InvalidOperationException("item"), "role"));

// 下游事件的 item_id 有两种给法：平铺的 item_id，或完整 item 对象里的 id。
// 语料统一用后者（与上游模型参数同形），平铺法留作兼容。
string ItemId() => fields["item_id"] is { } node
    ? node.GetValue<string>()
    : Track("item.id", () => Str(fields["item"] as JsonObject ?? throw new InvalidOperationException("item"), "id"));

// 模态由内容片 / 内容数组的类型推出，而不是另传一个布尔——这样语料与上游同形，
// 少一个可能与 part.type 矛盾的入参。
bool PartIsAudio() => fields["part"]?["type"]?.GetValue<string>() == "audio";
bool ItemIsAudio() => fields["item"]?["content"] is JsonArray { Count: > 0 } content
    && content[0]?["type"]?.GetValue<string>() == "output_audio";

// 音频模态下文本在 transcript，纯文本模态下在 text
string PartText() => Track("part.transcript", () =>
{
    var part = fields["part"] as JsonObject ?? throw new InvalidOperationException("part");
    return part["transcript"]?.GetValue<string>() ?? part["text"]?.GetValue<string>() ?? "";
});

string ItemText() => Track("item.content[0]", () =>
{
    var content = fields["item"]?["content"] as JsonArray ?? throw new InvalidOperationException("content");
    var part = content[0] as JsonObject ?? throw new InvalidOperationException("content[0]");
    return part["transcript"]?.GetValue<string>() ?? part["text"]?.GetValue<string>() ?? "";
});

// content 可能平铺，也可能在 item 里（后者与上游模型参数同形）
JsonArray? ContentArray()
{
    if (fields["content"] is JsonArray flat)
    {
        return (JsonArray)flat.DeepClone();
    }

    return fields["item"]?["content"] is JsonArray nested ? (JsonArray)nested.DeepClone() : null;
}

JsonObject? payload = wire switch
{
    EventNames.SpeechStarted => ServerEvent.SpeechStarted(Text("item_id"), Number("audio_start_ms")),
    EventNames.SpeechStopped => ServerEvent.SpeechStopped(Text("item_id"), Number("audio_end_ms")),

    EventNames.InputAudioBufferCommitted => ServerEvent.InputAudioBufferCommitted(
        Text("item_id"), MaybeText("previous_item_id")),

    EventNames.TranscriptionDelta => ServerEvent.TranscriptionDelta(
        Text("item_id"), Text("delta"), MaybeInt(fields, "content_index")),

    EventNames.TranscriptionCompleted => ServerEvent.TranscriptionCompleted(
        Text("item_id"), Text("transcript"), MaybeInt(fields, "content_index"), UsageSeconds()),

    EventNames.TranscriptionFailed => ServerEvent.TranscriptionFailed(
        Text("item_id"), ErrorText("message"), MaybeInt(fields, "content_index") ?? 0),

    EventNames.TranscriptionSnapshot => ServerEvent.TranscriptionSnapshot(
        Text("item_id"), Text("snapshot")),

    EventNames.ConversationItemCreated => ServerEvent.ConversationItemCreated(
        ItemId(),
        RoleValue(),
        ContentArray(),
        MaybeText("previous_item_id")),

    EventNames.ConversationItemDeleted => ServerEvent.ConversationItemDeleted(Text("item_id")),

    EventNames.ResponseCreated => ServerEvent.ResponseCreated(Text("response_id")),

    EventNames.OutputItemAdded => ServerEvent.OutputItemAdded(
        Text("response_id"), ItemId(), Number("output_index")),

    EventNames.OutputItemDone => ServerEvent.OutputItemDone(
        Text("response_id"),
        ItemId(),
        ItemText(),
        Number("output_index"),
        ItemIsAudio(),
        fields["item"]?["status"]?.GetValue<string>() != "incomplete"),

    EventNames.ContentPartAdded => ServerEvent.ContentPartAdded(
        Text("response_id"), Text("item_id"), Number("output_index"), Number("content_index"), PartIsAudio()),

    EventNames.ContentPartDone => ServerEvent.ContentPartDone(
        Text("response_id"), Text("item_id"), PartText(),
        Number("output_index"), Number("content_index"), PartIsAudio()),

    EventNames.OutputTextDelta => ServerEvent.OutputTextDelta(
        Text("response_id"), Text("item_id"), Text("delta"),
        Number("output_index"), Number("content_index")),

    EventNames.OutputTextDone => ServerEvent.OutputTextDone(
        Text("response_id"), Text("item_id"), Text("text"),
        Number("output_index"), Number("content_index")),

    EventNames.OutputAudioDelta => ServerEvent.OutputAudioDelta(
        Text("response_id"), Text("item_id"), Text("delta")),

    EventNames.OutputAudioDone => ServerEvent.OutputAudioDone(
        Text("response_id"), Text("item_id")),

    EventNames.OutputAudioTranscriptDelta => ServerEvent.OutputAudioTranscriptDelta(
        Text("response_id"), Text("item_id"), Text("delta")),

    EventNames.OutputAudioTranscriptDone => ServerEvent.OutputAudioTranscriptDone(
        Text("response_id"), Text("item_id"), Text("transcript")),

    EventNames.FunctionCallArgumentsDone => ServerEvent.FunctionCallArgumentsDone(
        Text("response_id"), Text("item_id"), Text("call_id"), Text("name"), Text("arguments")),

    EventNames.ResponseDone => ServerEvent.ResponseDone(
        RespText("id"),
        RespText("status"),
        DetailText("reason"),
        DetailErrorText("message")),

    // 上游发的是嵌套 error 对象；本实现工厂接受扁平参数，这里做展开
    EventNames.Error => ServerEvent.Error(
        ErrorText("code"), ErrorText("message"), MaybeErrorText("param")),

    _ => null,
};

if (missing.Count > 0)
{
    Console.Error.WriteLine($"fields 缺少字段：{string.Join(", ", missing)}");
    return 2;
}

if (payload is null)
{
    Console.Error.WriteLine($"本实现没有 {wire} 的构造路径");
    return 3;
}

// 归一化：两侧 event_id 生成规则不同，客户端不关心具体值
payload["event_id"] = "<event_id>";

// 必须走产品的序列化入口，否则编码器差异（中文被转义成 \uXXXX）测不出来
Console.Out.Write(ServerEvent.Serialize(payload));
Console.Out.Write('\n');
return 0;

T Track<T>(string name, Func<T> read)
{
    try
    {
        return read();
    }
    catch (Exception)
    {
        missing.Add(name);
        return default!;
    }
}

static JsonObject ErrorObject(JsonObject source) =>
    source["error"] as JsonObject ?? throw new InvalidOperationException("error");

static string Str(JsonObject source, string name) =>
    source[name]?.GetValue<string>() ?? throw new InvalidOperationException(name);

static string? OptStr(JsonObject source, string name) =>
    source[name] is { } node ? node.GetValue<string>() : null;

static int? MaybeInt(JsonObject source, string name) =>
    source[name] is { } node ? node.GetValue<int>() : null;

static int Int(JsonObject source, string name) =>
    source[name]?.GetValue<int>() ?? throw new InvalidOperationException(name);
