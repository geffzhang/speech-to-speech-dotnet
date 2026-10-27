namespace SpeechToSpeech.Backends.SmartTurn;

/// <summary>
/// 400 点复数 FFT（float64，numpy <c>np.fft.fft</c> 同号约定 e^-2πi·kn/N）。
///
/// Whisper 前端只需要这一个长度：n_fft=400。400 = 16×25，用两级 Cooley–Tukey（DIT）：
/// 内级对**跨步 25** 的 16 点子序列做 radix-2 FFT（j = 25·j1 + j2 的 j1 维），
/// 外级对 25 点残差做 DFT（j2 维）。注意内级必须是跨步取样——连续分块对应的
/// 是另一种重排，两级核就不再是干净的子 DFT（本项目实测踩过：实输入的非 DC
/// 频点全错，复数输入恰好也能过测试，靠真音频才暴露）。
///
/// 全程 float64——与 numpy rfft 的内部精度一致，这是与参照实现逐值对齐的前提。
/// 旋转因子预计算成 400 项表，避免每蝶形一次三角函数。
/// </summary>
internal static class Fft400
{
    public const int Size = 400;

    /// <summary>频率仓数。numpy rfft 口径：N/2 + 1。</summary>
    public const int Bins = Size / 2 + 1;

    private static readonly double[] Cos = new double[Size];
    private static readonly double[] Sin = new double[Size];

    /// <summary>外级 25 点 DFT 的核（e^-2πi·j2·k2/25 = 表[16·j2·k2 % 400]）。</summary>
    private static readonly double[] Dft25Cos = new double[25 * 25];
    private static readonly double[] Dft25Sin = new double[25 * 25];

    static Fft400()
    {
        for (var k = 0; k < Size; k++)
        {
            var angle = -2.0 * Math.PI * k / Size;
            Cos[k] = Math.Cos(angle);
            Sin[k] = Math.Sin(angle);
        }

        for (var k2 = 0; k2 < 25; k2++)
        {
            for (var j2 = 0; j2 < 25; j2++)
            {
                Dft25Cos[k2 * 25 + j2] = Cos[16 * k2 * j2 % Size];
                Dft25Sin[k2 * 25 + j2] = Sin[16 * k2 * j2 % Size];
            }
        }
    }

    /// <summary>
    /// 原地前向变换。<paramref name="re"/> / <paramref name="im"/> 长度必须为 400。
    /// 前 <see cref="Bins"/> 个仓即 rfft 的结果（实输入的对称半边）。
    /// </summary>
    public static void Forward(double[] re, double[] im)
    {
        var tmpRe = new double[Size];
        var tmpIm = new double[Size];
        Forward(re, im, tmpRe, tmpIm);
    }

    /// <summary>
    /// 带调用方提供的临时缓冲的变体：热路径（逐帧 801 次调用）避免每次分配。
    /// 结果写回 <paramref name="re"/> / <paramref name="im"/>，临时缓冲仅作中转。
    /// </summary>
    public static void Forward(double[] re, double[] im, double[] tmpRe, double[] tmpIm)
    {
        // 第一级：对每个 j2，取 x[25·j1 + j2]（j1 = 0..15）做 16 点 FFT，存回原位。
        Span<double> block = stackalloc double[16];
        Span<double> blockIm = stackalloc double[16];
        for (var j2 = 0; j2 < 25; j2++)
        {
            for (var j1 = 0; j1 < 16; j1++)
            {
                block[j1] = re[j1 * 25 + j2];
                blockIm[j1] = im[j1 * 25 + j2];
            }

            Forward16(block, blockIm);

            for (var k1 = 0; k1 < 16; k1++)
            {
                re[k1 * 25 + j2] = block[k1];
                im[k1 * 25 + j2] = blockIm[k1];
            }
        }

        // 旋转因子：A'[j2][k1] = A[j2][k1] · e^-2πi·j2·k1/400
        for (var k1 = 1; k1 < 16; k1++)
        {
            for (var j2 = 1; j2 < 25; j2++)
            {
                var index = j2 * k1 % Size;
                var c = Cos[index];
                var s = Sin[index];
                var position = k1 * 25 + j2;
                (re[position], im[position]) =
                    (re[position] * c - im[position] * s,
                     re[position] * s + im[position] * c);
            }
        }

        // 第二级：对每条 k1 做 25 点 DFT（j2 维），输出重排到 k = k1 + 16·k2。
        // 输入读自 re/im、输出写 tmp——两组下标集合交错，原地写会污染尚未读取的行。
        Span<double> src = stackalloc double[25];
        Span<double> srcIm = stackalloc double[25];
        for (var k1 = 0; k1 < 16; k1++)
        {
            for (var j2 = 0; j2 < 25; j2++)
            {
                var position = k1 * 25 + j2;
                src[j2] = re[position];
                srcIm[j2] = im[position];
            }

            for (var k2 = 0; k2 < 25; k2++)
            {
                var sumR = 0.0;
                var sumI = 0.0;
                var base_ = k2 * 25;
                for (var j2 = 0; j2 < 25; j2++)
                {
                    var c = Dft25Cos[base_ + j2];
                    var s = Dft25Sin[base_ + j2];
                    sumR += src[j2] * c - srcIm[j2] * s;
                    sumI += src[j2] * s + srcIm[j2] * c;
                }

                tmpRe[k1 + k2 * 16] = sumR;
                tmpIm[k1 + k2 * 16] = sumI;
            }
        }

        Array.Copy(tmpRe, re, Size);
        Array.Copy(tmpIm, im, Size);
    }

    /// <summary>16 点 radix-2 DIT，蝶形旋转因子取自 400 项表（步长 400/len）。</summary>
    private static void Forward16(Span<double> re, Span<double> im)
    {
        // 位反转（16 点）。
        for (int i = 1, j = 0; i < 16; i++)
        {
            var bit = 8;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= 16; len <<= 1)
        {
            var step = Size / len;
            for (var start = 0; start < 16; start += len)
            {
                for (var k = 0; k < len / 2; k++)
                {
                    var c = Cos[k * step];
                    var s = Sin[k * step];
                    var even = start + k;
                    var odd = even + len / 2;
                    var tr = re[odd] * c - im[odd] * s;
                    var ti = re[odd] * s + im[odd] * c;
                    re[odd] = re[even] - tr;
                    im[odd] = im[even] - ti;
                    re[even] += tr;
                    im[even] += ti;
                }
            }
        }
    }
}
