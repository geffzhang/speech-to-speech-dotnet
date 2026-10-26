namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// 语音转语音管线的统一取消信号。
///
/// 用代际计数器让管线线程（LLM、TTS）无需"脉冲时序把戏"即可察觉取消，
/// 另用内部 <c>discarding</c> 标志让异步发送循环丢弃过期输出。
///
/// 线程安全说明：上游注释称 Python GIL 使 int/bool 读写原子，因此无锁；
/// C# 侧没有同等保证，故用 <see cref="Interlocked"/> 与 <see cref="Volatile"/> 显式表达
/// 单写多读语义（见差异账 C-01）。
/// </summary>
public sealed class CancelScope
{
    private int _generation;
    private int _discarding;

    /// <summary>已被 <see cref="Cancel"/> 作废的代际；-1 表示无。</summary>
    private int _discardedGeneration = -1;

    /// <summary>当前代际。管线线程在每次响应开始时捕获并与 <see cref="IsStale"/> 比较。</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// 取消当前响应：递增代际（使管线线程捕获的代际变旧）并打开发送循环的丢弃保护。
    /// </summary>
    public void Cancel()
    {
        var current = Volatile.Read(ref _generation);
        _discardedGeneration = current;
        Interlocked.Exchange(ref _generation, unchecked((int)((uint)(current + 1) & 0xFFFFFFFFu)));
        Volatile.Write(ref _discarding, 1);
    }

    /// <summary>管线确认完成。清除丢弃保护。</summary>
    /// <param name="generation">
    /// 若给定且不等于被取消的代际、也不等于当前代际，则不做处理（迟到的完成通知不得清除新响应的保护）。
    /// </param>
    public void ResponseDone(int? generation = null)
    {
        if (generation is not null
            && _discardedGeneration >= 0
            && generation != _discardedGeneration
            && generation != Generation)
        {
            return;
        }

        Volatile.Write(ref _discarding, 0);
        _discardedGeneration = -1;
    }

    /// <summary>显式 <c>response.create</c> 开启新响应，清除丢弃保护。</summary>
    public void NewResponse()
    {
        Volatile.Write(ref _discarding, 0);
        _discardedGeneration = -1;
    }

    /// <summary><paramref name="generation"/> 是否已被 <see cref="Cancel"/> 超越。</summary>
    public bool IsStale(int generation) => generation != Generation;

    /// <summary>发送循环是否应静默丢弃过期输出。</summary>
    public bool Discarding => Volatile.Read(ref _discarding) != 0;

    /// <summary>清理丢弃状态（例如新会话接入）。</summary>
    public void Reset()
    {
        Volatile.Write(ref _discarding, 0);
        _discardedGeneration = -1;
    }
}
