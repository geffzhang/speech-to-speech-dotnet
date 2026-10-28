using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;

namespace SpeechToSpeech.Demo.Web;

/// <summary>
/// demo 服务端的请求分派：严格对齐 FastAPI/Starlette 的可观察行为。
///
/// 路由模型（与 server.py 的挂载顺序一致）：
/// <list type="number">
///   <item>API 路由必须<strong>方法精确匹配</strong>才接管（GET 不吃 HEAD——与 Starlette APIRoute 一致）；</item>
///   <item>其余一律落 StaticFiles：GET/HEAD 查文件（缺失 404 JSON），其它方法 405 JSON。</item>
/// </list>
/// 响应体统一走 <see cref="PythonJson"/> 序列化（紧凑、非 ASCII 不转义、NaN 触发 500），与上游 json.dumps 同形。
/// </summary>
public static class DemoHttpPipeline
{
    private static readonly Dictionary<string, (string Package, string MediaType)> LocalVadAssets = new()
    {
        ["bundle.min.js"] = ("@ricky0123/vad-web", "text/javascript"),
        ["vad.worklet.bundle.min.js"] = ("@ricky0123/vad-web", "text/javascript"),
        ["silero_vad_v6.onnx"] = ("@ricky0123/vad-web", "application/octet-stream"),
        ["ort.wasm.min.js"] = ("onnxruntime-web", "text/javascript"),
        ["ort-wasm-simd-threaded.mjs"] = ("onnxruntime-web", "text/javascript"),
        ["ort-wasm-simd-threaded.wasm"] = ("onnxruntime-web", "application/wasm"),
    };

    /// <summary>入站请求的最小抽象，便于单测不启真实 socket。</summary>
    public sealed record InRequest(string Method, string Path, string? ContentType, byte[] Body, Dictionary<string, string> Headers);

    /// <summary>出站响应。</summary>
    public sealed record OutResponse(int Status, string? ContentType, byte[] Body, string? Location = null);

    /// <summary>上游调用委托（calls 转发 / search 代理），单测注入假实现。</summary>
    public sealed record Upstream(
        Func<string, byte[], string, CancellationToken, Task<UpstreamResult>> PostAsync);

    public sealed record UpstreamResult(int Status, string? ContentType, byte[] Body, string? Location);

    /// <summary>处理一条请求。</summary>
    public static async Task<OutResponse> HandleAsync(
        InRequest req,
        DemoServerLogic.Config cfg,
        string demoRoot,
        Upstream upstream,
        CancellationToken ct = default)
    {
        var method = req.Method.ToUpperInvariant();
        var path = req.Path;

        // ── API 路由：方法必须精确匹配 ────────────────────────────────────────
        if (method == "GET" && path == "/api/config")
        {
            return Json(200, DemoServerLogic.ToPy(DemoServerLogic.BuildConfigPayload(cfg)));
        }

        if (method == "GET" && path == "/api/me")
        {
            return Json(200, new PythonJson.PyDict(new() { new("enabled", new PythonJson.PyBool(false)) }));
        }

        if (method == "POST" && path == "/api/search")
        {
            return await SearchAsync(req, cfg, upstream, ct);
        }

        if (method == "POST" && path == "/api/calls")
        {
            return await CallsAsync(req, cfg, upstream, ct);
        }

        if (method == "GET" && path == "/vendor/openai-realtime-agents.umd.js")
        {
            return VendorBundle(demoRoot);
        }

        if (method == "GET" && path.StartsWith("/vendor/local-vad/", StringComparison.Ordinal))
        {
            return LocalVadAsset(demoRoot, path["/vendor/local-vad/".Length..]);
        }

        // ── 未命中 API：落静态（GET/HEAD）或 405 ─────────────────────────────
        if (method is not ("GET" or "HEAD"))
        {
            return Json(405, Detail("Method Not Allowed"));
        }

        return StaticFile(demoRoot, path, isHead: method == "HEAD");
    }

    // ── /api/search ─────────────────────────────────────────────────────────

