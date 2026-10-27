# `corpus/lang` —— 助手输出语言检测的差分语料

守的是**语言判定**：一串助手文本碎片，判出什么语言（判不出就是 `null`），
以及每观察一片之后的探针窗口内容。

```bash
# 录制（基线只能由参照实现产出）
python <skill>/scripts/diff_corpus.py record --corpus corpus/lang \
  --cmd "python tools/refprobe/lang_probe.py"

# 比对
python <skill>/scripts/diff_corpus.py verify --corpus corpus/lang --json-canonical \
  --cmd "dotnet tools/LanguageProbe/bin/Debug/net10.0/LanguageProbe.dll"
```

## 输入输出形状

```jsonc
// 输入：按模型输出顺序的文本片
{ "note": "这条守什么", "texts": ["Sure,", " I can", " help you with that."] }

// 输出
{
  "detected": "en",                       // 判不出为 null
  "windows": ["Sure,", "Sure, I can", "Sure, I can help you with that."]
}
```

`windows` 不是调试信息，是断言的一部分：它把「窗口怎么累积、什么时候停止累积」变成可比对的字节。

## 为什么这层能差分

上游 `detect_language_from_text` 是纯函数：文本进、语言码出，不碰网络、模型、磁盘。
换掉检测器之后两侧仍能跑同一串输入，所以能逐值比，而不是「看起来差不多」。

检测器用的是**同一个算法的两种实现**：

| 侧 | 实现 |
|---|---|
| 上游 | `lingua-py`（`lingua-language-detector>=2.0.2`） |
| 本实现 | `Panlingo.LanguageIdentification.Lingua`（lingua 的 Rust 实现 `lingua-rs` 的 .NET 封装） |

同一套语言模型、同一套置信度算法，只是宿主机语言不同。这是本项目第一次做「算法同源、
实现不同」的互证——如果两侧结果一致，说明我们的移植没有引入语义偏差；
如果不一致，说明 lingua 的两种绑定本身有分歧，那必须记进差异账而不是调参对齐。

**实测：15/15 逐值一致**，含置信度差边界与 256 码点窗口截断。

## 15 条覆盖什么

| 语料 | 守的规则 |
|---|---|
| `english-single` / `chinese-single` / `japanese-single` / `korean-single` | 四个语系各一条；CJK 三区间（汉字、假名、谚文）都要命中 |
| `norwegian-bokmal` | `nb` → `no` 归一化。漏了这步挪威语用户挑不到音色 |
| `too-short-latin` | 19 个字符必须放弃，20 个才认——长度门槛先于置信度 |
| `short-cjk-allowed` | 含 CJK 时门槛降到 4。理由不是「CJK 更好判」，而是候选集天然窄 |
| `ambiguous-short` | 门槛以下直接 null，不进置信度分支 |
| `chunk-accumulation` | 单片刻不出、累积到第三片才够；片间补空格 |
| `chunk-stops-after-detect` | 判出后窗口冻结（后两片与第一片相同）——否则一轮回复中途换音色 |
| `window-truncates` | 超 256 码点丢头保尾。结论「英文重复被判成 pt」是截断后的真实判定，**一致才是验收标准，合理不是** |
| `mixed-zh-en` | 混排不产生 null；置信度差仍要够 |
| `digits-only` | 纯数字必须 null，不挑默认语言 |
| `leading-empty-chunks` | 空片不污染窗口；`strip` 只吃首尾 |
| `emoji-heavy` | 纯 emoji 无语言特征 → null；同时覆盖探针的 emoji 序列化（原样输出） |

## 压不住的部分

- **原生库平台覆盖**：lingua 原生库只有 win-x64 / linux-x64 / osx-arm64。语料在其它平台上跑不了，
  这不是语料问题而是部署边界，记为 D006 的遗留部分。
- **「换了语言之后 TTS 真的换了发音吗」**：这层只证明判定结果一致。语言怎么落到 `TtsRequest.Language`
  由 `LanguageDetectionTests` 的解析器用例锁，实际发音要听。

## 录制环境

```
Python 3.13
lingua-language-detector 2.2.0     # 上游 pyproject 约束 >=2.0.2
```

`tools/refprobe/requirements.txt` 里有完整清单。判定阈值与归一化直接调上游
`pipeline/language_detection.py`，不在探针里重写——重写就等于拿自己的理解当基线。
