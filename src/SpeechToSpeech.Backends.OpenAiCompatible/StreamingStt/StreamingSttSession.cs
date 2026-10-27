using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Runtime;

namespace SpeechToSpeech.Backends.OpenAiCompatible.StreamingStt;

/// <summary>
/// 一次提交的票据（上游 <c>_Commit</c>）：由 VAD 边界入队，worker 等远端
/// completed 后填充结果并放行，<see cref="StreamingSttSession.Commit"/> 阻塞等待。
/// </summary>
public sealed class StreamingCommit
{
    public int Generation { get; internal init; }

    public string? TurnId { get; internal init; }

    public int? TurnRevision { get; internal init; }

    internal ManualResetEventSlim Done { get; } = new(false);

    internal double BoundaryQueuedAtSeconds { get; init; }

    internal double? CompletedAtSeconds { get; set; }

    public string? Result { get; set; }

    public string? Language { get; set; }

    public string? Error { get; set; }
}

/// <summary>worker 内部的命令（上游 <c>_Command</c> 族）。</summary>
internal abstract class StreamingCommand
{
    internal virtual int Generation => 0;
}

internal sealed class CmdAppendAudio : StreamingCommand
{
    public CmdAppendAudio(int generation, byte[] audio)
    {
        Generation = generation;
        Audio = audio;
    }

    internal override int Generation { get; }

    public byte[] Audio { get; }
}

internal sealed class CmdStartTurn : StreamingCommand
{
    public CmdStartTurn(int generation, string? turnId, int? turnRevision)
    {
        Generation = generation;
        TurnId = turnId;
        TurnRevision = turnRevision;
    }

    internal override int Generation { get; }

    public string? TurnId { get; }

    public int? TurnRevision { get; }
}

internal sealed class CmdDiscardAudio : StreamingCommand
{
    public CmdDiscardAudio(int generation) => Generation = generation;

    internal override int Generation { get; }
}

internal sealed class CmdReset : StreamingCommand
{
    public CmdReset(int generation) => Generation = generation;

    internal override int Generation { get; }
}

internal sealed class CmdStop : StreamingCommand;

internal sealed class CmdCommit : StreamingCommand
{
    internal override int Generation => Commit.Generation;

    internal required StreamingCommit Commit { get; init; }
}

/// <summary>
/// 持有一条可重连 WebSocket 并串行化音频/提交命令的后台 worker
/// （上游 <c>_StreamingSession</c> 的逐行对应物）。
///
/// 结构映射：
/// <list type="bullet">
/// <item><c>_commands</c> → <see cref="_commands"/>（无界命令队列，VAD 线程只入队不阻塞）</item>
/// <item><c>generation</c> → cancel 代际（<see cref="CancelSession"/> 推进，陈旧命令与陈旧 partial 全丢）</item>
/// <item><c>_pending_commits</c> → 按 (generation, turn, rev) 挂起的提交票据</item>
/// <item><c>deferred_commands</c> → active commit 在飞时后到命令本地保留（一个连接只允许
/// 一条在途转写，不能让后到的轮次污染在飞结果）</item>
/// <item><c>retired_item_ids</c> / <c>committed_prefixes</c> / <c>failed_turn_ids</c> →
/// 迟到事件拒绝、多段转写前缀拼接、毒轮失败传播</item>
/// <item><c>emit_partial</c> → publication 锁内发射 <see cref="PartialTranscription"/></item>
/// </list>
/// </summary>
public sealed class StreamingSttSession : IDisposable
{
    private readonly IRealtimeTranscriptionProtocol _protocol;
    private readonly string _endpointUrl;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly double _connectTimeout;
    private readonly double _finalTimeout;
    private readonly StreamingConnectFactory _connectFactory;
    private readonly BlockingCollection<object> _queueOut;
    private readonly CancellationToken _stopped;
    private readonly SpeculativeTurnTracker? _speculativeTurns;
    private readonly int? _pipelineIndex;
    private readonly Action<string>? _log;

    private readonly BlockingCollection<StreamingCommand> _commands = [];

    private readonly object _generationLock = new();
    private int _generation;

