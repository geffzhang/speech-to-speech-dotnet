namespace SpeechToSpeech.Core.Pipeline;

/// <summary>一轮响应的延迟采样。</summary>
public sealed class TurnLatencySample
{
    public required string TurnId { get; init; }
    public double? SpeechStoppedAtSeconds { get; init; }
    public double? TranscriptionCompletedAtSeconds { get; set; }
    public double? ResponseCreatedAtSeconds { get; set; }
    public double? FirstAudioAtSeconds { get; set; }
    public double? ResponseDoneAtSeconds { get; set; }

    public double? TranscriptionLatencySeconds => Diff(SpeechStoppedAtSeconds, TranscriptionCompletedAtSeconds);

    public double? ResponseLatencySeconds => Diff(SpeechStoppedAtSeconds, FirstAudioAtSeconds);

    public double? TotalLatencySeconds => Diff(SpeechStoppedAtSeconds, ResponseDoneAtSeconds);

    private static double? Diff(double? from, double? to)
        => from is null || to is null ? null : Math.Round(to.Value - from.Value, 3);
}

/// <summary>
/// 按轮次记录"语音结束 → 转写完成 → 首段音频 → 响应结束"的延迟。
///
/// 上游同模块还负责把这些数字暴露给 <c>/latency</c> 端点与 UI；C# 侧只保留存储与汇总，
/// HTTP 端点属于后续阶段。
/// </summary>
public sealed class TurnLatencyStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TurnLatencySample> _samples = new(StringComparer.Ordinal);
    private readonly int _capacity;

    public TurnLatencyStore(int capacity = 256)
    {
        _capacity = capacity;
    }

    public TurnLatencySample Start(string turnId, double? speechStoppedAtSeconds)
    {
        var sample = new TurnLatencySample
        {
            TurnId = turnId,
            SpeechStoppedAtSeconds = speechStoppedAtSeconds,
        };

        lock (_lock)
        {
            if (_samples.Count >= _capacity)
            {
                var oldest = _samples.Keys.FirstOrDefault();
                if (oldest is not null)
                {
                    _samples.Remove(oldest);
                }
            }

            _samples[turnId] = sample;
        }

        return sample;
    }

    public void MarkTranscriptionCompleted(string turnId)
        => Mutate(turnId, s => s.TranscriptionCompletedAtSeconds ??= Monotonic.NowSeconds());

    public void MarkResponseCreated(string turnId)
        => Mutate(turnId, s => s.ResponseCreatedAtSeconds ??= Monotonic.NowSeconds());

    public void MarkFirstAudio(string turnId)
        => Mutate(turnId, s => s.FirstAudioAtSeconds ??= Monotonic.NowSeconds());

    public void MarkResponseDone(string turnId)
        => Mutate(turnId, s => s.ResponseDoneAtSeconds = Monotonic.NowSeconds());

    public IReadOnlyList<TurnLatencySample> Recent(int count = 20)
    {
        lock (_lock)
        {
            return [.. _samples.Values.TakeLast(count)];
        }
    }

    private void Mutate(string turnId, Action<TurnLatencySample> mutate)
    {
        lock (_lock)
        {
            if (_samples.TryGetValue(turnId, out var sample))
            {
                mutate(sample);
            }
        }
    }
}
