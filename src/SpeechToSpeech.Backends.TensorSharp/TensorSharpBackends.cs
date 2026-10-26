using SpeechToSpeech.Core.Backends;

namespace SpeechToSpeech.Backends.TensorSharp;

/// <summary>TensorSharp 后端注册入口。</summary>
public static class TensorSharpBackends
{
    public static void Register()
    {
        // 上游把本地方案叫 transformers；C# 侧同一位置的实现是 TensorSharp，
        // 两个名字都指向这一个后端，避免用户迁移时改命令行。
        BackendRegistry.RegisterLlm("transformers", context => new TensorSharpLanguageModel(context.Llm));
        BackendRegistry.RegisterLlm("tensorsharp", context => new TensorSharpLanguageModel(context.Llm));
    }
}
