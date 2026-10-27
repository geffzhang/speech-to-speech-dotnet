"""工具调用真实链路冒烟：session.tools → chat-completions 后端 → function_call 事件 → 客户端回填 → 二轮回答。

前提：serve 以 --llm_backend chat-completions 指向 mock_chat_completions.py
（或任何真实 OpenAI 兼容服务，如 llama-server --jinja——已验证 Qwen2.5-0.5B
Q4_K_M 端到端 PASS）。
注意：宿主机设了 HTTP_PROXY 时，启动 serve 必须带 NO_PROXY=127.0.0.1,localhost，
否则本机 LLM 服务的请求会被塞进代理隧道（D029）。
LLM 部分是脚本化 mock，其余全链路（HTTP SSE、tools 序列化、tool_calls 聚合、
协议映射、function_call_output 入历史、二轮生成）都是产品真实路径。

用法: python tool_call_smoke.py [--url ws://127.0.0.1:8765/v1/realtime]
"""
import asyncio
import json
import sys
import time

import websockets

TOOLS = [{
    "type": "function",
    "name": "get_weather",
    "description": "查询城市天气",
    "parameters": {
        "type": "object",
        "properties": {"city": {"type": "string"}, "unit": {"type": "string"}},
    },
}]


async def main(timeout):
    ws = await websockets.connect(
        "ws://127.0.0.1:8765/v1/realtime", max_size=None, proxy=None)
    events = []

    async def recv():
        async for raw in ws:
            events.append(json.loads(raw))

    t = asyncio.create_task(recv())

    def has_done():
        return [e for e in events if e.get("type") == "response.done"]

    async def wait_for(pred, give_up):
        while time.perf_counter() < give_up:
            await asyncio.sleep(0.1)
            if pred():
                return True
        return False

    await ws.send(json.dumps({
        "type": "session.update",
        "session": {"type": "realtime", "tools": TOOLS,
                    "instructions": "你是天气助手，回答简短。"}}))

    # 第一轮：应触发 get_weather 工具调用
    await ws.send(json.dumps({
        "type": "conversation.item.create",
        "item": {"type": "message", "role": "user",
                 "content": [{"type": "input_text", "text": "北京今天天气怎么样？"}]}}))
    await ws.send(json.dumps({
        "type": "response.create",
        "response": {"output_modalities": ["text"], "max_output_tokens": 64}}))

    give_up = time.perf_counter() + timeout
    ok1 = await wait_for(
        lambda: any(e.get("type") == "response.function_call_arguments.done" for e in events),
        give_up)
    if not ok1:
        print("FAIL：第一轮未收到 function_call_arguments.done")
        for e in events:
            print(" ", json.dumps(e, ensure_ascii=False)[:200])
        return 1

    arg_done = next(e for e in events
                    if e.get("type") == "response.function_call_arguments.done")
    call_id = arg_done.get("call_id")
    ok_name = arg_done.get("name") == "get_weather"
    ok_args = json.loads(arg_done.get("arguments") or "{}").get("city") == "北京"
    item_done = next((e for e in events
                      if e.get("type") == "response.output_item.done"
                      and e.get("item", {}).get("type") == "function_call"), None)
    ok_item = item_done is not None and item_done["item"].get("status") == "completed"

    print(f"[1 工具调用] name={'PASS' if ok_name else 'FAIL'} "
          f"args={'PASS' if ok_args else 'FAIL'} item_done={'PASS' if ok_item else 'FAIL'} "
          f"call_id={call_id!r}")

    # 客户端回填工具结果
    await ws.send(json.dumps({
        "type": "conversation.item.create",
        "item": {"type": "function_call_output", "call_id": call_id,
                 "output": json.dumps({"temperature": "25", "condition": "晴"},
                                      ensure_ascii=False)}}))
    # 第二轮：应基于工具结果回答
    await ws.send(json.dumps({
        "type": "response.create",
        "response": {"output_modalities": ["text"], "max_output_tokens": 64}}))

    give_up = time.perf_counter() + timeout
    ok2 = await wait_for(lambda: len(has_done()) >= 2, give_up)
    if not ok2:
        print("FAIL：第二轮未收口")
        return 1

    second_done = has_done()[-1]
    text = "".join(
        part.get("text") or part.get("transcript") or ""
        for item in second_done.get("response", {}).get("output", [])
        for part in item.get("content", []))
    ok_text = "25" in text
    print(f"[2 二轮回答] {'PASS' if ok_text else 'FAIL'}  text={text!r}")

    await ws.close()
    t.cancel()

    ok = ok_name and ok_args and ok_item and ok_text
    print()
    print(f"工具调用端到端：{'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    args = sys.argv
    timeout = int(args[args.index("--timeout") + 1]) if "--timeout" in args else 60
    sys.exit(asyncio.run(main(timeout)))
