using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Backends.ManySpeech;

/// <summary>
/// ManySpeech 后端注册入口。
///
/// 由 CLI 在启动时调用一次。注册名与上游 <c>--stt</c> / 上游 VAD 的取值对齐，
/// 使同一套命令行参数在 Python 与 C# 两侧含义一致。
/// </summary>
public static class ManySpeechBackends
{
    public static void Register()
    {
        BackendRegistry.RegisterVad("silero", context => new SileroVadBackend(context.Vad));
        BackendRegistry.RegisterVad("silero-vad", context => new SileroVadBackend(context.Vad));

        BackendRegistry.RegisterStt("whisper", context => new WhisperSttBackend(context.Stt));
        BackendRegistry.RegisterStt("paraformer", context => new ParaformerSttBackend(context.Stt, senseVoice: false));
        BackendRegistry.RegisterStt("sense-voice", context => new ParaformerSttBackend(context.Stt, senseVoice: true));
    }
}
