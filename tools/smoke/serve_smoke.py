"""把一段 WAV 喂给运行中的 `serve`，并按时间线打印服务端事件。

为什么要有这个脚本：差分语料锁得住「账本怎么算」，锁不住「队列跑起来什么顺序」。
`local` / `talk` 要走真实麦克风与扬声器，无法在无人值守下重复；`serve` 接受的是
同一套 Realtime 协议，脚本化客户端能把「一次完整说话」变成可复现的几十行输出。

用法：
    python tools/smoke/serve_smoke.py --wav speech_6s.wav --url ws://127.0.0.1:8765

输出分两列：左列是相对开播的毫秒数，右列是事件与关键字段。
音频增量只报字节数——把 base64 打满屏没有信息量。
"""

from __future__ import annotations

import argparse
import asyncio
import base64
import json
import time
import wave

import numpy as np
import websockets

INPUT_SAMPLE_RATE = 16_000   # 语料 wav 的口径
WIRE_SAMPLE_RATE = 24_000    # 服务端 session.update 里声明的手上口径
FRAME_MS = 20


def load_wav(path: str) -> np.ndarray:
    with wave.open(path) as handle:
        if handle.getsampwidth() != 2 or handle.getnchannels() != 1:
            raise SystemExit("只支持单声道 16 bit PCM 的 wav")
        rate = handle.getframerate()
        samples = np.frombuffer(handle.readframes(handle.getnframes()), dtype=np.int16)
    if rate != INPUT_SAMPLE_RATE:
        # 线性插值足够：VAD 看能量，STT 内部还会再归一化到 16 kHz。
        count = int(round(len(samples) * INPUT_SAMPLE_RATE / rate))
        samples = np.interp(
            np.linspace(0, len(samples) - 1, count, dtype=np.float64),
            np.arange(len(samples), dtype=np.float64),
            samples.astype(np.float64),
        ).astype(np.int16)
    return samples


def to_wire(samples: np.ndarray, trailing_silence_s: float = 1.5) -> bytes:
    """16 kHz → 24 kHz，回 int16 小端，尾部补一段静音。

    补静音的必须性：VAD 靠静音判「说完了」。不给尾巴，这一段会一直停在
    speech_started 之后——脚本只是停止推流，服务端并不知道用户说完了。
    """
    padded = np.concatenate([
        samples.astype(np.float64),
        np.zeros(int(INPUT_SAMPLE_RATE * trailing_silence_s)),
    ])
    count = int(round(len(padded) * WIRE_SAMPLE_RATE / INPUT_SAMPLE_RATE))
    upsampled = np.interp(
        np.linspace(0, len(padded) - 1, count, dtype=np.float64),
        np.arange(len(padded), dtype=np.float64),
        padded,
    )
    return np.clip(upsampled, -32768, 32767).astype("<i2").tobytes()


def describe(event: dict) -> str:
    kind = event.get("type", "?")
    if kind == "response.audio.delta":
        return f"response.audio.delta  ({len(event.get('delta', '')) * 3 // 4} bytes)"
    if kind.endswith("input_audio_transcription.completed"):
        # 线上名是 conversation.item.input_audio_transcription.completed，
        # 按后缀判，别按全名——前缀属于「挂在哪个会话项上」。
        return f"transcription.completed  {event.get('transcript', '')!r}"
    if kind == "response.audio_transcript.done":
        return f"audio_transcript.done  {event.get('transcript', '')!r}"
    if kind == "response.audio_transcript.delta":
        return "audio_transcript.delta"
    if kind == "response.done":
        response = event.get("response", {})
        metadata = response.get("metadata") or {}
        # conversation_id 的有无就是带外的线路签名：带内必有，带外（conversation="none"）必无。
        conv = response.get("conversation_id")
        conv_note = f" conv={conv}" if conv is not None else " conv=<absent>"
        return (
            f"response.done  status={response.get('status')}"
            f" output_items={len(response.get('output') or [])}"
            f"{conv_note}"
            f" metadata={json.dumps(metadata, ensure_ascii=False, sort_keys=True)}"
        )
    if kind == "error":
        return f"error  {json.dumps(event, ensure_ascii=False)}"
    if kind.startswith("response.function_call"):
        return f"{kind}  name={event.get('name')} args={event.get('arguments')}"
    return kind


