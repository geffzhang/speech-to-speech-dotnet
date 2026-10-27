"""生成 corpus/smart-turn 的 input.json —— 语料里那 13 个输入文件的出处。

不参与构建，也不进 CI。**会覆盖**已有输入文件，所以只在增删用例时手工跑一次，
反方向（inputs → 基线）必须走 `diff_corpus.py record`，别手改 expected。
"""

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CORPUS = ROOT / "corpus" / "smart-turn"
CORPUS.mkdir(parents=True, exist_ok=True)

BASE = {
    "speculative_reopen_ms": 800,
    "smart_turn_max_wait_ms": 2000,
    "smart_turn_incomplete_delay_ms": 600,
    "smart_turn_threshold": 0.5,
}

CASES = [
    (
        "timing-disabled",
        "没有判定器（未启用，或本构建无 ONNX 实现）：宽限退回投机窗口、处理不推迟，"
        "分析耗时记为 null（上游同样不记）",
        {"mode": "timing", "classifier": "disabled", "config": dict(BASE)},
    ),
    (
        "timing-complete",
        "判定说完了：宽限就是投机窗口，处理立刻开始——这是最常见的路径",
        {
            "mode": "timing",
            "classifier": "complete",
            "probability": 0.93,
            "config": dict(BASE),
        },
    ),
    (
        "timing-incomplete",
        "判定没说完：宽限拉到 smart_turn_max_wait_ms，并把处理推迟 incomplete_delay_ms，"
        "让用户接着说的那几个字在 STT/LLM 开工之前作废本轮修订",
        {
            "mode": "timing",
            "classifier": "incomplete",
            "probability": 0.21,
            "config": dict(BASE),
        },
    ),
    (
        "timing-threshold-boundary",
        "概率正好等于阈值：上游是严格大于（probability > threshold），所以算没说完。"
        "差一个等号就会把「刚好 0.5」判成说完，宽限从 2000 掉到 800",
        {
            "mode": "timing",
            "classifier": "incomplete",
            "probability": 0.5,
            "config": dict(BASE),
        },
    ),
    (
        "timing-incomplete-delay-clamped",
        "处理延迟大于最长等待：取 min(delay, max_wait)。不钳制的话，判定给自己发的期限"
        "会比它自己的宽限还晚，输出会在宽限过期后仍被认为还在处理",
        {
            "mode": "timing",
            "classifier": "incomplete",
            "probability": 0.1,
            "config": {
                **BASE,
                "smart_turn_max_wait_ms": 500,
                "smart_turn_incomplete_delay_ms": 900,
            },
        },
    ),
    (
        "timing-incomplete-zero-delay",
        "没说完但处理延迟配成 0：宽限仍然拉到 max_wait，只是不推迟处理。"
        "两项互相独立，不能用一个期限代替另一个",
        {
            "mode": "timing",
            "classifier": "incomplete",
            "probability": 0.1,
            "config": {**BASE, "smart_turn_incomplete_delay_ms": 0},
        },
    ),
    (
        "timing-failed",
        "判定器抛异常：退回投机窗口、不推迟处理。临时故障不该让回复凭空多等两秒；"
        "状态记 failed 以便与「用户关掉了」区分",
        {
            "mode": "timing",
            "classifier": "failed",
            "probability": 0.0,
            "config": dict(BASE),
        },
    ),
    (
        "timing-narrow-windows",
        "非默认配置：投机窗口 300、最长等待 1500、处理延迟 400。"
        "锁住三条都取自配置而不是写死在默认值上",
        {
            "mode": "timing",
            "classifier": "incomplete",
            "probability": 0.4,
            "config": {
                "speculative_reopen_ms": 300,
                "smart_turn_max_wait_ms": 1500,
                "smart_turn_incomplete_delay_ms": 400,
                "smart_turn_threshold": 0.5,
            },
        },
    ),
    (
        "window-left-pads",
        "不到 8 秒的音频：在**前面**补零。往后面补会把整句话挪到时间轴的开头，"
        "模型看到的就不是「刚刚说完」而是「很久以前说过」",
        {"mode": "window", "samples": 40000, "pattern": "ramp"},
    ),
    (
        "window-tail-truncates",
        "超过 8 秒：取**尾部**。判定看的是最后那句话的收束，不是开头的犹豫；"
        "取头会让说很久的用户永远判不出说完",
        {"mode": "window", "samples": 200000, "pattern": "ramp"},
    ),
    (
        "window-exact",
        "正好 8 秒：原样返回，不补不截。这条是另外两条的对照基准",
        {"mode": "window", "samples": 128000, "pattern": "ramp"},
    ),
    (
        "window-empty",
        "空音频：整窗补零。phantom 触发后定稿的空段会走到这里，"
        "不能因为除零或越界抛异常——上游这里返回全零",
        {"mode": "window", "samples": 0, "pattern": "ramp"},
    ),
    (
        "window-signed-bits",
        "含负值的音频：锁住 float32 符号位。位模式比较最容易被忽略的就是这一条——"
        "只看绝对值或只看和，符号反了都看不出来",
        {"mode": "window", "samples": 200000, "pattern": "signed"},
    ),
]

for name, note, payload in CASES:
    path = CORPUS / f"{name}.input.json"
    path.write_text(
        json.dumps({"note": note, "payload": payload}, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print("wrote", path.name)