    private readonly object _commitLock = new();
    private readonly Dictionary<(int, string?, int?), Queue<StreamingCommit>> _pendingCommits = new();

    private readonly object _publicationLock = new();
    private readonly object _connectionLock = new();
    private IStreamingSocket? _connection;

    private readonly Thread _thread;

    public StreamingSttSession(
        IRealtimeTranscriptionProtocol protocol,
        string endpointUrl,
        IReadOnlyDictionary<string, string> headers,
        double connectTimeout,
        double finalTimeout,
        StreamingConnectFactory connectFactory,
        BlockingCollection<object> queueOut,
        CancellationToken stopped,
        SpeculativeTurnTracker? speculativeTurns = null,
        int? pipelineIndex = null,
        Action<string>? log = null)
    {
        _protocol = protocol;
        _endpointUrl = endpointUrl;
        _headers = headers;
        _connectTimeout = connectTimeout;
        _finalTimeout = finalTimeout;
        _connectFactory = connectFactory;
        _queueOut = queueOut;
        _stopped = stopped;
        _speculativeTurns = speculativeTurns;
        _pipelineIndex = pipelineIndex;
        _log = log;

        _thread = new Thread(RunWorker) { Name = $"{protocol.Name}-session", IsBackground = true };
        _thread.Start();
    }

    public int Generation
    {
        get
        {
            lock (_generationLock)
            {
                return _generation;
            }
        }
    }

    public void AppendAudio(byte[] audio)
    {
        if (audio.Length == 0 || _stopped.IsCancellationRequested)
        {
            return;
        }

        _commands.Add(new CmdAppendAudio(Generation, audio));
    }

    public void StartTurn(string? turnId, int? turnRevision)
    {
        if (_stopped.IsCancellationRequested)
        {
            return;
        }

        _commands.Add(new CmdStartTurn(Generation, turnId, turnRevision));
    }

    public void DiscardUtterance()
    {
        if (_stopped.IsCancellationRequested)
        {
            return;
        }

        _commands.Add(new CmdDiscardAudio(Generation));
    }

    public void BeginCommit(string? turnId, int? turnRevision)
    {
        var generation = Generation;
        var commit = new StreamingCommit
        {
            Generation = generation,
            TurnId = turnId,
            TurnRevision = turnRevision,
            BoundaryQueuedAtSeconds = Monotonic.NowSeconds(),
        };
        lock (_commitLock)
        {
            var key = (generation, turnId, turnRevision);
            if (!_pendingCommits.TryGetValue(key, out var queue))
            {
                queue = new Queue<StreamingCommit>();
                _pendingCommits[key] = queue;
            }

            queue.Enqueue(commit);
        }

        _commands.Add(new CmdCommit { Commit = commit });
    }

    public bool HasPendingCommit(string? turnId, int? turnRevision)
    {
        var key = (Generation, turnId, turnRevision);
        lock (_commitLock)
        {
            return _pendingCommits.TryGetValue(key, out var queue) && queue.Count > 0;
        }
    }

    /// <summary>
    /// 取走一张挂起票据（没有就现开一张并入队）并阻塞等它落地。超时只在票据上
    /// 记错误立即返回——worker 仍然拥有真正的过期、迟到事件拒绝与关闭。
    /// </summary>
    public StreamingCommit? Commit(string? turnId, int? turnRevision)
    {
        var generation = Generation;
        var key = (generation, turnId, turnRevision);
        StreamingCommit? commit;
        lock (_commitLock)
        {
            if (_pendingCommits.TryGetValue(key, out var queue))
            {
                commit = queue.Count > 0 ? queue.Dequeue() : null;
                if (queue.Count == 0)
                {
                    _pendingCommits.Remove(key);
                }
            }
            else
            {
                commit = null;
            }
        }

        if (commit is null)
        {
            commit = new StreamingCommit
            {
                Generation = generation,
                TurnId = turnId,
                TurnRevision = turnRevision,
                BoundaryQueuedAtSeconds = Monotonic.NowSeconds(),
            };
            _commands.Add(new CmdCommit { Commit = commit });
        }

        var deadline = commit.BoundaryQueuedAtSeconds + _finalTimeout;
        while (!commit.Done.Wait(WaitSlice(deadline)))
        {
            if (_stopped.IsCancellationRequested || generation != Generation)
            {
                return null;
            }

            if (Monotonic.NowSeconds() >= deadline)
            {
                // Setup 或 socket I/O 可能正占着 worker。这里立即返回；
                // worker 仍负责过期、迟到事件拒绝与关闭。
                commit.Error = "streaming transcription timed out";
                break;
            }
        }

        return generation != Generation ? null : commit;
    }

