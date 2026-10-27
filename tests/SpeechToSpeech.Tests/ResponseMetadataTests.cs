using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Realtime.Protocol;

namespace SpeechToSpeech.Tests;

/// <summary>
/// <c>response.metadata</c> 的合并规则。
///
/// 这一层没有一条规则会抛异常——漏掉任何一条，客户端只是少看到或多看到几个键。
/// 所以每条规则都要有一条会红的用例，而不是只测「有 metadata 的时候能出来」。
/// </summary>
public class ResponseMetadataTests
{
    private const string Latency = TurnLatencyMetadata.Key;

    private static Dictionary<string, string> Client(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void InProgress_CarriesClientKeysOnly()
    {
        // created 事件只带客户端那份：服务端测量要等终态才有数字，
        // 中途塞半成品进去，客户端会拿它当最终值缓存。
        var metadata = ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Client = Client(("trace", "abc")),
            Status = "in_progress",
            LatencyPayload = "{\"version\":2}",
        });

        Assert.NotNull(metadata);
        Assert.Equal(["trace"], metadata.Select(kv => kv.Key));
        Assert.Equal("abc", metadata["trace"]!.GetValue<string>());
    }

    [Fact]
    public void Terminal_AppendsServerLatencyAfterClientKeys()
    {
        var metadata = ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Client = Client(("trace", "abc"), ("tenant", "t1")),
            Status = "completed",
            LatencyPayload = "{\"version\":2}",
        });

        Assert.NotNull(metadata);
        Assert.Equal(["trace", "tenant", Latency], metadata.Select(kv => kv.Key));
        Assert.Equal("{\"version\":2}", metadata[Latency]!.GetValue<string>());
    }

    [Fact]
    public void Terminal_DropsClientSuppliedLatencyKeyWhenNoMeasurement()
    {
        // 客户端不能靠自带一个 turn_latency 伪造「这轮有多快」。
        var metadata = ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Client = Client(("trace", "abc"), (Latency, "{\"e2e_s\":0.01}")),
            Status = "completed",
        });

        Assert.NotNull(metadata);
        Assert.Equal(["trace"], metadata.Select(kv => kv.Key));
        Assert.False(metadata.ContainsKey(Latency));
    }

    [Fact]
    public void Terminal_ReplacesClientSuppliedLatencyKeyInsteadOfDuplicating()
    {
        var metadata = ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Client = Client((Latency, "{\"e2e_s\":0.01}")),
            Status = "completed",
            LatencyPayload = "{\"e2e_s\":2.0}",
        });

        Assert.NotNull(metadata);
        Assert.Single(metadata);
        Assert.Equal("{\"e2e_s\":2.0}", metadata[Latency]!.GetValue<string>());
    }

    [Fact]
    public void Terminal_NoKeysAtAll_IsNullNotEmptyObject()
    {
        // 空对象会让客户端以为「这轮确实没有元数据」，null 才是「没有」。
        var metadata = ResponseMetadata.Assemble(new ResponseMetadataInput { Status = "completed" });

        Assert.Null(metadata);
    }

    [Fact]
    public void ToolInput_OnlyOnCompletedInBandResponses()
    {
        string[] ids = ["call_b", "call_a"];

        var completed = ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Status = "completed",
            ToolInputCallIds = ids,
        });
        Assert.Equal("[\"call_a\",\"call_b\"]", completed![ResponseMetadata.ToolInputKey]!.GetValue<string>());

        // 取消的一轮没有「到达模型的工具结果」可言
        Assert.Null(ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Status = "cancelled",
            ToolInputCallIds = ids,
        }));

        // 出带外响应不进会话，工具输入也就无从谈起
        Assert.Null(ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Status = "completed",
            OutOfBand = true,
            ToolInputCallIds = ids,
        }));
    }

    [Fact]
    public void ToolInput_ClientSuppliedKeyIsNeverTrusted()
    {
        // 这个键只有服务端知道，客户端写了也一律删掉——即使这轮确实没有工具调用。
        var metadata = ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Client = Client((ResponseMetadata.ToolInputKey, "[\"forged\"]")),
            Status = "completed",
        });

        Assert.Null(metadata);
    }

    [Fact]
    public void Metadata_SlotLimitDropsServerFieldsButKeepsClientOnes()
    {
        // 16 个槽位被客户端占满时，服务端测量不写——但绝不为了腾地方删客户端的键。
        var client = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < TurnLatencyMetadata.MaxSlots; i++)
        {
            client[$"k{i}"] = "v";
        }

        var metadata = ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Client = client,
            Status = "completed",
            ToolInputCallIds = ["call_a"],
            LatencyPayload = "{\"version\":2}",
        });

        Assert.NotNull(metadata);
        Assert.Equal(TurnLatencyMetadata.MaxSlots, metadata.Count);
        Assert.False(metadata.ContainsKey(Latency));
        Assert.False(metadata.ContainsKey(ResponseMetadata.ToolInputKey));
    }

    [Fact]
    public void Metadata_OneFreeSlotIsEnoughForLatency()
    {
        var client = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < TurnLatencyMetadata.MaxSlots - 1; i++)
        {
            client[$"k{i}"] = "v";
        }

        var metadata = ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Client = client,
            Status = "completed",
            LatencyPayload = "{\"version\":2}",
        });

        Assert.NotNull(metadata);
        Assert.Equal(TurnLatencyMetadata.MaxSlots, metadata.Count);
        Assert.True(metadata.ContainsKey(Latency));
    }

    [Fact]
    public void Metadata_ValueLongerThanTheLimitIsDroppedNotTruncated()
    {
        // 截断会产出坏 JSON，客户端解析直接失败——宁可整条不发。
        var oversized = "{\"pad\":\"" + new string('x', TurnLatencyMetadata.MaxValueLength) + "\"}";

        var metadata = ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Status = "completed",
            LatencyPayload = oversized,
        });

        Assert.Null(metadata);
    }

    [Fact]
    public void Metadata_PayloadExactlyAtTheLimitStillFits()
    {
        // 边界是「不超过 512」，不是「小于 512」。差一个字符就会让边缘的一轮丢掉整条遥测。
        var padding = TurnLatencyMetadata.MaxValueLength - "{\"pad\":\"\"}".Length;
        var exact = "{\"pad\":\"" + new string('x', padding) + "\"}";
        Assert.Equal(TurnLatencyMetadata.MaxValueLength, exact.Length);

        var metadata = ResponseMetadata.Assemble(new ResponseMetadataInput
        {
            Status = "completed",
            LatencyPayload = exact,
        });

        Assert.NotNull(metadata);
    }

    [Fact]
    public void ReadClientMetadata_SkipsNonStringValuesInsteadOfThrowing()
    {
        // 非字符串的值跳过而不是强转：强转会抛，把一条本来能跑的请求变成 400。
        var response = JsonNode.Parse("""
            {"metadata": {"trace": "abc", "count": 3, "flag": true, "nested": {"a": 1}}}
            """)!.AsObject();

        var metadata = ResponseMetadata.ReadClientMetadata(response);

        Assert.NotNull(metadata);
        Assert.Equal(["trace"], metadata.Keys);
    }

    [Fact]
    public void ReadClientMetadata_AbsentOrNonObjectIsNull()
    {
        Assert.Null(ResponseMetadata.ReadClientMetadata(new JsonObject()));
        Assert.Null(ResponseMetadata.ReadClientMetadata(JsonNode.Parse("""{"metadata": "x"}""")!.AsObject()));
        Assert.Null(ResponseMetadata.ReadClientMetadata(JsonNode.Parse("""{"metadata": {}}""")!.AsObject()));
    }
}
