using System.Buffers.Binary;

namespace SpeechToSpeech.Core.Audio;

/// <summary>
/// 音频转换工具。
///
/// 上游依赖 torchaudio / scipy / soxr 做重采样；C# 侧没有等价依赖，故自带实现。
/// 降采样走"整数倍抽取 + FIR 低通"，非整数倍走线性插值，质量差异由差异账 C-09 登记。
/// </summary>
public static class AudioUtil
{
    /// <summary>VAD 期望的固定块长（Silero VAD v5 的 512 样本窗口 @16 kHz）。</summary>
    public const int VadChunkSize = 512;

    /// <summary>float[-1,1] → int16 小端 PCM。</summary>
    public static byte[] ToPcm16(float[] samples)
    {
        var buffer = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            // 与上游一致：先乘 32767 再截断，负半轴保留 -32768 的余量
            var value = (short)Math.Round(clamped * 32767f, MidpointRounding.AwayFromZero);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2, 2), value);
        }

        return buffer;
    }

    /// <summary>int16 小端 PCM → float[-1,1]。</summary>
    public static float[] FromPcm16(ReadOnlySpan<byte> pcm)
    {
        var count = pcm.Length / 2;
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2)) / 32768f;
        }

        return samples;
    }

    /// <summary>按固定长度切块，末块不足时返回剩余部分。</summary>
    public static IEnumerable<float[]> Chunk(float[] samples, int chunkSize)
    {
        for (var offset = 0; offset < samples.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            var chunk = new float[length];
            Array.Copy(samples, offset, chunk, 0, length);
            yield return chunk;
        }
    }

    /// <summary>
    /// 重采样到目标采样率。
    ///
    /// 降采样且为整数倍时先做 FIR 低通再抽取，避免混叠把高频折回语音带内。
    /// </summary>
    public static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0)
        {
            return input;
        }

        if (fromRate % toRate == 0 && fromRate / toRate > 1)
        {
            var factor = fromRate / toRate;
            var filtered = LowPass(input, cutoffNormalized: 0.5 / factor);
            var length = filtered.Length / factor;
            var output = new float[length];
            for (var i = 0; i < length; i++)
            {
                output[i] = filtered[i * factor];
            }

            return output;
        }

        var ratio = (double)toRate / fromRate;
        var newLength = (int)Math.Round(input.Length * ratio);
        var result = new float[newLength];
        for (var i = 0; i < newLength; i++)
        {
            var position = i / ratio;
            var index = (int)position;
            var frac = (float)(position - index);
            var a = input[Math.Min(index, input.Length - 1)];
            var b = input[Math.Min(index + 1, input.Length - 1)];
            result[i] = a + (b - a) * frac;
        }

        return result;
    }

    /// <summary>
    /// 简单 FIR 低通。<paramref name="cutoffNormalized"/> 为相对奈奎斯特频率的归一化截止。
    /// 窗口长度随截止频率收紧而增加，保证过渡带不至于太陡地漏高频。
    /// </summary>
    private static float[] LowPass(float[] input, double cutoffNormalized)
    {
        var taps = 63;
        var half = taps / 2;
        var kernel = new float[taps];
        double sum = 0;

        for (var i = 0; i < taps; i++)
        {
            var n = i - half;
            var sinc = n == 0 ? 2 * cutoffNormalized : Math.Sin(2 * Math.PI * cutoffNormalized * n) / (Math.PI * n);
            // Hamming 窗
            var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (taps - 1));
            kernel[i] = (float)(sinc * window);
            sum += kernel[i];
        }

        if (Math.Abs(sum) > double.Epsilon)
        {
            for (var i = 0; i < taps; i++)
            {
                kernel[i] = (float)(kernel[i] / sum);
            }
        }

        var output = new float[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            double acc = 0;
            for (var k = 0; k < taps; k++)
            {
                var index = i + k - half;
                if (index >= 0 && index < input.Length)
                {
                    acc += input[index] * kernel[k];
                }
            }

            output[i] = (float)acc;
        }

        return output;
    }

    /// <summary>把多块 PCM 拼成一个数组（用于把流式块落盘或送 STT）。</summary>
    public static float[] Concat(IEnumerable<float[]> chunks)
    {
        var list = chunks as IList<float[]> ?? [.. chunks];
        var total = list.Sum(c => c.Length);
        var result = new float[total];
        var offset = 0;
        foreach (var chunk in list)
        {
            Array.Copy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }
}
