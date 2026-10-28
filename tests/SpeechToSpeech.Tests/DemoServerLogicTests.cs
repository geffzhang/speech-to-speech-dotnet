using SpeechToSpeech.Demo.Web;

namespace SpeechToSpeech.Tests;

/// <summary>
/// demo 服务端纯逻辑 + Python JSON 兼容层的单测。
/// 期望值全部取自参照机 Python 3.13（server.py 直接跑出），不是手推的。
/// </summary>
public class DemoServerLogicTests
{
    // ── _webrtc_calls_url ──────────────────────────────────────────────────

    [Theory]
    [InlineData("ws://host:port/v1/realtime", "http://host:port/v1/realtime/calls")]
    [InlineData("wss://host:port/v1/realtime", "https://host:port/v1/realtime/calls")]
    [InlineData("http://host:port/v1/realtime", "http://host:port/v1/realtime/calls")]
    [InlineData("https://host:port/v1/realtime", "https://host:port/v1/realtime/calls")]
    // scheme 前缀判定大小写敏感：WS:// 不匹配，先补 http:// 再被 urlsplit 拆成 netloc=WS:
    [InlineData("WS://h/p", "http://WS://h/p/calls")]
    [InlineData("WS://H/P", "http://WS://H/P/calls")]
    [InlineData("ws://h/p", "http://h/p/calls")]
    [InlineData("ws://h/", "http://h/v1/realtime/calls")]
    [InlineData("ws://h", "http://h/v1/realtime/calls")]
    // path.rstrip("/") 只去尾斜杠，内部的 // 在 netloc 里
    [InlineData("ws://h//", "http://h/calls")]
    [InlineData("ws://h/p//", "http://h/p/calls")]
    [InlineData("ws://h/p/", "http://h/p/calls")]
    // userinfo 保留、query 保留、fragment 丢弃
    [InlineData("ws://user:pass@h:1/v1/realtime", "http://user:pass@h:1/v1/realtime/calls")]
    [InlineData("ws://h/v1/realtime?x=1", "http://h/v1/realtime/calls?x=1")]
    [InlineData("ws://h/v1/realtime#frag", "http://h/v1/realtime/calls")]
    [InlineData("ws://h?q=1", "http://h/v1/realtime/calls?q=1")]
    [InlineData("ws://h#f", "http://h/v1/realtime/calls")]
    // 空串：urlsplit("") 空 netloc/path → http:///v1/realtime/calls
    [InlineData("", "http:///v1/realtime/calls")]
    [InlineData("  ws://h/p  ", "http://h/p/calls")]
    [InlineData("host:port", "http://host:port/v1/realtime/calls")]
    [InlineData("ws://h/v1/realtime/", "http://h/v1/realtime/calls")]
    [InlineData("wss://h/a/b/c", "https://h/a/b/c/calls")]
    [InlineData("http://h/p", "http://h/p/calls")]
    [InlineData("ws://h:443/v1/realtime", "http://h:443/v1/realtime/calls")]
    // 非已知 scheme 也补 http://，urlsplit 把 bad: 当 netloc
    [InlineData("bad://h/p", "http://bad://h/p/calls")]
    [InlineData("ws:///p", "http:///p/calls")]
    [InlineData("ws://", "http:///v1/realtime/calls")]
    public void WebRtcCallsUrl_matches_Python_urlsplit(string input, string expected)
        => Assert.Equal(expected, DemoServerLogic.WebRtcCallsUrl(input));

    // ── _parse_ice_servers ─────────────────────────────────────────────────

