#!/usr/bin/env python3
"""参照实现的文本层探针。

只做一件事：读 stdin 的 ``{"text": "..."}``，调上游 speech-to-speech 的三个文本函数，
把结果按固定键序写成 JSON 到 stdout。差分语料的 ``.expected.json`` 由它生成。

为什么不 ``import speech_to_speech``：那会连带拉起整条管线的依赖。
这里按文件路径单独加载 ``LLM/utils.py``，只要 nltk / pillow / requests 三个包。

依赖（缺一不可，否则录制出来的基线不可信）：
    pip install nltk pillow requests
    # punkt_tab 需另下：nltk.download('punkt_tab') 或从 nltk_data 仓库取
"""

import importlib.util
import json
import sys
from pathlib import Path

UPSTREAM_REPO = Path(r"C:\workshop\github\speech-to-speech")
UTILS_PATH = UPSTREAM_REPO / "src" / "speech_to_speech" / "LLM" / "utils.py"


def load_utils():
    spec = importlib.util.spec_from_file_location("upstream_llm_utils", UTILS_PATH)
    if spec is None or spec.loader is None:
        raise SystemExit(f"无法加载 {UTILS_PATH}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    if not UTILS_PATH.exists():
        print(f"参照实现不存在：{UTILS_PATH}", file=sys.stderr)
        return 2

    from nltk import sent_tokenize

    utils = load_utils()

    raw = sys.stdin.read()
    payload = json.loads(raw)
    text = payload["text"]
    split = payload.get("split", True)

    result = {
        "unspeakable": utils.remove_unspeechable(text),
        "markdown": utils.remove_markdown(text),
    }
    if split:
        result["sentences"] = utils.sent_tokenize_preserving_markdown_code(text, sent_tokenize)

    json.dump(result, sys.stdout, ensure_ascii=False, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
