using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Tools.TurnsProbe;

/// <summary>
/// 投机轮次状态机的本实现探针。
///
/// 读与 <c>tools/refprobe/turns_probe.py</c> 完全相同的脚本，驱动同一批操作，
/// 按同样的形状输出每一步的可观察结果。两侧输出逐值比对——比的是状态迁移，
/// 不是谁的结构体字段多一个。
///
/// 两条归一化约定（与上游探针一致，见 <c>corpus/turns/README.md</c>）：
/// - 枚举一律输出线上的 snake_case 值（<c>soft_ended</c> 而非 <c>SoftEnded</c>）
/// - 浮点一律输出 6 位小数的字符串，绕开两侧十进制最短表示的差异
/// </summary>
internal static class Program
{
    private const int FloatDigits = 6;

    private static int Main()
    {
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin, new UTF8Encoding(false));
        var raw = reader.ReadToEnd();

        var document = JsonNode.Parse(raw)!.AsObject();
        var payload = document["payload"] as JsonObject ?? document;

        var clock = new VirtualClock(OptDouble(payload, "clock_start") ?? 1000.0);
        var tracker = new SpeculativeTurnTracker(() => clock.Now);

        var trace = new JsonArray();
        var steps = payload["steps"] as JsonArray ?? [];
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index]!.AsObject();
            var op = Str(step, "op");
            trace.Add(new JsonObject
            {
                ["index"] = index,
                ["op"] = op,
                ["result"] = RunStep(tracker, clock, step, op),
            });
        }

        var output = new JsonObject { ["trace"] = trace };
        Console.Out.Write(output.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Console.Out.Write('\n');
        return 0;
    }

    private static JsonNode? RunStep(
        SpeculativeTurnTracker tracker,
        VirtualClock clock,
        JsonObject step,
        string op)
    {
        switch (op)
        {
            case "advance":
                clock.Advance(OptDouble(step, "seconds") ?? 0);
                return null;

            case "reset":
                tracker.Reset();
                return null;

            case "start_turn":
                var (startId, startRevision) = tracker.StartTurn();
                return Pair(startId, startRevision);

            case "configure_reopen":
                tracker.ConfigureReopen(Int(step, "unanswered_reopen_ms"));
                return null;

            case "can_reopen":
                return tracker.CanReopen(Int(step, "audio_ms"));

            case "speech_started":
                var (turnId, revision, reopened) = tracker.SpeechStarted(Int(step, "audio_ms"));
                return new JsonArray { turnId, revision, reopened };

            case "speech_candidate_started":
                return tracker.SpeechCandidateStarted(Int(step, "audio_ms"));

            case "speech_candidate_cancelled":
                tracker.SpeechCandidateCancelled();
                return null;

            case "has_speech_candidate":
                return tracker.HasSpeechCandidate();

            case "segment_finalized":
                tracker.SegmentFinalized(
                    Int(step, "audio_ms"),
                    (int)(OptDouble(step, "output_hold_ms") ?? 0),
                    (int)(OptDouble(step, "processing_delay_ms") ?? 0));
                return null;

            case "segment_discarded":
                tracker.SegmentDiscarded();
                return null;

            case "processing_deadline":
                return Float(tracker.ProcessingDeadline(OptStr(step, "turn_id"), OptInt(step, "revision")));

            case "gate":
                return GateResult(tracker.Gate(
                    OptStr(step, "turn_id"),
                    OptInt(step, "revision"),
                    Bool(step, "hold_for_grace", true),
                    OptDouble(step, "hold_until"),
                    Bool(step, "commit", false)));

            case "is_latest":
                return tracker.IsLatest(OptStr(step, "turn_id"), OptInt(step, "revision"));

            case "is_current_turn":
                return tracker.IsCurrentTurn(OptStr(step, "turn_id"));

            case "is_committed":
                return tracker.IsCommitted(OptStr(step, "turn_id"), OptInt(step, "revision"));

            case "close":
                tracker.Close(OptStr(step, "turn_id"), OptInt(step, "revision"));
                return null;

            case "begin_reopen_candidate":
                return tracker.BeginReopenCandidate(OptStr(step, "turn_id"), OptInt(step, "revision"));

            case "confirm_reopen_candidate":
                return tracker.ConfirmReopenCandidate(
                    OptStr(step, "turn_id"),
                    OptInt(step, "base_revision"),
                    OptInt(step, "candidate_revision"));

            case "cancel_reopen_candidate":
                tracker.CancelReopenCandidate(OptStr(step, "turn_id"), OptInt(step, "candidate_revision"));
                return null;

            case "start_reopen_grace":
                tracker.StartReopenGrace(
                    OptStr(step, "turn_id"),
                    OptInt(step, "revision"),
                    OptDouble(step, "grace_seconds") ?? 0);
                return null;

            case "observe":
                var (observedId, observedRevision) = tracker.CurrentTurn();
                return new JsonObject
                {
                    ["phase"] = PhaseName(tracker.Phase),
                    ["has_candidate"] = tracker.HasSpeechCandidate(),
                    ["turn"] = new JsonArray(observedId, observedRevision),
                };

            default:
                throw new InvalidOperationException($"unknown op: {op}");
        }
    }

    private static JsonObject GateResult(TurnGate gate) => new()
    {
        ["action"] = gate.Action switch
        {
            TurnGateAction.Accept => "accept",
            TurnGateAction.Drop => "drop",
            _ => "hold",
        },
        ["deadline"] = Float(gate.Deadline),
    };

    private static string? PhaseName(TurnPhase? phase) => phase switch
    {
        TurnPhase.Listening => "listening",
        TurnPhase.SoftEnded => "soft_ended",
        TurnPhase.Answering => "answering",
        TurnPhase.Closed => "closed",
        _ => null,
    };

    private static JsonArray Pair(string turnId, int revision) => new(turnId, revision);

    private static JsonNode? Float(double? value)
        => value is { } number ? number.ToString("F" + FloatDigits, CultureInfo.InvariantCulture) : null;

    private static string Str(JsonObject node, string name) => node[name]!.GetValue<string>();

    private static string? OptStr(JsonObject node, string name)
        => node[name] is { } value ? value.GetValue<string>() : null;

    private static int Int(JsonObject node, string name) => node[name]!.GetValue<int>();

    private static int? OptInt(JsonObject node, string name)
        => node[name] is { } value ? value.GetValue<int>() : null;

    private static double? OptDouble(JsonObject node, string name)
        => node[name] is { } value ? value.GetValue<double>() : null;

    private static bool Bool(JsonObject node, string name, bool fallback)
        => node[name] is { } value ? value.GetValue<bool>() : fallback;

    private sealed class VirtualClock(double start)
    {
        public double Now { get; private set; } = start;

        public void Advance(double seconds) => Now += seconds;
    }
}
