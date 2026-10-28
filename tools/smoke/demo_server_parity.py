#!/usr/bin/env python
"""demo 服务端行为对拍：Python uvicorn(server.py) vs ASP.NET Core 同批请求比对 JSON。

用法：
  python tools/smoke/demo_server_parity.py            # 起两侧，跑对拍，目标零差异
  python tools/smoke/demo_server_parity.py --keep     # 报差异后保留进程便于排查

两侧用同一组环境变量（直连模式，不挂 LB/auth），对关键端点发同一批请求，
逐条比对 status / content-type / body。差异打印后以非零码退出。
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PY = ROOT / ".venv-demo" / "Scripts" / "python.exe"
if not PY.exists():
    PY = ROOT / ".venv-demo" / "bin" / "python"

DOTNET = "dotnet"


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait_up(url: str, timeout: float = 30.0) -> bool:
    import urllib.request

    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            urllib.request.urlopen(url, timeout=1)
            return True
        except Exception:
            time.sleep(0.2)
    return False


def build_env(port: int) -> dict:
    env = os.environ.copy()
    # 直连模式：不挂 LB/auth；SERPER 故意留空以触发 503 分支
    for k in ("LOAD_BALANCER_URL", "SPACE_ID", "SERPER_API_KEY", "SPEECH_TO_SPEECH_URL", "RTC_ICE_SERVERS"):
        env.pop(k, None)
    env["DEMO_ROOT"] = str(ROOT / "demo")
    return env


# ── 对拍用例 ────────────────────────────────────────────────────────────────
# (method, path, body, content_type, note)
CASES: list[tuple[str, str, bytes | None, str | None, str]] = [
    ("GET", "/api/config", None, None, "config 直连形状"),
    ("GET", "/api/me", None, None, "me disabled"),
    ("GET", "/api/me/", None, None, "me 尾斜杠 404"),
    ("GET", "/API/config", None, None, "大小写 404"),
    ("HEAD", "/api/config", None, None, "HEAD 落静态 404"),
    ("POST", "/api/config", b"", None, "POST config 405"),
    ("PUT", "/api/config", b"", None, "PUT config 405"),
    ("OPTIONS", "/api/config", b"", None, "OPTIONS 405"),
    ("GET", "/api/calls", None, None, "GET calls 404"),
    ("POST", "/api/calls", b'{"sdp":"v=0"}', "application/json", "calls 无 URL 404"),
    ("POST", "/api/calls", b"{", "application/json", "calls 坏 body 仍 404"),
    ("GET", "/api/search", None, None, "GET search 404"),
    ("POST", "/api/search", b"", "application/json", "search 空 body missing"),
    ("POST", "/api/search", b"null", "application/json", "search null missing"),
    ("POST", "/api/search", b"{}", "application/json", "search 缺 query"),
    ("POST", "/api/search", b'{"query":""}', "application/json", "search 空 query 400"),
    ("POST", "/api/search", b'{"query":"x"}', "application/json", "search 503 未配置"),
    ("POST", "/api/search", b'{"query":"x","extra":1}', "application/json", "search extra 忽略"),
    ("POST", "/api/search", b'{"query":"x","key":null}', "application/json", "search null key 合法"),
    ("POST", "/api/search", b'{"query":123}', "application/json", "search string_type query"),
    ("POST", "/api/search", b'{"query":"x","key":5}', "application/json", "search string_type key"),
    ("POST", "/api/search", b'{"query":"x","sessionId":5}', "application/json", "search string_type sessionId"),
    ("POST", "/api/search", b'{"query":"x","key":-0.0}', "application/json", "search 负零回显"),
    ("POST", "/api/search", b'{"query":"x","key":99999999999999999999}', "application/json", "search 大整数回显"),
    ("POST", "/api/search", b"true", "application/json", "search model_attributes_type true"),
    ("POST", "/api/search", b"42", "application/json", "search model_attributes_type 42"),
    ("POST", "/api/search", b'"hello"', "application/json", "search model_attributes_type str"),
    ("POST", "/api/search", b"[1,2]", "application/json", "search model_attributes_type list"),
    ("POST", "/api/search", b'{"query":"x"}', "text/plain", "search text/plain 走原始"),
    ("POST", "/api/search", b'{"query":"x"}', "application/octet-stream", "search octet-stream 走原始"),
    ("POST", "/api/search", b'{"query":"x"}', "text/json", "search text/json 不是 JSON"),
    ("POST", "/api/search", b'{"query":"x"}', "application/json , charset=utf-8", "search 逗号怪写法"),
    ("POST", "/api/search", b'{"query":"x"}', "application/vnd.api+json", "search +json 算 JSON"),
    ("POST", "/api/search", b"   ", "application/json", "search 空白 json_invalid"),
    ("POST", "/api/search", b"{", "application/json", "search 未闭合 json_invalid"),
    ("POST", "/api/search", b'{"query":"a\tb"}', "application/json", "search 控制字符 json_invalid"),
    ("POST", "/api/search", b'{"query":"x","key":NaN}', "application/json", "search NaN input 500"),
    ("POST", "/api/search", b'{"query":"x","key":Infinity}', "application/json", "search Inf input 500"),
    ("POST", "/api/search", b'{"query":"x","extra":NaN}', "application/json", "search NaN extra 忽略"),
    ("GET", "/no-such", None, None, "静态缺失 404"),
    ("POST", "/no-such", b"", None, "非 GET/HEAD 405"),
    ("POST", "/main.js", b"", None, "静态文件 POST 405"),
    ("GET", "/vendor/openai-realtime-agents.umd.js", None, None, "vendor 缺失 503"),
    ("GET", "/vendor/local-vad/unknown.js", None, None, "vad 未知 404"),
    ("GET", "/vendor/local-vad/bundle.min.js", None, None, "vad 已知缺失 503"),
]


def fetch(port: int, method: str, path: str, body: bytes | None, ctype: str | None) -> tuple[int, str, bytes]:
    import http.client

    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=15)
    headers = {}
    if ctype:
        headers["Content-Type"] = ctype
    conn.request(method, path, body=body or b"", headers=headers)
    resp = conn.getresponse()
    data = resp.read()
    ct = resp.getheader("content-type", "")
    conn.close()
    return resp.status, ct, data


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep", action="store_true", help="报差异后保留进程")
    args = ap.parse_args()

    py_port = free_port()
    cs_port = free_port()
    env = build_env(py_port)

    procs: list[subprocess.Popen] = []
    try:
        # Python 侧
        py_proc = subprocess.Popen(
            [str(PY), "-m", "uvicorn", "server:app",
             "--host", "127.0.0.1", "--port", str(py_port), "--log-level", "warning"],
            cwd=str(ROOT / "demo"), env=env,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        procs.append(py_proc)

        # ASP.NET 侧
        cs_proc = subprocess.Popen(
            [DOTNET, "run", "--project", "src/SpeechToSpeech.Demo.Web", "--no-build",
             "--urls", f"http://127.0.0.1:{cs_port}"],
            cwd=str(ROOT), env=env,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        procs.append(cs_proc)

        if not wait_up(f"http://127.0.0.1:{py_port}/api/config"):
            print("Python 侧未就绪", file=sys.stderr)
            return 2
        if not wait_up(f"http://127.0.0.1:{cs_port}/api/config"):
            print("ASP.NET 侧未就绪", file=sys.stderr)
            return 2

        diffs = 0
        for method, path, body, ctype, note in CASES:
            try:
                s1, ct1, b1 = fetch(py_port, method, path, body, ctype)
            except Exception as e:
                print(f"[PY-ERR ] {note}: {e}")
                diffs += 1
                continue
            try:
                s2, ct2, b2 = fetch(cs_port, method, path, body, ctype)
            except Exception as e:
                print(f"[CS-ERR ] {note}: {e}")
                diffs += 1
                continue

            problems = []
            if s1 != s2:
                problems.append(f"status {s1}!={s2}")
            # content-type 只比主段（charset 参数两侧可能略异）
            if ct1.split(";")[0].strip() != ct2.split(";")[0].strip():
                problems.append(f"content-type {ct1!r}!={ct2!r}")
            # HEAD 无 body，只比状态
            if method != "HEAD" and b1 != b2:
                problems.append(f"body\n      py: {b1[:300]!r}\n      cs: {b2[:300]!r}")

            if problems:
                diffs += 1
                print(f"[DIFF   ] {note} ({method} {path})")
                for p in problems:
                    print(f"      {p}")
            else:
                print(f"[OK     ] {note}")

        print()
        if diffs:
            print(f"对拍完成：{diffs} 处差异")
            return 1
        print(f"对拍完成：{len(CASES)} 条零差异")
        return 0
    finally:
        if args.keep:
            print("进程保留（--keep），Ctrl+C 结束")
            try:
                while True:
                    time.sleep(1)
            except KeyboardInterrupt:
                pass
        for p in procs:
            p.terminate()
        for p in procs:
            try:
                p.wait(timeout=5)
            except Exception:
                p.kill()


if __name__ == "__main__":
    sys.exit(main())
