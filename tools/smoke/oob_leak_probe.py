"""带外响应历史污染的行为探针（哨兵词法）。

服务端日志不足以一锤定音时，用模型行为当探针：
    1. r1（带内）：要求只答「太阳」          → 「太阳」进历史
    2. r2（带外）：要求只答「月亮」          → 「月亮」上线但**不应**进历史
    3. r3（带内）：要求复述最近一条助手消息   → 干净=「太阳」，泄漏=「月亮」

0.8B 模型对「只答 X」与「复述」这类简单指令服从性足够；两端答案字面互斥，
不存在模糊地带。

用法：
    python tools/smoke/oob_leak_probe.py --url ws://127.0.0.1:8765/v1/realtime
"""

from __future__ import annotations

import argparse
import asyncio
import json
import time

import websockets


def text_of(event: dict) -> str:
    return "".join(
        part.get("transcript") or part.get("text") or ""
        for item in event.get("response", {}).get("output", [])
        for part in item.get("content", [])
    )


async def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="ws://127.0.0.1:8765/v1/realtime")
    parser.add_argument("--timeout", type=float, default=120.0)
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
            "session": {"type": "realtime", "instructions": "你是测试助手，严格遵守用户消息里的格式要求。"},
        }))

        # 本实现空历史 + response.create 会报「no conversation context」（D026），
        # 先塞一条用户消息让历史非空——上游没有这个限制，但行为探针不依赖它。
        await socket.send(json.dumps({
            "type": "conversation.item.create",
            "item": {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "开始"}]},
        }))

        async def ask(response_params: dict, expect_suns: int) -> None:
            before = sum(1 for e in events if e.get("type") == "response.done")
            await socket.send(json.dumps({"type": "response.create", "response": response_params}))
            give_up = time.perf_counter() + args.timeout
            while time.perf_counter() < give_up:
                await asyncio.sleep(0.1)
                if sum(1 for e in events if e.get("type") == "response.done") > before:
                    return
            raise TimeoutError("response.done 等待超时")

        # r1 带内：「太阳」进历史
        await ask({
            "output_modalities": ["text"],
            "max_output_tokens": 16,
            "instructions": "只回答两个字：太阳",
        }, 1)
        # r2 带外：「月亮」只上线，不应进历史
        await ask({
            "conversation": "none",
            "output_modalities": ["text"],
            "max_output_tokens": 16,
            "instructions": "只回答两个字：月亮",
        }, 2)
        # r3 带内：复述最近一条助手消息
        await ask({
            "output_modalities": ["text"],
            "max_output_tokens": 24,
            "instructions": "复述这段对话里你最近一次（上一条助手消息）说过的词，只回答那个词本身，不要别的字。",
        }, 3)

        # ── 历史存在性硬证据 ─────────────────────────────────────────
        # conversation.item.delete 直接查 ChatSession：在历史里 → item.deleted；
        # 不在 → error conversation_item_not_found。零模型依赖，一锤定音。
        def item_id_of(pred) -> str | None:
            for e in events:
                if pred(e):
                    return e.get("item_id")
            return None

        # 每轮输出项的 item_id 从 delta 事件取（delta 只挂当前响应的项上）。
        deltas = [(e) for e in events if e.get("type") == "response.output_text.delta"]
        # 三轮各自的 item_id 按响应分桶：用 response_id 区分。
        done_ids = [e["response"]["id"] for e in events if e.get("type") == "response.done"]
        bucket: dict[str, str] = {}
        for e in deltas:
            bucket.setdefault(e.get("response_id", ""), e.get("item_id") or "")

        r1_id = bucket.get(done_ids[0]) if len(done_ids) >= 1 else None
        r2_id = bucket.get(done_ids[1]) if len(done_ids) >= 2 else None
        print(f"item ids: r1={r1_id} r2={r2_id}")

        async def delete_item(item_id: str | None) -> str:
            before = len(events)
            await socket.send(json.dumps({"type": "conversation.item.delete", "item_id": item_id}))
            give_up = time.perf_counter() + 15
            while time.perf_counter() < give_up:
                await asyncio.sleep(0.05)
                for e in events[before:]:
                    if e.get("type") == "conversation.item.deleted":
                        return "deleted"
                    if e.get("type") == "error":
                        return "error:" + str(e.get("code", ""))
            return "timeout"

        r2_del = await delete_item(r2_id)
        r1_del = await delete_item(r1_id)
        print(f"删除 r2（带外）: {r2_del}")
        print(f"删除 r1（带内）: {r1_del}")

        await asyncio.sleep(0.5)
        receiver.cancel()
    except websockets.ConnectionClosed as closed:
        print(f"连接被关闭：code={closed.code} reason={closed.reason!r}")
    finally:
        await socket.close()

    dones = [e for e in events if e.get("type") == "response.done"]
    for i, e in enumerate(dones, 1):
        conv = "有" if "conversation_id" in e.get("response", {}) else "无"
        print(f"r{i}: conv={conv} text={text_of(e)!r}")

    if len(dones) < 3:
        print("泄漏探针：FAIL（不足三轮）")
        return 1

    r3 = text_of(dones[2])
    leaked = "月" in r3
    clean = "太" in r3

    # 硬证据：带外 item 不在历史（删除报 not_found），带内 item 在（删除成功）。
    not_found = "not_found" in r2_del or "error" in r2_del
    in_history = r1_del == "deleted"
    deletion_proof = not_found and in_history

    print()
    if leaked and not clean:
        print("泄漏探针：LEAKED —— r3 复述出「月亮」，带外回答写进了历史！")
        return 1
    if clean and not leaked and deletion_proof:
        print("泄漏探针：CLEAN —— r3 复述「太阳」+ 带外 item 删除报 not_found（不在历史）、带内 item 删除成功（在历史）。")
        return 0
    if clean and not leaked:
        print("泄漏探针：CLEAN* —— r3 复述「太阳」，但删除证据不完整，见上面两行。")
        return 0 if deletion_proof else 1
    print(f"泄漏探针：INCONCLUSIVE —— r3={r3!r}（既非太阳也非月亮，模型未服从复述指令）")
    return 1


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
