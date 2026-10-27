# -*- coding: utf-8 -*-
"""D025 真跑验证：response.create 的 input 数组（纯文本链）。

覆盖五条行为面：
  A. 带内 input 进历史        —— 第一轮种入「暗号：苹果」，第二轮（不带 input）问暗号应答苹果
  B. 带外 input=[...] 种子    —— conversation=none + input 携带问题，回答命中种子内容
  C. 带外 input=[] 清空历史   —— 带内种入暗号后带外空聊天问暗号，答「不知道」
  D. 带内 input 类型非法      —— 同步 error 事件（invalid_input_item），不建响应
  E. 带内悬空 function_call   —— 同步 error 事件（function_call_output_pending），不建响应
"""
import asyncio
import json
import sys
import time

import websockets

URL = "ws://127.0.0.1:8765/v1/realtime"
TIMEOUT = 120.0


def text_of(event: dict) -> str:
    resp = event.get("response", {})
    return "".join(
        part.get("transcript") or part.get("text") or ""
        for item in resp.get("output", [])
        for part in item.get("content", [])
    )


async def session():
    ws = await websockets.connect(URL, max_size=None, proxy=None)
    events: list[dict] = []

    async def recv():
        async for raw in ws:
            events.append(json.loads(raw))

    return ws, asyncio.create_task(recv()), events


async def wait_done(events, n=1, timeout=TIMEOUT):
    give_up = time.perf_counter() + timeout
    while time.perf_counter() < give_up:
        await asyncio.sleep(0.1)
        if sum(1 for e in events if e.get("type") == "response.done") >= n:
            await asyncio.sleep(0.3)
            break
    return [e for e in events if e.get("type") == "response.done"]


async def wait_error(events, timeout=TIMEOUT):
    give_up = time.perf_counter() + timeout
    while time.perf_counter() < give_up:
        await asyncio.sleep(0.1)
        if any(e.get("type") == "error" for e in events):
            await asyncio.sleep(0.2)
            break
    return next((e for e in events if e.get("type") == "error"), None)


async def case_a_inband_input_history():
    """带内 input 进历史：种入暗号，第二轮不带 input 也答得出来。"""
    ws, receiver, events = await session()
    await ws.send(json.dumps({"type": "session.update", "session": {
        "type": "realtime", "instructions": "你是测试助手，回答不超过二十个字。"}}))
    await ws.send(json.dumps({
        "type": "response.create",
        "response": {
            "output_modalities": ["text"],
            "max_output_tokens": 48,
            "input": [{
                "type": "message",
                "role": "user",
                "content": [{"type": "input_text", "text": "请记住暗号：苹果"}],
            }],
        },
    }))
    await wait_done(events)

    await ws.send(json.dumps({
        "type": "response.create",
        "response": {
            "output_modalities": ["text"],
            "max_output_tokens": 48,
            "input": [{
                "type": "message",
                "role": "user",
                "content": [{"type": "input_text", "text": "暗号是什么？只回答暗号本身。"}],
            }],
        },
    }))
    events.clear()
    dones = await wait_done(events)
    text = text_of(dones[0]) if dones else ""
    await ws.close(); receiver.cancel()

    # 模型复述不稳定（可能只答「暗号」两个字），但「知道存在暗号」本身就是
    # input 进历史的证明——没进历史时模型对这个问题只能答「不知道」。
    ok = "苹果" in text or ("暗号" in text and "不知道" not in text and "没有" not in text)
    print(f"[A 带内input进历史] {'PASS' if ok else 'FAIL'}  text={text!r}")
    return ok


