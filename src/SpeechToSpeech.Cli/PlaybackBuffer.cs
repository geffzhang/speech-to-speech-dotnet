using NAudio.Wave;

namespace SpeechToSpeech.Cli;

/// <summary>
/// 线程安全的播放缓冲，语义逐条对齐上游 <c>PlaybackBuffer</c>
/// （api/openai_realtime/audio_client.py）：Realtime 接收循环往里 append，
/// 音频回调线程从 <see cref="Read"/> 拉取。
///
/// 上游语义（都保留）：
/// <list type="bullet">
/// <item><b>首包门槛</b>：攒够 <paramref name="startupBufferMs"/> 对应的字节数才开播；
/// 不足时回调补静音。唯一的例外是 <see cref="Finish"/>：短响应可以不等攒满直接播。</item>
/// <item><b>写入不足即回 idle</b>：一次 Read 拿不满就补零并复位 playing，
/// 下次从门槛判定重来——上游 <c>write</c> 同款行为。</item>
/// <item><b>活动窗口</b>：<see cref="Append"/> 后至少 150 ms 内 <see cref="IsActive"/> 为真
/// （防 block_mic 判定抖动），上限按音频时长外推。</item>
/// <item><b>清空时机</b>（由 <see cref="AudioClient"/> 决定）：speech_started、
/// response.done 且 status=cancelled、output_audio_buffer.cleared —— 本地打断立即静音。</item>
/// </list>
///
/// 内部是一段可扩容的字节数组（head/tail 双游标，头部读空后整体前移压缩），
/// 避免 List&lt;byte&gt;/MemoryStream 在读路径上的逐次拷贝。
/// </summary>
internal sealed class PlaybackBuffer : IWaveProvider
{
    /// <summary>上游 _PLAYBACK_START_BUFFER_MS。</summary>
    private const double PlaybackStartBufferMs = 0.0;

    private readonly object _lock = new();
    private readonly int _recvRate;
    private readonly int _startupBufferBytes;
    private byte[] _data = new byte[64 * 1024];
    private int _head;
    private int _tail;

    private long _activeUntilMs;
    private bool _playing;
    private bool _inputComplete;

    public PlaybackBuffer(int recvRate, double startupBufferMs = PlaybackStartBufferMs)
    {
        if (recvRate <= 0)
        {
            throw new ArgumentException("recvRate must be positive", nameof(recvRate));
        }

        if (startupBufferMs < 0 || double.IsInfinity(startupBufferMs) || double.IsNaN(startupBufferMs))
        {
            throw new ArgumentException("startupBufferMs must be a finite non-negative number", nameof(startupBufferMs));
        }

        _recvRate = recvRate;
        WaveFormat = new WaveFormat(recvRate, 16, 1);

        // int16 单声道：每采样 2 字节。对齐偶数字节，与上游相同。
        var bytes = (long)(recvRate * 2 * startupBufferMs / 1000);
        _startupBufferBytes = (int)(bytes - bytes % 2);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>当前缓冲的字节数（诊断与测试用）。</summary>
    public int BufferedBytes
    {
        get
        {
            lock (_lock)
            {
                return _tail - _head;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _activeUntilMs = 0;
            _head = _tail = 0;
            _playing = false;
            _inputComplete = false;
        }
    }

    public void Append(ReadOnlySpan<byte> audio)
    {
        if (audio.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            if (_inputComplete && _tail == _head)
            {
                _playing = false;
            }

            _inputComplete = false;
            EnsureCapacity(audio.Length);
            audio.CopyTo(_data.AsSpan(_tail));
            _tail += audio.Length;

            var spanMs = audio.Length * 1000.0 / (2.0 * _recvRate);
            _activeUntilMs = Environment.TickCount64 + (long)Math.Max(150, spanMs);
        }
    }

    /// <summary>放行一个已经收完的短响应——不必等首包攒满。</summary>
    public void Finish()
    {
        lock (_lock)
        {
            _inputComplete = true;
        }
    }

    public bool IsActive()
    {
        lock (_lock)
        {
            return _tail > _head || Environment.TickCount64 < _activeUntilMs;
        }
    }

    /// <summary>音频回调线程的取数入口。不足补零，与上游 <c>write</c> 同语义。
    /// NAudio 3.x：IWaveProvider.Read 是 Span 签名（三参 byte[] 重载已移除）。</summary>
    public int Read(Span<byte> buffer)
    {
        lock (_lock)
        {
            var count = buffer.Length;
            var available = _tail - _head;

            if (!_playing)
            {
                var ready = available >= _startupBufferBytes;
                if (!ready && !(_inputComplete && available > 0))
                {
                    buffer.Clear();
                    return count;
                }

                _playing = true;
            }

            var taken = Math.Min(count, available);
            if (taken > 0)
            {
                _data.AsSpan(_head, taken).CopyTo(buffer);
                _head += taken;
                if (_head == _tail)
                {
                    _head = _tail = 0;
                }
            }

            if (taken < count)
            {
                buffer[taken..].Clear();
                _playing = false;
            }

            if (_tail == _head && _inputComplete)
            {
                _playing = false;
            }

            return count;
        }
    }

    private void EnsureCapacity(int additional)
    {
        var used = _tail - _head;
        if (_tail + additional <= _data.Length)
        {
            return;
        }

        // 头部读走了一截就先压缩；仍不够再扩容（至少翻倍）。
        if (_head > 0)
        {
            Array.Copy(_data, _head, _data, 0, used);
            _head = 0;
            _tail = used;
        }

        if (_tail + additional <= _data.Length)
        {
            return;
        }

        var newSize = Math.Max(used + additional, _data.Length * 2);
        Array.Resize(ref _data, newSize);
    }
}
