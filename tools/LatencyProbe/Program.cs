using System.Text.Json;
using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Json;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Tools.LatencyProbe;

/// <summary>
/// 时延账的本实现探针。
///
/// 读与 <c>tools/refprobe/latency_probe.py</c> 完全相同的脚本，驱动同一串操作。
/// 变更类操作不产出结果，只能由后续读操作观察到——比的是状态迁移，不是实现长相。
///
/// 唯一逐字符比的是 metadata 那段字符串：键序、<c>2.0</c> 而不是 <c>2</c>、
/// <c>1e-07</c> 而不是 <c>1E-07</c>，这些对客户端可见。
/// </summary>
internal static class Program
{
    private static int Main()
    {
        JsonNode? payload;
        using (var stdin = Console.OpenStandardInput())
        {
            payload = JsonNode.Parse(stdin);
        }

        if (payload?["ops"] is not JsonArray ops)
        {
            Console.Error.WriteLine("输入缺少 ops 数组");
            return 2;
        }

        var store = new TurnLatencyStore();
        var results = new JsonArray();
        foreach (var node in ops)
        {
            if (node is not JsonObject op)
            {
                Console.Error.WriteLine("ops 里出现非对象元素");
                return 2;
            }

            results.Add(Run(store, op));
        }

        var output = new JsonObject { ["results"] = results };

        // 用产品与探针共用的那份 options，不在这里另配编码器——另配一份的结果是
        // 产品修好了、探针还停在旧形态，diff 永远假绿。
        Console.Out.Write(output.ToJsonString(WireJson.Compact));
        Console.Out.Write('\n');
        return 0;
    }

