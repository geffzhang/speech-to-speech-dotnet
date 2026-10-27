using System.Collections.Concurrent;
using NAudio.Dsp;
using SpeechToSpeech.Core.Audio;

namespace SpeechToSpeech.Backends.OpenAiCompatible.StreamingStt;

/// <summary>
/// 流式转写的连接抽象（上游 <c>_WebSocket</c> Protocol：send / recv(timeout) / close）。
///
/// worker 是单线程阻塞模型：Receive 返回 null 表示超时（继续轮询），抛异常表示
/// 连接死亡（触发 <c>fail_connection</c>）。这与上游 <c>websockets.sync</c> 的
/// TimeoutError / ConnectionClosed 语义一一对应。
/// </summary>
public interface IStreamingSocket : IDisposable
{
    void Send(string message);

    /// <summary>阻塞读一条消息；超时返回 null，连接错误或对端关闭抛异常。</summary>
    string? Receive(TimeSpan timeout);
}

/// <summary>
/// 连接工厂（上游 <c>ConnectFactory</c>）。测试注入进程内 fake 服务器，产品用
/// <see cref="ClientWebSocketStreamingSocket.DefaultFactory"/>。
/// </summary>
public delegate IStreamingSocket StreamingConnectFactory(
    string url,
    IReadOnlyDictionary<string, string> headers,
    TimeSpan openTimeout);

/// <summary>上游 <c>StreamingTranscriptionError</c>：setup/连接阶段的致命错误。</summary>
public sealed class StreamingTranscriptionException(string message) : Exception(message);

/// <summary>
/// 默认连接：WebSocket（上游 <c>websockets.sync.client.connect</c> 的对应物）。
/// </summary>
public static class ClientWebSocketStreamingSocket
{
    public static readonly StreamingConnectFactory DefaultFactory = Connect;

    public static IStreamingSocket Connect(string url, IReadOnlyDictionary<string, string> headers,
        TimeSpan openTimeout)
        => new WebSocketImpl(url, headers, openTimeout);

    private sealed class WebSocketImpl : IStreamingSocket
    {
        private readonly System.Net.WebSockets.ClientWebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly string _url;
        private readonly BlockingCollection<string> _incoming = new(new ConcurrentQueue<string>());

        public WebSocketImpl(string url, IReadOnlyDictionary<string, string> headers, TimeSpan openTimeout)
        {
            _url = url;
            _socket = new System.Net.WebSockets.ClientWebSocket();
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            foreach (var (key, value) in headers)
            {
                _socket.Options.SetRequestHeader(key, value);
            }

            _socket.ConnectAsync(new Uri(_url), new CancellationTokenSource(openTimeout).Token)
                .GetAwaiter().GetResult();

            // 后台接收泵：ClientWebSocket 取消中的 Receive 会 abort 连接，
            // 所以超时轮询不能直接落在 ReceiveAsync 上——泵持续收，
            // Receive(timeout) 只是从队列 TryTake（语义与 websockets.sync 一致）。
            _ = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (!_socket.CloseStatus.HasValue)
                {
                    var result = await _socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.EndOfMessage)
                    {
                        _incoming.Add(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count));
                        continue;
                    }

                    // 多帧消息（目前只收服务端事件，不会大）：拼接完整帧再入队。
                    using var stream = new System.IO.MemoryStream(buffer, 0, result.Count);
                    while (!result.EndOfMessage)
                    {
                        result = await _socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                        stream.Write(buffer, 0, result.Count);
                    }

                    _incoming.Add(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
                }
            }
            catch (Exception)
            {
                // 连接死亡：complete adding 让 Receive 抛出，worker 走 fail_connection。
            }
            finally
            {
                _incoming.CompleteAdding();
            }
        }

        public void Send(string message)
        {
            _sendLock.Wait();
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(message);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                _socket.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text,
                    endOfMessage: true, cts.Token).GetAwaiter().GetResult();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public string? Receive(TimeSpan timeout)
        {
            if (_incoming.TryTake(out var message, timeout))
            {
                return message;
            }

            if (_incoming.IsCompleted)
            {
                throw new IOException("streaming transcription socket closed");
            }

            return null;
        }

        public void Dispose()
        {
            try
            {
                _socket.Abort();
            }
            catch (ObjectDisposedException)
            {
            }

            _socket.Dispose();
            _incoming.Dispose();
            _sendLock.Dispose();
        }
    }
}

