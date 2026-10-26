namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// 轮次相位。对应上游 <c>TurnPhase</c>。
/// </summary>
public enum TurnPhase
{
    Listening,
    SoftEnded,
    Answering,
    Closed,
}

/// <summary>带轮次标签工作的放行裁决。</summary>
public enum TurnGateAction
{
    Accept,
    Drop,
    Hold,
}

/// <summary>
/// 放行裁决的结果。<see cref="TurnGateAction.Hold"/> 时 <see cref="Deadline"/> 是单调时钟上的
/// 解冻期限——调用方据此决定等多久，而不是自己猜。
/// </summary>
public readonly record struct TurnGate(TurnGateAction Action, double? Deadline = null);

/// <summary>输出到达提交点时其轮次仍被挂起（VAD 还没判定续说），应继续排队而非发出。</summary>
public sealed class TurnOutputHeldException : Exception
{
    public TurnOutputHeldException()
        : base("输出在轮次被 HOLD 期间到达提交点，应继续排队")
    {
    }

    public TurnOutputHeldException(string message) : base(message)
    {
    }

    public TurnOutputHeldException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>轮次引用。序号单调递增，用来判断「谁更新」，而不是比较 id 字符串。</summary>
internal readonly record struct TurnReference(long Sequence, string TurnId, int Revision);

/// <summary>待确认的复开候选：VAD 正在检查续说的那一段。</summary>
internal readonly record struct PendingReopen(string TurnId, int BaseRevision, int CandidateRevision, double Deadline);

/// <summary>复开宽限期：这段话可能没说完，先压住输出等一等。</summary>
internal readonly record struct ReopenGrace(string TurnId, int Revision, double Deadline);

/// <summary>
/// 投机轮次跟踪器——会话游标。
///
/// 上游这个类的职责不是「记一下第几轮」，而是回答三个互相纠缠的问题：
/// 1. 这段带轮次标签的工作<span>还该不该做</span>（<see cref="Gate"/>：接受 / 丢弃 / 先挂着）
/// 2. 用户刚说完就停顿时，<span>该开新轮还是复开上一轮</span>（<see cref="SpeechStarted"/>）
/// 3. 上一轮的输出<span>要不要等</span>（reopen grace 与 processing deadline）
///
/// 三条约束决定了它的复杂度，缺一条就会在打断场景出错：
/// - **挂起不等于丢弃**：VAD 复查期间（<see cref="SpeechCandidateStarted"/>）输出只是压住，
///   候选被取消后必须能原样放行——所以 HOLD 必须带期限，且期限到了自动转 ACCEPT。
/// - **已提交的不可挂起**：已经 ACCEPT 过的工作再压住会把回复卡死（音频流永不关闭）。
/// - **复开不推进轮次序号**：复开是同一轮的 revision+1，序号推进会让「谁更新」的判断失真。
///
/// 时钟是可注入的：所有期限都是单调时钟上的绝对时间，测试与差分语料需要一个虚拟时钟
/// 才能确定性地复现跨语言行为。默认走 <see cref="Monotonic"/>（等价 <c>time.monotonic()</c>）。
/// </summary>
public sealed class SpeculativeTurnTracker
{
    /// <summary>候选复查最长挂起时间。上游硬编码 2 秒：VAD 迟迟不表态时不能无限压住输出。</summary>
    private const double PendingReopenHoldSeconds = 2.0;

    /// <summary>未应答轮次可复开的默认上限（按流过的音频毫秒计，不按墙上时间）。</summary>
    private const int DefaultUnansweredReopenMs = 7000;

    private static readonly TurnGate AcceptGate = new(TurnGateAction.Accept);
    private static readonly TurnGate DropGate = new(TurnGateAction.Drop);

    private readonly object _lock = new();
    private readonly Func<double> _monotonic;
    private readonly HashSet<TurnReference> _committed = [];

    private long _sequence;
    private TurnReference? _current;
    private TurnReference? _closedCurrent;
    private PendingReopen? _pendingReopen;
    private ReopenGrace? _reopenGrace;
    private TurnPhase? _phase;
    private int? _lastFinalAudioMs;
    private double? _processingDeadline;
    private int _unansweredReopenMs = DefaultUnansweredReopenMs;