async def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--wav", required=True)
    parser.add_argument("--url", default="ws://127.0.0.1:8765")
    parser.add_argument("--timeout", type=float, default=120.0)
    parser.add_argument("--speak-twice", action="store_true", help="播完后隔一段再播一次，验证同一条连接下的第二轮")
    parser.add_argument("--instructions", default=None,
                        help="通过 session.update 覆盖默认 instructions（控制 LLM 回复长度用）")
    parser.add_argument("--max-output-tokens", type=int, default=None,
                        help="session 级 max_response_output_tokens（硬截断回复长度）")
    parser.add_argument("--gap", type=float, default=3.0,
                        help="两轮之间的静默秒数。小间隔测打断（第二轮插进第一轮的回答里），"
                             "大间隔测正常收口后的新一轮")
    parser.add_argument("--side-question", default=None,
                        help="第一轮收口后发一条带外响应（conversation=\"none\" + 纯文本模态）"
                             "当侧问：验证 conversation_id 缺席、输出不污染会话历史")
    args = parser.parse_args()

    samples = load_wav(args.wav)
    wire = to_wire(samples)
    frame_bytes = int(WIRE_SAMPLE_RATE * FRAME_MS / 1000) * 2
    frames = [wire[i:i + frame_bytes] for i in range(0, len(wire), frame_bytes)]

    started = time.perf_counter()
    events: list[tuple[float, dict]] = []
    close_note = ""

    socket = await websockets.connect(args.url, max_size=None, proxy=None)
    try:

        async def receive() -> None:
            async for raw in socket:
                event = json.loads(raw)
                events.append((time.perf_counter() - started, event))

        receiver = asyncio.create_task(receive())

        session_payload = {
            "type": "session.update",
            "session": {
                "type": "realtime",
                "audio": {
                    "input": {
                        "format": {"type": "audio/pcm", "rate": WIRE_SAMPLE_RATE},
                        "turn_detection": {"type": "server_vad"},
                    },
                    "output": {"format": {"type": "audio/pcm", "rate": WIRE_SAMPLE_RATE}},
                },
            },
        }
        if args.instructions:
            session_payload["session"]["instructions"] = args.instructions
        if args.max_output_tokens:
            session_payload["session"]["max_response_output_tokens"] = args.max_output_tokens
        await socket.send(json.dumps(session_payload))

        # 按真实时长推流：一次性灌完会让 VAD 看到的时间轴与实际不符。
        async def speak() -> None:
            deadline = time.perf_counter()
            for frame in frames:
                await socket.send(json.dumps({
                    "type": "input_audio_buffer.append",
                    "audio": base64.b64encode(frame).decode("ascii"),
                }))
                deadline += FRAME_MS / 1000
                delay = deadline - time.perf_counter()
                if delay > 0:
                    await asyncio.sleep(delay)

        await speak()

        if args.speak_twice:
            await asyncio.sleep(args.gap)
            await speak()

        # 等齐该等的 response.done 再收尾，别在音频还在路上时就断开。
        # 带外侧问走纯文本模态（不出 TTS，秒级返回），done 数加一。
        expected_done = (2 if args.speak_twice else 1) + (1 if args.side_question else 0)
        asked = False
        give_up = time.perf_counter() + args.timeout
        while time.perf_counter() < give_up:
            await asyncio.sleep(0.1)
            done_count = sum(1 for _, e in events if e.get("type") == "response.done")
            if not asked and args.side_question and done_count >= (2 if args.speak_twice else 1):
                # 第一轮（或两轮）语音收口后再问：会话历史非空，带外响应才有上下文可读。
                asked = True
                await socket.send(json.dumps({
                    "type": "response.create",
                    "response": {
                        "conversation": "none",
                        "output_modalities": ["text"],
                        "max_output_tokens": 32,
                        "instructions": f"只回答问题本身，不超过二十个字。问题：{args.side_question}",
                    },
                }))
                continue
            if done_count >= expected_done:
                await asyncio.sleep(1.0)
                break

        receiver.cancel()
    except websockets.ConnectionClosed as closed:
        close_note = f"连接被关闭：code={closed.code} reason={closed.reason!r}"
    finally:
        await socket.close()

    if close_note:
        print(close_note)

    previous = None
    for offset, event in events:
        line = f"+{offset * 1000:8.0f}ms  {describe(event)}"
        if line == previous:
            continue
        print(line)
        previous = line

    if args.side_question:
        dones = [e for _, e in events if e.get("type") == "response.done"]
        speech = [e for e in dones if e.get("response", {}).get("conversation_id") is not None]
        side = [e for e in dones if "conversation_id" not in e.get("response", {})]
        oob_created = [e for _, e in events
                       if e.get("type") == "response.created" and "conversation_id" not in e.get("response", {})]
        ok = len(speech) >= 1 and len(side) >= 1 and len(oob_created) >= 1
        print()
        print(f"带外侧问断言：{'PASS' if ok else 'FAIL'}"
              f"（带内 done 带 conversation_id: {len(speech)} 条；"
              f"带外 created/done 缺 conversation_id: {len(oob_created)}/{len(side)} 条）")
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
