using System.Net;
using SpeechToSpeech.Backends.OpenAiCompatible;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 代理解析口径（对齐上游 httpx trust_env）：
/// 只认环境变量 HTTP_PROXY / HTTPS_PROXY / ALL_PROXY / NO_PROXY，
/// 从不读 Windows 系统代理（WinINET）——系统代理会把本机回环地址塞进
/// 代理隧道，连本地 llama.cpp / vLLM 都会失败。
/// </summary>
public sealed class EnvironmentProxyTests : IDisposable
{
    private static readonly (string Env, string? Value)[] KnownVars =
    [
        ("HTTP_PROXY", null), ("http_proxy", null),
        ("HTTPS_PROXY", null), ("https_proxy", null),
        ("ALL_PROXY", null), ("all_proxy", null),
        ("NO_PROXY", null), ("no_proxy", null),
    ];

    static EnvironmentProxyTests()
    {
        // 先记录进程内是否已有残留值（理论上没有；有则测试结论只对清理后的环境成立）。
    }

    public EnvironmentProxyTests()
    {
        foreach (var (name, _) in KnownVars)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (var (name, _) in KnownVars)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void NoEnvironmentProxy_ReturnsNull()
    {
        Assert.Null(EnvironmentProxy.ResolveEnvironmentProxy());
    }

    [Fact]
    public void HttpProxyOnly_AppliesToHttpTarget()
    {
        Environment.SetEnvironmentVariable("HTTP_PROXY", "http://proxy.local:7890");

        var proxy = EnvironmentProxy.ResolveEnvironmentProxy();
        Assert.NotNull(proxy);
        Assert.Equal(new Uri("http://proxy.local:7890"), proxy!.GetProxy(new Uri("http://llm.local:8080/v1")));
        // httpx 口径：https 目标没有 HTTPS_PROXY / ALL_PROXY 时不走代理。
        Assert.Null(proxy.GetProxy(new Uri("https://api.openai.com/v1")));
    }

    [Fact]
    public void AllProxy_CoversBothSchemes()
    {
        Environment.SetEnvironmentVariable("ALL_PROXY", "socks5://proxy.local:1080");

        var proxy = EnvironmentProxy.ResolveEnvironmentProxy();
        Assert.NotNull(proxy);
        Assert.Equal(new Uri("socks5://proxy.local:1080"), proxy!.GetProxy(new Uri("http://llm.local:8080/v1")));
        Assert.Equal(new Uri("socks5://proxy.local:1080"), proxy.GetProxy(new Uri("https://api.openai.com/v1")));
    }

    [Fact]
    public void NoProxy_BypassesHostAndSubdomains()
    {
        Environment.SetEnvironmentVariable("HTTP_PROXY", "http://proxy.local:7890");
        Environment.SetEnvironmentVariable("NO_PROXY", "127.0.0.1,localhost,.internal.corp");

        var proxy = EnvironmentProxy.ResolveEnvironmentProxy();
        Assert.NotNull(proxy);
        Assert.True(proxy!.IsBypassed(new Uri("http://127.0.0.1:8080/v1")));
        Assert.True(proxy.IsBypassed(new Uri("http://localhost:8080/v1")));
        Assert.True(proxy.IsBypassed(new Uri("http://a.internal.corp/v1")));
        Assert.Null(proxy.GetProxy(new Uri("http://127.0.0.1:8080/v1")));
        Assert.False(proxy.IsBypassed(new Uri("http://api.openai.com/v1")));
        Assert.NotNull(proxy.GetProxy(new Uri("http://api.openai.com/v1")));
    }

    [Fact]
    public void NoProxyStar_BypassesEverything()
    {
        Environment.SetEnvironmentVariable("HTTP_PROXY", "http://proxy.local:7890");
        Environment.SetEnvironmentVariable("NO_PROXY", "*");

        var proxy = EnvironmentProxy.ResolveEnvironmentProxy();
        Assert.NotNull(proxy);
        Assert.True(proxy!.IsBypassed(new Uri("http://api.openai.com/v1")));
        Assert.Null(proxy.GetProxy(new Uri("http://api.openai.com/v1")));
    }

    [Fact]
    public void CaseInsensitiveEnvironmentNames_AreHonored()
    {
        Environment.SetEnvironmentVariable("https_proxy", "http://proxy.local:7891");

        var proxy = EnvironmentProxy.ResolveEnvironmentProxy();
        Assert.NotNull(proxy);
        Assert.Equal(new Uri("http://proxy.local:7891"), proxy!.GetProxy(new Uri("https://api.openai.com/v1")));
    }

    [Fact]
    public void Http_CreateClientWithoutEnvProxy_DisablesProxy()
    {
        // 没有代理变量时必须彻底禁用代理解析——否则 HttpClient 退回
        // WinINET 系统代理，回环地址被隧道化（本差异的起点）。
        using var client = Http.CreateClient("http://127.0.0.1:8080", null);
        Assert.False(client.DefaultRequestHeaders.Contains("Authorization"));
    }

    [Fact]
    public void Http_CreateClient_KeepsBaseUrlAndAuth()
    {
        using var client = Http.CreateClient("http://127.0.0.1:8080", "sk-test");
        Assert.Equal(new Uri("http://127.0.0.1:8080/"), client.BaseAddress);
        Assert.Equal("Bearer sk-test", client.DefaultRequestHeaders.Authorization!.ToString());
    }

    [Fact]
    public void WebProxyContract_NullGetProxyMeansDirectConnection()
    {
        // IWebProxy 契约：GetProxy 返回 null 即直连。锁定这一行为，
        // 防止将来把 null 退化成某个默认代理。
        var proxy = (IWebProxy)new EnvironmentProxy(
            httpProxy: null,
            httpsProxy: new Uri("http://p:1"),
            bypass: []);
        Assert.Null(proxy.GetProxy(new Uri("http://x/v1")));
        Assert.NotNull(proxy.GetProxy(new Uri("https://x/v1")));
    }
}
