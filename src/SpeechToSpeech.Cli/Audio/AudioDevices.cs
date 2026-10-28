using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.Alsa;

namespace SpeechToSpeech.Cli.Audio;

/// <summary>
/// 音频设备工厂。把「哪套系统 API」关在这一个文件里，<see cref="AudioClient"/> 只认
/// <see cref="IWaveIn"/> / <see cref="IWavePlayer"/> 两个抽象。
///
/// 三个平台对应三个后端：
/// <list type="bullet">
/// <item>Windows —— WASAPI 播放（<c>WasapiPlayer</c>，可控延迟）+ WinMM 录制（<c>WaveIn</c>）；</item>
/// <item>Linux —— ALSA（<c>AlsaIn</c> / <c>AlsaOut</c>），需 <c>libasound.so.2</c> 运行库；</item>
/// <item>macOS —— PortAudio（<see cref="PortAudioAudioDevices"/>，PortAudioSharp2 1.0.6 正式版）。
/// 上游 <c>sounddevice</c> 底层就是 PortAudio，macOS 路径与上游同引擎；见差异账 E001。</item>
/// </list>
///
/// 注意录制侧为什么不用 <c>WasapiRecorder</c>：它虽然更现代，但**没有实现 <see cref="IWaveIn"/>**
/// （只有 <c>StartRecording</c>/<c>StopRecording</c> 同形方法），无法与 ALSA 侧共用抽象。
/// 播放侧 <c>WasapiPlayer</c> 实现了 <see cref="IWavePlayer"/>，所以能换。
/// </summary>
internal static class AudioDevices
{
    /// <summary>按当前操作系统选后端。不支持的平台在调用 <c>talk</c> 时立刻报错，而不是静默无声。</summary>
    public static IAudioDeviceSet Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsAudioDevices();
        }

        if (OperatingSystem.IsLinux())
        {
            return new AlsaAudioDevices();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new PortAudioAudioDevices();
        }

        throw new PlatformNotSupportedException(
            "音频设备 I/O 支持 Windows、Linux 与 macOS。" +
            "macOS 走 PortAudio（PortAudioSharp2），需要对应 RID 的原生库随应用发布。");
    }
}

/// <summary>一对输入/输出设备。创建即代表该平台可用。</summary>
internal interface IAudioDeviceSet
{
    /// <summary>人类可读的后端名，用于启动时打印。</summary>
    string Name { get; }

    /// <summary>建麦克风。调用方负责 <c>Dispose</c>。
    /// <paramref name="deviceIndex"/> 是上游 sounddevice 语义的设备索引；null = 平台默认。</summary>
    IWaveIn CreateMicrophone(int sampleRate, int bufferMilliseconds, int? deviceIndex);

    /// <summary>建扬声器。<paramref name="latencyMilliseconds"/> 是期望播放延迟，后端可忽略。</summary>
    IWavePlayer CreateSpeaker(int latencyMilliseconds, int? deviceIndex);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsAudioDevices : IAudioDeviceSet
{
    public string Name => "WASAPI / WinMM (Windows)";

    // WinMM 的设备索引与 sounddevice 的 MME 编号一致（都是 waveIn 枚举序）。
    public IWaveIn CreateMicrophone(int sampleRate, int bufferMilliseconds, int? deviceIndex) =>
        new WaveIn
        {
            WaveFormat = new WaveFormat(sampleRate, 16, 1),
            BufferMilliseconds = bufferMilliseconds,
            NumberOfBuffers = 3,
            DeviceNumber = deviceIndex ?? 0,
        };

    // WASAPI 没有数字索引语义，按枚举序对应 sounddevice 的 WASAPI 编号；
    // 越界在这里报出来，而不是静默回落默认设备（D034 纪律）。
    public IWavePlayer CreateSpeaker(int latencyMilliseconds, int? deviceIndex)
    {
        var builder = new WasapiPlayerBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(latencyMilliseconds);

        if (deviceIndex is { } index)
        {
            var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            if (index < 0 || index >= endpoints.Count)
            {
                throw new ArgumentException(
                    $"输出设备索引 {index} 超出范围（共 {endpoints.Count} 个活动渲染设备）。");
            }

            builder = builder.WithDevice(endpoints[index]);
        }

        return builder.Build();
    }
}

[SupportedOSPlatform("linux")]
internal sealed class AlsaAudioDevices : IAudioDeviceSet
{
    public string Name => "ALSA (Linux)";

    // AlsaIn 的缓冲由 PeriodFrames × Periods 在 StartRecording 时决定，没有毫秒级开关，
    // 所以 bufferMilliseconds 在这里不参与；保留参数是为了让两侧签名一致。
    //
    // ALSA 没有数字索引语义；sounddevice 的索引对应 PortAudio 设备表。这里按 ALSA
    // 惯例映射为 plughw:<n>,0（带格式转换层的hw设备），与上游 --input-device 的意图最接近。
    public IWaveIn CreateMicrophone(int sampleRate, int bufferMilliseconds, int? deviceIndex)
    {
        var format = new WaveFormat(sampleRate, 16, 1);
        return deviceIndex is { } index
            ? new AlsaIn($"plughw:{index},0") { WaveFormat = format }
            : new AlsaIn { WaveFormat = format };
    }

    public IWavePlayer CreateSpeaker(int latencyMilliseconds, int? deviceIndex) =>
        deviceIndex is { } index ? new AlsaOut($"plughw:{index},0") : new AlsaOut();
}