    private static async Task<OutResponse> SearchAsync(InRequest req, DemoServerLogic.Config cfg, Upstream upstream, CancellationToken ct)
    {
        var validated = ValidateSearchRequest(req.ContentType, req.Body);

        if (validated.Error is not null)
        {
            // error 的 input 若含 NaN/Inf，上游 json.dumps(allow_nan=False) 会炸成 500
            return JsonAllowNan(422, validated.Error, serverErrorOnNan: true);
        }

        var query = validated.Query!.Trim();
        if (query.Length == 0)
        {
            return Json(400, Detail("Empty query."));
        }

        var key = (validated.Key ?? "").Trim();
        if (key.Length == 0)
        {
            if (!DemoServerLogic.ServerSearch(cfg))
            {
                return Json(503, Detail("Search is not configured."));
            }

            key = cfg.SerperKey;
        }

        // Serper 代理
        var payload = Encoding.UTF8.GetBytes(PythonJson.Serialize(
            new PythonJson.PyDict(new()
            {
                new("q", new PythonJson.PyStr(query)),
                new("num", new PythonJson.PyInt("5")),
            })));
        var headers = new Dictionary<string, string> { ["X-API-KEY"] = key };
        UpstreamResult resp;
        try
        {
            resp = await upstream.PostAsync("https://google.serper.dev/search", payload, key, ct);
        }
        catch (HttpRequestException)
        {
            return Json(502, Detail("Search provider unreachable."));
        }

        if (resp.Status != 200)
        {
            string? msg = null;
            try
            {
                var err = PythonJson.Parse(Encoding.UTF8.GetString(resp.Body));
                if (err is PythonJson.PyDict d)
                {
                    foreach (var kv in d.Items)
                    {
                        if (kv.Key == "message" && kv.Value is PythonJson.PyStr s)
                        {
                            msg = s.Value;
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // ignore
            }

            var detail = $"Search provider error ({resp.Status})";
            if (msg is not null)
            {
                detail += $": {msg}";
            }

            return Json(502, Detail(detail));
        }

        PythonJson.PyValue data;
        try
        {
            data = PythonJson.Parse(Encoding.UTF8.GetString(resp.Body));
        }
        catch (Exception)
        {
            data = new PythonJson.PyDict(new());
        }

        var results = new List<PythonJson.PyValue>();
        if (data is PythonJson.PyDict dataDict)
        {
            foreach (var kv in dataDict.Items)
            {
                if (kv.Key != "organic" || kv.Value is not PythonJson.PyList organic)
                {
                    continue;
                }

                foreach (var item in organic.Items.Take(5))
                {
                    if (item is not PythonJson.PyDict o)
                    {
                        continue;
                    }

                    results.Add(new PythonJson.PyDict(new()
                    {
                        new("title", GetStr(o, "title")),
                        new("snippet", GetStr(o, "snippet")),
                        new("url", GetStr(o, "link")),
                    }));
                }
            }
        }

        string? answer = null;
        if (data is PythonJson.PyDict root)
        {
            var box = GetDict(root, "answerBox");
            answer = box is null ? null : GetStrOrNull(box, "answer") ?? GetStrOrNull(box, "snippet");
            if (answer is null)
            {
                var kg = GetDict(root, "knowledgeGraph");
                answer = kg is null ? null : GetStrOrNull(kg, "description");
            }
        }

        return Json(200, new PythonJson.PyDict(new()
        {
            new("query", new PythonJson.PyStr(query)),
            new("answer", answer is null ? PythonJson.PyNull.Instance : new PythonJson.PyStr(answer)),
            new("results", new PythonJson.PyList(results)),
        }));
    }

    /// <summary>SearchRequest 的 pydantic v2 校验形状（422 detail 数组）。</summary>
    public sealed record SearchValidation(
        string? Query,
        string? Key,
        string? SessionId,
        PythonJson.PyValue? Error);

    /// <summary>
    /// 复刻 FastAPI+pydantic v2 对 SearchRequest 的可观察校验行为：
    /// 内容类型路由 → JSON 解析（错误带 msg/pos）→ 模型字段校验，错误形状逐字段对齐。
    /// </summary>
    public static SearchValidation ValidateSearchRequest(string? contentType, byte[] body)
    {
        // FastAPI: 空 body → missing（input null）
        if (body is null || body.Length == 0)
        {
            return new SearchValidation(null, null, null, MissingBody());
        }

        // FastAPI 内容类型路由：无 content-type，或 application/(json|*+json) 才按 JSON 解析
        if (!IsJsonContentType(contentType))
        {
            // 原始 bytes 当作字符串喂给模型 → model_attributes_type
            var asText = Encoding.UTF8.GetString(body);
            return new SearchValidation(null, null, null,
                ModelAttributesType(new PythonJson.PyStr(asText)));
        }

        PythonJson.PyValue parsed;
        try
        {
            parsed = PythonJson.Parse(Encoding.UTF8.GetString(body));
        }
        catch (PythonJson.JsonError e)
        {
            return new SearchValidation(null, null, null, JsonInvalid(e.Msg, e.Pos));
        }

        // JSON null → missing（input null）
        if (parsed is PythonJson.PyNull)
        {
            return new SearchValidation(null, null, null, MissingBody());
        }

        if (parsed is not PythonJson.PyDict obj)
        {
            return new SearchValidation(null, null, null, ModelAttributesType(parsed));
        }

        // 字段校验：query 必填 str；key/sessionId 可选 str
        var errors = new List<PythonJson.PyValue>();
        string? query = null, key = null, sessionId = null;
        var hasQuery = false;

        foreach (var kv in obj.Items)
        {
            switch (kv.Key)
            {
                case "query":
                    hasQuery = true;
                    if (kv.Value is PythonJson.PyStr s)
                    {
                        query = s.Value;
                    }
                    else
                    {
                        errors.Add(StringType("query", kv.Value));
                    }

                    break;
                case "key":
                    if (kv.Value is PythonJson.PyStr ks)
                    {
                        key = ks.Value;
                    }
                    else if (kv.Value is not PythonJson.PyNull)
                    {
                        errors.Add(StringType("key", kv.Value));
                    }

                    break;
                case "sessionId":
                    if (kv.Value is PythonJson.PyStr ss)
                    {
                        sessionId = ss.Value;
                    }
                    else if (kv.Value is not PythonJson.PyNull)
                    {
                        errors.Add(StringType("sessionId", kv.Value));
                    }

                    break;
            }
        }

        if (!hasQuery)
        {
            errors.Insert(0, new PythonJson.PyDict(new()
            {
                new("type", new PythonJson.PyStr("missing")),
                new("loc", Loc("body", "query")),
                new("msg", new PythonJson.PyStr("Field required")),
                new("input", obj),
            }));
        }

        if (errors.Count > 0)
        {
            return new SearchValidation(null, null, null, new PythonJson.PyDict(new()
            {
                new("detail", new PythonJson.PyList(errors)),
            }));
        }

        return new SearchValidation(query, key, sessionId, null);
    }

    /// <summary>application/(json|*+json) 或空 content-type 才按 JSON 解析（FastAPI 同款判定）。</summary>
    public static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return true;
        }

        // email.message 语义：先按 ';' 取主段，再按 '/' 分 type/subtype（不做 subtype 内空白折叠）
        var semi = contentType.IndexOf(';');
        var main = (semi >= 0 ? contentType[..semi] : contentType);
        var slash = main.IndexOf('/');
        if (slash < 0)
        {
            return false;
        }

        var maintype = main[..slash].Trim().ToLowerInvariant();
        var subtype = main[(slash + 1)..].Trim().ToLowerInvariant();
        return maintype == "application" && (subtype == "json" || subtype.EndsWith("+json", StringComparison.Ordinal));
    }

    private static PythonJson.PyDict Detail(string message) => new()
    {
        new("detail", new PythonJson.PyStr(message)),
    };

    private static PythonJson.PyDict MissingBody() => new()
    {
        new("detail", new PythonJson.PyList(new()
        {
            new PythonJson.PyDict(new()
            {
                new("type", new PythonJson.PyStr("missing")),
                new("loc", Loc("body")),
                new("msg", new PythonJson.PyStr("Field required")),
                new("input", PythonJson.PyNull.Instance),
            }),
        })),
    };

    private static PythonJson.PyDict ModelAttributesType(PythonJson.PyValue input) => new()
    {
        new("detail", new PythonJson.PyList(new()
        {
            new PythonJson.PyDict(new()
            {
                new("type", new PythonJson.PyStr("model_attributes_type")),
                new("loc", Loc("body")),
                new("msg", new PythonJson.PyStr("Input should be a valid dictionary or object to extract fields from")),
                new("input", input),
            }),
        })),
    };

    private static PythonJson.PyDict StringType(string field, PythonJson.PyValue input) => new()
    {
        new("type", new PythonJson.PyStr("string_type")),
        new("loc", Loc("body", field)),
        new("msg", new PythonJson.PyStr("Input should be a valid string")),
        new("input", input),
    };

    private static PythonJson.PyDict JsonInvalid(string msg, int pos) => new()
    {
        new("detail", new PythonJson.PyList(new()
        {
            new PythonJson.PyDict(new()
            {
                new("type", new PythonJson.PyStr("json_invalid")),
                new("loc", new PythonJson.PyList(new()
                {
                    new PythonJson.PyStr("body"),
                    new PythonJson.PyInt(pos.ToString()),
                })),
                new("msg", new PythonJson.PyStr("JSON decode error")),
                new("input", new PythonJson.PyDict(new())),
                new("ctx", new PythonJson.PyDict(new() { new("error", new PythonJson.PyStr(msg)) })),
            }),
        })),
    };

    private static PythonJson.PyList Loc(params string[] parts)
    {
        var items = new List<PythonJson.PyValue>();
        foreach (var p in parts)
        {
            items.Add(new PythonJson.PyStr(p));
        }

        return new PythonJson.PyList(items);
    }

    private static PythonJson.PyStr GetStr(PythonJson.PyDict d, string key) => GetStrOrNull(d, key) is { } s ? new PythonJson.PyStr(s) : new PythonJson.PyStr("");

    private static string? GetStrOrNull(PythonJson.PyDict d, string key)
    {
        foreach (var kv in d.Items)
        {
            if (kv.Key == key && kv.Value is PythonJson.PyStr s)
            {
                return s.Value;
            }
        }

        return null;
    }

    private static PythonJson.PyDict? GetDict(PythonJson.PyValue root, string key)
    {
        if (root is not PythonJson.PyDict d)
        {
            return null;
        }

        foreach (var kv in d.Items)
        {
            if (kv.Key == key && kv.Value is PythonJson.PyDict inner)
            {
                return inner;
            }
        }

        return null;
    }

    // ── /api/calls ──────────────────────────────────────────────────────────

    private static async Task<OutResponse> CallsAsync(InRequest req, DemoServerLogic.Config cfg, Upstream upstream, CancellationToken ct)
    {
        // 只转发到 env 钉定的 SPEECH_TO_SPEECH_URL（复刻 server.py：不 honor 客户端目标，防 SSRF）
        if (cfg.SpeechToSpeechUrl.Length == 0)
        {
            return Json(404, Detail("Not found."));
        }

        var url = DemoServerLogic.WebRtcCallsUrl(cfg.SpeechToSpeechUrl);
        UpstreamResult resp;
        try
        {
            resp = await upstream.PostAsync(url, req.Body, "application/sdp", ct);
        }
        catch (HttpRequestException)
        {
            return Json(502, Detail("Speech service unreachable."));
        }

        return new OutResponse(resp.Status, resp.ContentType ?? "application/sdp", resp.Body, resp.Location);
    }

    // ── vendor 资产 ─────────────────────────────────────────────────────────

    private static OutResponse VendorBundle(string demoRoot)
    {
        var candidates = new[]
        {
            Path.Combine(demoRoot, "vendor", "openai-realtime-agents.umd.js"),
            Path.Combine(demoRoot, "node_modules", "@openai", "agents-realtime", "dist", "bundle", "openai-realtime-agents.umd.js"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        return path is null
            ? Json(503, Detail("Run npm ci in demo/"))
            : FileOut(path, "text/javascript");
    }

    private static OutResponse LocalVadAsset(string demoRoot, string filename)
    {
        if (!LocalVadAssets.TryGetValue(filename, out var asset))
        {
            return Json(404, Detail("Not Found"));
        }

        var candidates = new[]
        {
            Path.Combine(demoRoot, "vendor", "local-vad", filename),
            Path.Combine(demoRoot, "node_modules", asset.Package, "dist", filename),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        return path is null
            ? Json(503, Detail("Run npm ci in demo/"))
            : FileOut(path, asset.MediaType);
    }

    // ── 静态文件 ────────────────────────────────────────────────────────────

    private static OutResponse StaticFile(string demoRoot, string requestPath, bool isHead)
    {
        if (string.IsNullOrEmpty(demoRoot))
        {
            return Json(404, Detail("Not Found"));
        }

        // 规范化并防穿越（PhysicalFileProvider 同口径）
        var rel = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(demoRoot, rel));
        }
        catch (Exception)
        {
            return Json(404, Detail("Not Found"));
        }

        var rootFull = Path.GetFullPath(demoRoot);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            return Json(404, Detail("Not Found"));
        }

        if (Directory.Exists(full))
        {
            var index = Path.Combine(full, "index.html");
            if (File.Exists(index))
            {
                if (!requestPath.EndsWith('/'))
                {
                    // 目录 URL 重定向补尾斜杠（Starlette StaticFiles html=True 同款）
                    return new OutResponse(307, null, Array.Empty<byte>(), requestPath + "/");
                }

                return FileOut(index, GuessContentType("index.html"), isHead);
            }
        }
        else if (File.Exists(full))
        {
            return FileOut(full, GuessContentType(full), isHead);
        }

        // html=True：404.html 优先于 JSON 404
        var notFound = Path.Combine(rootFull, "404.html");
        if (File.Exists(notFound))
        {
            return FileOut(notFound, GuessContentType("404.html"), isHead, status: 404);
        }

        return Json(404, Detail("Not Found"));
    }

    /// <summary>
    /// Python <c>mimetypes.guess_type</c> + Starlette FileResponse 的 charset 规则：
    /// <c>text/*</c> 追加 <c>; charset=utf-8</c>；未知扩展按 <c>text/plain</c>。
    /// 表取自参照机 Python 3.13 的 mimetypes（与 server.py 可观察行为对齐）。
    /// </summary>
    public static string GuessContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var media = ext switch
        {
            ".js" => "application/javascript",
            ".mjs" or ".cjs" or ".map" or ".ts" or ".onnx" or ".woff2" or ".ttf" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".json" => "application/json",
            ".py" => "text/x-python",
            ".wasm" => "application/wasm",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".csv" => "application/vnd.ms-excel",
            _ => "text/plain",
        };

        return media.StartsWith("text/", StringComparison.Ordinal)
            ? media + "; charset=utf-8"
            : media;
    }

