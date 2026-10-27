#!/usr/bin/env python3
"""参照实现的助手语言探针。

读 stdin 的 ``{"texts": ["...", ...]}``，按上游 ``LMOutputProcessor._observe_assistant_language``
的规则累积探针窗口并判定语言，输出与 ``tools/LanguageProbe`` 同形的 JSON。

判定调用的是上游自己的 ``pipeline/language_detection.py``——阈值、长度门槛、CJK 短路、
``nb``→``no`` 归一化全部来自上游源码，不是这里重写的。

窗口累积那三行（``(probe + " " + text).strip()[-256:]``）在探针里复刻，
因为 ``lm_output_processor`` 连带整条管线依赖，无法单独加载。这段规则很短，
且「窗口是否停止累积」「截断点在哪」都会原样体现在输出的 windows 里，比对时能被抓住。

依赖：
    pip install lingua-language-detector
"""

import importlib.util
import json
import sys
from pathlib import Path

UPSTREAM_REPO = Path(r"C:\workshop\github\speech-to-speech")
LANG_PATH = UPSTREAM_REPO / "src" / "speech_to_speech" / "pipeline" / "language_detection.py"

PROBE_LIMIT = 256


def load_language_detection():
    spec = importlib.util.spec_from_file_location("upstream_language_detection", LANG_PATH)
    if spec is None or spec.loader is None:
        raise SystemExit(f"无法加载 {LANG_PATH}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    if not LANG_PATH.exists():
        print(f"参照实现不存在：{LANG_PATH}", file=sys.stderr)
        return 2

    language_detection = load_language_detection()
    detector = language_detection.warm_language_detector()

    payload = json.loads(sys.stdin.read())
    texts = payload["texts"]

    window = ""
    detected = None
    windows = []
    for text in texts:
        if detected is None:
            window = (window + " " + text).strip()[-PROBE_LIMIT:]
            detected = language_detection.detect_language_from_text(
                window,
                detector,
                minimum_confidence_gap=language_detection.MIN_ASSISTANT_CONFIDENCE_GAP,
                allow_short_cjk=True,
            )
        windows.append(window)

    json.dump(
        {"detected": detected, "windows": windows},
        sys.stdout,
        ensure_ascii=False,
        indent=2,
    )
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
