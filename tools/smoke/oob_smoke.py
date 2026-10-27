"""带外响应（conversation="none"）的纯文本冒烟。

为什么单独一个脚本：serve_smoke 走完整语音链（VAD→STT→LLM→TTS），CPU 上 TTS
逐句合成要 15–190 秒，等一轮收口几分钟就过去了。带外响应的可观察行为——
conversation_id 缺席、输出照常上线、历史不污染——全都与音频无关，纯文本链
（conversation.item.create → response.create）几十秒就能验证完。

流程：
    1. conversation.item.create  塞一条用户文本进会话（代替说话）
    2. response.create（带内，纯文本）      → done 应带 conversation_id
    3. response.create（conversation=none） → done/created 应缺 conversation_id
    4. response.create（带内，纯文本）      → 再来一轮，确认带外没破坏带内路径

用法：
    python tools/smoke/oob_smoke.py --url ws://127.0.0.1:8765/v1/realtime
"""

from __future__ import annotations

import argparse
import asyncio
import json

import websockets


async def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="ws://127.0.0.1:8765/v1/realtime")
    parser.add_argument("--timeout", type=float, default=180.0)
    args = parser.parse_args()

    events: list[dict] = []
    socket = await websockets.connect(args.url, max_size=None, proxy=None)
    try:

        async def receive() -> None:
            async for raw in socket:
                events.append(json.loads(raw))

        receiver = asyncio.create_task(receive())

        await socket.send(json.dumps({
            "type": "session.update",
            "session": {
                "type": "realtime",
                "instructions": "你是测试助手，回答不超过十个字。",
            },
        }))

        # 代替说话：文本项直接进会话历史，后续响应才有上下文可读。
        await socket.send(json.dumps({
            "type": "conversation.item.create",
            "item": {
                "type": "message",
                "role": "user",
                "content": [{"type": "input_text", "text": "用一句话介绍你自己"}],
            },
        }))

        # 三轮必须串行：服务端同一时刻只允许一条活动响应，
        # 背靠背发 create 会让后面的撞上 conversation_already_has_active_response。
        import time

        text_create = {
            "output_modalities": ["text"],
            "max_output_tokens": 32,
        }

        async def ask(response_params: dict) -> None:
            before = sum(1 for e in events if e.get("type") == "response.done")
            await socket.send(json.dumps({
                "type": "response.create",
                "response": response_params,
            }))
            give_up = time.perf_counter() + args.timeout
            while time.perf_counter() < give_up:
                await asyncio.sleep(0.1)
                if sum(1 for e in events if e.get("type") == "response.done") > before:
                    return
            raise TimeoutError("response.done 等待超时")

        # 第一轮：带内
        await ask({"conversation": "auto", **text_create})
        # 第二轮：带外侧问
        await ask({"conversation": "none", **text_create})
        # 第三轮：带内回归
        await ask({**text_create})

        await asyncio.sleep(0.5)
        receiver.cancel()
    except websockets.ConnectionClosed as closed:
        print(f"连接被关闭：code={closed.code} reason={closed.reason!r}")
    finally:
        await socket.close()

    dones = [e for e in events if e.get("type") == "response.done"]
    created = [e for e in events if e.get("type") == "response.created"]

    def has_conv(event: dict) -> bool:
        return "conversation_id" in event.get("response", {})

    print(f"response.created: {len(created)} 条，带 conversation_id: {sum(1 for e in created if has_conv(e))}")
    for i, e in enumerate(dones, 1):
        resp = e.get("response", {})
        text = "".join(
            part.get("transcript") or part.get("text") or ""
            for item in resp.get("output", [])
            for part in item.get("content", [])
        )
        usage = resp.get("usage", {})
        print(f"response.done #{i}: conv={'有' if has_conv(e) else '无'}"
              f" status={resp.get('status')} text={text!r}"
              f" input_tokens={usage.get('input_tokens')}")

    # 断言：第 1、3 轮带内必有 conversation_id；第 2 轮带外必无。
    # 历史不污染的旁证：第 3 轮与第 1 轮的上下文应当完全一致（同样的会话、
    # 中间只插了一条不进历史的侧问），input_tokens 相等。
    # 若侧问的回答写进了历史，第 3 轮的 input_tokens 会多出侧问答案的长度。
    same_context = (
        len(dones) == 3
        and dones[0].get("response", {}).get("usage", {}).get("input_tokens") is not None
        and dones[0]["response"]["usage"]["input_tokens"]
        == dones[2]["response"]["usage"]["input_tokens"]
    )
    ok = (
        len(dones) == 3
        and has_conv(dones[0]) and has_conv(dones[2])
        and not has_conv(dones[1])
        and any(not has_conv(e) for e in created)
        and same_context
    )
    print()
    print(f"历史不污染（第 3 轮 input_tokens == 第 1 轮）：{'PASS' if same_context else 'FAIL'}")
    print(f"带外响应断言：{'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
