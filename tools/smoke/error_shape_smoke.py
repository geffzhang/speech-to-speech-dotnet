"""D027 真跑冒烟：归位后的错误事件形状（上游 make_error 口径）。

验证点：
  A. 未知事件   → error={type:"unknown_or_invalid_event", message:"Unknown or invalid event: foo"}，
                  且 error.event_id == 客户端 event_id（send_correlated 关联）。
  B. item.delete 不存在 → conversation_item_not_found + error.event_id 关联。
  C. session.update 带 transcription 会话 → invalid_session_type。
  D. 活动响应期间再 response.create → conversation_already_has_active_response（新消息）。

用法: python input_array_smoke.py 同款参数; 默认 ws://127.0.0.1:8765/v1/realtime
"""
import asyncio
import json
import sys
import time

import websockets

URL = "ws://127.0.0.1:8765/v1/realtime"


def error_of(event):
    err = event.get("error") or {}
    return err.get("type"), err.get("message"), err.get("event_id")


async def main(timeout):
    ws = await websockets.connect(URL, max_size=None, proxy=None)
    events = []

    async def recv():
        async for raw in ws:
            events.append(json.loads(raw))

    t = asyncio.create_task(recv())

    def sent_eids():
        return {e.get("event_id") for e in events if e.get("type", "").startswith("error")}

    # A. 未知事件（带客户端 event_id）
    await ws.send(json.dumps({"type": "foo.bar", "event_id": "evt_A"}))
    await asyncio.sleep(0.5)

    # B. delete 不存在的 item
    await ws.send(json.dumps({
        "type": "conversation.item.delete", "item_id": "item_nope", "event_id": "evt_B"}))
    await asyncio.sleep(0.5)

    # C. transcription 会话
    await ws.send(json.dumps({
        "type": "session.update",
        "session": {"type": "transcription"}, "event_id": "evt_C"}))
    await asyncio.sleep(0.5)

    # D. 开一个响应（LLM 生成中），立刻再开第二个
    await ws.send(json.dumps({
        "type": "response.create",
        "response": {"output_modalities": ["text"], "max_output_tokens": 16}}))
    await asyncio.sleep(0.2)
    await ws.send(json.dumps({
        "type": "response.create", "event_id": "evt_D",
        "response": {"output_modalities": ["text"], "max_output_tokens": 16}}))

    give_up = time.perf_counter() + timeout
    while time.perf_counter() < give_up:
        await asyncio.sleep(0.2)
        if sum(1 for e in events if e.get("type") == "response.done") >= 1:
            await asyncio.sleep(0.5)
            break

    await ws.close()
    t.cancel()

    errs = [e for e in events if e.get("type") == "error"]
    by_type = {e["error"]["type"]: e["error"] for e in errs}

    def check(name, ok, detail):
        print(f"[{name}] {'PASS' if ok else 'FAIL'}  {detail}")
        return ok

    a = by_type.get("unknown_or_invalid_event", {})
    ok_a = check(
        "A 未知事件",
        a.get("message") == "Unknown or invalid event: foo.bar" and a.get("event_id") == "evt_A",
        f"type={a.get('type')} msg={a.get('message')!r} corr={a.get('event_id')}")

    b = by_type.get("conversation_item_not_found", {})
    ok_b = check(
        "B delete not found",
        "item_nope" in (b.get("message") or "") and b.get("event_id") == "evt_B"
        and "code" not in b,
        f"msg={b.get('message')!r} corr={b.get('event_id')}")

    c = by_type.get("invalid_session_type", {})
    ok_c = check(
        "C transcription 会话",
        c.get("message") == "Only 'realtime' session type is supported; transcription sessions are not.",
        f"msg={c.get('message')!r}")

    d = by_type.get("conversation_already_has_active_response", {})
    ok_d = check(
        "D 活动响应闸",
        d.get("message") == "Cannot create response while another response is in progress or pending.",
        f"msg={d.get('message')!r} corr={d.get('event_id')}")

    ok = ok_a and ok_b and ok_c and ok_d
    print()
    print(f"D027 错误形状归位：{'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    timeout = int(sys.argv[sys.argv.index("--timeout") + 1]) if "--timeout" in sys.argv else 120
    sys.exit(asyncio.run(main(timeout)))
