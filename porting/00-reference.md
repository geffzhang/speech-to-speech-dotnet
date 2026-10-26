# 参照物锁定

- 项目：**speech-to-speech-dotnet**
- 方向：`Python` → `C#`
- 建立日期：2026-10-23

## 参照实现

| 项 | 值 |
|---|---|
| 仓库 | `C:\workshop\github\speech-to-speech`（huggingface/speech-to-speech） |
| 锁定版本 | `0835002ad1c73cf1cc408ac10ebd2a19f777ae12`（main，2026-10-22） |
| 发布版本 | pyproject `version = "1.1.0"` |
| 锁定方式 | `reference_lock.py lock --repo C:/workshop/github/speech-to-speech` |
| 指纹文件 | `porting/reference-lock.json`（385 文件，tree `57483db8…`） |

### 参考实现（仅作运行时来源，不参与行为等价）

| 用途 | 仓库 | 锁定版本 | 落地方式 |
|---|---|---|---|
| VAD / STT | `C:\workshop\github\ManySpeech` | — | NuGet `ManySpeech.SileroVad 1.1.2`、`ManySpeech.WhisperAsr 1.0.1`、`ManySpeech.AliParaformerAsr 1.1.9` |
| LLM | `C:\workshop\github\TensorSharp` | — | NuGet `TensorSharp.Chat 2026.10.11` |
| TTS | `C:\workshop\github\ElBruno.QwenTTS` | — | NuGet `ElBruno.QwenTTS 1.10.2` |
| 音频设备 I/O | `C:\workshop\github\NAudio` | `54cb8188`（`VersionPrefix` 3.3.1，未发布） | NuGet `NAudio 3.3.0`、`NAudio.Alsa 3.3.0` |

四者均以 NuGet 包引用，不挂本地工程引用——构建可复现，也不受参考仓库工作区状态影响。

> NAudio 的特殊之处：本地仓库在 3.3.1 开发线上，NuGet 上最新正式版是 3.3.0，故取 3.3.0。
> 跨平台后端里 `NAudio.Alsa` 有 3.3.0 正式版，而 `NAudio.MacOS` 只有 `3.3.0-preview.34`，
> 官方 README 明写 "still in pre-release phase and breaking code changes may be performed"，
> 因此 macOS 未接入默认构建，见差异账 E-01。

## 同步策略

- [x] 长期跟随上游，每次动工前核对漂移
- 目标节奏：上游 `main` 每有新 release tag 时跟进一次

## 纪律

> **每次动工前先跑 `reference_lock.py check`。漂移了先决策，再动手。**

漂移后必须完成三件事：
1. 明确本次跟进到哪个 commit
2. 列出受影响的语义范围
3. 逐项回审受影响的实现，统一混用之处

```bash
python C:/Users/geffzhang/.workbuddy/skills/cross-lang-porting/scripts/reference_lock.py \
  check --repo C:/workshop/github/speech-to-speech --dest porting/reference-lock.json
```

## 漂移记录

| 日期 | 从 | 到 | 影响范围 | 处置 |
|---|---|---|---|---|
| 2026-10-23 | — | `0835002` | 首次锁定 | 无 |

## 移植形态

采用**行为等价重写**：不逐行翻译，以「可观察行为一致」为验收标准。
所有不一致项进入 `differences.jsonl`，每条有编号、原因、决策与对应测试。

上游规模实测：

| 维度 | 数量 |
|---|---|
| `src/` Python 文件 | ~130 |
| `src/` 代码行数 | 20,075 |
| `tests/` Python 文件 | 92 |
| 后端数（STT / LLM / TTS） | 15 / 4 / 12 |
| 协议传输 | WebSocket + WebRTC |
