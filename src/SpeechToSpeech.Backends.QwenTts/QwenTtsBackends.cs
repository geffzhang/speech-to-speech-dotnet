using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Backends.QwenTts;

/// <summary>Qwen3-TTS 后端注册入口。</summary>
public static class QwenTtsBackends
{
    public static void Register()
    {
        BackendRegistry.RegisterTts("qwen3", context => new QwenTtsBackend(context.Tts));
    }
}
