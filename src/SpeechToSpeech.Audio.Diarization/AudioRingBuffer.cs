namespace SpeechToSpeech.Audio.Diarization;

/// <summary>
/// 按<b>绝对样本索引</b>寻址的定容环形缓冲，是流式分离唯一的音频状态。
///
/// 绝对寻址是时间轴能在淘汰后存活的原因：段、窗口、声纹区间都用「距会话起点的样本数」表达，
/// 丢掉旧音频改变的只是「还能不能重读」，从不改变「某个时刻是多少」。容量满了覆盖最旧的样本，
/// 所以无论会话多长，内存都被 <c>capacity</c> 钉住。
/// </summary>
internal sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private long _oldest; // 最旧可读样本的绝对索引
    private long _end;    // 最新样本之后一位的绝对索引

    public AudioRingBuffer(int capacitySamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacitySamples);
        _buffer = new float[capacitySamples];
    }

    /// <summary>最新写入样本之后一位的绝对索引。</summary>
    public long End => _end;

    /// <summary>最旧仍可读样本的绝对索引。</summary>
    public long Oldest => _oldest;

    /// <summary>当前保留的样本数（永不超过容量）。</summary>
    public long Count => _end - _oldest;

    /// <summary>容量，单位样本。</summary>
    public int Capacity => _buffer.Length;

    /// <summary>追加样本；缓冲满时淘汰最旧的。</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return;

        int cap = _buffer.Length;
        if (samples.Length >= cap)
        {
            // 一次写入就超过整个缓冲：只有尾部留得住，其余按定义已被覆盖。
            samples[^cap..].CopyTo(_buffer);
            _end += samples.Length;
            _oldest = _end - cap;
            return;
        }

        int startIndex = (int)(_end % cap);
        int first = Math.Min(samples.Length, cap - startIndex);
        samples[..first].CopyTo(_buffer.AsSpan(startIndex, first));
        if (first < samples.Length) samples[first..].CopyTo(_buffer);

        _end += samples.Length;
        if (_end - _oldest > cap) _oldest = _end - cap;
    }

    /// <summary>
    /// 把从 <paramref name="absoluteStart"/> 起的绝对区间拷进 <paramref name="destination"/>。
    /// 区间越过最新样本、或起点早于最旧保留样本时，那部分补零（调用方要的就是补零窗口）；
    /// 返回值是实际写入了多少真实样本。
    /// </summary>
    public int CopyTo(long absoluteStart, Span<float> destination)
    {
        destination.Clear();
        long from = Math.Max(absoluteStart, _oldest);
        long to = Math.Min(absoluteStart + destination.Length, _end);
        if (to <= from) return 0;

        int count = (int)(to - from);
        int offset = (int)(from - absoluteStart);
        int startIndex = (int)(from % _buffer.Length);
        int first = Math.Min(count, _buffer.Length - startIndex);
        _buffer.AsSpan(startIndex, first).CopyTo(destination.Slice(offset, first));
        if (count > first) _buffer.AsSpan(0, count - first).CopyTo(destination.Slice(offset + first, count - first));
        return count;
    }

    /// <summary>丢掉早于 <paramref name="absoluteIndex"/> 的一切（夹到当前保留范围内）。</summary>
    public void DiscardBefore(long absoluteIndex)
    {
        if (absoluteIndex > _oldest) _oldest = Math.Min(absoluteIndex, _end);
    }
}