    private static OutResponse FileOut(string path, string contentType, bool isHead = false, int status = 200)
    {
        var bytes = isHead ? Array.Empty<byte>() : File.ReadAllBytes(path);
        return new OutResponse(status, contentType, bytes);
    }

    // ── 序列化出口 ──────────────────────────────────────────────────────────

    private static OutResponse Json(int status, PythonJson.PyValue body)
        => JsonAllowNan(status, body, serverErrorOnNan: false);

    private static OutResponse JsonAllowNan(int status, PythonJson.PyValue body, bool serverErrorOnNan)
    {
        try
        {
            var text = PythonJson.Serialize(body, allowNan: false);
            return new OutResponse(status, "application/json", Encoding.UTF8.GetBytes(text));
        }
        catch (Exception) when (serverErrorOnNan)
        {
            // 上游 json.dumps(allow_nan=False) 对 NaN input 抛 ValueError → 500 纯文本
            return new OutResponse(500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Internal Server Error"));
        }
    }
}

/// <summary>JsonNode → PyValue，数字保留原文以区分 int/float。</summary>
public static class JsonNodePy
{
    public static PythonJson.PyValue ToPy(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return PythonJson.PyNull.Instance;
            case JsonArray arr:
                return new PythonJson.PyList(arr.Select(ToPy).ToList());
            case JsonObject obj:
            {
                var items = new List<KeyValuePair<string, PythonJson.PyValue>>();
                foreach (var kv in obj)
                {
                    items.Add(new KeyValuePair<string, PythonJson.PyValue>(kv.Key, ToPy(kv.Value)));
                }

                return new PythonJson.PyDict(items);
            }

            default:
            {
                var raw = node.ToJsonString();
                if (raw == "null")
                {
                    return PythonJson.PyNull.Instance;
                }

                if (raw == "true")
                {
                    return new PythonJson.PyBool(true);
                }

                if (raw == "false")
                {
                    return new PythonJson.PyBool(false);
                }

                if (raw.StartsWith('"'))
                {
                    return new PythonJson.PyStr(node.GetValue<string>());
                }

                // 数字：保留往返文本，按有无小数点/指数区分 int/float
                if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
                {
                    return new PythonJson.PyFloat(node.GetValue<double>());
                }

                return new PythonJson.PyInt(raw);
            }
        }
    }
}
