using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.FileProviders;
using SpeechToSpeech.Demo.Web;

// ── 配置 ────────────────────────────────────────────────────────────────────
// 复刻 server.py 顶部：SPEECH_TO_SPEECH_URL 优先，设置后 LB 逻辑禁用。
var cfg = DemoServerLogic.ReadConfig(name => Environment.GetEnvironmentVariable(name));

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

// 解析 demo 前端目录（index.html/main.js 所在），供静态文件与 vendor 资产回退使用。
var demoRoot = ResolveDemoRoot(app.Environment);

// 上游 HTTP 代理（calls 转发 / search 代理），走 IHttpClientFactory。
var upstream = new DemoHttpPipeline.Upstream(async (url, body, contentType, ct) =>
{
    using var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
    http.Timeout = TimeSpan.FromSeconds(15);
    using var content = new ByteArrayContent(body);
    content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
    var resp = await http.PostAsync(url, content, ct);
    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
    return new DemoHttpPipeline.UpstreamResult(
        (int)resp.StatusCode,
        resp.Content.Headers.ContentType?.ToString(),
        bytes,
        resp.Headers.Location?.ToString());
});

// 单一入口：路由分派 + 静态文件 + JSON 404/405 全在 pipeline 内，
// 与 Starlette「API 路由方法精确匹配 → 其余落 StaticFiles」同构。
app.Run(async ctx =>
{
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var h in ctx.Request.Headers)
    {
        headers[h.Key] = h.Value.ToString();
    }

    var req = new DemoHttpPipeline.InRequest(
        ctx.Request.Method,
        ctx.Request.Path.Value ?? "/",
        ctx.Request.ContentType,
        ms.ToArray(),
        headers);

    var resp = await DemoHttpPipeline.HandleAsync(req, cfg, demoRoot, upstream, ctx.RequestAborted);

    ctx.Response.StatusCode = resp.Status;
    if (resp.ContentType is not null)
    {
        ctx.Response.ContentType = resp.ContentType;
    }

    if (resp.Location is not null)
    {
        ctx.Response.Headers.Location = resp.Location;
    }

    await ctx.Response.Body.WriteAsync(resp.Body, ctx.RequestAborted);
});

app.Run();

/// <summary>定位 demo 前端目录：优先 DEMO_ROOT 环境变量；否则从 BaseDirectory 逐级向上找含 index.html 的 demo/。</summary>
static string ResolveDemoRoot(IHostEnvironment env)
{
    var overridePath = Environment.GetEnvironmentVariable("DEMO_ROOT");
    if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath))
    {
        return Path.GetFullPath(overridePath);
    }

    var root = Path.GetFullPath(env.ContentRootPath);
    var start = Path.GetFullPath(AppContext.BaseDirectory);
    foreach (var baseDir in new[] { root, start })
    {
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 7 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "demo");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.html")))
            {
                return Path.GetFullPath(candidate);
            }

            dir = dir.Parent;
        }
    }

    throw new InvalidOperationException(
        "找不到 demo 前端目录。请用 --demo-root 或环境变量 DEMO_ROOT 指向含 index.html 的 demo/ 目录。");
}
