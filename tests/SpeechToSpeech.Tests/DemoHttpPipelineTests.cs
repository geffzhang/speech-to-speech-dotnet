using System.Text;
using SpeechToSpeech.Demo.Web;

namespace SpeechToSpeech.Tests;

/// <summary>
/// demo HTTP 分派的单测：search 的 pydantic 422 形状、内容类型路由、
/// calls 404/502 JSON detail、vendor 404/503、HEAD/405 矩阵。
/// 期望值取自参照机 FastAPI TestClient 实测。
/// </summary>
public class DemoHttpPipelineTests
{
    private static readonly DemoServerLogic.Config EmptyCfg =
        DemoServerLogic.ReadConfig(_ => null);

    private static DemoServerLogic.Config CfgWith(
        string? s2s = null, string? serper = null, string? space = null)
        => DemoServerLogic.ReadConfig(new Dictionary<string, string?>
        {
            ["SPEECH_TO_SPEECH_URL"] = s2s,
            ["SERPER_API_KEY"] = serper,
            ["SPACE_ID"] = space,
        }.GetValueOrDefault);

    private static DemoHttpPipeline.Upstream FakeUpstream(
        Func<string, byte[], string, DemoHttpPipeline.UpstreamResult>? impl = null)
        => new((url, body, ct, _) => Task.FromResult(
            impl?.Invoke(url, body, ct)
            ?? new DemoHttpPipeline.UpstreamResult(200, "application/json", "{}"u8.ToArray(), null)));

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static async Task<DemoHttpPipeline.OutResponse> Send(
        string method,
        string path,
        string? body = null,
        string? contentType = "application/json",
        DemoServerLogic.Config? cfg = null,
        DemoHttpPipeline.Upstream? upstream = null,
        string demoRoot = "")
    {
        var req = new DemoHttpPipeline.InRequest(
            method, path, contentType, body is null ? Array.Empty<byte>() : B(body),
            new Dictionary<string, string>());
        return await DemoHttpPipeline.HandleAsync(req, cfg ?? EmptyCfg, demoRoot, upstream ?? FakeUpstream());
    }

    private static string Text(DemoHttpPipeline.OutResponse r) => Encoding.UTF8.GetString(r.Body);

    // ── /api/search 校验形状 ───────────────────────────────────────────────

    [Fact]
    public async Task Search_empty_body_is_missing_at_body()
    {
        var r = await Send("POST", "/api/search", body: "");
        Assert.Equal(422, r.Status);
        Assert.Equal("""{"detail":[{"type":"missing","loc":["body"],"msg":"Field required","input":null}]}""", Text(r));
    }

    [Fact]
    public async Task Search_json_null_is_missing_at_body()
    {
        var r = await Send("POST", "/api/search", body: "null");
        Assert.Equal(422, r.Status);
        Assert.Equal("""{"detail":[{"type":"missing","loc":["body"],"msg":"Field required","input":null}]}""", Text(r));
    }

    [Fact]
    public async Task Search_missing_query_is_missing_at_body_query()
    {
        var r = await Send("POST", "/api/search", body: "{}");
        Assert.Equal(422, r.Status);
        Assert.Equal(
            """{"detail":[{"type":"missing","loc":["body","query"],"msg":"Field required","input":{}}]}""",
            Text(r));
    }

