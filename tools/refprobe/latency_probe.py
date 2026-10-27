"""时延账的参照侧探针：把一串操作喂给上游 TurnLatencyStore。

比的是**状态迁移**，不是字节：
- 变更类操作返回 null，只能通过后续读操作观察到 →
- 所有时间都由语料给定，不调用 perf_counter → 没有时钟就没有抖动
- 输出是纯量（found / recorded / 字符串），没有浮点输出、不需要容差

唯一「字节级」的地方是 payload：metadata 里存的就是那段压缩 JSON 字符串，
键序、浮点写法（2.0 而不是 2）都是对客户端可见的，所以要逐字符比。

自动化串跑，输入 JSON 走 stdin：
  {"ops": [{"op": "stt", "turn_id": "turn_1", "turn_revision": 0, "seconds": 0.18}, ...]}
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]


def _resolve_repo() -> Path:
    for candidate in (Path("C:/workshop/github/speech-to-speech"), REPO.parent.parent / "workshop" / "github" / "speech-to-speech"):
        if (candidate / "src" / "speech_to_speech" / "pipeline" / "turn_latency.py").is_file():
            return candidate
    raise SystemExit("找不到参照仓库（speech-to-speech）")


sys.path.insert(0, str(_resolve_repo() / "src"))

from speech_to_speech.pipeline.turn_latency import (  # noqa: E402
    SpeechLatencyAnchor,
    TurnLatencyStore,
)


def render_payload(tracker, response_key: str, status: str) -> dict[str, object]:
    text = json.dumps(
        tracker.metadata_payload(response_key=response_key, status=status),
        separators=(",", ":"),
        sort_keys=True,
    )
    return {"json": text, "length": len(text)}


def run(op: dict) -> object | None:
    name = op["op"]

    if name == "for_turn":
        return {"found": store.get_or_create_for_turn(op["turn_id"], op["turn_revision"]) is not None}

    if name == "pending":
        return {"found": store.pending_for_turn(op["turn_id"], op["turn_revision"]) is not None}

    if name == "discard_pending":
        store.discard_pending_turn(op["turn_id"], op["turn_revision"])
        return None

    if name == "stt":
        tracker = store.get_or_create_for_turn(op["turn_id"], op["turn_revision"])
        tracker.record_stt(op["seconds"])
        return None

    if name == "vad_settle":
        tracker = store.get_or_create_for_turn(op["turn_id"], op["turn_revision"])
        tracker.record_vad_settle(op["seconds"])
        return None

    if name == "smart":
        tracker = store.get_or_create_for_turn(op["turn_id"], op["turn_revision"])
        tracker.smart_turn_status = op["status"]
        if "grace_s" in op:
            tracker.smart_turn_grace_s = op["grace_s"]
        if "delay_s" in op:
            tracker.smart_turn_processing_delay_s = op["delay_s"]
        if "analysis_s" in op:
            tracker.smart_turn_analysis_s = op["analysis_s"]
        if "wait_s" in op:
            tracker.smart_turn_wait_s = op["wait_s"]
        return None

    if name == "smart_wait":
        store.record_smart_wait(op["turn_id"], op["turn_revision"], op["started_s"], op["ended_s"])
        return None

    if name == "for_response":
        tracker = store.get_or_create_response(
            op["response_key"],
            turn_id=op.get("turn_id"),
            turn_revision=op.get("turn_revision"),
            session_id=op.get("session_id"),
        )
        return {
            "turn_id": tracker.turn_id,
            "turn_revision": tracker.turn_revision,
            "source": tracker.source,
        }

    if name == "bind_source":
        tracker = store.get_response(op["response_key"])
        if tracker is not None:
            tracker.source = op["source"]
        return None

    if name == "anchor_request":
        tracker = store.get_response(op["response_key"])
        if tracker is not None:
            anchor = SpeechLatencyAnchor()
            tracker.speech_anchor = anchor
            anchor.requested = True
        return None

    if name == "anchor_attach_existing":
        # 同一轮的另一条响应共用一个名额（工具续接链）
        tracker = store.get_response(op["response_key"])
        source = store.get_response(op["from_response_key"])
        if tracker is not None and source is not None:
            tracker.speech_anchor = source.speech_anchor
            if source.speech_anchor is not None:
                source.speech_anchor.requested = True
        return None

    if name == "anchor_close":
        tracker = store.get_response(op["response_key"])
        if tracker is not None and tracker.speech_anchor is not None:
            tracker.speech_anchor.close()
        return None

    if name == "anchor_available":
        tracker = store.get_response(op["response_key"])
        return {"available": None if tracker is None or tracker.speech_anchor is None else tracker.speech_anchor.available}

    if name == "record_llm":
        store.get_response(op["response_key"]).record_llm(op["seconds"])
        return None

    if name == "record_llm_ttft":
        store.get_response(op["response_key"]).record_llm_ttft(op["seconds"])
        return None

    if name == "record_ttfa":
        store.get_response(op["response_key"]).record_tts_ttfa(op["seconds"])
        return None

    if name == "record_e2e":
        return {"recorded": store.get_response(op["response_key"]).record_e2e(op["seconds"])}

    if name == "record_resp_ttfa":
        from speech_to_speech.pipeline.turn_latency import record_response_tts_ttfa

        record_response_tts_ttfa(store, op["response_key"], op["seconds"])
        return None

    if name == "record_resp_e2e":
        from speech_to_speech.pipeline.turn_latency import record_response_e2e

        return {"recorded": record_response_e2e(store, op["response_key"], op["seconds"])}

    if name == "set_vad_decision":
        store.get_response(op["response_key"]).vad_decision_s = op["seconds"]
        return None

    if name == "mlx_wait":
        store.get_response(op["response_key"]).record_mlx_lock_wait(op["seconds"], op.get("handler"))
        return None

    if name == "mlx_hold":
        store.get_response(op["response_key"]).record_mlx_lock_hold(op["seconds"], op.get("handler"))
        return None

    if name == "set_status":
        store.get_response(op["response_key"]).status = op["status"]
        return None

    if name == "pop":
        return {"found": store.pop(op["response_key"], session_id=op.get("session_id")) is not None}

    if name == "discard_response":
        store.discard_response(op["response_key"], session_id=op.get("session_id"))
        return None

    if name == "clear_session":
        store.clear_session(op["session_id"])
        return None

    if name == "payload":
        tracker = store.get_response(op["response_key"])
        if tracker is None:
            return {"found": False}
        return {"found": True, **render_payload(tracker, op["response_key"], op["status"])}

    if name == "log":
        tracker = store.get_response(op["response_key"])
        return {"line": None if tracker is None else tracker.format_log_line()}

    raise SystemExit(f"未知操作：{name}")


payload = json.load(sys.stdin)
store = TurnLatencyStore()
results = [run(op) for op in payload["ops"]]
json.dump({"results": results}, sys.stdout, ensure_ascii=False)
