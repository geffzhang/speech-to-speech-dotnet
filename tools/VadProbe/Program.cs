using SpeechToSpeech.Backends.ManySpeech;
using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Runtime;

// 把一段 16 kHz 单声道 wav 按 512 样本的块喂进 SileroVadBackend，打印每个帧事件。
//
// 存在的理由：`serve` 跑起来只看到 speech_started 就没了，有两种可能——后端没切出语音段，
// 或者切出来了但管线没往下传。把后端单独拉出来跑一遍，一眼就能分清是哪一种。
// 用法：dotnet run --project tools/VadProbe -- <wav> <modelDir>

if (args.Length < 2)
{
    Console.Error.WriteLine("用法：VadProbe <wav> <vad_model_dir>");
    return 2;
}

var samples = ReadWav16kMono(args[0]);
Console.WriteLine($"样本 {samples.Length} 个（{samples.Length / 16_000.0:F2}s），模型目录 {args[1]}");

if (args.Contains("--offline"))
{
    // 整段识别模式：模型自己能不能切出语音段，与我们的分块方式无关。
    // 它不成立就说明是模型/配置问题；它成立而在线模式没结果，问题在我们的分块或流用法。
    var modelPath = Path.Combine(args[1], "silero_vad.onnx");
    var configPath = Path.Combine(args[1], "vad.yaml");
    using var offline = new ManySpeech.SileroVad.OfflineVad(
        modelFilePath: modelPath,
        configFilePath: File.Exists(configPath) ? configPath : string.Empty,
        threshold: 0.5f,
        sampleRate: 16_000,
        threadsNum: 1,
        isDebug: false);
    var offlineStream = offline.CreateOfflineStream();
    offlineStream.AddSamples(samples);
    var result = offline.GetResult(offlineStream);
    Console.WriteLine($"offline 段数 {result.Waveforms.Count}");
    foreach (var segment in result.Segments)
    {
        Console.WriteLine($"  {segment.Start / 16}ms → {segment.End / 16}ms");
    }

    return result.Waveforms.Count > 0 ? 0 : 1;
}

if (args.Contains("--online-renew"))
{
    // 假设验证：每吐出一段就换一条新流，看第二段能不能出来。
    // 依据：OnlineStream.IsFinished(isEndpoint:true) 在全静音缓冲上会走 PushIndexEnd()，
    // 那是「流结束」的收尾路径，跑过一次之后这条流就再没产出新段。
    var modelPath = Path.Combine(args[1], "silero_vad.onnx");
    var configPath = Path.Combine(args[1], "vad.yaml");
    using var online = new ManySpeech.SileroVad.OnlineVad(
        modelFilePath: modelPath,
        configFilePath: File.Exists(configPath) ? configPath : string.Empty,
        threshold: 0.5f,
        sampleRate: 16_000,
        threadsNum: 1,
        isDebug: false);
    var stream = online.CreateOnlineStream();
    var chunkSize = AudioUtil.VadChunkSize;
    var segments = 0;

    for (var offset = 0; offset < samples.Length; offset += chunkSize)
    {
        var length = Math.Min(chunkSize, samples.Length - offset);
        var buffer = new float[chunkSize];
        Array.Copy(samples, offset, buffer, 0, length);
        stream.AddSamples(buffer);

        _ = stream.IsFinished(isEndpoint: true);
        var result = online.GetResult(stream);
        if (result.Waveforms.Count > 0)
        {
            segments++;
            var segment = result.Segments[0];
            Console.WriteLine($"+{(offset + length) / 16_000.0,7:F3}s  段#{segments} {segment.Start / 16}ms → {segment.End / 16}ms");
            stream = online.CreateOnlineStream();
        }
    }

    Console.WriteLine($"累计段数 {segments}");
    return segments > 1 ? 0 : 1;
}

