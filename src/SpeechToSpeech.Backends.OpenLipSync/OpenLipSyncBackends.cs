using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Backends.OpenLipSync;

/// <summary>口型提取后端注册入口（与其它后端程序集同形，由组合根在启动时调用）。</summary>
public static class OpenLipSyncBackends
{
    public static void Register()
    {
        // 管线音频率取 TTS 输出率：本构建各 TTS 后端按自己的采样率出 PCM，
        // 不做二次重采样，所以这就是窗口时长与偏移计算的基准。
        BackendRegistry.RegisterViseme(
            context => OpenLipSyncVisemeExtractor.Load(
                context.Stv.ModelDirectory!,
                context.Tts.SampleRate,
                context.Stv.ConfigPath));
    }
}
