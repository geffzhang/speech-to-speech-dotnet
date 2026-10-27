"""生成 corpus/latency 的 input.json（不参与构建，留作复现记录）。

每条一个自包含脚本：只描述「按什么顺序做了什么」，不描述期望结果。
期望结果只能由参照实现录制（diff_corpus.py record），手写基线会掩盖探针本身的错。
"""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "corpus" / "latency"

T = {"turn_id": "turn_1", "turn_revision": 0}


def case(name: str, ops: list[dict]) -> dict[str, object]:
    return {"file": name, "payload": {"ops": ops}}


CASES = [
    # 一轮说完：STT 的账先挂在待定槽位，响应建立时并进来。
    # stt_s 与 vad_settle_s 分属两个字段——vad_settle 只进日志，不进 metadata。
    case(
        "full-turn-completed",
        [
            {"op": "stt", **T, "seconds": 0.18},
            {"op": "vad_settle", **T, "seconds": 0.61},
            {"op": "smart", **T, "status": "complete", "grace_s": 0.8, "delay_s": 0.0, "analysis_s": 0.037},
            {"op": "smart_wait", **T, "started_s": 100.0, "ended_s": 100.24},
            {"op": "for_response", "response_key": "resp_1", **T, "session_id": "s1"},
            {"op": "bind_source", "response_key": "resp_1", "source": "speech"},
            {"op": "record_llm", "response_key": "resp_1", "seconds": 1.24},
            {"op": "record_ttfa", "response_key": "resp_1", "seconds": 0.12},
            {"op": "record_e2e", "response_key": "resp_1", "seconds": 2.01},
            {"op": "set_vad_decision", "response_key": "resp_1", "seconds": 0.36},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
            {"op": "log", "response_key": "resp_1"},
        ],
    ),
    # 挂起等待记的是区间并集：两个 worker 同时等同一段，各记一次不能相加。
    # 相加会把 0.4 秒的等待算成 0.6 秒，然后没人看得出来哪个数是错的。
    case(
        "hold-union-overlapping",
        [
            {"op": "smart", **T, "status": "incomplete", "grace_s": 2.0, "delay_s": 0.6},
            {"op": "for_response", "response_key": "resp_1", **T},
            {"op": "smart_wait", **T, "started_s": 10.0, "ended_s": 10.3},
            {"op": "smart_wait", **T, "started_s": 10.2, "ended_s": 10.4},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
        ],
    ),
    # 判定器不可用时 hold 恒为 null，即使有人硬记一段等待也不该出现数字。
    case(
        "hold-disabled-ignores-wait",
        [
            {"op": "smart", **T, "status": "disabled"},
            {"op": "for_response", "response_key": "resp_1", **T},
            {"op": "smart_wait", **T, "started_s": 10.0, "ended_s": 10.5},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
        ],
    ),
    # E2E 名额只发一次：说起话来的第一条响应量过就算，后面同链的响应不能再量。
    case(
        "e2e-anchor-once",
        [
            {"op": "for_response", "response_key": "resp_1", **T},
            {"op": "anchor_request", "response_key": "resp_1"},
            {"op": "record_e2e", "response_key": "resp_1", "seconds": 1.5},
            {"op": "record_e2e", "response_key": "resp_1", "seconds": 9.9},
            {"op": "anchor_available", "response_key": "resp_1"},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
        ],
    ),
    # 名额被关掉（终态非 completed，或带出了待执行的函数调用）之后不能再记。
    case(
        "e2e-anchor-closed",
        [
            {"op": "for_response", "response_key": "resp_1", **T},
            {"op": "anchor_request", "response_key": "resp_1"},
            {"op": "anchor_close", "response_key": "resp_1"},
            {"op": "record_e2e", "response_key": "resp_1", "seconds": 1.5},
            {"op": "anchor_available", "response_key": "resp_1"},
            {"op": "payload", "response_key": "resp_1", "status": "cancelled"},
        ],
    ),
    # 工具续接：开场白已经出声量过 E2E，后续工具回答只留阶段耗时，不重复记 E2E。
    case(
        "e2e-tool-followup",
        [
            {"op": "for_response", "response_key": "resp_1", **T},
            {"op": "anchor_request", "response_key": "resp_1"},
            {"op": "record_e2e", "response_key": "resp_1", "seconds": 0.9},
            {"op": "for_response", "response_key": "resp_2", **T},
            {"op": "bind_source", "response_key": "resp_2", "source": "tool"},
            {"op": "anchor_attach_existing", "response_key": "resp_2", "from_response_key": "resp_1"},
            {"op": "record_llm", "response_key": "resp_2", "seconds": 0.4},
            {"op": "record_e2e", "response_key": "resp_2", "seconds": 5.0},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
            {"op": "payload", "response_key": "resp_2", "status": "completed"},
        ],
    ),
    # 空转写：待定槽位被丢弃，响应建立后 stt_s 就该是 null，而不是继承上一轮的数字。
    case(
        "empty-final-stt-discarded",
        [
            {"op": "stt", **T, "seconds": 0.5},
            {"op": "discard_pending", **T},
            {"op": "pending", **T},
            {"op": "for_response", "response_key": "resp_1", **T},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
        ],
    ),
    # 修订被顶替：旧修订的 STT 测量要单独丢掉，不能污染新修订。
    case(
        "stale-revision-keeps-new",
        [
            {"op": "stt", "turn_id": "turn_1", "turn_revision": 0, "seconds": 0.9},
            {"op": "stt", "turn_id": "turn_1", "turn_revision": 1, "seconds": 0.3},
            {"op": "discard_pending", "turn_id": "turn_1", "turn_revision": 0},
            {"op": "pending", "turn_id": "turn_1", "turn_revision": 0},
            {"op": "pending", "turn_id": "turn_1", "turn_revision": 1},
            {"op": "for_response", "response_key": "resp_1", "turn_id": "turn_1", "turn_revision": 1},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
        ],
    ),
    # 会话清理：只清自己那条会话的在途测量，别人的还在。
    case(
        "clear-session-scoped",
        [
            {"op": "for_response", "response_key": "resp_1", **T, "session_id": "s1"},
            {"op": "for_response", "response_key": "resp_2", "turn_id": "turn_2", "turn_revision": 0, "session_id": "s2"},
            {"op": "clear_session", "session_id": "s1"},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
            {"op": "payload", "response_key": "resp_2", "status": "completed"},
        ],
    ),
    # 从没登记过的会话也可能触发「最后一个会话清空待定槽位」——STT 跑过但一次响应都没建
    # 的会话就是这样。提前 return 会让这些槽位永久留在表里。
    case(
        "clear-unknown-session-drops-pending",
        [
            {"op": "stt", **T, "seconds": 0.2},
            {"op": "pending", **T},
            {"op": "clear_session", "session_id": "never-registered"},
            {"op": "pending", **T},
        ],
    ),
    # 取走即消失：pop 之后再查就查不到，且不能再被 worker 建回来。
    case(
        "pop-detaches",
        [
            {"op": "for_response", "response_key": "resp_1", **T, "session_id": "s1"},
            {"op": "pop", "response_key": "resp_1", "session_id": "s1"},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
            {"op": "for_response", "response_key": "resp_2", "turn_id": "turn_2", "turn_revision": 0},
            {"op": "discard_response", "response_key": "resp_2"},
            {"op": "payload", "response_key": "resp_2", "status": "completed"},
        ],
    ),
    # 浮点写法对客户端可见：整值要带 .0、小指数用小写 e、vad/hold 走 9 位定长。
    case(
        "float-rendering",
        [
            {"op": "for_response", "response_key": "resp_1", **T},
            {"op": "record_llm", "response_key": "resp_1", "seconds": 2.0},
            {"op": "record_ttfa", "response_key": "resp_1", "seconds": 1e-7},
            {"op": "record_resp_e2e", "response_key": "resp_1", "seconds": 12.345},
            {"op": "set_vad_decision", "response_key": "resp_1", "seconds": 1.2345678901234},
            {"op": "smart", **T, "status": "complete"},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
            {"op": "log", "response_key": "resp_1"},
        ],
    ),
    # 取消前已经出过声：日志要能区分「还没出声就被取消」和「说到一半被打断」。
    case(
        "cancelled-with-tts",
        [
            {"op": "for_response", "response_key": "resp_1", **T},
            {"op": "record_ttfa", "response_key": "resp_1", "seconds": 0.3},
            {"op": "set_status", "response_key": "resp_1", "status": "cancelled"},
            {"op": "log", "response_key": "resp_1"},
            {"op": "payload", "response_key": "resp_1", "status": "cancelled"},
        ],
    ),
    # 既没有轮次也没有来源（客户端独立 create）：这行日志不该出现，出现就是噪声。
    case(
        "log-requires-identity",
        [
            {"op": "for_response", "response_key": "resp_1"},
            {"op": "record_llm", "response_key": "resp_1", "seconds": 0.7},
            {"op": "log", "response_key": "resp_1"},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
        ],
    ),
    # 没有 STT 的独立响应：stt_s 为 null，但 llm/tts 照记，e2e 没有来源所以也是 null。
    case(
        "independent-response-no-e2e",
        [
            {"op": "for_response", "response_key": "resp_1"},
            {"op": "bind_source", "response_key": "resp_1", "source": "client"},
            {"op": "record_llm", "response_key": "resp_1", "seconds": 0.55},
            {"op": "record_ttfa", "response_key": "resp_1", "seconds": 0.08},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
            {"op": "log", "response_key": "resp_1"},
        ],
    ),
    # 同一轮的两条响应各自记账：工具调用与它的口头回答不能挤进同一个格子。
    case(
        "two-responses-one-turn",
        [
            {"op": "stt", **T, "seconds": 0.25},
            {"op": "for_response", "response_key": "resp_1", **T},
            {"op": "record_llm", "response_key": "resp_1", "seconds": 0.8},
            {"op": "for_response", "response_key": "resp_2", **T},
            {"op": "record_llm", "response_key": "resp_2", "seconds": 0.35},
            {"op": "payload", "response_key": "resp_1", "status": "completed"},
            {"op": "payload", "response_key": "resp_2", "status": "completed"},
        ],
    ),
]


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for item in CASES:
        path = OUT / f"{item['file']}.input.json"
        path.write_text(
            json.dumps(item["payload"], ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
    print(f"写出 {len(CASES)} 条到 {OUT}")


if __name__ == "__main__":
    main()
