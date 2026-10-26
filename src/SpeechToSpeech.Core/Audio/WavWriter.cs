namespace SpeechToSpeech.Core.Audio;

/// <summary>最小 WAV 写出器：16-bit 单声道 PCM。</summary>
public static class WavWriter
{
    private const int HeaderSize = 44;

    public static byte[] FromFloat32(float[] samples, int sampleRate)
    {
        var pcm = AudioUtil.ToPcm16(samples);
        return FromPcm16(pcm, sampleRate, channels: 1);
    }

    public static byte[] FromPcm16(byte[] pcm, int sampleRate, short channels = 1)
    {
        var buffer = new byte[HeaderSize + pcm.Length];
        var byteRate = sampleRate * channels * 2;

        WriteAscii(buffer, 0, "RIFF");
        WriteInt32(buffer, 4, 36 + pcm.Length);
        WriteAscii(buffer, 8, "WAVE");
        WriteAscii(buffer, 12, "fmt ");
        WriteInt32(buffer, 16, 16);           // PCM 子块大小
        WriteInt16(buffer, 20, 1);            // 格式：PCM
        WriteInt16(buffer, 22, channels);
        WriteInt32(buffer, 24, sampleRate);
        WriteInt32(buffer, 28, byteRate);
        WriteInt16(buffer, 32, (short)(channels * 2));
        WriteInt16(buffer, 34, 16);           // 位深
        WriteAscii(buffer, 36, "data");
        WriteInt32(buffer, 40, pcm.Length);
        Buffer.BlockCopy(pcm, 0, buffer, HeaderSize, pcm.Length);

        return buffer;
    }

    private static void WriteAscii(byte[] buffer, int offset, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            buffer[offset + i] = (byte)value[i];
        }
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
        => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), value);

    private static void WriteInt16(byte[] buffer, int offset, short value)
        => System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset, 2), value);
}