    [Theory]
    [InlineData("", "[]")]
    [InlineData("   ", "[]")]
    [InlineData("[]", "[]")]
    [InlineData("""[{"urls":"stun:x"}]""", """[{"urls":"stun:x"}]""")]
    [InlineData("""{"urls":"stun:x"}""", """[{"urls":"stun:x"}]""")]
    // JSON 解析成功但不是 list/dict → 落回逗号分支（注意 "null"/"true"/"123" 是字面文本）
    [InlineData("null", """[{"urls":"null"}]""")]
    [InlineData("true", """[{"urls":"true"}]""")]
    [InlineData("123", """[{"urls":"123"}]""")]
    [InlineData("\"str\"", """[{"urls":"\"str\""}]""")]
    [InlineData("stun:a,stun:b", """[{"urls":"stun:a"},{"urls":"stun:b"}]""")]
    [InlineData(" stun:a , turn:b ", """[{"urls":"stun:a"},{"urls":"turn:b"}]""")]
    [InlineData("stun:a,,stun:b", """[{"urls":"stun:a"},{"urls":"stun:b"}]""")]
    // list 原样保留，非对象成员不丢
    [InlineData("""[{"urls":"stun:x"},5]""", """[{"urls":"stun:x"},5]""")]
    [InlineData("[1,2]", "[1,2]")]
    [InlineData("[null]", "[null]")]
    // 解析失败 → 逗号分支吃整个原文
    [InlineData("{bad", """[{"urls":"{bad"}]""")]
    [InlineData("\"a,b\"", """[{"urls":"\"a"},{"urls":"b\""}]""")]
    [InlineData("""{"urls":"stun:x"},""", """[{"urls":"{\"urls\":\"stun:x\"}"}]""")]
    [InlineData(",", "[]")]
    [InlineData("ws://h", """[{"urls":"ws://h"}]""")]
    [InlineData("""{"urls":"stun:x","username":"u","credential":"c"}""",
        """[{"urls":"stun:x","username":"u","credential":"c"}]""")]
    public void ParseIceServers_matches_Python(string raw, string expectedJson)
    {
        var arr = DemoServerLogic.ParseIceServers(raw);
        Assert.Equal(expectedJson, PythonJson.Serialize(DemoServerLogic.ToPy(arr)));
    }

    // ── config 推导 ────────────────────────────────────────────────────────

    [Fact]
    public void ReadConfig_speech_url_clears_load_balancer()
    {
        var cfg = DemoServerLogic.ReadConfig(new Dictionary<string, string?>
        {
            ["SPEECH_TO_SPEECH_URL"] = "ws://s2s:8791/v1/realtime",
            ["LOAD_BALANCER_URL"] = "https://lb.example",
            ["SERPER_API_KEY"] = "k",
        }.GetValueOrDefault);

        Assert.Equal("ws://s2s:8791/v1/realtime", cfg.SpeechToSpeechUrl);
        Assert.Equal("", cfg.LoadBalancerUrl);
        Assert.False(DemoServerLogic.LimiterEnabled(cfg));
        Assert.True(DemoServerLogic.ServerSearch(cfg));
    }

    [Fact]
    public void ServerSearch_disabled_on_space_even_with_key()
    {
        // server.py: SERVER_SEARCH = bool(SERPER_KEY) and (AUTH_ENABLED or not SPACE_ID)
        // AUTH_ENABLED 恒 false → SPACE_ID 存在时服务端 key 一律不用
        var cfg = DemoServerLogic.ReadConfig(new Dictionary<string, string?>
        {
            ["SERPER_API_KEY"] = "k",
            ["SPACE_ID"] = "owner/space",
        }.GetValueOrDefault);

        Assert.False(DemoServerLogic.ServerSearch(cfg));
    }

    [Fact]
    public void ServerSearch_requires_key()
    {
        var cfg = DemoServerLogic.ReadConfig(new Dictionary<string, string?>().GetValueOrDefault);
        Assert.False(DemoServerLogic.ServerSearch(cfg));
    }

    [Fact]
    public void BuildConfigPayload_shape_matches_server_py()
    {
        var cfg = DemoServerLogic.ReadConfig(new Dictionary<string, string?>().GetValueOrDefault);
        var payload = PythonJson.Serialize(DemoServerLogic.ToPy(DemoServerLogic.BuildConfigPayload(cfg)));
        Assert.Equal(
            """{"search":false,"lb":false,"allowDirect":true,"s2sUrl":"","rtc":false,"iceServers":[],"startupGreeting":"","auth":false}""",
            // STARTUP_GREETING 有默认值，这里只比前缀形状
            payload.Replace(cfg.StartupGreeting, ""));
    }

    [Fact]
    public void Config_rtc_true_only_when_s2s_url_set()
    {
        var cfg = DemoServerLogic.ReadConfig(new Dictionary<string, string?>
        {
            ["SPEECH_TO_SPEECH_URL"] = "ws://h/v1/realtime",
        }.GetValueOrDefault);
        var payload = DemoServerLogic.BuildConfigPayload(cfg);
        Assert.True(payload["rtc"]!.GetValue<bool>());
        Assert.True(payload["allowDirect"]!.GetValue<bool>());
    }
}

