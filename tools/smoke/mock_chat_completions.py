"""最小 OpenAI 兼容 mock：只服务工具调用端到端验证。

行为（脚本化，不智能）：
  - messages 里没有 role=tool   → 流式回一个 tool_calls 增量（get_weather(city=北京)），
    finish_reason=tool_calls。
  - messages 里有 role=tool      → 流式回文本，把工具结果里的温度念出来，
    finish_reason=stop。

只实现 /v1/chat/completions 的 SSE 形状（data: {...} / data: [DONE]），
够 OpenAiChatLanguageModel 消费即可。
"""
import json
import re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TOOL_ARGS = {"city": "北京", "unit": "celsius"}
TOOL_RESULT_TEMP = "25"


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def do_POST(self):
        if not self.path.rstrip().endswith("/chat/completions"):
            self.send_error(404)
            return

        length = int(self.headers.get("Content-Length", 0))
        body = json.loads(self.rfile.read(length))
        messages = body.get("messages", [])
        tool_results = [m for m in messages if m.get("role") == "tool"]

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()

        def send(obj):
            payload = ("data: " + json.dumps(obj, ensure_ascii=False) + "\n\n").encode()
            self.wfile.write(f"{len(payload):x}\r\n".encode() + payload + b"\r\n")
            self.wfile.flush()

        def send_done():
            payload = b"data: [DONE]\n\n"
            self.wfile.write(f"{len(payload):x}\r\n".encode() + payload + b"\r\n")
            self.wfile.flush()

        def chunk(delta, finish=None):
            send({"id": "chatcmpl_mock", "object": "chat.completion.chunk",
                  "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]})

        if not tool_results:
            # 第一轮：工具调用。arguments 按 OpenAI 流式习惯拆两片，验证累加。
            chunk({})
            chunk({"tool_calls": [{"index": 0, "id": "call_from_provider_ignored",
                                   "type": "function",
                                   "function": {"name": "get_weather",
                                                "arguments": json.dumps({"city": "北京"}, ensure_ascii=False)[:6]}}]})
            chunk({"tool_calls": [{"index": 0,
                                   "function": {"arguments": json.dumps({"city": "北京"}, ensure_ascii=False)[6:]}}]})
            chunk({}, finish="tool_calls")
        else:
            # 第二轮：把工具结果念出来。
            raw = tool_results[-1].get("content") or ""
            match = re.search(r"-?\d+(?:\.\d+)?", str(raw))
            temp = match.group(0) if match else TOOL_RESULT_TEMP
            city = TOOL_ARGS["city"]
            for piece in (f"{city}今天", f" {temp}", " 度。"):
                chunk({"content": piece})
            chunk({}, finish="stop")

        send_done()
        self.wfile.write(b"0\r\n\r\n")


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", 18080), Handler).serve_forever()
