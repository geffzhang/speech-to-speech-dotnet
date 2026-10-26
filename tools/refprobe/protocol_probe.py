"""Reference probe for the Realtime wire-protocol shape layer.

Feeds one event-construction request in and prints the resulting server event
as JSON. The baseline is produced here, never hand-written.

Input (stdin, UTF-8):
  {"event": "<wire type>", "args": {"<snake_case field>": <value>, ...}}

`args` carries business fields only. `type` and `event_id` are filled in by
the probe, and `event_id` is normalized to "<event_id>" on output because the
two implementations generate it differently (and no client cares).

This uses the official `openai.types.realtime` models — the same classes the
upstream service constructs when it emits events, and the same union its
contract tests validate against (`assert_openai_schema`).
"""

from __future__ import annotations

import json
import sys

from schema_dump import _server_event_types


def _normalize(payload: dict) -> dict:
    if "event_id" in payload:
        payload["event_id"] = "<event_id>"
    return payload


def main() -> int:
    raw = sys.stdin.read()
    if not raw.strip():
        print("输入为空", file=sys.stderr)
        return 2

    try:
        request = json.loads(raw)
    except json.JSONDecodeError as exc:
        print(f"入参不是合法 JSON：{exc}", file=sys.stderr)
        return 2

    wire = request["event"]
    args = request.get("args") or {}

    catalog = _server_event_types()
    model = catalog.get(wire)
    if model is None:
        print(f"官方 schema 里没有事件 {wire!r}", file=sys.stderr)
        return 3

    try:
        event = model(type=wire, event_id="event_probe", **args)
    except Exception as exc:  # 参数不满足模型约束时如实报错，不猜
        print(f"{wire} 构造失败：{type(exc).__name__}: {exc}", file=sys.stderr)
        return 4

    payload = _normalize(event.model_dump(mode="json", exclude_none=True))
    json.dump(payload, sys.stdout, ensure_ascii=False, sort_keys=True)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
