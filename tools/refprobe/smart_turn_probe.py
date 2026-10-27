"""Smart Turn 端点判定的差分探针（参照实现侧）。

比两件事：

1. ``mode=timing`` —— 给定判定器的结论（说完了 / 没说完 / 抛异常 / 没有判定器）与
   一组配置，上游 ``VADHandler._smart_turn_timing_ms`` 给出的
   ``(宽限 ms, 处理延迟 ms)`` 与状态字符串。
2. ``mode=window`` —— ``SmartTurnAnalyzer._prepare_audio`` 把任意长度音频整理成
   模型要的定长窗口（8 秒 × 16 kHz）：取头还是取尾、短了往哪边补。

两处都不碰 torch，也不需要 onnxruntime 与 transformers —— 这里只跑纯决策与纯预处理。
上游模块顶层 import torch，参照环境没有装（PyPI 不可达），所以塞一个桩模块：
``_smart_turn_timing_ms`` 与 ``_prepare_audio`` 都不调用任何 torch API，
桩只用于让模块导入通过。这一点记在 corpus/smart-turn/README.md 里。

音频不进语料文件：按 ``pattern`` 现场生成。生成规则在两侧各写一份（C# 侧见
``tools/SmartTurnProbe/Program.cs``），只用到整数运算与一次 double→float32 转换，
IEEE 转换是确定性的，所以两侧逐位相同。

输出里**不出现浮点数**：概率只影响判定结论、结果只以布尔与整数示人；
窗口的样本以 float32 位模式（uint32）示人。理由与 corpus/turns 一致——
浮点的十进制写法是两侧运行时各自的格式化策略，比它等于允许漂移。
"""

from __future__ import annotations

import json
import sys
import types
from pathlib import Path

import numpy as np


def _resolve_repo() -> Path:
    """定位参照仓库。优先环境变量，其次 ../workshop/github/speech-to-speech。"""
    import os

    candidate = os.environ.get("S2S_REFERENCE_REPO")
    if candidate:
        path = Path(candidate)
        if (path / "src" / "speech_to_speech").is_dir():
            return path

    for base in Path(__file__).resolve().parents:
        guess = base / "workshop" / "github" / "speech-to-speech"
        if (guess / "src" / "speech_to_speech").is_dir():
            return guess
        guess = base.parent / "workshop" / "github" / "speech-to-speech"
        if (guess / "src" / "speech_to_speech").is_dir():
            return guess

    raise SystemExit("找不到参照仓库；用 S2S_REFERENCE_REPO 指定 C:/workshop/github/speech-to-speech")


def _install_torch_stub() -> None:
    """让 vad_handler 的顶层 ``import torch`` 通过。只在本探针进程内生效。"""

    class _StubModule(types.ModuleType):
        def __getattr__(self, name):  # noqa: D105 - 桩
            if name.startswith("__"):
                raise AttributeError(name)

            class _Dummy:
                def __init__(self, *args, **kwargs):
                    pass

                def __call__(self, *args, **kwargs):
                    return self

            _Dummy.__name__ = name
            return _Dummy

    sys.modules.setdefault("torch", _StubModule("torch"))


def _load_upstream():
    repo = _resolve_repo()
    sys.path.insert(0, str(repo / "src"))
    _install_torch_stub()

    from speech_to_speech.VAD.smart_turn import SmartTurnAnalyzer, SmartTurnResult
    from speech_to_speech.VAD.vad_handler import VADHandler

    return VADHandler, SmartTurnAnalyzer, SmartTurnResult


VADHandler, SmartTurnAnalyzer, SmartTurnResult = _load_upstream()


class StubAnalyzer:
    """替身判定器：结论由语料指定，不做任何推理。"""

    def __init__(self, *, probability: float, threshold: float, fail: bool) -> None:
        self.probability = probability
        self.threshold = threshold
        self.fail = fail

    def predict(self, audio, sample_rate: int = 16000):
        if self.fail:
            raise RuntimeError("stub inference failure")
        return SmartTurnResult(
            complete=self.probability > self.threshold,
            probability=self.probability,
            inference_ms=0.0,
        )


