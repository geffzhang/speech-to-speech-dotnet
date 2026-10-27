namespace SpeechToSpeech.Realtime.Protocol;

/// <summary>
/// 每响应的 token 记账。
///
/// 规则来自上游 <c>_on_token_usage</c>：只有「说的是当前响应」才直接累加，其余按
/// <c>response_key</c> 挂起，等对应响应真正收口时再结转；已收口 key 上迟到的用量直接丢弃。
///
/// 这不是优化而是正确性。投机轮次会产出用量，但那一轮的输出可能最终被丢弃——
/// 直接累加就把丢弃轮次的账算到活的那一轮头上。计费错比数字空着糟糕得多：
/// 空着客户端读到 0，可解释；错了没法解释。
///
/// 抽成独立类型是为了能单测：这套判断埋在长方法里时，改一处就悄悄失效。
/// </summary>
public sealed class ResponseUsageLedger
{
    private const int ClosedKeyRetention = 128;

    /// <summary>挂起表的上限。被打断的投机轮次不会再被结转，没有上限就是一条慢性泄漏。</summary>
    private const int PendingKeyRetention = 128;

    private readonly Dictionary<string, (int Input, int Output)> _pending = new(StringComparer.Ordinal);
    private readonly Queue<string> _pendingOrder = new();

    // 已收口的 key 用「集合 + 队列」而不是单个 HashSet：淘汰必须按加入顺序（FIFO），
    // HashSet 的枚举顺序不是插入序，按它淘汰等于随机删——近期 key 会被先删掉，
    // 于是一条迟到的用量又落进了下一轮。上游用 dict 的插入序做同一件事。
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
    private readonly Queue<string> _closedOrder = new();

    private int _inputTokens;
    private int _outputTokens;

    /// <summary>当前响应迄今为止的用量。</summary>
    public ResponseUsage Current => new(_inputTokens, _outputTokens);

    /// <summary>挂起中的 key 数。仅供测试与诊断。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// 归账一条用量。<paramref name="activeResponseKey"/> 是当前正在生成的响应 key（无响应时为 null）。
    /// </summary>
    public void Accrue(string? responseKey, int inputTokens, int outputTokens, string? activeResponseKey)
    {
        if (responseKey is not null && _closed.Contains(responseKey))
        {
            // 已收口：迟到用量丢弃。落到下一轮会让那一轮凭空多出上百万 token。
            return;
        }

        if (responseKey is not null && !string.Equals(responseKey, activeResponseKey, StringComparison.Ordinal))
        {
            if (!_pending.TryGetValue(responseKey, out var existing))
            {
                _pendingOrder.Enqueue(responseKey);
            }

            _pending[responseKey] = (existing.Input + inputTokens, existing.Output + outputTokens);
            while (_pendingOrder.Count > PendingKeyRetention)
            {
                // 被打断的投机轮次永远不会被结转，不设上限就是一条慢性泄漏。
                _pending.Remove(_pendingOrder.Dequeue());
            }

            return;
        }

        _inputTokens += inputTokens;
        _outputTokens += outputTokens;
    }

    /// <summary>把挂在某个响应 key 上的用量结转进当前响应。收口前调一次。</summary>
    public void ApplyPending(string responseKey)
    {
        if (!_pending.Remove(responseKey, out var pending))
        {
            return;
        }

        // 队列里的残留项由上限回收；这里只删表，不动队列（Queue 不支持随机删）。

        _inputTokens += pending.Input;
        _outputTokens += pending.Output;
    }

    /// <summary>
    /// 开始一次新响应：只把计数归零。
    ///
    /// 刻意不动「已关闭 key」的记忆——那条记忆的用途就是挡住上一轮的迟到用量，
    /// 每轮清掉等于把防护拆了。彻底清空走 <see cref="Reset"/>（断连场景）。
    /// </summary>
    public void BeginResponse()
    {
        _inputTokens = 0;
        _outputTokens = 0;
    }

    /// <summary>
    /// 收口一个响应 key：用量归零、key 进已关闭集合。
    ///
    /// 不归零的话下一轮会继承上一轮的数字——用量是每响应计的，不是累计值。
    /// </summary>
    public void Close(string responseKey)
    {
        _inputTokens = 0;
        _outputTokens = 0;

        if (_closed.Add(responseKey))
        {
            _closedOrder.Enqueue(responseKey);
        }

        while (_closedOrder.Count > ClosedKeyRetention)
        {
            // 只保留最近若干个：无界增长会让长连接慢慢吃满内存，而更早的 key 不会再被引用。
            _closed.Remove(_closedOrder.Dequeue());
        }
    }

    /// <summary>仅供测试：当前是否把这个 key 记为已收口。</summary>
    public bool IsClosed(string responseKey) => _closed.Contains(responseKey);

    /// <summary>断连等场景下彻底清空，避免跨会话串账。</summary>
    public void Reset()
    {
        _inputTokens = 0;
        _outputTokens = 0;
        _pending.Clear();
        _pendingOrder.Clear();
        _closed.Clear();
        _closedOrder.Clear();
    }
}