if (args.Contains("--online-raw"))
{
    // 绕过我们的适配层，直接按包的口径喂流：每次 AddSamples 后问一次 IsFinished 与 GetResults，
    // 打印每一块的结果规模。用来分清「库不吐段」与「我们漏读了段」。
    var modelPath = Path.Combine(args[1], "silero_vad.onnx");
    var configPath = Path.Combine(args[1], "vad.yaml");
    using var online = new ManySpeech.SileroVad.OnlineVad(
        modelFilePath: modelPath,
        configFilePath: File.Exists(configPath) ? configPath : string.Empty,
        threshold: 0.5f,
        sampleRate: 16_000,
        threadsNum: 1,
        isDebug: false);
    var stream = online.CreateOnlineStream();
    var chunkSize = AudioUtil.VadChunkSize;
    var total = 0;
    var seen = 0;

    for (var offset = 0; offset < samples.Length; offset += chunkSize)
    {
        var length = Math.Min(chunkSize, samples.Length - offset);
        var buffer = new float[chunkSize];
        Array.Copy(samples, offset, buffer, 0, length);
        stream.AddSamples(buffer);
        total++;

        var skipEndpoint = Environment.GetEnvironmentVariable("VADPROBE_SKIP_ISFINISHED") == "1";
        var finished = skipEndpoint ? false : stream.IsFinished(isEndpoint: false);
        var endpoint = skipEndpoint ? false : stream.IsFinished(isEndpoint: true);
        var result = online.GetResult(stream);
        if (result.Waveforms.Count > 0 || finished || endpoint)
        {
            Console.WriteLine(
                $"+{(offset + length) / 16_000.0,7:F3}s  块#{total} finished={finished} endpoint={endpoint}"
                + $" 段={result.Waveforms.Count}");
        }

        seen += result.Waveforms.Count;
    }

    Console.WriteLine($"累计段数 {seen}");
    return seen > 0 ? 0 : 1;
}

var backend = new SileroVadBackend(new VadOptions { ModelPath = args[1], SampleRate = 16_000 });
var config = new RuntimeConfig();
var chunk = AudioUtil.VadChunkSize;
var started = 0;
var stopped = 0;

for (var offset = 0; offset < samples.Length; offset += chunk)
{
    var length = Math.Min(chunk, samples.Length - offset);
    var buffer = new float[chunk];
    Array.Copy(samples, offset, buffer, 0, length);

    foreach (var frame in backend.Push(buffer, config))
    {
        var at = (offset + length) / 16_000.0;
        switch (frame.Kind)
        {
            case VadFrameKind.SpeechStarted:
                started++;
                Console.WriteLine($"+{at,7:F3}s  speech_started");
                break;
            case VadFrameKind.SpeechStopped:
                stopped++;
                Console.WriteLine(
                    $"+{at,7:F3}s  speech_stopped  音频 {frame.Audio?.Length ?? 0} 样本"
                    + $" start={frame.AudioStartMs}ms end={frame.AudioEndMs}ms"
                    + $" 有声尾={frame.SpeechEndSample}");
                break;
        }
    }
}

Console.WriteLine($"合计 speech_started={started} speech_stopped={stopped}");
return stopped > 0 ? 0 : 1;

static float[] ReadWav16kMono(string path)
{
    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream);

    var riff = new string(reader.ReadChars(4));
    if (riff != "RIFF")
    {
        throw new InvalidDataException("不是 RIFF wav");
    }

    reader.ReadInt32();
    reader.ReadChars(4);
    var format = 0;
    var channels = 0;
    var rate = 0;
    var bits = 0;

    while (stream.Position < stream.Length - 8)
    {
        var id = new string(reader.ReadChars(4));
        var size = reader.ReadInt32();
        if (id == "fmt ")
        {
            format = reader.ReadInt16();
            channels = reader.ReadInt16();
            rate = reader.ReadInt32();
            reader.ReadInt32();
            reader.ReadInt16();
            bits = reader.ReadInt16();
            stream.Position += Math.Max(0, size - 16);
        }
        else if (id == "data")
        {
            if (format != 1 || channels != 1 || bits != 16)
            {
                throw new NotSupportedException($"只支持单声道 16 bit PCM（format={format} ch={channels} bits={bits}）");
            }

            if (rate != 16_000)
            {
                throw new NotSupportedException($"只支持 16 kHz（实际 {rate}）");
            }

            var count = size / 2;
            var buffer = reader.ReadBytes(size);
            var samples = new float[count];
            for (var i = 0; i < count; i++)
            {
                samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
            }

            return samples;
        }
        else
        {
            stream.Position += size;
        }
    }

    throw new InvalidDataException("wav 里没有 data 段");
}