    private static TimeSpan WaitSlice(double deadline)
    {
        var remaining = Math.Min(0.05, Math.Max(0.0, deadline - Monotonic.NowSeconds()));
        return TimeSpan.FromSeconds(remaining);
    }

    public void CancelSession()
    {
        int generation;
        lock (_generationLock)
        {
            generation = ++_generation;
        }

        // 已通过代际检查的发布者必须完成发射，CancelSession 返回后 SESSION_END 才能转发。
        lock (_publicationLock)
        {
        }

        lock (_commitLock)
        {
            _pendingCommits.Clear();
        }

        CloseConnection();
        _commands.Add(new CmdReset(generation));
    }

    public void Close()
    {
        CancelSession();
        _commands.Add(new CmdStop());
        _thread.Join(TimeSpan.FromSeconds(2));
        CloseConnection();
    }

    public void Dispose() => Close();

    // ── worker ────────────────────────────────────────────────────────

    private void CloseConnection()
    {
        lock (_connectionLock)
        {
            var connection = _connection;
            _connection = null;
            if (connection is not null)
            {
                try
                {
                    connection.Dispose();
                }
                catch (Exception ex)
                {
                    TranscriptLog.Error($"ignoring error while interrupting {_protocol.Name} STT connection", ex);
                }
            }
        }
    }

    private IStreamingSocket? TakeConnection()
    {
        lock (_connectionLock)
        {
            return _connection;
        }
    }

    private void PublishConnection(IStreamingSocket? connection)
    {
        lock (_connectionLock)
        {
            _connection = connection;
        }
    }

    private bool GenerationIsCurrent(int generation) => generation == Generation && !_stopped.IsCancellationRequested;

