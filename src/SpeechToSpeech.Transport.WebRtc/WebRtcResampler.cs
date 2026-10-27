namespace SpeechToSpeech.Transport.WebRtc;

/// <summary>
/// 有状态的单声道重采样器（16 kHz 管线率 ↔ 48 kHz WebRTC 率）。
///
/// 与 C009 同口径：自实现而非引入音频库。线性插值在 16→48k 上采样里引入的
/// 镜像能量极小（源带宽上限 8 kHz），听感无损带宽。
///
/// 一致性保证：输出按<b>绝对输出游标</b>与源绝对位置对齐，且每块最后一个
/// 插值步保守延迟到下一块（它需要本块最后一个源样本作 lookahead）——
/// 因此「分块喂入的输出拼接」与「整块一次喂入」逐样本一致。
/// 跨块的插值样本用上一块末样本（<c>_lastSample</c>）补位。
/// </summary>
public sealed class WebRtcResampler
{
    private int _fromRate;
    private int _toRate;
    private double _step;
    private long _sourceOffset;  // 已被完整消费的源样本数（绝对）
    private long _produced;      // 已产出的输出样本数（绝对）
    private short _lastSample;   // 上一块最后一个源样本（跨块插值补位）
    private bool _hasLast;

    public WebRtcResampler(int fromRate, int toRate)
    {
        if (fromRate <= 0 || toRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fromRate), "采样率必须为正");
        }

        _fromRate = fromRate;
        _toRate = toRate;
        _step = (double)fromRate / toRate;
    }

    /// <summary>清空流式状态并更换采样率（远端格式变化时用）。</summary>
    public void Reset(int fromRate, int toRate)
    {
        if (fromRate <= 0 || toRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fromRate), "采样率必须为正");
        }

        _fromRate = fromRate;
        _toRate = toRate;
        _step = (double)fromRate / toRate;
        _sourceOffset = 0;
        _produced = 0;
        _lastSample = 0;
        _hasLast = false;
    }

    /// <summary>重采样一批多声道交错样本：多通道先按声道平均降为单声道。</summary>
    public short[] Resample(ReadOnlySpan<short> interleaved, int channels)
    {
        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        var sourceCount = interleaved.Length / channels;
        if (sourceCount == 0)
        {
            return [];
        }

        if (_fromRate == _toRate)
        {
            _sourceOffset += sourceCount;
            _produced += sourceCount;
            _lastSample = interleaved[sourceCount - 1];
            _hasLast = true;
            if (channels == 1)
            {
                return interleaved.ToArray();
            }

            var mono = new short[sourceCount];
            Downmix(interleaved, channels, mono);
            return mono;
        }

        // 保守产出：最后一个源样本保留作 lookahead——产出上界是「插值右邻点
        // 仍落在本块内」的最大 j。条件 j*from < (offset+count-1)*to 用纯整数
        // 上取整表达，避免浮点精度损失（48k↔16k 均可整除，long 不溢出）。
        var producible = ((_sourceOffset + sourceCount - 1) * _toRate + _fromRate - 1) / _fromRate - _produced;
        if (producible < 0)
        {
            producible = 0;
        }

        var output = new short[producible];
        var downmixed = channels == 1 ? null : new short[sourceCount];
        if (downmixed is not null)
        {
            Downmix(interleaved, channels, downmixed);
        }

        var source = downmixed ?? interleaved;
        for (var j = 0; j < producible; j++)
        {
            var pos = (_produced + j) * _step;
            var absIndex = (long)Math.Floor(pos);
            var frac = pos - absIndex;
            var a = SourceAt(absIndex, source, sourceCount);
            var b = SourceAt(absIndex + 1, source, sourceCount);
            output[j] = (short)Math.Round(a + (b - a) * frac);
        }

        _lastSample = source[sourceCount - 1];
        _hasLast = true;
        _sourceOffset += sourceCount;
        _produced += producible;
        return output;
    }

    private short SourceAt(long absIndex, ReadOnlySpan<short> source, int sourceCount)
    {
        var rel = absIndex - _sourceOffset;
        if (rel < 0)
        {
            // 跨块样本：插值点落在上一块范围（producible 的保守边界把它留到了本块）。
            return _hasLast ? _lastSample : source[0];
        }

        if (rel >= sourceCount)
        {
            return _hasLast ? _lastSample : source[sourceCount - 1];
        }

        return source[(int)rel];
    }

    private static void Downmix(ReadOnlySpan<short> interleaved, int channels, Span<short> mono)
    {
        for (var i = 0; i < mono.Length; i++)
        {
            var sum = 0;
            for (var c = 0; c < channels; c++)
            {
                sum += interleaved[i * channels + c];
            }

            mono[i] = (short)(sum / channels);
        }
    }
}
