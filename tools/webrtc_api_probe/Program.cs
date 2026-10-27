using System.Reflection;
foreach (var m in typeof(Concentus.Structs.OpusDecoder).GetConstructors())
    Console.WriteLine($"DEC.ctor({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
foreach (var m in typeof(Concentus.Structs.OpusEncoder).GetConstructors())
    Console.WriteLine($"ENC.ctor({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
