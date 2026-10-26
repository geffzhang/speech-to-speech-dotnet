using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Backends.OpenAiCompatible;

/// <summary>OpenAI 兼容后端的公共 HTTP 处理。</summary>
internal static class Http
{
    public static HttpClient CreateClient(string? baseUrl, string? apiKey)
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            var normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
            client.BaseAddress = new Uri(normalized);
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return client;
    }
}

/// <summary>
/// OpenAI 兼容 <c>/v1/audio/transcriptions</c> 后端。
///
/// 对应上游 <c>--stt openai</c>。这条路径的价值不在"调用远程"，
/// 而在于它让 C# 侧能接住任何兼容服务（含本地 vLLM / whisper.cpp server），
/// 从而在缺少 C# 运行时的模型上有退路。
/// </summary>
public sealed class OpenAiSttBackend : ISttBackend
{
    private readonly SttOptions _options;
    private readonly HttpClient _client;

    public OpenAiSttBackend(SttOptions options)
    {
        _options = options;
        _client = Http.CreateClient(options.BaseUrl ?? "https://api.openai.com", options.ApiKey);
    }

    public string Name => "openai";

    public IEnumerable<PipelineMessage> Transcribe(VadAudio audio, CancellationToken cancellationToken)
    {
        var (text, error) = Request(audio, cancellationToken);

        if (error is not null)
        {
            yield return new TranscriptionFailure
            {
                Message = error,
                TurnId = audio.TurnId,
                TurnRevision = audio.TurnRevision,
                SpeechStoppedAtSeconds = audio.SpeechEndAtSeconds,
            };
            yield break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            yield return new TranscriptionFailure
            {
                Message = "no speech recognized",
                TurnId = audio.TurnId,
                TurnRevision = audio.TurnRevision,
                SpeechStoppedAtSeconds = audio.SpeechEndAtSeconds,
            };
            yield break;
        }

        yield return new Transcription
        {
            Text = text.Trim(),
            LanguageCode = _options.Language,
            TurnId = audio.TurnId,
            TurnRevision = audio.TurnRevision,
            SpeechStoppedAtSeconds = audio.SpeechEndAtSeconds,
        };
    }

    /// <summary>执行上传并解析结果；失败以字符串返回，绝不抛给调用方。</summary>
    private (string? Text, string? Error) Request(VadAudio audio, CancellationToken cancellationToken)
    {
        var wav = WavWriter.FromFloat32(audio.Audio, audio.SampleRate);
        var model = _options.ModelName ?? "whisper-1";

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("json"), "response_format");

        if (!string.IsNullOrWhiteSpace(_options.Language))
        {
            form.Add(new StringContent(_options.Language), "language");
        }

        try
        {
            using var response = _client
                .PostAsync("v1/audio/transcriptions", form, cancellationToken)
                .GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"transcription endpoint returned {(int)response.StatusCode}: {Trim(body)}");
            }

            var parsed = JsonNode.Parse(body);
            return (parsed?["text"]?.GetValue<string>(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    private static string Trim(string body)
        => body.Length <= 300 ? body : body[..300] + "…";

    public void Dispose() => _client.Dispose();
}
