using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.Wave;
using PortAudioSharp;

namespace SpeechToSpeech.Cli.Audio;

/// <summary>
/// PortAudio 音频设备后端——macOS 接入（差异账 E001）。
///
/// 上游 <c>talk</c> 的 <c>sounddevice</c> 底层就是 PortAudio，所以这是**同引擎对齐**而不是近似替代：
/// 设备枚举、默认设备选择、块粒度语义（<c>blocksize</c> ↔ <c>framesPerBuffer</c>）与上游一致。
/// Windows/Linux 仍走 NAudio 后端（已实现且已验），只有 macOS 落到本类。
///
/// 包来源：<c>PortAudioSharp2 1.0.6</c>（NuGet 正式版，绑定层为 csukuangfj/PortAudioSharp2，
/// Apache-2.0），原生库由配套的 <c>org.k2fsa.portaudio.runtime.*</c> 包按 RID 提供
/// （osx-x64 / osx-arm64 / linux-x64 / linux-arm64 / win-x64）。
///
/// 两个适配器都实现了 NAudio 的 <see cref="IWaveIn"/> / <see cref="IWavePlayer"/>，
/// <see cref="AudioClient"/> 不感知后端差异（与 WASAPI/ALSA 同一套抽象，见 AudioDevices.cs 注释）。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class PortAudioAudioDevices : IAudioDeviceSet
{
    public string Name => "PortAudio (macOS)";

    // 索引与上游 sounddevice（底层同为 PortAudio）的设备编号同表同义。
    public IWaveIn CreateMicrophone(int sampleRate, int bufferMilliseconds, int? deviceIndex) =>
        new PortAudioWaveIn(sampleRate, bufferMilliseconds, deviceIndex);

    public IWavePlayer CreateSpeaker(int latencyMilliseconds, int? deviceIndex) =>
        new PortAudioWavePlayer(latencyMilliseconds, deviceIndex);
}

/// <summary>
/// PortAudio 录制侧 → <see cref="IWaveIn"/>。
///
/// 回调只做拷贝 + 入事件，与 AudioClient 的约束一致（驱动线程上不做重采样、不碰 socket）。
/// PortAudio 的 <c>Pa_Initialize</c>/<c>Pa_Terminate</c> 自身引用计数（配对可重叠），每个适配器
/// 打开时 Initialize、Dispose 时 Terminate 即可；Initialize 失败则不调 Terminate（官方要求）。
/// </summary>
internal sealed class PortAudioWaveIn : IWaveIn
{
    private readonly uint _framesPerBuffer;
    private readonly int? _deviceIndex;
    private WaveFormat _waveFormat;
    private PortAudioSharp.Stream? _stream;
    private bool _recording;
    private bool _initialized;
    private bool _disposed;

    public PortAudioWaveIn(int sampleRate, int bufferMilliseconds, int? deviceIndex)
    {
        _waveFormat = new WaveFormat(sampleRate, 16, 1);
        _framesPerBuffer = (uint)Math.Max(1, sampleRate * bufferMilliseconds / 1000);
        _deviceIndex = deviceIndex;
    }

    /// <summary>NAudio 的 WaveFormat 是可写属性；录制开始后改格式会被拒绝。</summary>
    public WaveFormat WaveFormat
    {
        get => _waveFormat;
        set
        {
            if (_recording)
            {
                throw new InvalidOperationException("录制中不能修改 WaveFormat。");
            }

            _waveFormat = value;
        }
    }

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PortAudioWaveIn));
        }

        if (_recording)
        {
            return;
        }

        PortAudio.Initialize();
        _initialized = true;
        try
        {
            if (_stream is null)
            {
                _stream = OpenInputStream();
            }

            _stream.Start();
            _recording = true;
        }
        catch
        {
            CloseStream();
            PortAudio.Terminate();
            _initialized = false;
            throw;
        }
    }

    private PortAudioSharp.Stream OpenInputStream()
    {
        var device = _deviceIndex ?? PortAudio.DefaultInputDevice;
        if (device == PortAudio.NoDevice || device < 0 || device >= PortAudio.DeviceCount)
        {
            throw new InvalidOperationException(
                _deviceIndex is { } index
                    ? $"PortAudio：输入设备索引 {index} 不存在（共 {PortAudio.DeviceCount} 个设备）。"
                    : "PortAudio：没有可用的默认输入设备。");
        }

        var deviceInfo = PortAudio.GetDeviceInfo(device);
        var parameters = new StreamParameters
        {
            device = device,
            channelCount = _waveFormat.Channels,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = deviceInfo.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        return new PortAudioSharp.Stream(
            parameters,
            null,
            _waveFormat.SampleRate,
            _framesPerBuffer,
            StreamFlags.NoFlag,
            RecordCallback,
            userData: this);
    }

    private StreamCallbackResult RecordCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        var bytes = (int)(frameCount * (uint)_waveFormat.BlockAlign);
        if (input == IntPtr.Zero || bytes <= 0)
        {
            return StreamCallbackResult.Continue;
        }

        var buffer = new byte[bytes];
        Marshal.Copy(input, buffer, 0, bytes);
        DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
        return StreamCallbackResult.Continue;
    }

    public void StopRecording()
    {
        if (!_recording)
        {
            return;
        }

        _recording = false;
        _stream?.Stop();
        RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }

    private void CloseStream()
    {
        if (_stream is null)
        {
            return;
        }

        try
        {
            _stream.Abort();
        }
        catch (PortAudioException)
        {
            // 关闭路径上的错误不再放大（设备可能已被拔掉）
        }

        _stream.Dispose();
        _stream = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_recording)
        {
            StopRecording();
        }

        CloseStream();
        if (_initialized)
        {
            PortAudio.Terminate();
            _initialized = false;
        }

        _disposed = true;
    }
}

