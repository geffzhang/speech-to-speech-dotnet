using System.Text.Json.Nodes;

namespace SpeechToSpeech.Demo.Web;

/// <summary>
/// 浏览器 demo 服务端的纯逻辑，与上游 <c>demo/server.py</c> 的直连/本地模式逐字段对齐。
///
/// 只放「输入 -&gt; 输出」的纯计算与配置组装，不含 HTTP/IO，便于单测直接对拍。
/// </summary>
public static class DemoServerLogic
{
    /// <summary>解析后的服务端配置快照（由环境变量驱动）。</summary>
    public sealed record Config(
        string SpeechToSpeechUrl,
        string LoadBalancerUrl,
        string SerperKey,
        string SpaceId,
        string StartupGreeting,
        JsonArray IceServers);

    /// <summary>从环境变量读取配置，复刻 server.py 顶部的推导逻辑。</summary>
    /// <param name="env">按名取环境变量的委托（可注入字典以便单测）。</param>
    public static Config ReadConfig(Func<string, string?> env)
    {
        var serperKey = (env("SERPER_API_KEY") ?? "").Trim();
        var loadBalancerUrl = (env("LOAD_BALANCER_URL") ?? "").Trim();
        var speechToSpeechUrl = (env("SPEECH_TO_SPEECH_URL") ?? "").Trim();
        var spaceId = (env("SPACE_ID") ?? "").Trim();
        var startupGreeting = (env("STARTUP_GREETING")
            ?? "Start the conversation now with a brief, spontaneous greeting in character. "
               + "Keep it to one sentence, invite the user in naturally, and vary the wording each time.").Trim();

        // SPEECH_TO_SPEECH_URL 优先：设置后 LB 逻辑全部禁用（server.py 同口径）。
        if (speechToSpeechUrl.Length > 0)
        {
            loadBalancerUrl = "";
        }

        return new Config(
            speechToSpeechUrl,
            loadBalancerUrl,
            serperKey,
            spaceId,
            startupGreeting,
            ParseIceServers(env("RTC_ICE_SERVERS") ?? ""));
    }

    /// <summary>限流只在「配置了 LB 且处于 Space」时启用（本地/直连恒 false）。</summary>
    public static bool LimiterEnabled(Config c) => c.LoadBalancerUrl.Length > 0 && c.SpaceId.Length > 0;

    /// <summary>
    /// 服务端是否持有可用于 /api/search 的 Serper key。
    /// server.py：SERVER_SEARCH = bool(SERPER_KEY) and (AUTH_ENABLED or not SPACE_ID)。
    /// 本移植不接 HF OAuth，AUTH_ENABLED 恒 false，故退化为 bool(SerperKey) and not SPACE_ID。
    /// 注意 SPACE_ID 存在时（Space 部署）服务端 key 一律不用，即便没挂 auth。
    /// </summary>
    public static bool ServerSearch(Config c) => c.SerperKey.Length > 0 && c.SpaceId.Length == 0;

    /// <summary>JsonNode → PyValue（数字保留原文以区分 int/float）。</summary>
    public static PythonJson.PyValue ToPy(JsonNode? node) => JsonNodePy.ToPy(node);

    /// <summary>/api/config 的响应形状。</summary>
    public static JsonObject BuildConfigPayload(Config c) => new()
    {
        ["search"] = ServerSearch(c),
        ["lb"] = c.LoadBalancerUrl.Length > 0,
        ["allowDirect"] = c.LoadBalancerUrl.Length == 0,
        ["s2sUrl"] = c.SpeechToSpeechUrl,
        // WebRTC 传输仅在 env 钉定 URL 时提供（/api/calls 只转发到它，见 Program）。
        ["rtc"] = c.SpeechToSpeechUrl.Length > 0,
        ["iceServers"] = c.IceServers.DeepClone(),
        ["startupGreeting"] = c.StartupGreeting,
        ["auth"] = false,
    };

    /// <summary>
    /// 复刻 server.py 的 <c>_parse_ice_servers</c>：
    /// 接受 JSON 数组、单个 RTCIceServer 对象、或逗号分隔的 STUN/TURN URL 列表；空串返回空数组。
    /// JSON 解析成功但不是 list/dict（如 <c>null</c>/<c>true</c>/<c>123</c>/<c>"str"</c>）落回逗号分支。
    /// </summary>
    public static JsonArray ParseIceServers(string raw)
    {
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return new JsonArray();
        }

