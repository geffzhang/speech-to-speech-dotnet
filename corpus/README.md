# 差分语料

命名：`<case>.input.json` / `<case>.expected.json`。

- 输入约定：文件里的 `payload` 键会被序列化后送入被测程序 stdin；没有 `payload` 则整个文件原样送入
- `.expected.json` 只能由参照实现生成：`diff_corpus.py record`
- 每条语料的名字要说明它在守什么
- 参照实现升级时先重录、再逐条看差异决策，不要一把 `--update` 全过

## 本项目的语料目录

| 目录 | 被测层 | 探针 | 说明 |
|---|---|---|---|
| `text/` | 文本规范化与分句 | 参照 `tools/refprobe/text_probe.py` · 本实现 `tools/TextProbe` | 15 条，全通过。说明与复现环境见 `text/README.md` |
| `protocol/` | 线路协议事件的形状与编码 | 参照 `tools/refprobe/protocol_probe.py` · 本实现 `tools/ProtocolProbe` | 36 条，全通过。覆盖转写链路与助手消息项渲染链；缺口审计方式见 `protocol/README.md` |
| `turns/` | 投机轮次状态迁移 | 参照 `tools/refprobe/turns_probe.py` · 本实现 `tools/TurnsProbe` | 13 条，全通过。虚拟时钟逐值比对裁决与期限，做法见 `turns/README.md` |
| `lang/` | 助手输出语言识别 | 参照 `tools/refprobe/lang_probe.py` · 本实现 `tools/LanguageProbe` | 15 条，全通过。同一个算法的两种实现互证（lingua-py / lingua-rs） |
| `smart-turn/` | 端点判定到宽限与处理延迟的映射、判定器输入窗口 | 参照 `tools/refprobe/smart_turn_probe.py` · 本实现 `tools/SmartTurnProbe` | 13 条，全通过。脚本化判定器，不需要模型；见 `smart-turn/README.md` |
| `latency/` | 响应时延账的状态迁移与 metadata 字符串 | 参照 `tools/refprobe/latency_probe.py` · 本实现 `tools/LatencyProbe` | 16 条，全通过。操作脚本驱动，时间全部由语料给定；payload 与日志行逐字符比，见 `latency/README.md` |

音频层尚未建立语料：模型推理受浮点累加顺序影响，不可逐字节比对，只能锁采样率/声道/位深/段数/时长误差带。

`input.json` 里的 `note` 字段不参与比对，用来写清这条守什么；`payload` 内的其它键由探针自行解释
（例如文本探针的 `split`）。
