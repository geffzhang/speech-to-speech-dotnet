using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// 终态响应的时延状态。字符串值与上游 <c>TurnLatencyStatus</c> 逐字相同。
/// 与 <see cref="Pipeline.TurnEndStatus"/> 不是同一个东西：后者是 Smart Turn 的判定结论，
/// 前者是这条响应最终怎么了。
/// </summary>
public static class TurnLatencyStatus
{
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Incomplete = "incomplete";
}

/// <summary>这条响应的输入是谁给的。客户端不可见，只进遥测。</summary>
public static class TurnLatencySource
{
    /// <summary>首个由用户语音触发的响应（含关闭自动响应后的手动 create）。</summary>
    public const string Speech = "speech";

    /// <summary>客户端独立创建的响应，与任何语音无关。</summary>
    public const string Client = "client";

    /// <summary>经函数调用身份续接出来的响应。</summary>
    public const string Tool = "tool";
}

/// <summary>metadata 里保留的键。客户端自带同名键会被服务端测量覆盖。</summary>
public static class TurnLatencyMetadata
{
    public const string Key = "speech_to_speech.turn_latency";

    /// <summary>Realtime 元数据值的长度上限。</summary>
    public const int MaxValueLength = 512;

    /// <summary>Realtime 元数据的槽位上限。</summary>
    public const int MaxSlots = 16;
}

/// <summary>
/// 一次语音输入的「E2E 资格」。一条响应链上只有第一个真正产出音频的响应
/// 能记 <c>e2e_s</c>：说话的开场白已经量过一次，后续工具回答再量一次就是重复计账。
/// </summary>
public sealed class SpeechLatencyAnchor
{
    private readonly Lock _lock = new();
    private bool _closed;

    /// <summary>是否被某条响应认领过。认领之后关掉不会清掉这个标记。</summary>
    public bool Requested { get; set; }

    /// <summary>是否还能记一次 E2E。</summary>
    public bool Available
    {
        get
        {
            lock (_lock)
            {
                return !_closed;
            }
        }
    }

    /// <summary>取用唯一的 E2E 名额。只有第一次返回 true。</summary>
    public bool Consume()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }

            _closed = true;
            return true;
        }
    }

    /// <summary>关掉名额（该不该关由 <see cref="ShouldClose"/> 判定）。</summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
        }
    }

    /// <summary>
    /// 终态时该不该关掉这条链的 E2E 名额。
    ///
    /// 抽成纯函数的理由与 <see cref="TurnEndTiming"/> 相同：这条规则只有两种结论，
    /// 写在收尾流程里就没法在没有 socket 的情况下断言，只能靠「读代码觉得对」。
    /// 判据是上游 <c>_log_turn_latency</c> 的一句表达式：
    /// <c>status != "completed" or not pending_function_calls</c>——带出待执行函数调用的
    /// completed 响应<b>不</b>关名额，因为同一条链上还有回答要量。
    /// </summary>
    public static bool ShouldClose(string status, int pendingFunctionCalls)
        => status != "completed" || pendingFunctionCalls == 0;
}

/// <summary>单个 handler 占 MLX 锁的累计量，以及背后有几次获取。</summary>
public sealed class MlxLockStat
{
    public double TotalSeconds { get; private set; }

    public int Count { get; private set; }

    public void Add(double seconds)
    {
        TotalSeconds += seconds;
        Count++;
    }

    public void Merge(MlxLockStat other)
    {
        TotalSeconds += other.TotalSeconds;
        Count += other.Count;
    }
}

/// <summary>
/// 一条响应的服务端计时账。对应上游 <c>TurnLatencyTracker</c>。
///
/// 几条不是「加总分解」的语义，写错了不会报错但数字会骗人：
/// - 各阶段**互相重叠**，加起来不等于 E2E。
/// - <c>llm_ttft_s</c> 止于首个非空白文本增量，<c>llm_s</c> 覆盖整段生成（含等锁）。
/// - <c>tts_ttfa_s</c> 止于首个模型音频分片，早于裁剪与分块组装。
/// - <c>e2e_s</c> 从**估计的语音结束**到首个产出音频，全程只取一次。
/// - <c>vad_settle_s</c> 是整个 STT 输入闸门的等待，<c>smart_turn_wait_s</c> 只覆盖
///   客户端要求的处理延迟——两者名字像，含义不同。
/// </summary>
public sealed class TurnLatencyTracker
{
    private static readonly (double Start, double End)[] NoIntervals = [];

