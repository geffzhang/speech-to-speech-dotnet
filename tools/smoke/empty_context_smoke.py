# -*- coding: utf-8 -*-
"""D026 / C027 真跑验证（纯文本链，绕开慢速 TTS）。

D026：空历史 + response.create —— 上游拼上 system 照常生成，不再报
      "no conversation context"。
C027：response.create 不带 response 字段（overrides=null，与语音轮同款）
      时，session.update 配的 instructions 也要回落生效。

判据：回答里出现指令要求的哨兵词。
"""
import argparse
import asyncio
import json
import sys
import time

import websockets


def text_of(event: dict) -> str:
    resp = event.get("response", {})
    return "".join(
        part.get("transcript") or part.get("text") or ""
        for item in resp.get("output", [])
        for part in item.get("content", [])
    )


async def run_case(url: str, timeout: float, *, seed_user_item: bool,
                   with_response_field: bool, instructions: str, user_text: str,
                   sentinel: str, label: str) -> bool:
    ws = await websockets.connect(url, max_size=None, proxy=None)
    events: list[dict] = []

    async def recv():
        async for raw in ws:
            events.append(json.loads(raw))

    receiver = asyncio.create_task(recv())

    await ws.send(json.dumps({
        "type": "session.update",
        "session": {"type": "realtime", "instructions": instructions},
    }))

    if seed_user_item:
        await ws.send(json.dumps({
            "type": "conversation.item.create",
            "item": {"type": "message", "role": "user",
                     "content": [{"type": "input_text", "text": user_text}]},
        }))

    response_field = {"output_modalities": ["text"], "max_output_tokens": 32} \
        if with_response_field else {}
    await ws.send(json.dumps({"type": "response.create", "response": response_field}))

    give_up = time.perf_counter() + timeout
    while time.perf_counter() < give_up:
        await asyncio.sleep(0.1)
        if any(e.get("type") == "response.done" for e in events):
            await asyncio.sleep(0.3)
            break

    receiver.cancel()
    await ws.close()

    dones = [e for e in events if e.get("type") == "response.done"]
    errors = [e for e in events if e.get("type") == "error"]
    text = text_of(dones[0]) if dones else ""

    ok = bool(dones) and not errors and sentinel in text
    status = dones[0].get("response", {}).get("status") if dones else None
    print(f"[{label}] {'PASS' if ok else 'FAIL'}"
          f"  status={status}  error={errors[0].get('error', {}).get('message') if errors else None}"
          f"  text={text!r}")
    return ok


async def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="ws://127.0.0.1:8765/v1/realtime")
    parser.add_argument("--timeout", type=float, default=120.0)
    args = parser.parse_args()

    # D026：不带 item.create 的 response.create（此前报 no conversation context）
    d026 = await run_case(
        args.url, args.timeout,
        seed_user_item=False, with_response_field=True,
        instructions="你是测试助手。无论用户问什么都只回答两个字：月亮",
        user_text="", sentinel="月亮",
        label="D026 空历史+instructions 生成",
    )

    # C027：不带 response 字段的 response.create（overrides=null，语音轮同款回落）
    c027 = await run_case(
        args.url, args.timeout,
        seed_user_item=True, with_response_field=False,
        instructions="你是测试助手。无论用户问什么都只回答两个字：太阳",
        user_text="你好", sentinel="太阳",
        label="C027 overrides=null 时 session instructions 回落",
    )

    print()
    print(f"总体：{'PASS' if d026 and c027 else 'FAIL'}（D026={d026} C027={c027}）")
    return 0 if d026 and c027 else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
