using System.Runtime.Versioning;
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

    /// <summary>建麦克风。调用方负责 <c>Dispose</c>。</summary>
    IWaveIn CreateMicrophone(int sampleRate, int bufferMilliseconds);

    /// <summary>建扬声器。<paramref name="latencyMilliseconds"/> 是期望播放延迟，后端可忽略。</summary>
    IWavePlayer CreateSpeaker(int latencyMilliseconds);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsAudioDevices : IAudioDeviceSet
{
    public string Name => "WASAPI / WinMM (Windows)";

    public IWaveIn CreateMicrophone(int sampleRate, int bufferMilliseconds) =>
        new WaveIn
        {
            WaveFormat = new WaveFormat(sampleRate, 16, 1),
            BufferMilliseconds = bufferMilliseconds,
            NumberOfBuffers = 3,
        };

    public IWavePlayer CreateSpeaker(int latencyMilliseconds) =>
        new WasapiPlayerBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(latencyMilliseconds)
            .Build();
}

[SupportedOSPlatform("linux")]
internal sealed class AlsaAudioDevices : IAudioDeviceSet
{
    public string Name => "ALSA (Linux)";

    // AlsaIn 的缓冲由 PeriodFrames × Periods 在 StartRecording 时决定，没有毫秒级开关，
    // 所以 bufferMilliseconds 在这里不参与；保留参数是为了让两侧签名一致。
    public IWaveIn CreateMicrophone(int sampleRate, int bufferMilliseconds) =>
        new AlsaIn { WaveFormat = new WaveFormat(sampleRate, 16, 1) };

    public IWavePlayer CreateSpeaker(int latencyMilliseconds) => new AlsaOut();
}