/// <summary>
/// PortAudio 播放侧 → <see cref="IWavePlayer"/>。pull 模式：回调线程直接从
/// <see cref="IWaveProvider"/> 读数据（<c>BufferedWaveProvider</c> 内部有锁，欠载自动补零，
/// 满足 PortAudio「回调必须填满整个输出缓冲」的约定）。
/// 流在 <see cref="Init"/> 时打开，失败立刻暴露（AudioClient 在构造期就调 Init）。
/// </summary>
internal sealed class PortAudioWavePlayer : IWavePlayer
{
    private readonly double _suggestedLatencySeconds;
    private readonly int? _deviceIndex;
    private IWaveProvider? _provider;
    private PortAudioSharp.Stream? _stream;
    private float _volume = 1.0f;
    private PlaybackState _state = PlaybackState.Stopped;
    private bool _initialized;
    private bool _disposed;

    public PortAudioWavePlayer(int latencyMilliseconds, int? deviceIndex)
    {
        _suggestedLatencySeconds = latencyMilliseconds / 1000.0;
        _deviceIndex = deviceIndex;
    }

    /// <summary>软件音量：PortAudio 不暴露系统音量，在回调里对 int16 样本做缩放。</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public PlaybackState PlaybackState => _state;

    /// <summary>Init 之后由 provider 的格式决定。</summary>
    public WaveFormat OutputWaveFormat => _provider?.WaveFormat
        ?? throw new InvalidOperationException("IWavePlayer 尚未 Init。");

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public void Init(IWaveProvider waveProvider)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PortAudioWavePlayer));
        }

        if (_provider is not null)
        {
            throw new InvalidOperationException("IWavePlayer 已初始化，不能重复 Init。");
        }

        PortAudio.Initialize();
        _initialized = true;
        try
        {
            _provider = waveProvider;
            _stream = OpenOutputStream(waveProvider.WaveFormat);
        }
        catch
        {
            _provider = null;
            PortAudio.Terminate();
            _initialized = false;
            throw;
        }
    }

    private PortAudioSharp.Stream OpenOutputStream(WaveFormat format)
    {
        var device = _deviceIndex ?? PortAudio.DefaultOutputDevice;
        if (device == PortAudio.NoDevice || device < 0 || device >= PortAudio.DeviceCount)
        {
            throw new InvalidOperationException(
                _deviceIndex is { } index
                    ? $"PortAudio：输出设备索引 {index} 不存在（共 {PortAudio.DeviceCount} 个设备）。"
                    : "PortAudio：没有可用的默认输出设备。");
        }

        var deviceInfo = PortAudio.GetDeviceInfo(device);
        var parameters = new StreamParameters
        {
            device = device,
            channelCount = format.Channels,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = Math.Max(_suggestedLatencySeconds, deviceInfo.defaultLowOutputLatency),
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        // framesPerBuffer 用 Unspecified：回调块长随宿主变化，延迟最优（PortAudio 文档建议）。
        return new PortAudioSharp.Stream(
            null,
            parameters,
            format.SampleRate,
            PortAudio.FramesPerBufferUnspecified,
            StreamFlags.NoFlag,
            PlaybackCallback,
            userData: this);
    }

    private StreamCallbackResult PlaybackCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        var provider = _provider;
        if (output == IntPtr.Zero || provider is null)
        {
            return StreamCallbackResult.Continue;
        }

        var bytes = (int)(frameCount * (uint)provider.WaveFormat.BlockAlign);
        var buffer = new byte[bytes];
        // NAudio 3.x：IWaveProvider.Read 是 Span 签名（老的三参 byte[]/offset/count 已移除）。
        var read = provider.Read(buffer.AsSpan(0, bytes));

        if (_volume < 1f)
        {
            ApplyVolume(buffer, read);
        }

        Marshal.Copy(buffer, 0, output, bytes);
        return StreamCallbackResult.Continue;
    }

    /// <summary>对已读入的 int16 样本就地缩放；欠载补零由 BufferedWaveProvider 完成，这里不碰。</summary>
    private void ApplyVolume(byte[] buffer, int count)
    {
        for (var offset = 0; offset + 1 < count; offset += 2)
        {
            var sample = (short)(buffer[offset] | (buffer[offset + 1] << 8));
            var scaled = (short)Math.Round(sample * _volume);
            buffer[offset] = (byte)scaled;
            buffer[offset + 1] = (byte)(scaled >> 8);
        }
    }

    public void Play()
    {
        if (_disposed || _stream is null)
        {
            return;
        }

        if (_state != PlaybackState.Playing)
        {
            _stream.Start();
            _state = PlaybackState.Playing;
        }
    }

    public void Pause()
    {
        if (_stream is null)
        {
            return;
        }

        // PortAudio 的 Stop 等待缓冲排空；暂停语义下用 Abort 立即停，残余缓冲丢弃。
        _stream.Abort();
        _state = PlaybackState.Paused;
    }

    public void Stop()
    {
        if (_state == PlaybackState.Stopped)
        {
            return;
        }

        // 立即停：打断语义（AudioClient 在 speech_started 时清缓冲后靠下一轮响应续播）。
        _stream?.Abort();
        _state = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_state == PlaybackState.Playing)
        {
            Stop();
        }

        if (_stream is not null)
        {
            try
            {
                _stream.Dispose();
            }
            catch (PortAudioException)
            {
                // 关闭路径上的错误不再放大
            }

            _stream = null;
        }

        if (_initialized)
        {
            PortAudio.Terminate();
            _initialized = false;
        }

        _provider = null;
        _disposed = true;
    }
}