    private List<(double Start, double End)>? _smartWaitIntervals;

    public string? TurnId { get; set; }

    public int? TurnRevision { get; set; }

    public string? Source { get; set; }

    public SpeechLatencyAnchor? SpeechAnchor { get; set; }

    public double? SttSeconds { get; set; }

    public double? LlmTtftSeconds { get; set; }

    public double? LlmSeconds { get; set; }

    public double? TtsTtfaSeconds { get; set; }

    public double? E2eSeconds { get; set; }

    public double? VadSettleSeconds { get; set; }

    public double? VadDecisionSeconds { get; set; }

    public double? SmartTurnAnalysisSeconds { get; set; }

    public string? SmartTurnStatus { get; set; }

    public double? SmartTurnGraceSeconds { get; set; }

    public double? SmartTurnProcessingDelaySeconds { get; set; }

    public double? SmartTurnWaitSeconds { get; set; }

    public double MlxLockWaitSeconds { get; set; }

    public double MlxLockHoldSeconds { get; set; }

    /// <summary>按 handler 名归集的等锁时间。用列表而不是字典：日志里的括号顺序就是首次出现的顺序。</summary>
    public List<KeyValuePair<string, MlxLockStat>> MlxLockWaits { get; } = [];

    public List<KeyValuePair<string, MlxLockStat>> MlxLockHolds { get; } = [];

    public string Status { get; set; } = TurnLatencyStatus.Completed;

    public void RecordStt(double seconds) => SttSeconds = Math.Max(0.0, seconds);

    public void RecordLlm(double seconds) => LlmSeconds = Math.Max(0.0, seconds);

    public void RecordVadSettle(double seconds) => VadSettleSeconds = Math.Max(0.0, seconds);

    /// <summary>
    /// 段定稿时一次性写四项。上游在 <c>_process_realtime</c> 里就是这么成组写的，
    /// 拆成四个 setter 由调用方各写一处，漏写不会报错，只会让 metadata 里那格恒为 null。
    /// </summary>
    /// <param name="vadDecisionSeconds">见 <see cref="VadDecisionTiming"/>；无锚点时为 null。</param>
    /// <param name="decision">端点判定结论（宽限、处理延迟、状态、判定耗时）。</param>
    public void RecordSegmentFinalized(double? vadDecisionSeconds, TurnEndDecision decision)
    {
        VadDecisionSeconds = vadDecisionSeconds;
        SmartTurnStatus = decision.Status;
        SmartTurnAnalysisSeconds = decision.AnalysisSeconds;

        if (decision.Status == TurnEndStatus.Disabled)
        {
            // 没有判定器时上游不动这三项：宽限就是配置里的普通投机窗口，
            // 记成「Smart Turn 宽限」会在时延分解里凭空多出一段并不存在的等待。
            return;
        }

        SmartTurnGraceSeconds = decision.GraceMs / 1000.0;
        SmartTurnProcessingDelaySeconds = decision.ProcessingDelayMs / 1000.0;

        // 计时归零，但**不清已记区间**。上游同样只写字段不清 _smart_wait_intervals——
        // 这里刻意与上游一致：真到了「先有等待、后定稿」的顺序，重置计数却保留区间
        // 会让下一次累加把旧区间重新算进来，那是上游的行为，不是我们该擅自修正的。
        SmartTurnWaitSeconds = 0.0;
    }

    /// <summary>只记第一次。</summary>
    public void RecordLlmTtft(double seconds) => LlmTtftSeconds ??= Math.Max(0.0, seconds);

    /// <summary>只记第一次：后面几段合成再快也不能把首包时间改小。</summary>
    public void RecordTtsTtfa(double seconds) => TtsTtfaSeconds ??= Math.Max(0.0, seconds);

