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
| `protocol/` | 线路协议事件的形状与编码 | 参照 `tools/refprobe/protocol_probe.py` · 本实现 `tools/ProtocolProbe` | 28 条，全通过。覆盖转写链路与助手消息项渲染链；缺口审计方式见 `protocol/README.md` |

音频层尚未建立语料：模型推理受浮点累加顺序影响，不可逐字节比对，只能锁采样率/声道/位深/段数/时长误差带。

`input.json` 里的 `note` 字段不参与比对，用来写清这条守什么；`payload` 内的其它键由探针自行解释
（例如文本探针的 `split`）。
