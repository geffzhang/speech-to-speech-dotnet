"""WebRTC 传输端到端冒烟：aiortc 对端 ↔ C# serve。

覆盖（上游 webrtc_session + calls 端点的跨实现行为等价）：
  1. POST Content-Type 错 → 415 "Content-Type must be application/sdp"
  2. aiortc offer（recvonly opus + oai-events datachannel）→ 201 + SDP answer + Location
  3. ICE/DTLS 连接 → datachannel 开启 → 服务端推 session.created（_on_open 口径）
  4. input_audio_buffer.append 经 datachannel → invalid_event_for_transport error
     （传输门控跨实现：音频必须走媒体轨）
  5. DELETE Location id → 200；重复 DELETE → 404 "Unknown call"

用法：python tools/smoke/webrtc_smoke.py [--port 8791] [--serve-cmd DOTNET_ARGS]
serve 需已另行启动（或用 --start-serve 让脚本自己拉起 dotnet serve）。
"""
from __future__ import annotations

import argparse
import asyncio
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request

from aiortc import RTCPeerConnection, RTCSessionDescription
from aiortc.mediastreams import AudioStreamTrack


def http(method: str, url: str, body: bytes | None = None, content_type: str | None = None):
    # 回环请求绕开一切代理：Windows 系统代理会劫持 127.0.0.1（D029 的 Python 侧镜像）。
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    req = urllib.request.Request(url, data=body, method=method)
    if content_type:
        req.add_header("Content-Type", content_type)
    try:
        with opener.open(req, timeout=15) as resp:
            return resp.status, dict(resp.headers), resp.read()
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read()


class RecvOnlyAudioTrack(AudioStreamTrack):
    """只收不发的音频轨：让 offer 带 audio m-line，与服务端 sendonly OPUS 协商。"""


async def run(port: int) -> int:
    base = f"http://127.0.0.1:{port}"

    # 1. Content-Type 门
    status, _, body = http("POST", f"{base}/v1/realtime/calls", b"{}", "application/json")
    assert status == 415, f"expected 415, got {status}: {body!r}"
    assert body.decode().strip() == "Content-Type must be application/sdp", body
    print("[1] 415 content-type gate OK")

    # 2. offer 协商
    pc = RTCPeerConnection()
    pc.addTransceiver("audio", direction="recvonly")
    channel = pc.createDataChannel("oai-events")

    events: asyncio.Queue[str] = asyncio.Queue()
    dc_open = asyncio.Event()
    connected = asyncio.Event()

    @channel.on("open")
    def _open() -> None:
        dc_open.set()

    @channel.on("message")
    def _message(message) -> None:
        events.put_nowait(message if isinstance(message, str) else message.decode())

    @pc.on("connectionstatechange")
    def _state() -> None:
        print(f"    pc state: {pc.connectionState}")
        if pc.connectionState == "connected":
            connected.set()

    offer = await pc.createOffer()
    await pc.setLocalDescription(offer)
    while pc.iceGatheringState != "complete":
        await asyncio.sleep(0.05)

    offer_sdp = pc.localDescription.sdp
    status, headers, body = http(
        "POST", f"{base}/v1/realtime/calls", offer_sdp.encode(), "application/sdp")
    assert status == 201, f"expected 201, got {status}: {body!r}"
    answer_sdp = body.decode()
    assert headers.get("Content-Type", "").startswith("application/sdp"), headers
    location = headers.get("Location", "")
    assert location.startswith("/v1/realtime/calls/"), location
    call_id = location.rsplit("/", 1)[1]
    print(f"[2] 201 + answer + Location OK (call_id={call_id})")

    # 3. 连接 + session.created
    await pc.setRemoteDescription(RTCSessionDescription(answer_sdp, type="answer"))
    await asyncio.wait_for(connected.wait(), timeout=20)
    await asyncio.wait_for(dc_open.wait(), timeout=10)
    created = await asyncio.wait_for(events.get(), timeout=15)
    created_event = json.loads(created)
    assert created_event["type"] == "session.created", created_event
    print("[3] datachannel open + session.created OK")

    # 4. 传输门控：append 经 datachannel 必须被拒
    channel.send(json.dumps({"type": "input_audio_buffer.append", "event_id": "evt_smoke", "audio": "AQID"}))
    error = json.loads(await asyncio.wait_for(events.get(), timeout=10))
    assert error["type"] == "error", error
    assert error["error"]["type"] == "invalid_event_for_transport", error
    assert error["error"]["message"] == (
        "In WebRTC mode audio arrives via the media track; "
        "input_audio_buffer.append is not supported."), error
    assert error["error"].get("event_id") == "evt_smoke", error
    print("[4] append gated over WebRTC OK")

    # 5. 挂断
    status, _, body = http("DELETE", f"{base}{location}")
    assert status == 200, f"expected 200, got {status}: {body!r}"
    status, _, body = http("DELETE", f"{base}{location}")
    assert status == 404, f"expected 404, got {status}: {body!r}"
    assert body.decode().strip() == "Unknown call", body
    print("[5] DELETE 200 + repeat 404 OK")

    await pc.close()
    print("WEBRTC SMOKE PASS")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8791)
    args = parser.parse_args()
    return asyncio.run(run(args.port))


if __name__ == "__main__":
    sys.exit(main())
