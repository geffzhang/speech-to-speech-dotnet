namespace SpeechToSpeech.Evals.BigBenchAudio;

/// <summary>
/// CPython <c>random.Random</c> 的 MT19937 移植（仅本套件用到的切片：
/// 整数种子 seeding、<c>getrandbits</c>、<c>shuffle</c>）。
///
/// 上游 <c>build_subset</c> 用 <c>random.Random(seed).shuffle</c> 做均衡抽样；
/// .NET 的 Random 是不同算法，不同实现各自抽题会让「同一份种子」失去可比性。
/// 逐位复刻 CPython 的 _randommodule：init_by_array 播种 + 铁 temperature +
/// <c>_randbelow_with_getrandbits</c>，使 C 端与上游在相同种子下抽出**同一批题**。
/// </summary>
public sealed class PythonRandom
{
    private const int N = 624;
    private const int M = 397;
    private const uint MatrixA = 0x9908b0df;
    private const uint UpperMask = 0x80000000;
    private const uint LowerMask = 0x7fffffff;

    private readonly uint[] _mt = new uint[N];
    private int _mti = N + 1;

    public PythonRandom(int seed)
    {
        // CPython random_seed：取绝对值，按小端拆成 32 位字，init_by_array 播种。
        var abs = Math.Abs((long)seed);
        var key = new List<uint> { (uint)(abs & 0xffffffff) };
        if (abs > uint.MaxValue)
        {
            key.Add((uint)(abs >> 32));
        }

        InitByArray(key.ToArray());
    }

    private void InitGenrand(uint s)
    {
        _mt[0] = s;
        for (var i = 1; i < N; i++)
        {
            _mt[i] = 1812433253u * (_mt[i - 1] ^ (_mt[i - 1] >> 30)) + (uint)i;
        }

        _mti = N;
    }

    private void InitByArray(uint[] initKey)
    {
        InitGenrand(19650218u);
        const uint multiplier2 = 1566083941u;
        var i = 1;
        var j = 0;
        var k = Math.Max(N, initKey.Length);
        for (; k > 0; k--)
        {
            _mt[i] = (_mt[i] ^ ((_mt[i - 1] ^ (_mt[i - 1] >> 30)) * 1664525u)) + initKey[j] + (uint)j;
            i++;
            j++;
            if (i >= N)
            {
                _mt[0] = _mt[N - 1];
                i = 1;
            }

            if (j >= initKey.Length)
            {
                j = 0;
            }
        }

        for (k = N - 1; k > 0; k--)
        {
            _mt[i] = (_mt[i] ^ ((_mt[i - 1] ^ (_mt[i - 1] >> 30)) * multiplier2)) - (uint)i;
            i++;
            if (i >= N)
            {
                _mt[0] = _mt[N - 1];
                i = 1;
            }
        }

        _mt[0] = 0x80000000u;
    }

    private uint GenRandUint32()
    {
        uint y;
        if (_mti >= N)
        {
            if (_mti == N + 1)
            {
                InitGenrand(5489u);
            }

            for (var kk = 0; kk < N - M; kk++)
            {
                y = (_mt[kk] & UpperMask) | (_mt[kk + 1] & LowerMask);
                _mt[kk] = _mt[kk + M] ^ (y >> 1) ^ ((y & 1) != 0 ? MatrixA : 0u);
            }

            for (var kk = N - M; kk < N - 1; kk++)
            {
                y = (_mt[kk] & UpperMask) | (_mt[kk + 1] & LowerMask);
                _mt[kk] = _mt[kk + (M - N)] ^ (y >> 1) ^ ((y & 1) != 0 ? MatrixA : 0u);
            }

            y = (_mt[N - 1] & UpperMask) | (_mt[0] & LowerMask);
            _mt[N - 1] = _mt[M - 1] ^ (y >> 1) ^ ((y & 1) != 0 ? MatrixA : 0u);
            _mti = 0;
        }

        y = _mt[_mti++];
        y ^= y >> 11;
        y ^= (y << 7) & 0x9d2c5680u;
        y ^= (y << 15) & 0xefc60000u;
        y ^= y >> 18;
        return y;
    }

    /// <summary>CPython <c>getrandbits(k)</c>：低位在前拼 32 位字，最高字右移补齐。</summary>
    public ulong GetRandBits(int bits)
    {
        if (bits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bits), "number of bits must be greater than zero");
        }

        if (bits == 0)
        {
            return 0;
        }

        var words = (bits - 1) / 32 + 1;
        ulong result = 0;
        var k = bits;
        for (var i = 0; i < words; i++, k -= 32)
        {
            var r = GenRandUint32();
            if (k < 32)
            {
                r >>= 32 - k;
            }

            result |= (ulong)r << (32 * i);
        }

        return result;
    }

    private int RandBelow(int n)
    {
        var k = BitLength(n);
        var r = GetRandBits(k);
        while (r >= (ulong)n)
        {
            r = GetRandBits(k);
        }

        return (int)r;
    }

    private static int BitLength(int n)
    {
        var length = 0;
        while (n > 0)
        {
            length++;
            n >>= 1;
        }

        return length;
    }

    /// <summary>CPython <c>random.shuffle</c>：Fisher-Yates，从尾到头，_randbelow(i+1)。</summary>
    public void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandBelow(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
