namespace SpeechToSpeech.Backends.OpenLipSync.Inference.Audio;

/// <summary>
/// 迭代基-2 FFT（实输入优化路径直出功率谱）。
/// 移植自 BasisVR/Basis com.basis.openlipsync（MIT）。
/// </summary>
public sealed class FFTProcessor
{
    private readonly int _size;

    // br[i] = bit-reverse(i)（log2(size) 位）——全尺寸复 FFT 用。
    private readonly int[] _br;

    // brHalf[i] = bit-reverse(i)（log2(size/2) 位）——ForwardPower 的半尺寸 FFT 用。
    private readonly int[] _brHalf;

    // 旋转因子表，按级打包。长度 len 的级，twiddle k=0..len/2-1 存于 (len/2 + k)。
    // len=size 的条目（[size/2 .. size-1]）兼作 ForwardPower 的重组 twiddle。
    private readonly float[] _twC;
    private readonly float[] _twS;

    // 拆分式工作缓冲：避开 Complex 运算符的 IEEE 保护与 double 开销。
    private readonly float[] _re;
    private readonly float[] _im;

    public FFTProcessor(int size)
    {
        if (size <= 0 || (size & (size - 1)) != 0)
        {
            throw new ArgumentException("FFT size must be a power of 2", nameof(size));
        }

        _size = size;
        _br = BuildBitReverseTable(size);
        _brHalf = size >= 2 ? BuildBitReverseTable(size / 2) : null!;
        (_twC, _twS) = BuildTwiddleTables(size);
        _re = new float[size];
        _im = new float[size];
    }

    /// <summary>
    /// 实输入前向 FFT，直接写功率谱 |X[k]|²（k=0..N/2）。
    /// 内部跑 N/2 复 FFT 再重组，省掉打包与逐 bin 取模。
    /// </summary>
    public void ForwardPower(ReadOnlySpan<float> realInput, Span<float> powerOut)
    {
        int n = _size;
        int halfN = n >> 1;

        if (n < 2)
        {
            throw new InvalidOperationException("ForwardPower requires FFT size >= 2");
        }

        if (realInput.Length != n)
        {
            throw new ArgumentException($"Input length must be {n}", nameof(realInput));
        }

        if (powerOut.Length != halfN + 1)
        {
            throw new ArgumentException($"Power output length must be {halfN + 1}", nameof(powerOut));
        }

        float[] re = _re;
        float[] im = _im;
        int[] br = _brHalf;

        // 长度 N 实信号打包成长度 N/2 复信号 z[k] = x[2k] + i·x[2k+1]，
        // 位反转置换直接烘焙进摆放。
        for (int i = 0; i < halfN; i++)
        {
            int j = br[i];
            int j2 = j << 1;
            re[i] = realInput[j2];
            im[i] = realInput[j2 + 1];
        }

        Butterflies(re, im, halfN);

        // 重组为原长度 N DFT 的 |X[k]|²：
        //   E[k] =  (Z[k] + conj(Z[N/2-k])) / 2
        //   O[k] = -i·(Z[k] - conj(Z[N/2-k])) / 2
        //   X[k] = E[k] + e^(-i·2πk/N) · O[k]
        // k=0 与 k=N/2 是实数特例：
        //   X[0] = Re(Z[0]) + Im(Z[0])；X[N/2] = Re(Z[0]) - Im(Z[0])
        float z0r = re[0];
        float z0i = im[0];
        float dcSum = z0r + z0i;
        float dcDiff = z0r - z0i;
        powerOut[0] = dcSum * dcSum;
        powerOut[halfN] = dcDiff * dcDiff;

        // 重组 twiddle e^(-i·2πk/N) 在表的 len=N 级（索引 [N/2 .. N-1]）。
        float[] twC = _twC;
        float[] twS = _twS;
        int twBase = halfN;

        for (int k = 1; k < halfN; k++)
        {
            float a = re[k];
            float b = im[k];
            float c = re[halfN - k];
            float d = im[halfN - k];

            float wRe = twC[twBase + k];
            float wIm = twS[twBase + k];

            // E[k].real=(a+c)/2, E[k].imag=(b-d)/2
            // O[k].real=(b+d)/2, O[k].imag=(c-a)/2
            // X[k] = E[k] + (wRe + wIm·i)·O[k]
            float halfApC = 0.5f * (a + c);
            float halfBmD = 0.5f * (b - d);
            float halfBpD = 0.5f * (b + d);
            float halfCmA = 0.5f * (c - a);

            float xRe = halfApC + (wRe * halfBpD) - (wIm * halfCmA);
            float xIm = halfBmD + (wRe * halfCmA) + (wIm * halfBpD);

            powerOut[k] = (xRe * xRe) + (xIm * xIm);
        }
    }

    private void Butterflies(float[] re, float[] im, int n)
    {
        float[] twC = _twC;
        float[] twS = _twS;

        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            int twBase = half;

            for (int i = 0; i < n; i += len)
            {
                for (int j = 0; j < half; j++)
                {
                    float wRe = twC[twBase + j];
                    float wIm = twS[twBase + j];

                    int p = i + j;
                    int q = p + half;

                    float uRe = re[p];
                    float uIm = im[p];
                    float vRawRe = re[q];
                    float vRawIm = im[q];

                    float vRe = (vRawRe * wRe) - (vRawIm * wIm);
                    float vIm = (vRawRe * wIm) + (vRawIm * wRe);

                    re[p] = uRe + vRe;
                    im[p] = uIm + vIm;
                    re[q] = uRe - vRe;
                    im[q] = uIm - vIm;
                }
            }
        }
    }

    private static int[] BuildBitReverseTable(int n)
    {
        int logN = 0;
        while ((1 << logN) < n)
        {
            logN++;
        }

        var tbl = new int[n];
        for (int i = 0; i < n; i++)
        {
            int j = 0, x = i;
            for (int k = 0; k < logN; k++)
            {
                j = (j << 1) | (x & 1);
                x >>= 1;
            }

            tbl[i] = j;
        }

        return tbl;
    }

    private static (float[] cos, float[] sin) BuildTwiddleTables(int n)
    {
        var c = new float[n];
        var s = new float[n];
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            for (int k = 0; k < half; k++)
            {
                double angle = -2.0 * Math.PI * k / len;
                c[half + k] = (float)Math.Cos(angle);
                s[half + k] = (float)Math.Sin(angle);
            }
        }

        return (c, s);
    }
}