    /// <param name="monotonic">
    /// 单调时钟（秒）。传 null 用真实时钟；测试/差分语料传虚拟时钟。
    /// </param>
    public SpeculativeTurnTracker(Func<double>? monotonic = null)
    {
        _monotonic = monotonic ?? Monotonic.NowSeconds;
    }

    /// <summary>
    /// grace 等待结束后的回调：(turnId, revision, 开始等待的时刻, 结束时刻)。
    ///
    /// 只统计「等 grace」那类等待，不含等 pending reopen 的等待——后者是 VAD 判定延迟，
    /// 混进延迟指标会把两件事搞成一件。
    /// </summary>
    public Action<string, int, double, double>? WaitObserver { get; set; }

    /// <summary>当前相位；尚未开轮时为 null。</summary>
    public TurnPhase? Phase
    {
        get
        {
            lock (_lock)
            {
                return _phase;
            }
        }
    }

    /// <summary>当前轮次；尚未开轮时两项皆 null。</summary>
    public (string? TurnId, int? Revision) CurrentTurn()
    {
        lock (_lock)
        {
            return _current is { } current ? (current.TurnId, current.Revision) : (null, null);
        }
    }

    /// <summary>设置未应答轮次的可复开上限（毫秒音频时间）。</summary>
    public void ConfigureReopen(int unansweredReopenMs)
    {
        lock (_lock)
        {
            _unansweredReopenMs = Math.Max(0, unansweredReopenMs);
        }
    }

    /// <summary>
    /// 该时间点能否复开当前轮。
    ///
    /// 用的是**流过的音频时间**（<paramref name="audioMs"/>），不是墙上时间：
    /// 用户暂停三十秒后才继续说，与一口气说三十秒，是两种不同的输入。
    /// </summary>
    public bool CanReopen(int audioMs)
    {
        lock (_lock)
        {
            return CanReopenLocked(audioMs);
        }
    }

    /// <summary>
    /// VAD 检测到可能的续说起点：先压住输出，等 VAD 判定这段是不是语音。
    ///
    /// 返回 false 表示「不构成复查候选」（不在 SOFT_ENDED、已提交、或超出可复开上限），
    /// 调用方应直接开新轮。
    /// </summary>
    public bool SpeechCandidateStarted(int audioMs)
    {
        lock (_lock)
        {
            if (_pendingReopen is not null)
            {
                return true;
            }

            if (!CanReopenLocked(audioMs))
            {
                return false;
            }

            return BeginReopenCandidate(_current!.Value.TurnId, _current.Value.Revision) is not null;
        }
    }

    /// <summary>VAD 判定这段不是语音：撤掉候选，之前被压住的输出恢复放行。</summary>
    public void SpeechCandidateCancelled()
    {
        lock (_lock)
        {
            if (_pendingReopen is { } pending)
            {
                CancelReopenCandidate(pending.TurnId, candidateRevision: null);
            }
        }
    }

    /// <summary>是否有待确认的续说候选。</summary>
    public bool HasSpeechCandidate()
    {
        lock (_lock)
        {
            return _pendingReopen is not null;
        }
    }

    /// <summary>
    /// 用户开口：原子地决定「复开上一轮」还是「开新轮」。
    ///
    /// 返回 <c>Reopened=true</c> 时是同一轮的 revision+1——客户端看到的仍是同一条输入项，
    /// 这是「用户只是停顿了一下」与「用户说完了又说一句」在协议层的区别所在。
    /// </summary>
    public (string TurnId, int Revision, bool Reopened) SpeechStarted(int audioMs)
    {
        lock (_lock)
        {
            if (_current is { } current && _phase == TurnPhase.Listening)
            {
                return (current.TurnId, current.Revision, false);
            }

            var pending = _pendingReopen;
            if (pending is null && CanReopenLocked(audioMs))
            {
                BeginReopenCandidate(_current!.Value.TurnId, _current.Value.Revision);
                pending = _pendingReopen;
            }

            if (pending is { } candidate
                && ConfirmReopenCandidate(candidate.TurnId, candidate.BaseRevision, candidate.CandidateRevision))
            {
                return (candidate.TurnId, candidate.CandidateRevision, true);
            }

            var (turnId, revision) = StartTurn();
            return (turnId, revision, false);
        }
    }