    private void RunWorker()
    {
        IStreamingSocket? connection = null;
        var workerGeneration = Generation;
        string? turnId = null;
        int? turnRevision = null;
        var utteranceStarted = false;
        var utteranceHasAudio = false;
        var remoteHypothesis = string.Empty;
        string? audioError = null;
        var audioErrorRequiresClose = false;
        StreamingCommit? activeCommit = null;
        string? activeItemId = null;
        int? activeContentIndex = null;
        var retiredItemIds = new HashSet<string>(StringComparer.Ordinal);
        var committedPrefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        var failedTurnIds = new HashSet<string?>(StringComparer.Ordinal);
        string? failedTurnMessage = null;
        var pendingUnassignedAudio = new Queue<CmdAppendAudio>();
        var deferredCommands = new Deque<StreamingCommand>();

        void ResetUtterance()
        {
            utteranceStarted = false;
            utteranceHasAudio = false;
            remoteHypothesis = string.Empty;
            turnId = null;
            turnRevision = null;
            activeItemId = null;
            activeContentIndex = null;
        }

        void PoisonTurn(string? failedTurnId, string message)
        {
            failedTurnIds.Clear();
            failedTurnIds.Add(failedTurnId);
            failedTurnMessage = message;
        }

        void ClearFailedTurn()
        {
            failedTurnIds.Clear();
            failedTurnMessage = null;
        }

        void RetireActiveItem()
        {
            if (activeItemId is not null)
            {
                retiredItemIds.Add(activeItemId);
            }
        }

        void FailCommit(StreamingCommit commit, string message)
        {
            commit.Error = message;
            commit.Done.Set();
        }

        void CloseConnectionLocal()
        {
            if (connection is not null)
            {
                try
                {
                    connection.Dispose();
                }
                catch (Exception ex)
                {
                    TranscriptLog.Error($"ignoring error while closing {_protocol.Name} STT connection", ex);
                }
            }

            connection = null;
            retiredItemIds.Clear();
            PublishConnection(null);
        }

        void FailConnection(Exception ex, string message = "streaming transcription connection failed")
        {
            TranscriptLog.Info($"{_protocol.Name} STT connection failed: {ex.GetType().Name}");
            CloseConnectionLocal();
            if (activeCommit is not null)
            {
                PoisonTurn(activeCommit.TurnId, message);
                FailCommit(activeCommit, message);
                activeCommit = null;
                ResetUtterance();
                audioError = null;
                audioErrorRequiresClose = false;
            }
            else if (utteranceStarted || utteranceHasAudio)
            {
                PoisonTurn(turnId, message);
                audioError = message;
                audioErrorRequiresClose = true;
            }
        }

        void DiscardUtteranceLocal()
        {
            pendingUnassignedAudio.Clear();
            if (connection is not null && (utteranceStarted || utteranceHasAudio))
            {
                var discardEvent = _protocol.DiscardUtterance();
                if (discardEvent is null || activeItemId is null)
                {
                    // 清字节没法圈住一个还没拿到身份的 item 的迟到转写——只能断连。
                    CloseConnectionLocal();
                }
                else
                {
                    try
                    {
                        Send(discardEvent);
                    }
                    catch (Exception ex)
                    {
                        TranscriptLog.Info($"{_protocol.Name} STT discard failed: {ex.GetType().Name}");
                        CloseConnectionLocal();
                    }
                }
            }

            RetireActiveItem();
            ResetUtterance();
            audioError = null;
            audioErrorRequiresClose = false;
            // 拒收一个未归属片段可以清掉它的失败；已知轮次重开缺音频时仍要失败。
            if (failedTurnIds.Contains(null))
            {
                ClearFailedTurn();
            }
        }

        void Send(JsonObject @event)
        {
            if (connection is null)
            {
                throw new StreamingTranscriptionException("streaming transcription is not connected");
            }

            connection.Send(@event.ToJsonString());
        }

        string ReceiveSetupEvent(string expected)
        {
            if (connection is null)
            {
                throw new StreamingTranscriptionException("streaming transcription is not connected");
            }

            var deadline = Monotonic.NowSeconds() + _connectTimeout;
            while (Monotonic.NowSeconds() < deadline)
            {
                var raw = connection.Receive(TimeSpan.FromSeconds(
                    Math.Min(0.05, Math.Max(0.0, deadline - Monotonic.NowSeconds()))));
                if (raw is null)
                {
                    continue;
                }

                var evt = DecodeEvent(raw);
                if ((string?)evt["type"] == "error")
                {
                    throw new StreamingTranscriptionException(StreamingSttText.RemoteErrorMessage(evt));
                }

                if ((string?)evt["type"] == expected)
                {
                    return raw;
                }
            }

            throw new StreamingTranscriptionException(
                $"streaming transcription setup timed out waiting for {expected}");
        }

        bool EnsureConnection()
        {
            if (connection is not null)
            {
                return true;
            }

            var setupStartedAt = Monotonic.NowSeconds();
            try
            {
                connection = _connectFactory(_endpointUrl, _headers, TimeSpan.FromSeconds(_connectTimeout));
                PublishConnection(connection);
                ReceiveSetupEvent("session.created");
                Send(_protocol.SessionUpdate());
                if (_protocol.RequiresSessionUpdated)
                {
                    ReceiveSetupEvent("session.updated");
                }
            }
            catch (Exception ex)
            {
                FailConnection(ex);
                return false;
            }

            TranscriptLog.Info(
                $"{_protocol.Name} STT connection setup completed in {Monotonic.NowSeconds() - setupStartedAt:F3}s");
            return true;
        }

        string CombinedHypothesis(string text)
        {
            var prefix = turnId is not null && committedPrefixes.TryGetValue(turnId, out var p) ? p : "";
            return StreamingSttText.JoinTranscripts(prefix, text);
        }

        void EmitPartial()
        {
            lock (_publicationLock)
            {
                if (remoteHypothesis.Length == 0 || turnId is null
                    || !GenerationIsCurrent(workerGeneration))
                {
                    return;
                }

                var output = new PartialTranscription
                {
                    Text = CombinedHypothesis(remoteHypothesis),
                    TurnId = turnId,
                    TurnRevision = turnRevision,
                };
                if (_speculativeTurns is not null
                    && !_speculativeTurns.IsLatest(output.TurnId, output.TurnRevision))
                {
                    return;
                }

                _queueOut.Add(output);
            }
        }

        void HandleProtocolEvent(ProtocolEvent evt)
        {
            if (activeCommit is not null
                && Monotonic.NowSeconds() >= activeCommit.BoundaryQueuedAtSeconds + _finalTimeout)
            {
                FailConnection(new TimeoutException(), "streaming transcription timed out");
                return;
            }

            if (evt.Kind == ProtocolEventKind.Ignore)
            {
                return;
            }

            if (evt.Kind == ProtocolEventKind.Committed)
            {
                if (activeCommit is null || evt.ItemId is null || retiredItemIds.Contains(evt.ItemId))
                {
                    TranscriptLog.Info($"ignoring retired or unowned {_protocol.Name} STT event (item={evt.ItemId})");
                    return;
                }

                if (activeItemId is null)
                {
                    activeItemId = evt.ItemId;
                }
                else if (evt.ItemId != activeItemId)
                {
                    TranscriptLog.Info($"ignoring unowned {_protocol.Name} STT event (item={evt.ItemId})");
                }

                return;
            }

            if (evt.ItemId is not null)
            {
                if (retiredItemIds.Contains(evt.ItemId)
                    || (activeCommit is null && !utteranceHasAudio)
                    || (evt.Kind == ProtocolEventKind.Completed && activeCommit is null))
                {
                    TranscriptLog.Info($"ignoring retired or unowned {_protocol.Name} STT event (item={evt.ItemId})");
                    return;
                }

                if (activeItemId is null)
                {
                    activeItemId = evt.ItemId;
                    activeContentIndex = evt.ContentIndex;
                }
                else if (evt.ItemId != activeItemId)
                {
                    TranscriptLog.Info(
                        $"ignoring unowned {_protocol.Name} STT event (item={evt.ItemId} content={evt.ContentIndex})");
                    return;
                }
                else if (activeContentIndex is null)
                {
                    activeContentIndex = evt.ContentIndex;
                }
                else if (evt.ContentIndex != activeContentIndex)
                {
                    TranscriptLog.Info(
                        $"ignoring unowned {_protocol.Name} STT event (item={evt.ItemId} content={evt.ContentIndex})");
                    return;
                }
            }
            else if (evt.Kind is ProtocolEventKind.Delta or ProtocolEventKind.Failed
                     && activeCommit is null && !utteranceHasAudio)
            {
                TranscriptLog.Info($"ignoring unowned {_protocol.Name} STT event without an item ID");
                return;
            }

            switch (evt.Kind)
            {
                case ProtocolEventKind.Delta:
                {
                    if (evt.Text.Length == 0)
                    {
                        return;
                    }

                    remoteHypothesis += evt.Text;
                    EmitPartial();
                    return;
                }

                case ProtocolEventKind.Error:
                {
                    TranscriptLog.Info($"{_protocol.Name} STT remote error: {evt.Message}");
                    var hasUnfinishedTurn = activeCommit is not null || utteranceStarted || utteranceHasAudio;
                    var failedTurnId = activeCommit?.TurnId ?? turnId;
                    CloseConnectionLocal();
                    const string message = "remote streaming transcription failed";
                    if (hasUnfinishedTurn)
                    {
                        PoisonTurn(failedTurnId, message);
                    }

                    if (activeCommit is not null)
                    {
                        FailCommit(activeCommit, message);
                        activeCommit = null;
                        ResetUtterance();
                        audioError = null;
                        audioErrorRequiresClose = false;
                    }
                    else if (hasUnfinishedTurn)
                    {
                        audioError = message;
                        audioErrorRequiresClose = true;
                    }

                    return;
                }

                case ProtocolEventKind.Failed:
                {
                    TranscriptLog.Info(
                        $"{_protocol.Name} STT transcription failed (item={evt.ItemId ?? "unknown"})");
                    const string message = "remote streaming transcription failed";
                    var failedTurnId = activeCommit?.TurnId ?? turnId;
                    PoisonTurn(failedTurnId, message);
                    RetireActiveItem();
                    if (activeCommit is not null)
                    {
                        FailCommit(activeCommit, message);
                        activeCommit = null;
                        audioError = null;
                    }
                    else
                    {
                        CloseConnectionLocal();
                        audioError = message;
                    }

                    audioErrorRequiresClose = false;
                    ResetUtterance();
                    return;
                }
            }

            if (activeCommit is null)
            {
                TranscriptLog.Info($"ignoring duplicate or unowned {_protocol.Name} STT completion");
                return;
            }

            RetireActiveItem();
            var combined = CombinedHypothesis(evt.Text);
            if (activeCommit.TurnId is not null)
            {
                committedPrefixes[activeCommit.TurnId] = combined;
            }

            activeCommit.Result = combined;
            activeCommit.Language = evt.Language;
            activeCommit.CompletedAtSeconds = Monotonic.NowSeconds();
            TranscriptLog.Info(
                $"{_protocol.Name} VAD commit to final transcript completed in "
                + $"{activeCommit.CompletedAtSeconds - activeCommit.BoundaryQueuedAtSeconds:F3}s "
                + $"turn={activeCommit.TurnId} rev={activeCommit.TurnRevision}");
            activeCommit.Done.Set();
            activeCommit = null;
            ResetUtterance();
            audioError = null;
            audioErrorRequiresClose = false;
        }

        while (true)
        {
            // VAD 可能在 process() 消费票据前就丢掉一个陈旧终稿。worker 拥有期限，
            // 也自己释放被推迟的音频。
            if (activeCommit is not null
                && Monotonic.NowSeconds() >= activeCommit.BoundaryQueuedAtSeconds + _finalTimeout)
            {
                FailConnection(new TimeoutException(), "streaming transcription timed out");
            }

            StreamingCommand? command = null;
            if (activeCommit is null && deferredCommands.Count > 0)
            {
                command = deferredCommands.PopFront();
            }
            else
            {
                _commands.TryTake(out command, TimeSpan.FromMilliseconds(10));
            }

            if (command is CmdStop stop)
            {
                _ = stop;
                if (activeCommit is not null)
                {
                    FailCommit(activeCommit, "streaming transcription stopped");
                }

                CloseConnectionLocal();
                return;
            }

            if (command is CmdReset reset)
            {
                workerGeneration = reset.Generation;
                if (activeCommit is not null)
                {
                    activeCommit.Done.Set();
                    activeCommit = null;
                }

                audioError = null;
                audioErrorRequiresClose = false;
                committedPrefixes.Clear();
                ClearFailedTurn();
                pendingUnassignedAudio.Clear();
                deferredCommands.Clear();
                ResetUtterance();
                CloseConnectionLocal();
                continue;
            }

            if (command is not null && command.Generation != Generation)
            {
                if (command is CmdCommit staleCommit)
                {
                    staleCommit.Commit.Done.Set();
                }

                continue;
            }

            // 服务端连接只允许一条在途转写。active commit 未落地前，后到轮次的命令
            // 本地保留——它们不能改动或污染在飞结果。
            if (activeCommit is not null
                && command is CmdStartTurn or CmdAppendAudio or CmdCommit or CmdDiscardAudio)
            {
                deferredCommands.PushBack(command);
                command = null;
            }

            switch (command)
            {
                case CmdDiscardAudio:
                    DiscardUtteranceLocal();
                    break;

                case CmdStartTurn startTurn:
                {
                    if (failedTurnIds.Count > 0)
                    {
                        if (failedTurnIds.Contains(null) && startTurn.TurnId is not null)
                        {
                            failedTurnIds.Clear();
                            failedTurnIds.Add(startTurn.TurnId);
                        }
                        else if (!failedTurnIds.Contains(startTurn.TurnId))
                        {
                            ClearFailedTurn();
                            audioError = null;
                            audioErrorRequiresClose = false;
                        }
                    }

                    turnId = startTurn.TurnId;
                    turnRevision = startTurn.TurnRevision;
                    if (startTurn.TurnId is not null && failedTurnIds.Contains(startTurn.TurnId))
                    {
                        audioError = failedTurnMessage ?? "streaming transcription failed";
                        pendingUnassignedAudio.Clear();
                    }
                    else
                    {
                        while (pendingUnassignedAudio.Count > 0)
                        {
                            // pop 后 prepend：保持原始顺序地插回队头。
                            deferredCommands.PushFront(pendingUnassignedAudio.Dequeue());
                        }
                    }

                    EmitPartial();
                    break;
                }

                case CmdAppendAudio appendAudio:
                {
                    if (failedTurnIds.Count > 0 && turnId is null)
                    {
                        pendingUnassignedAudio.Enqueue(appendAudio);
                    }
                    else if (audioError is null)
                    {
                        if (!EnsureConnection())
                        {
                            audioError = "streaming transcription connection failed";
                            audioErrorRequiresClose = true;
                            PoisonTurn(turnId, audioError);
                        }
                        else
                        {
                            try
                            {
                                if (!utteranceStarted)
                                {
                                    var startEvent = _protocol.StartUtterance();
                                    if (startEvent is not null)
                                    {
                                        Send(startEvent);
                                    }

                                    utteranceStarted = true;
                                }

                                Send(_protocol.AppendAudio(appendAudio.Audio));
                                utteranceHasAudio = true;
                            }
                            catch (Exception ex)
                            {
                                utteranceHasAudio = true;
                                FailConnection(ex);
                            }
                        }
                    }

                    break;
                }

                case CmdCommit commitCommand:
                {
                    var commit = commitCommand.Commit;
                    turnId = commit.TurnId;
                    turnRevision = commit.TurnRevision;
                    if (Monotonic.NowSeconds() >= commit.BoundaryQueuedAtSeconds + _finalTimeout)
                    {
                        activeCommit = commit;
                        FailConnection(new TimeoutException(), "streaming transcription timed out");
                    }
                    else if (audioError is not null)
                    {
                        var shouldClose = audioErrorRequiresClose;
                        FailCommit(commit, audioError);
                        audioError = null;
                        audioErrorRequiresClose = false;
                        ResetUtterance();
                        if (shouldClose)
                        {
                            CloseConnectionLocal();
                        }
                    }
                    else if (!utteranceHasAudio || connection is null)
                    {
                        FailCommit(commit, "streaming transcription received no audio");
                        ResetUtterance();
                    }
                    else
                    {
                        try
                        {
                            activeCommit = commit;
                            Send(_protocol.FinishUtterance());
                        }
                        catch (Exception ex)
                        {
                            FailConnection(ex);
                        }
                    }

                    break;
                }
            }

            if (connection is null)
            {
                continue;
            }

            string? rawEvent;
            try
            {
                // 排队的 PCM 不能等在一个阻塞读后面。active commit 在飞时短阻塞读避免轮询。
                rawEvent = connection.Receive(TimeSpan.FromSeconds(activeCommit is not null ? 0.01 : 0.0));
            }
            catch (Exception ex)
            {
                FailConnection(ex);
                continue;
            }

            if (rawEvent is null)
            {
                continue;
            }

            JsonObject evt;
            try
            {
                evt = DecodeEvent(rawEvent);
            }
            catch (Exception ex)
            {
                FailConnection(ex);
                continue;
            }

            HandleProtocolEvent(_protocol.ParseEvent(evt));
        }
    }

    private static JsonObject DecodeEvent(string raw)
    {
        var payload = JsonNode.Parse(raw);
        return payload as JsonObject
            ?? throw new ArgumentException("streaming transcription event must be an object");
    }
}

/// <summary>最小双端队列（worker 的 deferred_commands 需要 O(1) 两端操作）。</summary>
internal sealed class Deque<T>
{
    private readonly LinkedList<T> _items = new();

    public int Count => _items.Count;

    public void PushFront(T item) => _items.AddFirst(item);

    public void PushBack(T item) => _items.AddLast(item);

    public T PopFront()
    {
        var item = _items.First!.Value;
        _items.RemoveFirst();
        return item;
    }

    public void Clear() => _items.Clear();
}
