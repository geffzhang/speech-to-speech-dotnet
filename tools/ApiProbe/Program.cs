using System;
using System.Linq;
using System.Reflection;

// 传 --self 只导出本实现的公开表面；默认导出参考依赖包的公开表面。
bool selfOnly = args.Contains("--self");

string[] targets = selfOnly
    ?
    [
        "SpeechToSpeech.Core",
        "SpeechToSpeech.Realtime",
        "SpeechToSpeech.Backends.Lingua",
        "SpeechToSpeech.Backends.ManySpeech",
        "SpeechToSpeech.Backends.TensorSharp",
        "SpeechToSpeech.Backends.QwenTts",
        "SpeechToSpeech.Backends.OpenAiCompatible",
    ]
    :
    [
        "ManySpeech.SileroVad",
        "ManySpeech.WhisperAsr",
        "ManySpeech.AliParaformerAsr",
        "TensorSharp.Chat",
        "ElBruno.QwenTTS.Core",
    ];

foreach (var name in targets)
{
    Assembly asm;
    try
    {
        asm = Assembly.Load(name);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"### {name}: LOAD FAILED {ex.GetType().Name} {ex.Message}");
        continue;
    }

    Console.WriteLine($"### {name}");
    Type[] types;
    try
    {
        types = asm.GetExportedTypes();
    }
    catch (ReflectionTypeLoadException ex)
    {
        types = ex.Types.Where(t => t is not null).ToArray()!;
    }

    foreach (var t in types.Where(t => (t.IsClass || t.IsInterface) && t.IsPublic).OrderBy(t => t.FullName))
    {
        if (t.Name.Contains("Attribute") || t.Name.Contains("Exception"))
        {
            continue;
        }

        Console.WriteLine($"  {t.FullName}");
        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            Console.WriteLine($"    .ctor({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
        }

        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
        {
            Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
        }
    }
}
