using PortAudioSharp;

namespace SpeechToSpeech.Tests;

/// <summary>
/// PortAudio 绑定冒烟（差异账 E001：macOS 音频后端走 PortAudioSharp2 1.0.6）。
///
/// 与设备无关：只验证①RID 原生库（portaudio.dll / libportaudio.so / dylib）在测试宿主里能被
/// 解析加载；②Pa_Initialize/Pa_Terminate 的引用计数语义与适配器的逐设备配对用法兼容；
/// ③设备枚举 API 不炸（CI/容器里设备数可以为 0）。
/// 真实设备的开流行为属 macOS 真机验证，不在单测覆盖范围。
/// </summary>
public class PortAudioSmokeTests
{
    [Fact]
    public void NativeLibraryLoadsAndInitializes()
    {
        PortAudio.Initialize();
        try
        {
            Assert.True(PortAudio.Version > 0, "Pa_GetVersion 应返回正的版本号");
            Assert.False(string.IsNullOrWhiteSpace(PortAudio.VersionInfo.versionText));
        }
        finally
        {
            PortAudio.Terminate();
        }
    }

    [Fact]
    public void NestedInitializeTerminatePairsAreSupported()
    {
        // PortAudio 官方语义：Pa_Initialize 可配对重叠（文档明写 "may overlap"）。
        // 适配器按「每个设备一对」使用，这里验证两层嵌套不会把引擎关死。
        PortAudio.Initialize();
        PortAudio.Initialize();
        PortAudio.Terminate();

        // 第一层还活着：枚举 API 必须仍然可用（未初始化时返回负错误码）。
        Assert.True(PortAudio.DeviceCount >= 0, "嵌套配对后 DeviceCount 应仍可枚举");

        PortAudio.Terminate();
    }

    [Fact]
    public void DeviceEnumerationDoesNotThrow()
    {
        PortAudio.Initialize();
        try
        {
            var count = PortAudio.DeviceCount;
            Assert.True(count >= 0);

            // 无设备环境下默认设备就是 paNoDevice(-1)，只断言不抛异常。
            var input = PortAudio.DefaultInputDevice;
            var output = PortAudio.DefaultOutputDevice;
            Assert.True(input >= -1);
            Assert.True(output >= -1);
        }
        finally
        {
            PortAudio.Terminate();
        }
    }
}
