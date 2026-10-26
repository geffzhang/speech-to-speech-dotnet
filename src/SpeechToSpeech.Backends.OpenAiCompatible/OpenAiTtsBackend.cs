using System.Text;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Audio;
using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Backends.OpenAiCompatible;

/// <summary>
/// OpenAI 兼容 <c>/v1/audio/speech</c> 后端。
///
/// 对应上游 <c>--tts openai</c>。返回的 WAV/PCM 会被转成本项目统一的
/// int16 单声道 PCM 块，下游不必关心提供方给的封装格式。
/// </summary>
public sealed class OpenAiTtsBackend : ITtsBackend
{
    private readonly TtsOptions _options;
    private readonly HttpClient _client;

    public OpenAiTtsBackend(TtsOptions options)
    {
        _options = options;
        _client = Http.CreateClient(options.BaseUrl ?? "https://api.openai.com", options.ApiKey);
    }

    public string Name => "openai";

    public int SampleRate => _options.SampleRate;

    public IEnumerable<byte[]> Synthesize(TtsRequest request, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = _options.ModelName ?? "gpt-4o-mini-tts",
            ["input"] = request.Text,
            ["voice"] = request.Speaker ?? _options.Voice ?? "alloy",
            ["response_format"] = "pcm",
            ["sample_rate"] = SampleRate,
        };

        byte[] body;
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };

            using var response = _client.Send(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);
            body = response.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                var message = Encoding.UTF8.GetString(body);
                TranscriptLog.Error($"speech endpoint returned {(int)response.StatusCode}", new Exception(message));
                yield break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TranscriptLog.Error("speech request failed", ex);
            yield break;
        }

        var pcm = StripWavHeaderIfPresent(body);
        foreach (var chunk in Split(pcm, 4096))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// 提供方可能返回裸 PCM 或 WAV。带 RIFF 头就按 <c>data</c> 块长度切出载荷，
    /// 否则原样返回——两者都不做重采样，采样率由请求里的 <c>sample_rate</c> 保证。
    /// </summary>
    private static byte[] StripWavHeaderIfPresent(byte[] body)
    {
        if (body.Length < 44
            || body[0] != 'R' || body[1] != 'I' || body[2] != 'F' || body[3] != 'F')
        {
            return body;
        }

        var offset = 12;
        while (offset + 8 <= body.Length)
        {
            var chunkId = Encoding.ASCII.GetString(body, offset, 4);
            var chunkSize = BitConverter.ToInt32(body, offset + 4);
            if (chunkId == "data")
            {
                var start = offset + 8;
                var length = Math.Min(chunkSize, body.Length - start);
                var payload = new byte[length];
                Array.Copy(body, start, payload, 0, length);
                return payload;
            }

            offset += 8 + chunkSize + (chunkSize % 2);
        }

        return body;
    }

    private static IEnumerable<byte[]> Split(byte[] data, int size)
    {
        for (var offset = 0; offset < data.Length; offset += size)
        {
            var length = Math.Min(size, data.Length - offset);
            var chunk = new byte[length];
            Array.Copy(data, offset, chunk, 0, length);
            yield return chunk;
        }
    }

    public void Dispose() => _client.Dispose();
}