/// <summary>PythonJson 的解析/序列化与 CPython 3.13 json 模块对拍。</summary>
public class PythonJsonTests
{
    [Theory]
    [InlineData("1.0", "1.0")]
    [InlineData("1.00", "1.0")]
    [InlineData("1.5", "1.5")]
    [InlineData("1e2", "100.0")]
    [InlineData("1E2", "100.0")]
    [InlineData("-0.0", "-0.0")]
    [InlineData("0.0", "0.0")]
    [InlineData("100.0", "100.0")]
    [InlineData("0.0001", "0.0001")]
    [InlineData("0.00001", "1e-05")]
    [InlineData("1e15", "1000000000000000.0")]
    [InlineData("1e16", "1e+16")]
    [InlineData("1e17", "1e+17")]
    [InlineData("1.5e-5", "1.5e-05")]
    [InlineData("3.14159265358979", "3.14159265358979")]
    [InlineData("1e-7", "1e-07")]
    [InlineData("2.0", "2.0")]
    [InlineData("-1.5", "-1.5")]
    [InlineData("1e308", "1e+308")]
    [InlineData("5e-324", "5e-324")]
    public void Float_roundtrip_matches_Python_json_dumps(string input, string expected)
    {
        var v = PythonJson.Parse(input);
        Assert.Equal(expected, PythonJson.Serialize(v));
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("99999999999999999999", "99999999999999999999")]
    [InlineData("-42", "-42")]
    public void Int_roundtrip_preserves_digits(string input, string expected)
    {
        var v = PythonJson.Parse(input);
        Assert.Equal(expected, PythonJson.Serialize(v));
    }

    [Theory]
    [InlineData("NaN", "NaN")]
    [InlineData("Infinity", "Infinity")]
    [InlineData("-Infinity", "-Infinity")]
    public void Nan_inf_parse_but_serialization_can_refuse(string input, string pyForm)
    {
        var v = PythonJson.Parse(input);
        Assert.Equal(pyForm, PythonJson.Serialize(v, allowNan: true));
        Assert.ThrowsAny<Exception>(() => PythonJson.Serialize(v, allowNan: false));
    }

    [Theory]
    [InlineData("   ", "Expecting value", 3)]
    [InlineData("{", "Expecting property name enclosed in double quotes", 1)]
    [InlineData("}", "Expecting value", 0)]
    [InlineData("{,}", "Expecting property name enclosed in double quotes", 1)]
    [InlineData("""{"a"}""", "Expecting ':' delimiter", 4)]
    [InlineData("""{"a":}""", "Expecting value", 5)]
    [InlineData("""{"a":1""", "Expecting ',' delimiter", 6)]
    [InlineData("[", "Expecting value", 1)]
    [InlineData("[1", "Expecting ',' delimiter", 2)]
    [InlineData("[1,", "Expecting value", 3)]
    [InlineData("\"abc", "Unterminated string starting at", 0)]
    [InlineData("{\"q\":\"a\tb\"}", "Invalid control character at", 7)]
    [InlineData("""{"q":"a\qb"}""", "Invalid \\escape", 7)]
    [InlineData("""{"q":"\u12"}""", "Invalid \\uXXXX escape", 7)]
    [InlineData("tru", "Expecting value", 0)]
    [InlineData("nul", "Expecting value", 0)]
    [InlineData("{1:2}", "Expecting property name enclosed in double quotes", 1)]
    [InlineData("{'a':1}", "Expecting property name enclosed in double quotes", 1)]
    [InlineData("[1 2]", "Expecting ',' delimiter", 3)]
    [InlineData("{\"a\":1,}", "Illegal trailing comma before end of object", 6)]
    [InlineData("[1,]", "Illegal trailing comma before end of array", 2)]
    [InlineData("{\"a\":01}", "Expecting ',' delimiter", 6)]
    [InlineData("{\"query\":x}", "Expecting value", 9)]
    public void Json_error_msg_and_pos_match_Python(string input, string msg, int pos)
    {
        var ex = Assert.Throws<PythonJson.JsonError>(() => PythonJson.Parse(input));
        Assert.Equal(msg, ex.Msg);
        Assert.Equal(pos, ex.Pos);
    }

    [Fact]
    public void Duplicate_keys_last_wins()
    {
        var v = PythonJson.Parse("""{"query":"a","query":"b"}""");
        Assert.Equal("""{"query":"b"}""", PythonJson.Serialize(v));
    }

    [Fact]
    public void Extra_data_is_error()
    {
        var ex = Assert.Throws<PythonJson.JsonError>(() => PythonJson.Parse("{} x"));
        Assert.Equal("Extra data", ex.Msg);
    }
}