def make_handler(config: dict, classifier: str, probability: float):
    """不经 __init__ 造一个只够跑 _smart_turn_timing_ms 的对象。

    直接调 __init__ 会去加载 Silero 模型与判定器，两者都不在测的范围内。
    这里只补被测方法真正读到的属性，方法本身体仍是上游源码。
    """
    handler = object.__new__(VADHandler)
    handler.speculative_reopen_ms = int(config["speculative_reopen_ms"])
    handler.smart_turn_max_wait_ms = int(config["smart_turn_max_wait_ms"])
    handler.smart_turn_incomplete_delay_ms = int(config["smart_turn_incomplete_delay_ms"])
    handler.sample_rate = 16000
    handler._last_smart_turn_status = None
    handler._last_smart_turn_analysis_s = None

    if classifier == "disabled":
        handler.smart_turn_analyzer = None
    else:
        handler.smart_turn_analyzer = StubAnalyzer(
            probability=probability,
            threshold=float(config.get("smart_turn_threshold", 0.5)),
            fail=classifier == "failed",
        )
    return handler


def generate_audio(samples: int, pattern: str) -> np.ndarray:
    """与 C# 侧同一套整数规则。"""
    out = np.empty(samples, dtype=np.float64)
    for i in range(samples):
        if pattern == "signed":
            out[i] = ((i * 37) % 2001 - 1000) / 1000.0
        else:
            out[i] = ((i * 37) % 1000) / 1000.0
    return out.astype(np.float32)


def bits_hash(window: np.ndarray) -> str:
    """FNV-1a 64 over the little-endian float32 bit patterns."""
    payload = np.ascontiguousarray(window, dtype="<f4").tobytes()
    value = 0xCBF29CE484222325
    for byte in payload:
        value ^= byte
        value = (value * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return f"0x{value:016x}"


def head_bits(window: np.ndarray, count: int) -> list[int]:
    return [int(x) for x in window[:count].view(np.uint32)]


def tail_bits(window: np.ndarray, count: int) -> list[int]:
    return [int(x) for x in window[-count:].view(np.uint32)]


def count_leading_zeros(window: np.ndarray) -> int:
    nonzero = np.flatnonzero(window)
    return int(nonzero[0]) if nonzero.size else int(window.size)


def count_trailing_zeros(window: np.ndarray) -> int:
    nonzero = np.flatnonzero(window)
    return int(window.size - 1 - nonzero[-1]) if nonzero.size else int(window.size)


def run_timing(payload: dict) -> dict:
    config = payload["config"]
    classifier = payload.get("classifier", "disabled")
    handler = make_handler(config, classifier, float(payload.get("probability", 0.0)))

    silence = np.zeros(16000, dtype=np.float32)
    grace_ms, delay_ms = handler._smart_turn_timing_ms(silence)

    return {
        "grace_ms": int(grace_ms),
        "processing_delay_ms": int(delay_ms),
        "status": handler._last_smart_turn_status,
        "analysis_seconds_is_null": handler._last_smart_turn_analysis_s is None,
    }


def run_window(payload: dict) -> dict:
    audio = generate_audio(int(payload.get("samples", 0)), payload.get("pattern", "ramp"))
    window = SmartTurnAnalyzer._prepare_audio(audio, int(payload.get("sample_rate", 16000)))
    window = np.asarray(window, dtype=np.float32)

    return {
        "length": int(window.size),
        "leading_zeros": count_leading_zeros(window),
        "trailing_zeros": count_trailing_zeros(window),
        "head_bits": head_bits(window, 4),
        "tail_bits": tail_bits(window, 4),
        "bits_hash": bits_hash(window),
    }


def main() -> int:
    payload = json.load(sys.stdin)
    mode = payload.get("mode", "timing")
    if mode == "timing":
        result = run_timing(payload)
    elif mode == "window":
        result = run_window(payload)
    else:
        raise SystemExit(f"未知 mode: {mode}")

    json.dump(result, sys.stdout, ensure_ascii=False, sort_keys=True)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