        try
        {
            var node = JsonNode.Parse(trimmed);
            if (node is JsonArray arr)
            {
                return arr.DeepClone().AsArray();
            }

            if (node is JsonObject obj)
            {
                return new JsonArray(obj.DeepClone());
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // fall through to comma list
        }

        var result = new JsonArray();
        foreach (var url in trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            result.Add(new JsonObject { ["urls"] = url });
        }

        return result;
    }

    /// <summary>
    /// 复刻 server.py 的 <c>_webrtc_calls_url</c>（Python <c>urlsplit</c>/<c>urlunsplit</c> 语义）：
    /// <c>ws://host:port/v1/realtime</c> -&gt; <c>http://host:port/v1/realtime/calls</c>
    /// （ws→http、wss→https；裸 host 补 <c>http://</c>；缺省路径取 <c>/v1/realtime</c>；保留 query、丢 fragment）。
    ///
    /// 注意 scheme 前缀判定是<strong>大小写敏感</strong>的 <c>startswith</c>：
    /// <c>WS://h/p</c> 不匹配 <c>ws://</c>，先补成 <c>http://WS://h/p</c>，
    /// 再被 urlsplit 拆成 netloc=<c>WS:</c> path=<c>//h/p</c>。
    /// </summary>
    public static string WebRtcCallsUrl(string s2sUrl)
    {
        var s = (s2sUrl ?? "").Trim();
        if (s.Length == 0)
        {
            // Python: 不加前缀，urlsplit("") 得空 netloc/path，路径取默认 → http:///v1/realtime/calls
            return "http:///v1/realtime/calls";
        }

        if (!s.StartsWith("ws://", StringComparison.Ordinal)
            && !s.StartsWith("wss://", StringComparison.Ordinal)
            && !s.StartsWith("http://", StringComparison.Ordinal)
            && !s.StartsWith("https://", StringComparison.Ordinal))
        {
            s = "http://" + s;
        }

        var (scheme, netloc, path, query) = UrlSplit(s);
        scheme = scheme switch
        {
            "ws" => "http",
            "wss" => "https",
            var sc => sc,
        };

        if (path is "" or "/")
        {
            path = "/v1/realtime";
        }

        // Python: path.rstrip("/") + "/calls" —— 只去尾部斜杠，内部的 // 保留
        var trimmedPath = path.TrimEnd('/');
        return scheme + "://" + netloc + trimmedPath + "/calls" + (query.Length > 0 ? "?" + query : "");
    }

    /// <summary>
    /// Python <c>urllib.parse.urlsplit</c> 的最小实现，只覆盖本函数用到的行为：
    /// scheme 大小写归一为小写；netloc 吃到下一个 <c>/ ? #</c>；query 取 <c>?</c> 后到 <c>#</c> 前；fragment 丢弃。
    /// </summary>
    private static (string Scheme, string Netloc, string Path, string Query) UrlSplit(string url)
    {
        var scheme = "";
        var rest = url;
        var colon = url.IndexOf(':');
        if (colon > 0)
        {
            var candidate = url[..colon];
            var valid = char.IsAsciiLetter(candidate[0]);
            for (var i = 1; valid && i < candidate.Length; i++)
            {
                var ch = candidate[i];
                valid = char.IsAsciiLetterOrDigit(ch) || ch is '+' or '-' or '.';
            }

            if (valid)
            {
                scheme = candidate.ToLowerInvariant();
                rest = url[(colon + 1)..];
            }
        }

        var netloc = "";
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
            var end = rest.Length;
            foreach (var d in new[] { '/', '?', '#' })
            {
                var i = rest.IndexOf(d);
                if (i >= 0 && i < end)
                {
                    end = i;
                }
            }

            netloc = rest[..end];
            rest = rest[end..];
        }

        // fragment 丢弃
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            rest = rest[..hash];
        }

        var query = "";
        var qm = rest.IndexOf('?');
        if (qm >= 0)
        {
            query = rest[(qm + 1)..];
            rest = rest[..qm];
        }

        return (scheme, netloc, rest, query);
    }
}