    /// <summary>
    /// 一段语音定稿：记下音频时间点与两个期限。
    ///
    /// <paramref name="outputHoldMs"/> 是 grace（这段话可能没说完，输出再等等），
    /// <paramref name="processingDelayMs"/> 是 Smart Turn 处理延迟（判定还没出结果）。
    /// 两者独立：把所有等待合成一个期限，会让「VAD 慢」和「用户可能续说」互相覆盖。
    /// </summary>
    public void SegmentFinalized(int audioMs, int outputHoldMs = 0, int processingDelayMs = 0)
    {
        lock (_lock)
        {
            if (_current is not { } current || BlocksReopenLocked(current.TurnId, current.Revision))
            {
                return;
            }

            _phase = TurnPhase.SoftEnded;
            _lastFinalAudioMs = audioMs;
            var now = _monotonic();
            _processingDeadline = now + (Math.Max(0, processingDelayMs) / 1000.0);
            StartReopenGrace(current.TurnId, current.Revision, Math.Max(0, outputHoldMs) / 1000.0);
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>检测到的语音无法作为有效片段放行：收掉候选，相位退到 SOFT_ENDED。</summary>
    public void SegmentDiscarded()
    {
        lock (_lock)
        {
            SpeechCandidateCancelled();
            if (_phase == TurnPhase.Listening)
            {
                _phase = TurnPhase.SoftEnded;
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>
    /// 该轮次修订的处理期限；无期限或不是当前修订时返回 null（上游注释：legacy 未定稿输入）。
    /// </summary>
    public double? ProcessingDeadline(string? turnId, int? revision)
    {
        lock (_lock)
        {
            if (turnId is null || revision is null || !IsCurrentLocked(turnId, revision.Value))
            {
                return null;
            }

            return _processingDeadline;
        }
    }

    /// <summary>推进会话游标，开一轮新的。</summary>
    public (string TurnId, int Revision) StartTurn()
    {
        lock (_lock)
        {
            _sequence++;
            _current = new TurnReference(_sequence, $"turn_{_sequence}", Revision: 0);
            _closedCurrent = null;
            _phase = TurnPhase.Listening;
            _lastFinalAudioMs = null;
            _processingDeadline = null;
            _pendingReopen = null;
            _reopenGrace = null;
            Monitor.PulseAll(_lock);
            return (_current.Value.TurnId, _current.Value.Revision);
        }
    }

    /// <summary>
    /// 该轮次修订是否仍然相关（当前轮或已提交）。无轮次标签视为相关。
    /// </summary>
    public bool IsLatest(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return true;
        }

        lock (_lock)
        {
            return IsRelevantLocked(turnId, revision.Value);
        }
    }

    /// <summary>
    /// 不阻塞地裁决。工作已提交时永不 HOLD——已放行的工作再压住，会把回复卡在半截。
    /// <paramref name="commit"/> 为 true 时，ACCEPT 即提交该轮。
    /// </summary>
    public TurnGate Gate(
        string? turnId,
        int? revision,
        bool holdForGrace = true,
        double? holdUntil = null,
        bool commit = false)
    {
        if (turnId is null || revision is null)
        {
            return AcceptGate;
        }

        lock (_lock)
        {
            return GateLocked(turnId, revision.Value, holdForGrace, holdUntil, commit);
        }
    }

    /// <summary>
    /// 阻塞工作线程直到放行或丢弃。返回 true=放行，false=丢弃。
    ///
    /// 不要在事件循环线程上调用——它真的会等。等待期间锁被释放，状态变化会 pulse 唤醒它；
    /// 没有状态变化时按 HOLD 的期限超时。
    /// </summary>
    public bool WaitForGate(
        string? turnId,
        int? revision,
        bool holdForGrace = true,
        double? holdUntil = null,
        bool commit = false)
    {
        if (turnId is null || revision is null)
        {
            return true;
        }

        lock (_lock)
        {
            while (true)
            {
                var decision = GateLocked(turnId, revision.Value, holdForGrace, holdUntil, commit);
                if (decision.Action != TurnGateAction.Hold)
                {
                    return decision.Action == TurnGateAction.Accept;
                }

                var deadline = decision.Deadline!.Value;
                var waitingForGrace = holdUntil is null && !PendingHoldsLocked(turnId, revision.Value);
                var waitStartedAt = Monotonic.NowSeconds();
                var remainingMs = Math.Max(0, deadline - _monotonic()) * 1000;
                Monitor.Wait(_lock, TimeSpan.FromMilliseconds(Math.Min(remainingMs, int.MaxValue)));

                if (waitingForGrace)
                {
                    WaitObserver?.Invoke(turnId, revision.Value, waitStartedAt, Monotonic.NowSeconds());
                }
            }
        }
    }

    /// <summary>开启复开宽限（只想延长，不想缩短）。</summary>
    public void StartReopenGrace(string? turnId, int? revision, double graceSeconds)
    {
        if (turnId is null || revision is null || graceSeconds <= 0)
        {
            return;
        }

        lock (_lock)
        {
            if (!IsCurrentLocked(turnId, revision.Value) || BlocksReopenLocked(turnId, revision.Value))
            {
                return;
            }

            var deadline = _monotonic() + graceSeconds;
            var existing = _reopenGrace;
            if (existing is not { } current
                || current.TurnId != turnId
                || current.Revision != revision.Value
                || deadline > current.Deadline)
            {
                _reopenGrace = new ReopenGrace(turnId, revision.Value, deadline);
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>
    /// 响应走到终态后释放提交态。
    ///
    /// 已关闭但仍是当前轮的一轮**保持已应答**：工具回填与客户端主动发起的响应仍然有效，
    /// 且它不能再复开。直到下一轮开起来，它才变陈旧。
    /// </summary>
    public void Close(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return;
        }

        lock (_lock)
        {
            if (CommittedReferenceLocked(turnId, revision.Value) is not { } committed)
            {
                return;
            }

            _committed.Remove(committed);
            if (_current == committed)
            {
                _closedCurrent = committed;
                _phase = TurnPhase.Closed;
                _processingDeadline = null;
            }

            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>该 turnId 是否为当前轮（任意修订）。</summary>
    public bool IsCurrentTurn(string? turnId)
    {
        lock (_lock)
        {
            return _current is { } current && current.TurnId == turnId;
        }
    }

    /// <summary>该轮次修订是否已提交（含已关闭的当前轮）。</summary>
    public bool IsCommitted(string? turnId, int? revision = null)
    {
        if (turnId is null)
        {
            return false;
        }

        lock (_lock)
        {
            foreach (var reference in _committed)
            {
                if (reference.TurnId == turnId && (revision is null || reference.Revision == revision.Value))
                {
                    return true;
                }
            }

            return _closedCurrent is { } closed
                && closed.TurnId == turnId
                && (revision is null || closed.Revision == revision.Value);
        }
    }

    /// <summary>开启复开候选，返回候选修订号；不构成候选时返回 null。</summary>
    public int? BeginReopenCandidate(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return null;
        }

        lock (_lock)
        {
            if (!IsCurrentLocked(turnId, revision.Value) || BlocksReopenLocked(turnId, revision.Value))
            {
                return null;
            }

            if (_pendingReopen is { } existing)
            {
                return existing.TurnId == turnId && existing.BaseRevision == revision.Value
                    ? existing.CandidateRevision
                    : null;
            }

            var candidateRevision = revision.Value + 1;
            _pendingReopen = new PendingReopen(
                turnId,
                revision.Value,
                candidateRevision,
                _monotonic() + PendingReopenHoldSeconds);
            Monitor.PulseAll(_lock);
            return candidateRevision;
        }
    }

    /// <summary>
    /// 确认复开：候选修改变成当前修订。这是一次真正的游标推进，但仍属同一轮（序号不变）。
    /// </summary>
    public bool ConfirmReopenCandidate(string? turnId, int? baseRevision, int? candidateRevision)
    {
        if (turnId is null || baseRevision is null || candidateRevision is null)
        {
            return false;
        }

        lock (_lock)
        {
            if (_pendingReopen is not { } pending
                || pending.TurnId != turnId
                || pending.BaseRevision != baseRevision.Value
                || pending.CandidateRevision != candidateRevision.Value)
            {
                return false;
            }

            if (!IsCurrentLocked(turnId, baseRevision.Value) || BlocksReopenLocked(turnId, baseRevision.Value))
            {
                _pendingReopen = null;
                Monitor.PulseAll(_lock);
                return false;
            }

            _current = new TurnReference(_current!.Value.Sequence, turnId, candidateRevision.Value);
            _phase = TurnPhase.Listening;
            _processingDeadline = null;
            _pendingReopen = null;
            _reopenGrace = null;
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>撤掉复开候选（可指定候选修订，避免误撤别的候选）。</summary>
    public void CancelReopenCandidate(string? turnId, int? candidateRevision = null)
    {
        if (turnId is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_pendingReopen is not { } pending || pending.TurnId != turnId)
            {
                return;
            }

            if (candidateRevision is not null && pending.CandidateRevision != candidateRevision.Value)
            {
                return;
            }

            _pendingReopen = null;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>清空全部状态，序号归零。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _sequence = 0;
            _current = null;
            _phase = null;
            _lastFinalAudioMs = null;
            _processingDeadline = null;
            _committed.Clear();
            _closedCurrent = null;
            _pendingReopen = null;
            _reopenGrace = null;
            Monitor.PulseAll(_lock);
        }
    }

    private bool CanReopenLocked(int audioMs)
        => _current is { } current
            && _phase == TurnPhase.SoftEnded
            && _lastFinalAudioMs is { } lastFinal
            && !BlocksReopenLocked(current.TurnId, current.Revision)
            && Math.Max(0, audioMs - lastFinal) <= _unansweredReopenMs;

    private bool CommitLocked(string turnId, int revision)
    {
        if (CommittedReferenceLocked(turnId, revision) is not null)
        {
            return true;
        }

        if (_current is not { } current || !IsCurrentLocked(turnId, revision))
        {
            return false;
        }

        _committed.Add(current);
        _phase = TurnPhase.Answering;
        _processingDeadline = null;
        if (GraceMatchesLocked(turnId, revision))
        {
            _reopenGrace = null;
        }

        Monitor.PulseAll(_lock);
        return true;
    }

    private bool IsCurrentLocked(string turnId, int revision)
        => _current is { } current && current.TurnId == turnId && current.Revision == revision;

    private bool IsRelevantLocked(string turnId, int revision)
        => _current is not null
            && (IsCurrentLocked(turnId, revision) || CommittedReferenceLocked(turnId, revision) is not null);

    /// <summary>已接受或已收口的输出会让复开变得不安全。</summary>
    private bool BlocksReopenLocked(string turnId, int revision)
        => IsClosedCurrentLocked(turnId, revision) || CommittedReferenceLocked(turnId, revision) is not null;

    private bool IsClosedCurrentLocked(string turnId, int revision)
        => _closedCurrent is { } closed && closed.TurnId == turnId && closed.Revision == revision;

    private TurnReference? CommittedReferenceLocked(string turnId, int revision)
    {
        foreach (var reference in _committed)
        {
            if (reference.TurnId == turnId && reference.Revision == revision)
            {
                return reference;
            }
        }

        return null;
    }

    private TurnGate GateLocked(
        string turnId,
        int revision,
        bool holdForGrace,
        double? holdUntil,
        bool commit)
    {
        if (!IsRelevantLocked(turnId, revision))
        {
            return DropGate;
        }

        if (CommittedReferenceLocked(turnId, revision) is null)
        {
            var now = _monotonic();
            double? earliest = holdUntil is { } until && until > now ? until : null;

            if (PendingHoldsLocked(turnId, revision))
            {
                var pendingDeadline = _pendingReopen!.Value.Deadline;
                earliest = earliest is null ? pendingDeadline : Math.Min(earliest.Value, pendingDeadline);
            }

            if (holdForGrace
                && _reopenGrace is { } grace
                && GraceMatchesLocked(turnId, revision)
                && IsCurrentLocked(turnId, revision)
                && grace.Deadline > now)
            {
                earliest = earliest is null ? grace.Deadline : Math.Min(earliest.Value, grace.Deadline);
            }

            if (earliest is not null)
            {
                return new TurnGate(TurnGateAction.Hold, earliest);
            }
        }

        if (commit)
        {
            CommitLocked(turnId, revision);
        }

        return AcceptGate;
    }

    private bool PendingHoldsLocked(string turnId, int revision)
        => _pendingReopen is { } pending
            && pending.TurnId == turnId
            && pending.BaseRevision == revision
            && pending.Deadline > _monotonic();

    private bool GraceMatchesLocked(string turnId, int revision)
        => _reopenGrace is { } grace && grace.TurnId == turnId && grace.Revision == revision;
}