/// <summary>
/// 有状态的说话级重采样器（上游 <c>_StreamingPCMResampler</c>：soxr 流式、
/// <c>finish_utterance</c> 冲尾、<c>reset</c> 换流）。
///
/// 内核用 <c>NAudio.Dsp.WdlResampler</c> 的窗口化 sinc 插值（64 tap / 32 相位，
/// 参考 Aurio 项目对同一托管内核的用法，差异账 C030）：纯托管、无原生依赖，
/// 抗混叠/抗镜像能力远优于线性插值，语音带内表现接近上游 soxr HQ。
/// 流式语义：输入驱动模式逐块推入；冲尾走 WDL 的「实际输入少于
/// ResamplePrepare 请求量即零填充冲尾 + 裁剪」机制，沿用保守产出纪律——
/// 分块喂入 + 尾部冲刷 == 整块一次喂入（WDL fracpos 逐调用锚定带来浮点
/// 末位级差异 ≤1e-11，与 soxr 流式语义同级；总长精确收敛到 输入长×率比）。
/// </summary>
public sealed class StreamingResampler
{
    private const int SincSize = 64;
    private const int SincInterpSize = 32;

    private readonly int _fromRate;
    private readonly int _toRate;
    private readonly WdlResampler _resampler = new();
    private long _totalSource;
    private long _produced;

    public StreamingResampler(int fromRate, int toRate)
    {
        _fromRate = fromRate;
        _toRate = toRate;
        if (fromRate == toRate)
        {
            return;
        }

        // sinc 模式（filtercnt 归零、IIR 关闭）：SetMode 里 sinc 优先于 interp/filtercnt。
        _resampler.SetMode(interp: false, filtercnt: 0, sinc: true, SincSize, SincInterpSize);
        _resampler.SetFeedMode(wantInputDriven: true);
        _resampler.SetRates(fromRate, toRate);
    }

    /// <summary>说话期间逐块推入；返回本块产出的输出样本（可能为空）。</summary>
    public float[] Push(ReadOnlySpan<float> audio)
    {
        if (audio.Length == 0)
        {
            return [];
        }

        _totalSource += audio.Length;
        if (_fromRate == _toRate)
        {
            return audio.ToArray();
        }

        var output = Convert(audio, flush: false);
        _produced += output.Length;
        return output;
    }

    /// <summary>说话收口：冲出滤波尾并复位流（上游 soxr last=True + 新建流）。</summary>
    public float[] FinishUtterance()
    {
        if (_fromRate == _toRate || _totalSource == 0)
        {
            Reset();
            return [];
        }

        var output = Convert(ReadOnlySpan<float>.Empty, flush: true);
        _produced += output.Length;
        Reset();
        return output;
    }

    /// <summary>丢弃/取消：复位流状态，源偏移从零重算。</summary>
    public void Reset()
    {
        _resampler.Reset();
        _totalSource = 0;
        _produced = 0;
    }

    /// <summary>
    /// 逐块转换。请求量取「理论目标 + 滤波余量」；WDL 只产出输入可支撑的数量，
    /// 多要的部分由内核按实际可用输入截断（冲尾时零填充部分会被裁剪）。
    /// </summary>
    private float[] Convert(ReadOnlySpan<float> audio, bool flush)
    {
        var fed = audio.Length;
        var target = (long)Math.Ceiling(_totalSource * (double)_toRate / _fromRate);
        var margin = flush ? 2 * SincSize : SincSize;
        var want = (int)Math.Min(Math.Max(target + margin - _produced, 0), int.MaxValue - 1L);
        if (want == 0)
        {
            return [];
        }

        // NAudio.Core 3.3.0 的 Span API：inbuffer 已指向内部写入位；输出从索引 0 写。
        var prepared = _resampler.ResamplePrepare(flush ? want : fed, 1, out var inBuffer);
        for (var i = 0; i < prepared && i < fed; i++)
        {
            inBuffer[i] = audio[i];
        }

        var outBuffer = new float[want];
        var produced = _resampler.ResampleOut(outBuffer, fed, want, 1);
        if (produced == want)
        {
            return outBuffer;
        }

        var result = new float[produced];
        Array.Copy(outBuffer, result, produced);
        return result;
    }
}
