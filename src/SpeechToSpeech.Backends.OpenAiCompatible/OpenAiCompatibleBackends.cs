using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Backends.OpenAiCompatible;

/// <summary>OpenAI 兼容后端注册入口。</summary>
public static class OpenAiCompatibleBackends
{
    public static void Register()
    {
        BackendRegistry.RegisterStt("openai", context => new OpenAiSttBackend(context.Stt));
        BackendRegistry.RegisterLlm("chat-completions", context => new OpenAiChatLanguageModel(context.Llm));
        BackendRegistry.RegisterTts("openai", context => new OpenAiTtsBackend(context.Tts));
    }
}