    private static JsonNode? Run(TurnLatencyStore store, JsonObject op)
    {
        var name = op["op"]?.GetValue<string>() ?? throw new InvalidOperationException("op 缺少 op 字段");
        var turnId = op["turn_id"]?.GetValue<string>();
        var turnRevision = op["turn_revision"] is { } revision ? revision.GetValue<int>() : (int?)null;
        var responseKey = op["response_key"]?.GetValue<string>();

        switch (name)
        {
            case "for_turn":
                return Found(store.GetOrCreateForTurn(turnId, turnRevision) is not null);

            case "pending":
                return Found(store.PendingForTurn(turnId, turnRevision) is not null);

            case "discard_pending":
                store.DiscardPendingTurn(turnId, turnRevision);
                return null;

            case "stt":
                store.GetOrCreateForTurn(turnId, turnRevision)!.RecordStt(Seconds(op, "seconds"));
                return null;

            case "vad_settle":
                store.GetOrCreateForTurn(turnId, turnRevision)!.RecordVadSettle(Seconds(op, "seconds"));
                return null;

            case "smart":
                {
                    var tracker = store.GetOrCreateForTurn(turnId, turnRevision)!;
                    tracker.SmartTurnStatus = op["status"]?.GetValue<string>();
                    if (op["grace_s"] is { } grace)
                    {
                        tracker.SmartTurnGraceSeconds = grace.GetValue<double>();
                    }

                    if (op["delay_s"] is { } delay)
                    {
                        tracker.SmartTurnProcessingDelaySeconds = delay.GetValue<double>();
                    }

                    if (op["analysis_s"] is { } analysis)
                    {
                        tracker.SmartTurnAnalysisSeconds = analysis.GetValue<double>();
                    }

                    if (op["wait_s"] is { } wait)
                    {
                        tracker.SmartTurnWaitSeconds = wait.GetValue<double>();
                    }

                    return null;
                }

            case "smart_wait":
                store.RecordSmartWait(
                    turnId,
                    turnRevision,
                    Seconds(op, "started_s"),
                    Seconds(op, "ended_s"));
                return null;

            case "for_response":
                {
                    var tracker = store.GetOrCreateResponse(
                        responseKey!,
                        turnId,
                        turnRevision,
                        op["session_id"]?.GetValue<string>());
                    return new JsonObject
                    {
                        ["turn_id"] = tracker.TurnId,
                        ["turn_revision"] = tracker.TurnRevision,
                        ["source"] = tracker.Source,
                    };
                }

            case "bind_source":
                {
                    var tracker = store.GetResponse(responseKey);
                    if (tracker is not null)
                    {
                        tracker.Source = op["source"]?.GetValue<string>();
                    }

                    return null;
                }

            case "anchor_request":
                {
                    var tracker = store.GetResponse(responseKey);
                    if (tracker is not null)
                    {
                        var anchor = new SpeechLatencyAnchor { Requested = true };
                        tracker.SpeechAnchor = anchor;
                    }

                    return null;
                }

            case "anchor_attach_existing":
                {
                    // 工具续接链：同一条链上的后续响应共用说出那句话时的那份名额
                    var tracker = store.GetResponse(responseKey);
                    var source = store.GetResponse(op["from_response_key"]?.GetValue<string>());
                    if (tracker is not null && source is not null)
                    {
                        tracker.SpeechAnchor = source.SpeechAnchor;
                        if (source.SpeechAnchor is not null)
                        {
                            source.SpeechAnchor.Requested = true;
                        }
                    }

                    return null;
                }

            case "anchor_close":
                store.GetResponse(responseKey)?.SpeechAnchor?.Close();
                return null;

            case "anchor_available":
                {
                    var tracker = store.GetResponse(responseKey);
                    return new JsonObject
                    {
                        ["available"] = tracker?.SpeechAnchor?.Available,
                    };
                }

            case "record_llm":
                store.GetResponse(responseKey)!.RecordLlm(Seconds(op, "seconds"));
                return null;

            case "record_llm_ttft":
                store.GetResponse(responseKey)!.RecordLlmTtft(Seconds(op, "seconds"));
                return null;

            case "record_ttfa":
                store.GetResponse(responseKey)!.RecordTtsTtfa(Seconds(op, "seconds"));
                return null;

            case "record_e2e":
                return new JsonObject
                {
                    ["recorded"] = store.GetResponse(responseKey)!.RecordE2e(Seconds(op, "seconds")),
                };

            case "record_resp_ttfa":
                store.RecordResponseTtsTtfa(responseKey, Seconds(op, "seconds"));
                return null;

            case "record_resp_e2e":
                return new JsonObject
                {
                    ["recorded"] = store.RecordResponseE2e(responseKey, Seconds(op, "seconds")),
                };

            case "set_vad_decision":
                store.GetResponse(responseKey)!.VadDecisionSeconds = Seconds(op, "seconds");
                return null;

            case "mlx_wait":
                store.GetResponse(responseKey)!.RecordMlxLockWait(
                    Seconds(op, "seconds"), op["handler"]?.GetValue<string>());
                return null;

            case "mlx_hold":
                store.GetResponse(responseKey)!.RecordMlxLockHold(
                    Seconds(op, "seconds"), op["handler"]?.GetValue<string>());
                return null;

            case "set_status":
                store.GetResponse(responseKey)!.Status = op["status"]?.GetValue<string>() ?? TurnLatencyStatus.Completed;
                return null;

            case "pop":
                return Found(store.Pop(responseKey, op["session_id"]?.GetValue<string>()) is not null);

            case "discard_response":
                store.DiscardResponse(responseKey!, op["session_id"]?.GetValue<string>());
                return null;

            case "clear_session":
                store.ClearSession(op["session_id"]?.GetValue<string>() ?? string.Empty);
                return null;

            case "payload":
                {
                    var tracker = store.GetResponse(responseKey);
                    if (tracker is null)
                    {
                        return new JsonObject { ["found"] = false };
                    }

                    var status = op["status"]?.GetValue<string>() ?? TurnLatencyStatus.Completed;
                    var text = tracker.MetadataPayloadJson(responseKey!, status);
                    return new JsonObject
                    {
                        ["found"] = true,
                        ["json"] = text,
                        ["length"] = text.Length,
                    };
                }

            case "log":
                return new JsonObject
                {
                    ["line"] = store.GetResponse(responseKey)?.FormatLogLine(),
                };

            default:
                throw new InvalidOperationException($"未知操作：{name}");
        }
    }

    private static JsonObject Found(bool value) => new() { ["found"] = value };

    private static double Seconds(JsonObject op, string field)
        => op[field]?.GetValue<double>() ?? throw new InvalidOperationException($"op 缺少 {field}");
}