    [Theory]
    [InlineData("""{"query":123}""", """{"detail":[{"type":"string_type","loc":["body","query"],"msg":"Input should be a valid string","input":123}]}""")]
    [InlineData("""{"query":true}""", """{"detail":[{"type":"string_type","loc":["body","query"],"msg":"Input should be a valid string","input":true}]}""")]
    [InlineData("""{"query":{"a":1}}""", """{"detail":[{"type":"string_type","loc":["body","query"],"msg":"Input should be a valid string","input":{"a":1}}]}""")]
    [InlineData("""{"query":[1]}""", """{"detail":[{"type":"string_type","loc":["body","query"],"msg":"Input should be a valid string","input":[1]}]}""")]
    [InlineData("""{"query":null}""", """{"detail":[{"type":"string_type","loc":["body","query"],"msg":"Input should be a valid string","input":null}]}""")]
    [InlineData("""{"query":"x","key":5}""", """{"detail":[{"type":"string_type","loc":["body","key"],"msg":"Input should be a valid string","input":5}]}""")]
    [InlineData("""{"query":"x","sessionId":5}""", """{"detail":[{"type":"string_type","loc":["body","sessionId"],"msg":"Input should be a valid string","input":5}]}""")]
    [InlineData("""{"query":"x","key":-0.0}""", """{"detail":[{"type":"string_type","loc":["body","key"],"msg":"Input should be a valid string","input":-0.0}]}""")]
    [InlineData("""{"query":"x","key":99999999999999999999}""", """{"detail":[{"type":"string_type","loc":["body","key"],"msg":"Input should be a valid string","input":99999999999999999999}]}""")]
    public async Task Search_string_type_echoes_input(string body, string expected)
    {
        var r = await Send("POST", "/api/search", body);
        Assert.Equal(422, r.Status);
        Assert.Equal(expected, Text(r));
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("42", "42")]
    [InlineData("\"hello\"", "\"hello\"")]
    [InlineData("[1,2]", "[1,2]")]
    public async Task Search_non_object_json_is_model_attributes_type(string body, string inputEcho)
    {
        var r = await Send("POST", "/api/search", body);
        Assert.Equal(422, r.Status);
        var expected = "{\"detail\":[{\"type\":\"model_attributes_type\",\"loc\":[\"body\"],"
            + "\"msg\":\"Input should be a valid dictionary or object to extract fields from\",\"input\":"
            + inputEcho + "}]}";
        Assert.Equal(expected, Text(r));
    }