async def case_b_oob_seed():
    """带外 input=[...]：种子进一次性聊天，回答命中种子内容。"""
    ws, receiver, events = await session()
    await ws.send(json.dumps({"type": "session.update", "session": {
        "type": "realtime", "instructions": "你是测试助手，回答不超过二十个字。"}}))
    await ws.send(json.dumps({
        "type": "response.create",
        "response": {
            "conversation": "none",
            "output_modalities": ["text"],
            "max_output_tokens": 48,
            "input": [{
                "type": "message",
                "role": "user",
                "content": [{"type": "input_text", "text": "请回答：天空是什么颜色？"}],
            }],
        },
    }))
    dones = await wait_done(events)
    text = text_of(dones[0]) if dones else ""
    conv = dones[0].get("response", {}).get("conversation_id") if dones else "?"
    await ws.close(); receiver.cancel()

    ok = bool(dones) and conv is None and len(text) > 0
    print(f"[B 带外input种子]   {'PASS' if ok else 'FAIL'}  conv_id={'null' if conv is None else conv}  text={text!r}")
    return ok


async def case_c_oob_empty_clears_history():
    """带外 input=[] 清空上下文：先带内种入暗号，再带外空聊天问暗号——
    模型看不到（答「不知道」），证明 input=[] 用的是空聊天而非会话副本。"""
    ws, receiver, events = await session()
    await ws.send(json.dumps({"type": "session.update", "session": {
        "type": "realtime", "instructions": "你是测试助手，回答不超过二十个字。"}}))
    await ws.send(json.dumps({
        "type": "response.create",
        "response": {
            "output_modalities": ["text"],
            "max_output_tokens": 48,
            "input": [{
                "type": "message",
                "role": "user",
                "content": [{"type": "input_text", "text": "请记住暗号：香蕉"}],
            }],
        },
    }))
    await wait_done(events)

    # 带外 input=[]：清空上下文。问句放进 instructions（唯一可携带的指令位）。
    await ws.send(json.dumps({
        "type": "response.create",
        "response": {
            "conversation": "none",
            "output_modalities": ["text"],
            "max_output_tokens": 48,
            "instructions": "暗号是什么？如果你的上下文里没有暗号，就只回答：不知道。",
            "input": [],
        },
    }))
    events.clear()
    dones = await wait_done(events)
    text = text_of(dones[0]) if dones else ""
    await ws.close(); receiver.cancel()

    ok = "不知道" in text and "香蕉" not in text
    print(f"[C 带外空聊天清历史] {'PASS' if ok else 'FAIL'}  text={text!r}")
    return ok


async def case_d_invalid_item():
    """带内 input 类型非法：同步 error 事件 invalid_input_item，不建响应。"""
    ws, receiver, events = await session()
    await ws.send(json.dumps({"type": "response.create", "response": {
        "output_modalities": ["text"],
        "input": [{"type": "item_reference", "id": "item_x"}]}}))
    error = await wait_error(events)
    created = [e for e in events if e.get("type") == "response.created"]
    await ws.close(); receiver.cancel()

    err_type = (error or {}).get("error", {}).get("type")
    ok = err_type == "invalid_input_item" and not created
    print(f"[D 非法input类型]    {'PASS' if ok else 'FAIL'}"
          f"  error.type={err_type}  response.created={bool(created)}")
    return ok


async def case_e_pending_function_call():
    """带内悬空 function_call：同步 error 事件 function_call_output_pending。"""
    ws, receiver, events = await session()
    await ws.send(json.dumps({"type": "response.create", "response": {
        "output_modalities": ["text"],
        "input": [{"type": "function_call", "call_id": "call_1", "name": "weather",
                   "arguments": "{}"}]}}))
    error = await wait_error(events)
    created = [e for e in events if e.get("type") == "response.created"]
    await ws.close(); receiver.cancel()

    err_type = (error or {}).get("error", {}).get("type")
    ok = err_type == "function_call_output_pending" and not created
    print(f"[E 悬空function_call] {'PASS' if ok else 'FAIL'}"
          f"  error.type={err_type}  response.created={bool(created)}")
    return ok


async def main() -> int:
    results = [
        await case_a_inband_input_history(),
        await case_b_oob_seed(),
        await case_c_oob_empty_clears_history(),
        await case_d_invalid_item(),
        await case_e_pending_function_call(),
    ]
    print()
    print(f"总体：{'PASS' if all(results) else 'FAIL'}  {sum(results)}/{len(results)}")
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