    /// <summary>
    /// 记一次 E2E。已记过、或语音名额已被同链前一条响应取走时返回 false。
    /// </summary>
    public bool RecordE2e(double seconds)
    {
        if (E2eSeconds is not null)
        {
            return false;
        }

        if (SpeechAnchor is not null && !SpeechAnchor.Consume())
        {
            return false;
        }

        E2eSeconds = Math.Max(0.0, seconds);
        return true;
    }

    public void RecordMlxLockWait(double seconds, string? handlerName = null)
    {
        if (seconds <= 0.0)
        {
            return;
        }

        MlxLockWaitSeconds += seconds;
        if (!string.IsNullOrEmpty(handlerName))
        {
            Stat(MlxLockWaits, handlerName).Add(seconds);
        }
    }

    public void RecordMlxLockHold(double seconds, string? handlerName = null)
    {
        if (seconds <= 0.0)
        {
            return;
        }

        MlxLockHoldSeconds += seconds;
        if (!string.IsNullOrEmpty(handlerName))
        {
            Stat(MlxLockHolds, handlerName).Add(seconds);
        }
    }

    /// <summary>
    /// 记一段真实的闸门等待。多个 worker 可能一起等同一段，所以记的是**区间并集**，
    /// 不是各段相加——相加会把 4 个 worker 同时等 0.2 秒算成 0.8 秒。
    /// </summary>
    public void RecordSmartWait(double startedAtSeconds, double endedAtSeconds)
    {
        if (SmartTurnStatus is not (TurnEndStatus.Complete or TurnEndStatus.Incomplete or TurnEndStatus.Failed)
            || endedAtSeconds <= startedAtSeconds)
        {
            return;
        }

        var intervals = _smartWaitIntervals ??= [];
        intervals.Add((startedAtSeconds, endedAtSeconds));
        intervals.Sort();

        var merged = new List<(double Start, double End)>(intervals.Count);
        foreach (var (start, end) in intervals)
        {
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        _smartWaitIntervals = merged;
        var total = 0.0;
        foreach (var (start, end) in merged)
        {
            total += end - start;
        }

        SmartTurnWaitSeconds = total;
    }

    /// <summary>
    /// 终态测量值。缺的写 JSON null 而不是省略——省略会让客户端以为「这一版没有这个字段」。
    ///
    /// 这是**唯一的产地**：metadata 里那个字符串只能从这里出来。另写一份 JsonObject
    /// 版本必然漂（浮点渲染规则一改，两处只改一处）。
    /// </summary>
    public string MetadataPayloadJson(string responseKey, string status)
    {
        var buffer = new MemoryStream(160);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            // 键序是 sort_keys=True 的字母序。这不是随手排的：客户端按字符串做比对与去重的场景
            // 真实存在，键序变了 hash 就变。
            writer.WriteStartObject();
            WriteNumber(writer, "e2e_s", E2eSeconds);
            WriteNumber(writer, "hold_s", Compact(SmartTurnWaitSeconds));
            WriteNumber(writer, "llm_s", LlmSeconds);
            writer.WriteString("response_key", responseKey);
            WriteNullableString(writer, "smart_status", SmartTurnStatus);
            WriteNullableString(writer, "source", Source);
            writer.WriteString("status", status);
            WriteNumber(writer, "stt_s", SttSeconds);
            WriteNumber(writer, "tts_ttfa_s", TtsTtfaSeconds);
            WriteNullableString(writer, "turn_id", TurnId);
            writer.WriteNumber("turn_revision", TurnRevision ?? 0);
            WriteNumber(writer, "vad_decision_s", Compact(VadDecisionSeconds));
            writer.WriteNumber("version", 2);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>把待定槽位里 STT 阶段的测量并进来。响应建立时调用一次。</summary>
    public void AbsorbPending(TurnLatencyTracker pending)
    {
        if (pending.SttSeconds is not null)
        {
            SttSeconds = pending.SttSeconds;
        }

        if (pending.VadSettleSeconds is not null)
        {
            VadSettleSeconds = pending.VadSettleSeconds;
        }

        MlxLockWaitSeconds += pending.MlxLockWaitSeconds;
        MlxLockHoldSeconds += pending.MlxLockHoldSeconds;
        Merge(MlxLockWaits, pending.MlxLockWaits);
        Merge(MlxLockHolds, pending.MlxLockHolds);
        VadDecisionSeconds = pending.VadDecisionSeconds;
        SmartTurnAnalysisSeconds = pending.SmartTurnAnalysisSeconds;
        SmartTurnStatus = pending.SmartTurnStatus;
        SmartTurnGraceSeconds = pending.SmartTurnGraceSeconds;
        SmartTurnProcessingDelaySeconds = pending.SmartTurnProcessingDelaySeconds;
        SmartTurnWaitSeconds = pending.SmartTurnWaitSeconds;
        _smartWaitIntervals = pending._smartWaitIntervals;
    }

    /// <summary>人类可读的一行日志。上游只在 <c>turn_id</c> 与 <c>source</c> 都空时返回 null。</summary>
    public string? FormatLogLine()
    {
        if (TurnId is null && Source is null)
        {
            return null;
        }

        var revision = TurnRevision ?? 0;
        var settle = VadSettleSeconds is null ? string.Empty : $"vad_settle={Format(VadSettleSeconds)} ";
        var mlx = string.Empty;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            mlx = $" mlx_lock_wait={FormatLock(MlxLockWaitSeconds, MlxLockWaits)}";
            if (MlxLockHoldSeconds > 0.0)
            {
                mlx += $" mlx_lock_hold={FormatLock(MlxLockHoldSeconds, MlxLockHolds)}";
            }
        }

        // 已经出过声的取消不是「没说完」，是「说到一半被打断」，要能一眼分辨。
        var cut = Status == TurnLatencyStatus.Cancelled && TtsTtfaSeconds is not null ? " tts=cut" : string.Empty;
        var source = Source is null ? string.Empty : " source=" + Source;
        var turnId = string.IsNullOrEmpty(TurnId) ? "n/a" : TurnId;

        return $"Turn {turnId} rev={revision} latency: "
            + $"stt={Format(SttSeconds)} "
            + $"llm={Format(LlmSeconds)} "
            + $"tts_ttfa={Format(TtsTtfaSeconds)} e2e={Format(E2eSeconds)} "
            + settle
            + $"vad_decision={Format(VadDecisionSeconds)} "
            + $"hold={Format(SmartTurnWaitSeconds)} "
            + $"smart_turn_status={SmartTurnStatus ?? "n/a"} "
            + $"status={Status}{mlx}{cut}{source}";
    }

    private static string Format(double? seconds)
        => seconds is null ? "n/a" : seconds.Value.ToString("F2", CultureInfo.InvariantCulture) + "s";

    private static string FormatLock(double total, List<KeyValuePair<string, MlxLockStat>> stats)
    {
        if (stats.Count == 0)
        {
            return total.ToString("F2", CultureInfo.InvariantCulture) + "s";
        }

        var named = string.Join(",", stats.Select(kv =>
            kv.Key + ":" + kv.Value.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + "s"
            + (kv.Value.Count > 1 ? "x" + kv.Value.Count.ToString(CultureInfo.InvariantCulture) : string.Empty)));
        return total.ToString("F2", CultureInfo.InvariantCulture) + "s[" + named + "]";
    }

    /// <summary>上游 <c>round(seconds, 9)</c>：只有 VAD 与 Smart Turn 新字段用它。</summary>
    private static double? Compact(double? seconds)
        => seconds is null ? null : Math.Round(seconds.Value, 9, MidpointRounding.ToEven);

    private static MlxLockStat Stat(List<KeyValuePair<string, MlxLockStat>> stats, string name)
    {
        for (var i = 0; i < stats.Count; i++)
        {
            if (stats[i].Key == name)
            {
                return stats[i].Value;
            }
        }

        var created = new MlxLockStat();
        stats.Add(new KeyValuePair<string, MlxLockStat>(name, created));
        return created;
    }

    private static void Merge(
        List<KeyValuePair<string, MlxLockStat>> target,
        List<KeyValuePair<string, MlxLockStat>> other)
    {
        foreach (var (name, stat) in other)
        {
            Stat(target, name).Merge(stat);
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, double? value)
    {
        writer.WritePropertyName(name);
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(RenderFloat(value.Value));
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteString(name, value);
    }

    /// <summary>
    /// 与 Python <c>json.dumps</c> 的浮点渲染对齐。两处差异必须补上，
    /// 否则 <c>2.0</c> 会写成 <c>2</c>、<c>1e-07</c> 会写成 <c>1E-07</c>——
    /// 数字相等但字符串不等，而 metadata 里存的就是字符串。
    /// </summary>
    internal static string RenderFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        text = text.Replace('E', 'e');
        if (text.IndexOf('.') < 0 && text.IndexOf('e') < 0)
        {
            text += ".0";
        }

        return text;
    }
}

/// <summary>
/// 时延账的存储。对应上游 <c>TurnLatencyStore</c>。
///
/// 三件在别处容易做丢的事：
/// - <b>STT 先于 response_key 存在</b>。最终转写跑完时还没有响应键，所以先挂在
///   「按轮次」的待定槽位上，响应建立时再并进去。就地丢弃会永久丢掉 stt_s。
/// - <b>worker 只查不建</b>。响应键由服务端在入队之前创建；被取消的 worker 若能
///   自己新建，就会给一条已经没人要的响应留下一条永远不结算的账。
/// - <b>会话清理要能只清自己</b>。<c>ClearSession</c> 按会话索引摘除在途测量，
///   最后一个会话摘完才顺带清待定槽位——待定槽位不带会话标记，这是单会话
///   管线模型下的近似，与上游一致。
/// </summary>
public sealed class TurnLatencyStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TurnLatencyTracker> _trackers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string TurnId, int Revision), TurnLatencyTracker> _pendingTurn = [];
    private readonly Dictionary<(string TurnId, int Revision), string> _turnResponses = [];
    private readonly Dictionary<string, HashSet<string>> _sessionKeys = new(StringComparer.Ordinal);

    public static (string TurnId, int Revision) TurnKey(string turnId, int? turnRevision)
        => (turnId, turnRevision ?? 0);

    /// <summary>只读待定槽位，不新建。</summary>
    public TurnLatencyTracker? PendingForTurn(string? turnId, int? turnRevision)
    {
        if (turnId is null)
        {
            return null;
        }

        lock (_lock)
        {
            return _pendingTurn.GetValueOrDefault(TurnKey(turnId, turnRevision));
        }
    }

    public TurnLatencyTracker? GetOrCreateForTurn(string? turnId, int? turnRevision)
    {
        if (turnId is null)
        {
            return null;
        }

        var key = TurnKey(turnId, turnRevision);
        lock (_lock)
        {
            if (!_pendingTurn.TryGetValue(key, out var tracker))
            {
                tracker = new TurnLatencyTracker { TurnId = turnId, TurnRevision = key.Revision };
                _pendingTurn[key] = tracker;
            }

            return tracker;
        }
    }

    /// <summary>丢掉一条不会被响应消费的最终 STT 测量。</summary>
    public void DiscardPendingTurn(string? turnId, int? turnRevision)
    {
        if (turnId is null)
        {
            return;
        }

        lock (_lock)
        {
            _pendingTurn.Remove(TurnKey(turnId, turnRevision));
        }
    }

    public TurnLatencyTracker GetOrCreateResponse(
        string responseKey,
        string? turnId = null,
        int? turnRevision = null,
        string? sessionId = null)
    {
        lock (_lock)
        {
            if (!_trackers.TryGetValue(responseKey, out var tracker))
            {
                tracker = new TurnLatencyTracker { TurnId = turnId, TurnRevision = turnRevision };
                _trackers[responseKey] = tracker;
                if (sessionId is not null)
                {
                    Session(sessionId).Add(responseKey);
                }

                if (turnId is not null)
                {
                    var key = TurnKey(turnId, turnRevision);
                    _turnResponses.TryAdd(key, responseKey);
                    if (_pendingTurn.Remove(key, out var pending))
                    {
                        tracker.AbsorbPending(pending);
                        if (tracker.TurnId is null)
                        {
                            tracker.TurnId = pending.TurnId;
                            tracker.TurnRevision = pending.TurnRevision;
                        }
                    }
                }
            }
            else if (turnId is not null && tracker.TurnId is null)
            {
                tracker.TurnId = turnId;
                tracker.TurnRevision = turnRevision;
            }

            return tracker;
        }
    }

    /// <summary>
    /// 记一段闸门等待。落到该轮次**已有响应**的账上；还没有响应就落待定槽位。
    /// 不能反过来（先建账再找槽位），否则会在取消路径上凭空造出账目。
    /// </summary>
    public void RecordSmartWait(string? turnId, int? turnRevision, double startedAtSeconds, double endedAtSeconds)
    {
        if (turnId is null)
        {
            return;
        }

        lock (_lock)
        {
            var key = TurnKey(turnId, turnRevision);
            TurnLatencyTracker? tracker = null;
            if (_turnResponses.TryGetValue(key, out var responseKey))
            {
                _trackers.TryGetValue(responseKey, out tracker);
            }
            else
            {
                _pendingTurn.TryGetValue(key, out tracker);
            }

            tracker?.RecordSmartWait(startedAtSeconds, endedAtSeconds);
        }
    }

    /// <summary>只查已有响应，不复活被取消的测量。</summary>
    public TurnLatencyTracker? GetResponse(string? responseKey)
    {
        if (responseKey is null)
        {
            return null;
        }

        lock (_lock)
        {
            return _trackers.GetValueOrDefault(responseKey);
        }
    }

    public TurnLatencyTracker? Pop(string? responseKey, string? sessionId = null)
    {
        if (responseKey is null)
        {
            return null;
        }

        lock (_lock)
        {
            if (!_trackers.Remove(responseKey, out var tracker))
            {
                DetachFromSession(sessionId, responseKey);
                return null;
            }

            if (tracker.TurnId is not null)
            {
                var key = TurnKey(tracker.TurnId, tracker.TurnRevision);
                if (_turnResponses.TryGetValue(key, out var mapped) && mapped == responseKey)
                {
                    _turnResponses.Remove(key);
                }
            }

            DetachFromSession(sessionId, responseKey);
            return tracker;
        }
    }

    public void DiscardResponse(string responseKey, string? sessionId = null) => Pop(responseKey, sessionId);

    public void ClearSession(string sessionId)
    {
        lock (_lock)
        {
            // 注意：会话可能从未登记过（STT 跑过但一次响应都没建）。此时仍要走下面的
            // 「最后一个会话清空待定槽位」——提前 return 会让那些槽位永久留在表里。
            if (_sessionKeys.Remove(sessionId, out var keys))
            {
                foreach (var responseKey in keys)
                {
                    if (!_trackers.Remove(responseKey, out var tracker) || tracker.TurnId is null)
                    {
                        continue;
                    }

                    var key = TurnKey(tracker.TurnId, tracker.TurnRevision);
                    if (_turnResponses.TryGetValue(key, out var mapped) && mapped == responseKey)
                    {
                        _turnResponses.Remove(key);
                    }
                }
            }

            if (_sessionKeys.Count == 0)
            {
                _pendingTurn.Clear();
            }
        }
    }

    /// <summary>把首个 TTS 音频记到已有响应上。没有响应就什么都不做（不新建）。</summary>
    public void RecordResponseTtsTtfa(string? responseKey, double seconds)
        => GetResponse(responseKey)?.RecordTtsTtfa(seconds);

    /// <summary>把可用的语音 E2E 记到已有响应上；返回是否真的记下了首个样本。</summary>
    public bool RecordResponseE2e(string? responseKey, double seconds)
        => GetResponse(responseKey)?.RecordE2e(seconds) ?? false;

    private HashSet<string> Session(string sessionId)
    {
        if (!_sessionKeys.TryGetValue(sessionId, out var keys))
        {
            keys = new HashSet<string>(StringComparer.Ordinal);
            _sessionKeys[sessionId] = keys;
        }

        return keys;
    }

    private void DetachFromSession(string? sessionId, string responseKey)
    {
        if (sessionId is null)
        {
            return;
        }

        if (!_sessionKeys.TryGetValue(sessionId, out var keys))
        {
            return;
        }

        keys.Remove(responseKey);
        if (keys.Count == 0)
        {
            _sessionKeys.Remove(sessionId);
        }
    }
}
