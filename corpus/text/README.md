# 文本层差分语料

守护上游 `src/speech_to_speech/LLM/utils.py` 里三个文本函数：

| 探针字段 | 上游函数 | 本实现 |
|---|---|---|
| `unspeakable` | `remove_unspeechable` | `TextNormalizer.RemoveUnspeakable` |
| `markdown` | `remove_markdown` | `TextNormalizer.RemoveMarkdown` |
| `sentences` | `sent_tokenize_preserving_markdown_code(text, nltk.sent_tokenize)` | `TextNormalizer.SentenceTokenizePreservingMarkdownCode` |

注意上游函数名是 `remove_unspeechable`（带 speech），本实现叫 `RemoveUnspeakable`——只是命名差异，无语义差异。

## 怎么跑

```bash
# 1) 录基线（参照实现，只需在有 Python 依赖的机器上做一次）
export PYTHONUTF8=1
python <skill>/scripts/diff_corpus.py record --corpus corpus/text \
  --cmd "C:/Users/geffzhang/.workbuddy/binaries/python/envs/default/Scripts/python.exe tools/refprobe/text_probe.py"

# 2) 比对（本实现，可进 CI）
python <skill>/scripts/diff_corpus.py verify --corpus corpus/text --json-canonical \
  --cmd "dotnet tools/TextProbe/bin/Debug/net10.0/TextProbe.dll"
```

## 录制环境（基线可复现的前提）

参照实现跑在 Python 3.13 + `nltk 3.10.4` + `punkt_tab`（数据取自 nltk_data 仓库的
`packages/tokenizers/punkt_tab.zip`）+ `pillow` + `requests`。
Punkt 版本变了分句结果会变，重录前先核对。

## `split` 开关

`payload.split` 默认为 `true`。置 `false` 时探针只输出 `unspeakable` 与 `markdown`，
不出 `sentences`。

用途：中文文本的切分是**已登记的刻意差异**（C005——上游 Punkt 英文模型完全不切 CJK，
本实现按全角标点切）。这类用例只想逐字节比两个纯函数，不想让已知分歧把整份语料染红。

## 已知分歧（保留在此，作为 C005 的证据）

上游实测输出（`cjk-terminator-boundary`，切分未关闭时）：

```json
{ "sentences": ["一句话。第二句话！第三句话？"] }
```

本实现：

```json
{ "sentences": ["一句话。", "第二句话！", "第三句话？"] }
```

`markdown` 与 `unspeakable` 两栏在全部 15 条用例上都与上游**逐字节相同**；
分歧仅存在于 `sentences` 且仅由 CJK 标点触发。

该行为由 `TextNormalizerTests.SentenceTokenize_TreatsCjkTerminatorsAsBoundaries_UnlikeUpstreamPunkt`
锁定——想改行为必须先改那条测试，再改 C005。

## 未覆盖

- 音频层不可用同输入逐字节比对（模型推理受浮点累加顺序影响），只能锁采样率/段数/时长误差带。
- 上游分句的 Punkt 模型是统计式的，本实现是确定性规则；英文长难句仍可能有边界差异，
  当前 15 条只覆盖了常见缩写与连续标点，属抽样而非穷举。