    [Theory]
    // 无 content-type / application/json / *+json → 按 JSON 解析
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("Application/JSON", true)]
    [InlineData("application/vnd.api+json", true)]
    [InlineData("application/vnd.foo+json", true)]
    [InlineData("application/other+json", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    // 其余（含 text/json、application/jsonx、逗号分隔怪写法）→ 原始 bytes
    [InlineData("text/plain", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("text/json", false)]
    [InlineData("application/jsonx", false)]
    [InlineData("application/json , charset=utf-8", false)]
    public void Search_content_type_routing_matches_FastAPI(string? ct, bool isJson)
        => Assert.Equal(isJson, DemoHttpPipeline.IsJsonContentType(ct));

    [Fact]
    public async Task Search_text_plain_body_is_model_attributes_type_with_string_input()
    {
        var r = await Send("POST", "/api/search", body: "{\"query\":\"x\"}", contentType: "text/plain");
        Assert.Equal(422, r.Status);
        Assert.Equal(
            """{"detail":[{"type":"model_attributes_type","loc":["body"],"msg":"Input should be a valid dictionary or object to extract fields from","input":"{\"query\":\"x\"}"}]}""",
            Text(r));
    }

    [Theory]
    [InlineData("   ", 3, "Expecting value")]
    [InlineData("{", 1, "Expecting property name enclosed in double quotes")]
    [InlineData("{\"query\":\"a\tb\"}", 11, "Invalid control character at")]
    public async Task Search_json_invalid_carries_pos_and_ctx(string body, int pos, string err)
    {
        var r = await Send("POST", "/api/search", body);
        Assert.Equal(422, r.Status);
        var expected = "{\"detail\":[{\"type\":\"json_invalid\",\"loc\":[\"body\"," + pos
            + "],\"msg\":\"JSON decode error\",\"input\":{},\"ctx\":{\"error\":\"" + err + "\"}}]}";
        Assert.Equal(expected, Text(r));
    }

    [Theory]
    [InlineData("""{"query":"x","key":NaN}""")]
    [InlineData("""{"query":"x","key":Infinity}""")]
    [InlineData("""{"query":NaN}""")]
    public async Task Search_nan_in_error_input_is_500(string body)
    {
        var r = await Send("POST", "/api/search", body);
        Assert.Equal(500, r.Status);
        Assert.Equal("Internal Server Error", Text(r));
    }

    [Fact]
    public async Task Search_nan_in_ignored_extra_field_is_fine()
    {
        var r = await Send("POST", "/api/search", """{"query":"x","extra":NaN}""");
        Assert.Equal(503, r.Status);
        Assert.Equal("""{"detail":"Search is not configured."}""", Text(r));
    }

    [Fact]
    public async Task Search_empty_query_is_400()
    {
        var r = await Send("POST", "/api/search", """{"query":""}""");
        Assert.Equal(400, r.Status);
        Assert.Equal("""{"detail":"Empty query."}""", Text(r));
    }

    [Fact]
    public async Task Search_no_key_no_server_search_is_503()
    {
        var r = await Send("POST", "/api/search", """{"query":"x"}""");
        Assert.Equal(503, r.Status);
        Assert.Equal("""{"detail":"Search is not configured."}""", Text(r));
    }

    [Fact]
    public async Task Search_extra_fields_ignored()
    {
        var r = await Send("POST", "/api/search", """{"query":"x","extra":1}""");
        Assert.Equal(503, r.Status);
    }

    [Fact]
    public async Task Search_duplicate_key_last_wins()
    {
        var r = await Send("POST", "/api/search", """{"query":"a","query":"b"}""");
        Assert.Equal(503, r.Status);
    }

    [Fact]
    public async Task Search_null_key_is_allowed()
    {
        var r = await Send("POST", "/api/search", """{"query":"x","key":null}""");
        Assert.Equal(503, r.Status);
    }

    [Fact]
    public async Task Search_success_shape()
    {
        var upstream = new DemoHttpPipeline.Upstream((_, _, _, _) =>
        {
            var payload = PythonJson.Serialize(new PythonJson.PyDict
            {
                new("organic", new PythonJson.PyList
                {
                    new PythonJson.PyDict
                    {
                        new("title", new PythonJson.PyStr("T")),
                        new("snippet", new PythonJson.PyStr("S")),
                        new("link", new PythonJson.PyStr("http://x")),
                    },
                }),
                new("answerBox", new PythonJson.PyDict
                {
                    new("answer", new PythonJson.PyStr("A")),
                }),
            });
            return Task.FromResult(new DemoHttpPipeline.UpstreamResult(
                200, "application/json", B(payload), null));
        });

        var r = await Send("POST", "/api/search", """{"query":"q","key":"k"}""", upstream: upstream);
        Assert.Equal(200, r.Status);
        Assert.Equal(
            """{"query":"q","answer":"A","results":[{"title":"T","snippet":"S","url":"http://x"}]}""",
            Text(r));
    }

    [Fact]
    public async Task Search_provider_error_is_502_with_message()
    {
        var upstream = new DemoHttpPipeline.Upstream((_, _, _, _) =>
            Task.FromResult(new DemoHttpPipeline.UpstreamResult(
                403, "application/json", B("""{"message":"Not enough credits"}"""), null)));
        var r = await Send("POST", "/api/search", """{"query":"q","key":"k"}""", upstream: upstream);
        Assert.Equal(502, r.Status);
        Assert.Equal("""{"detail":"Search provider error (403): Not enough credits"}""", Text(r));
    }

    // ── /api/calls ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Calls_without_url_is_404_json_detail_with_period()
    {
        // 注意文案带句点、小写 n —— 与静态 404 的 "Not Found" 不同
        var r = await Send("POST", "/api/calls", body: "{\"sdp\":\"v=0\"}");
        Assert.Equal(404, r.Status);
        Assert.Equal("""{"detail":"Not found."}""", Text(r));
    }

    [Fact]
    public async Task Calls_bad_body_still_404_when_no_url()
    {
        var r = await Send("POST", "/api/calls", body: "{");
        Assert.Equal(404, r.Status);
        Assert.Equal("""{"detail":"Not found."}""", Text(r));
    }

    [Fact]
    public async Task Calls_upstream_failure_is_502_json_detail()
    {
        var upstream = new DemoHttpPipeline.Upstream((_, _, _, _) =>
            throw new HttpRequestException("down"));
        var r = await Send(
            "POST", "/api/calls", body: "{\"sdp\":\"v=0\"}",
            cfg: CfgWith(s2s: "ws://h/v1/realtime"), upstream: upstream);
        Assert.Equal(502, r.Status);
        Assert.Equal("""{"detail":"Speech service unreachable."}""", Text(r));
    }

    [Fact]
    public async Task Calls_forwards_to_derived_url_with_sdp_content_type()
    {
        string? seenUrl = null;
        string? seenCt = null;
        var upstream = new DemoHttpPipeline.Upstream((url, _, ct, _) =>
        {
            seenUrl = url;
            seenCt = ct;
            return Task.FromResult(new DemoHttpPipeline.UpstreamResult(
                200, "application/sdp", B("v=0"), "call-1"));
        });

        var r = await Send(
            "POST", "/api/calls", body: "v=0",
            cfg: CfgWith(s2s: "ws://h/v1/realtime"), upstream: upstream);
        Assert.Equal(200, r.Status);
        Assert.Equal("http://h/v1/realtime/calls", seenUrl);
        Assert.Equal("application/sdp", seenCt);
        Assert.Equal("call-1", r.Location);
        Assert.Equal("v=0", Text(r));
    }

    // ── 路由矩阵 / 静态 ────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/config", 200)]
    [InlineData("HEAD", "/api/config", 404)]
    [InlineData("POST", "/api/config", 405)]
    [InlineData("PUT", "/api/config", 405)]
    [InlineData("PATCH", "/api/config", 405)]
    [InlineData("DELETE", "/api/config", 405)]
    [InlineData("OPTIONS", "/api/config", 405)]
    [InlineData("TRACE", "/api/config", 405)]
    [InlineData("GET", "/api/me", 200)]
    [InlineData("GET", "/api/me/", 404)]
    [InlineData("GET", "/API/config", 404)]
    [InlineData("GET", "/api/calls", 404)]
    [InlineData("GET", "/api/search", 404)]
    [InlineData("GET", "/no-such", 404)]
    [InlineData("POST", "/no-such", 405)]
    [InlineData("POST", "/main.js", 405)]
    public async Task Method_path_matrix_matches_FastAPI(string method, string path, int status)
    {
        var r = await Send(method, path, body: method is "POST" or "PUT" or "PATCH" ? "" : null);
        Assert.Equal(status, r.Status);
    }

    [Fact]
    public async Task Method_not_allowed_body_is_json_detail()
    {
        var r = await Send("POST", "/api/config", body: "");
        Assert.Equal(405, r.Status);
        Assert.Equal("""{"detail":"Method Not Allowed"}""", Text(r));
    }

    [Fact]
    public async Task Missing_static_is_json_not_found()
    {
        var r = await Send("GET", "/no-such");
        Assert.Equal(404, r.Status);
        Assert.Equal("""{"detail":"Not Found"}""", Text(r));
    }

    [Fact]
    public async Task Head_on_static_file_returns_headers_without_body()
    {
        var root = MakeDemoRoot(("main.js", "console.log(1);"));
        try
        {
            var r = await Send("HEAD", "/main.js", demoRoot: root);
            Assert.Equal(200, r.Status);
            Assert.Equal("application/javascript", r.ContentType);
            Assert.Empty(r.Body);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Static_js_content_type_has_no_charset()
    {
        var root = MakeDemoRoot(("main.js", "x"));
        try
        {
            var r = await Send("GET", "/main.js", demoRoot: root);
            Assert.Equal(200, r.Status);
            Assert.Equal("application/javascript", r.ContentType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Static_py_content_type_has_charset()
    {
        var root = MakeDemoRoot(("server.py", "x"));
        try
        {
            var r = await Send("GET", "/server.py", demoRoot: root);
            Assert.Equal(200, r.Status);
            Assert.Equal("text/x-python; charset=utf-8", r.ContentType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("main.js", "application/javascript")]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("style.css", "text/css; charset=utf-8")]
    [InlineData("data.json", "application/json")]
    [InlineData("mod.mjs", "text/plain; charset=utf-8")]
    [InlineData("noext", "text/plain; charset=utf-8")]
    public void GuessContentType_matches_Python_mimetypes(string file, string expected)
        => Assert.Equal(expected, DemoHttpPipeline.GuessContentType(file));

    [Fact]
    public async Task Vendor_bundle_missing_is_503()
    {
        var root = MakeDemoRoot(("index.html", "x"));
        try
        {
            var r = await Send("GET", "/vendor/openai-realtime-agents.umd.js", demoRoot: root);
            Assert.Equal(503, r.Status);
            Assert.Equal("""{"detail":"Run npm ci in demo/"}""", Text(r));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Vad_unknown_asset_is_404_json_not_found()
    {
        var root = MakeDemoRoot(("index.html", "x"));
        try
        {
            var r = await Send("GET", "/vendor/local-vad/unknown.js", demoRoot: root);
            Assert.Equal(404, r.Status);
            Assert.Equal("""{"detail":"Not Found"}""", Text(r));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Vad_known_asset_missing_is_503()
    {
        var root = MakeDemoRoot(("index.html", "x"));
        try
        {
            var r = await Send("GET", "/vendor/local-vad/bundle.min.js", demoRoot: root);
            Assert.Equal(503, r.Status);
            Assert.Equal("""{"detail":"Run npm ci in demo/"}""", Text(r));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Index_html_served_at_root()
    {
        var root = MakeDemoRoot(("index.html", "<html>hi</html>"));
        try
        {
            var r = await Send("GET", "/", demoRoot: root);
            Assert.Equal(200, r.Status);
            Assert.Equal("text/html; charset=utf-8", r.ContentType);
            Assert.Equal("<html>hi</html>", Text(r));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string MakeDemoRoot(params (string Name, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "s2s-demo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(root, name), content);
        }

        return root;
    }
}
