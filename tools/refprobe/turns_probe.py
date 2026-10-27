"""投机轮次状态机的参照探针。

从 stdin 读一段 JSON 脚本，逐步驱动上游 ``SpeculativeTurnTracker``，把每一步的
可观察结果按顺序写出来。对侧 ``tools/TurnsProbe`` 读同一份脚本、驱动本实现，
两边输出逐值比对——这样比对的是**状态迁移本身**，而不是谁的注释更像。

为什么必须注入虚拟时钟
----------------------
状态机里所有期限都是单调时钟上的绝对时刻（reopen grace、pending reopen、
processing deadline）。用真实时钟，两侧跑出来的数字必然不同，也就没法比。
所以这里把模块里的 ``time`` 换成可推进的假时钟：脚本用 ``advance`` 步推时间，
两侧从同一个起点出发，走同样的步，得到的绝对时刻就该逐位相同。

浮点怎么比
----------
期限是 ``起点 + delta/1000.0`` 这种二进制浮点累加的结果，两侧的十进制最短表示
可能不同（Python 写 ``1000.0``，C# 写 ``1000``）。所以**所有浮点输出统一格式化为
6 位小数的字符串**，两侧用同一个格式，比较就退化成字符串相等。
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

REPO = Path(r"C:\workshop\github\speech-to-speech")
sys.path.insert(0, str(REPO / "src"))

from speech_to_speech.pipeline import speculative_turns as st  # noqa: E402

_FLOAT_DIGITS = 6


class _VirtualClock:
    """可推进的单调时钟，替掉模块里的 ``time``。"""

    def __init__(self, start: float) -> None:
        self.now = float(start)

    def monotonic(self) -> float:
        return self.now

    def perf_counter(self) -> float:
        return self.now

    def advance(self, seconds: float) -> None:
        self.now += float(seconds)


def _format_float(value: float) -> str:
    return f"{value:.{_FLOAT_DIGITS}f}"


def _normalize(value):
    """把返回值转成两侧都能逐字节一致的形状。"""
    if isinstance(value, float):
        return _format_float(value)
    if isinstance(value, st.TurnPhase):
        return value.value
    if isinstance(value, st.TurnGateAction):
        return value.value
    if isinstance(value, st.TurnGate):
        return {
            "action": value.action.value,
            "deadline": None if value.deadline is None else _format_float(value.deadline),
        }
    if isinstance(value, tuple):
        return [_normalize(item) for item in value]
    return value


def _opt_int(step: dict, name: str):
    return step.get(name)


def _run_step(tracker: st.SpeculativeTurnTracker, clock: _VirtualClock, step: dict):
    op = step["op"]

    if op == "advance":
        clock.advance(step["seconds"])
        return None

    if op == "reset":
        tracker.reset()
        return None

    if op == "start_turn":
        return _normalize(tracker.start_turn())

    if op == "configure_reopen":
        tracker.configure_reopen(int(step["unanswered_reopen_ms"]))
        return None

    if op == "can_reopen":
        return tracker.can_reopen(int(step["audio_ms"]))

    if op == "speech_started":
        return _normalize(tracker.speech_started(int(step["audio_ms"])))

    if op == "speech_candidate_started":
        return tracker.speech_candidate_started(int(step["audio_ms"]))

    if op == "speech_candidate_cancelled":
        tracker.speech_candidate_cancelled()
        return None

    if op == "has_speech_candidate":
        return tracker.has_speech_candidate()

    if op == "segment_finalized":
        tracker.segment_finalized(
            int(step["audio_ms"]),
            output_hold_ms=int(step.get("output_hold_ms", 0)),
            processing_delay_ms=int(step.get("processing_delay_ms", 0)),
        )
        return None

    if op == "segment_discarded":
        tracker.segment_discarded()
        return None

    if op == "processing_deadline":
        return _normalize(tracker.processing_deadline(step.get("turn_id"), _opt_int(step, "revision")))

    if op == "gate":
        return _normalize(
            tracker.gate(
                step.get("turn_id"),
                _opt_int(step, "revision"),
                hold_for_grace=bool(step.get("hold_for_grace", True)),
                hold_until=step.get("hold_until"),
                commit=bool(step.get("commit", False)),
            )
        )

    if op == "is_latest":
        return tracker.is_latest(step.get("turn_id"), _opt_int(step, "revision"))

    if op == "is_current_turn":
        return tracker.is_current_turn(step.get("turn_id"))

    if op == "is_committed":
        return tracker.is_committed(step.get("turn_id"), _opt_int(step, "revision"))

    if op == "close":
        tracker.close(step.get("turn_id"), _opt_int(step, "revision"))
        return None

    if op == "begin_reopen_candidate":
        return tracker.begin_reopen_candidate(step.get("turn_id"), _opt_int(step, "revision"))

    if op == "confirm_reopen_candidate":
        return tracker.confirm_reopen_candidate(
            step.get("turn_id"),
            _opt_int(step, "base_revision"),
            _opt_int(step, "candidate_revision"),
        )

    if op == "cancel_reopen_candidate":
        tracker.cancel_reopen_candidate(step.get("turn_id"), _opt_int(step, "candidate_revision"))
        return None

    if op == "start_reopen_grace":
        tracker.start_reopen_grace(step.get("turn_id"), _opt_int(step, "revision"), float(step["grace_seconds"]))
        return None

    if op == "observe":
        turn_id, revision = tracker.current_turn()
        return {
            "phase": _normalize(tracker.phase),
            "turn": [turn_id, revision],
            "has_candidate": tracker.has_speech_candidate(),
        }

    raise SystemExit(f"unknown op: {op}")


def main() -> int:
    raw = sys.stdin.buffer.read().decode("utf-8")
    script = json.loads(raw)
    payload = script.get("payload", script)

    clock = _VirtualClock(payload.get("clock_start", 1000.0))
    st.time = clock  # 模块里所有 time.monotonic() 走虚拟时钟

    tracker = st.SpeculativeTurnTracker()

    trace = []
    for index, step in enumerate(payload.get("steps", [])):
        result = _run_step(tracker, clock, step)
        trace.append({"index": index, "op": step["op"], "result": result})

    sys.stdout.write(json.dumps({"trace": trace}, ensure_ascii=False, sort_keys=True))
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
