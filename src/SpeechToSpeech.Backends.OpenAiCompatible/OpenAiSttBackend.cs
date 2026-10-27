using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Backends.OpenAiCompatible;

/// <summary>OpenAI 兼容后端的公共 HTTP 处理。</summary>
internal static class Http
{
    public static HttpClient CreateClient(string? baseUrl, string? apiKey)
    {
        // 上游 httpx 只认环境变量代理（HTTP(S)_PROXY / ALL_PROXY / NO_PROXY），
        // 从不读 Windows 系统代理。HttpClient 默认却按 WinINET 解析系统代理——
        // 在挂了系统代理的机器上，连本机 llama.cpp / vLLM 的回环地址也会被塞进
        // 代理隧道（NO_PROXY 环境变量管不到它），直接 502。对齐 httpx 口径。
        var handler = new SocketsHttpHandler();
        var proxyUri = EnvironmentProxy.ResolveEnvironmentProxy();
        if (proxyUri is not null)
        {
            handler.Proxy = proxyUri;
        }
        else
        {
            handler.UseProxy = false;
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            var normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
            client.BaseAddress = new Uri(normalized);
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return client;
    }
}

/// <summary>
/// 只按环境变量解析代理（对齐 httpx trust_env 语义）。
/// <para>
/// httpx 的规则：HTTP_PROXY 作用于 http 目标、HTTPS_PROXY 作用于 https 目标、
/// ALL_PROXY 兜底；NO_PROXY 逗号分隔豁免表，条目按主机名匹配——点前缀（.example.com）
/// 与裸域名都按「该域及其子域」豁免，<c>*</c> 全豁免。未设置任何代理变量时
/// <see cref="ResolveEnvironmentProxy"/> 返回 null，调用方应彻底禁用代理解析。
/// </para>
/// </summary>
internal sealed class EnvironmentProxy : IWebProxy
{
    private readonly Uri? _httpProxy;
    private readonly Uri? _httpsProxy;
    private readonly string[] _bypass;

    internal EnvironmentProxy(Uri? httpProxy, Uri? httpsProxy, string[] bypass)
    {
        _httpProxy = httpProxy;
        _httpsProxy = httpsProxy;
        _bypass = bypass;
    }

    public ICredentials? Credentials { get; set; }

    /// <summary>按环境变量解析出代理；没有代理变量时返回 null。</summary>
    public static IWebProxy? ResolveEnvironmentProxy()
    {
        var http = GetProxyUri("HTTP_PROXY", "http_proxy");
        var https = GetProxyUri("HTTPS_PROXY", "https_proxy");
        var all = GetProxyUri("ALL_PROXY", "all_proxy");

        if (http is null && https is null && all is null)
        {
            return null;
        }

        var bypass = ParseBypassList(
            Environment.GetEnvironmentVariable("NO_PROXY")
            ?? Environment.GetEnvironmentVariable("no_proxy"));

        return new EnvironmentProxy(http ?? all, https ?? all, bypass);
    }

    private static Uri? GetProxyUri(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }

        return null;
    }

    private static string[] ParseBypassList(string? noProxy)
    {
        if (string.IsNullOrWhiteSpace(noProxy))
        {
            return [];
        }

        return noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public Uri? GetProxy(Uri destination) => IsBypassed(destination)
        ? null
        : destination.Scheme == Uri.UriSchemeHttps ? _httpsProxy : _httpProxy;

    public bool IsBypassed(Uri host)
    {
        var name = host.Host;
        foreach (var entry in _bypass)
        {
            if (entry == "*")
            {
                return true;
            }

            var bare = entry.StartsWith('.') ? entry[1..] : entry;
            // .example.com 或 example.com 都豁免该域及其子域（httpx 对点前缀的口径）。
            if (name.Equals(bare, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("." + bare, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// OpenAI 兼容 <c>/v1/audio/transcriptions</c> 后端。
///
/// 对应上游 <c>--stt openai</c>。这条路径的价值不在"调用远程"，
/// 而在于它让 C# 侧能接住任何兼容服务（含本地 vLLM / whisper.cpp server），
/// 从而在缺少 C# 运行时的模型上有退路。
/// </summary>
public sealed class OpenAiSttBackend : ISttBackend
{
    private readonly SttOptions _options;
    private readonly HttpClient _client;

    public OpenAiSttBackend(SttOptions options)
    {
        _options = options;
        _client = Http.CreateClient(options.BaseUrl ?? "https://api.openai.com", options.ApiKey);
    }

    public string Name => "openai";

    public IEnumerable<PipelineMessage> Transcribe(VadAudio audio, CancellationToken cancellationToken)
    {
        var (text, error) = Request(audio, cancellationToken);

        if (error is not null)
        {
            yield return new TranscriptionFailure
            {
                Message = error,
                TurnId = audio.TurnId,
                TurnRevision = audio.TurnRevision,
                SpeechStoppedAtSeconds = audio.SpeechEndAtSeconds,
            };
            yield break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            yield return new TranscriptionFailure
            {
                Message = "no speech recognized",
                TurnId = audio.TurnId,
                TurnRevision = audio.TurnRevision,
                SpeechStoppedAtSeconds = audio.SpeechEndAtSeconds,
            };
            yield break;
        }

        yield return new Transcription
        {
            Text = text.Trim(),
            LanguageCode = _options.Language,
            TurnId = audio.TurnId,
            TurnRevision = audio.TurnRevision,
            SpeechStoppedAtSeconds = audio.SpeechEndAtSeconds,
        };
    }

    /// <summary>执行上传并解析结果；失败以字符串返回，绝不抛给调用方。</summary>
    private (string? Text, string? Error) Request(VadAudio audio, CancellationToken cancellationToken)
    {
        var wav = WavWriter.FromFloat32(audio.Audio, audio.SampleRate);
        var model = _options.ModelName ?? "whisper-1";

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("json"), "response_format");

        if (!string.IsNullOrWhiteSpace(_options.Language))
        {
            form.Add(new StringContent(_options.Language), "language");
        }

        try
        {
            using var response = _client
                .PostAsync("v1/audio/transcriptions", form, cancellationToken)
                .GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"transcription endpoint returned {(int)response.StatusCode}: {Trim(body)}");
            }

            var parsed = JsonNode.Parse(body);
            return (parsed?["text"]?.GetValue<string>(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    private static string Trim(string body)
        => body.Length <= 300 ? body : body[..300] + "…";

    public void Dispose() => _client.Dispose();
}
