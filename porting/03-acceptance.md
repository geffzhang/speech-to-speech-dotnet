# 验收账（第三本账）

验收是体检报告，不是「我感觉挺好」。

审计日期：2026-10-23 · 参照物 `0835002`

---

## 构建

- 结果：**`0 警告 / 0 错误`**（`dotnet build SpeechToSpeech.slnx --no-incremental`）
- 目标框架：`net10.0`（`Directory.Build.props` 统一设定）
- Nullable：`enable`（全解决方案）
- 全局 `NoWarn`：`CS1591`（缺 XML 注释）、`CA1848`（LoggerMessage 委托）、`CA2007`（ConfigureAwait）、`CA1031`（宽泛 catch）—— 前两项是文档/性能建议，后两项与上游「异常只记录不终止线程」的语义直接冲突，抑制是有意识的决定，不是疏漏
- 附带验证：
  - `dotnet run --project tools/ApiProbe` —— 反射导出依赖包公开表面（132 个类型，5 个程序集）
  - `dotnet run --project tools/ApiProbe -- --self` —— 反射导出本实现公开表面（97 个类型，6 个程序集）
  - `speech-to-speech list-backends` —— 交互跑通，三族计数 15 / 5 / 12
  - 差分语料 `corpus/text` —— **15/15 通过**（基线由上游参照实现录制，非手写）
  - 差分语料 `corpus/protocol` —— **32/32 通过**（基线由官方 `openai.types.realtime` 模型构造，与上游 emit 同类）
  - 协议覆盖审计 `tools/refprobe/protocol_coverage.py` —— 上游 26 种事件，本实现可构造 **25 种（96%）**，唯一缺口是扩展事件 visemes（D009）
  - 官方 schema `tools/refprobe/schema_dump.py` —— 导出 `openai 2.28.0` 的 46 种服务端事件及其必填字段
  - `speech-to-speech talk` —— 音频后端创建已验（Windows：`WasapiPlayerBuilder` 共享模式 + `WaveIn`），失败点在 `ConnectAsync` 而非设备初始化，说明 NAudio 调用链正常

## 测试

| 项目 | 总数 | 通过 | 失败 | 跳过 |
|---|---|---|---|---|
| `SpeechToSpeech.Tests` | 83 | 83 | 0 | 0 |
| `corpus/text`（差分语料） | 15 | 15 | 0 | 0 |
| `corpus/protocol`（差分语料） | 32 | 32 | 0 | 0 |

单元测试耗时约 25 ms；差分语料需先构建 `tools/TextProbe` / `tools/ProtocolProbe`，比对在百毫秒级。两类都不依赖模型文件、网络与音频设备——所以能进 CI。

跳过项说明：**无跳过项**。但有 3 类「未覆盖」不等于「跳过」，单列在此以免混淆：

| 测试 | 跳过原因 | 风险 |
|---|---|---|
| `local` 全链路 | 未写成测试，只写成 CLI 子命令。缺 Silero/Whisper/Qwen3-TTS 模型 | 高——队列时序首次真跑可能暴露问题 |
| `talk` 端到端 | 同上 + 需麦克风/扬声器 | 中——仅 Windows |
| 上游 2,082 例中的后端专属部分 | 不移植（见 `01-baseline.md` §1.2） | 低——统一契约已锁 |

## 公开 API 审计

- 参照实现导出数：`src/` 对外暴露的是 CLI 与模块级函数，无稳定 API 契约（Python 项目常态）。故本项不按「函数逐个对齐」验收，按**语义接缝对齐**验收。
- 本实现公开表面：**97 个类型 / 6 个程序集**

| 程序集 | 类型数 | 角色 |
|---|---|---|
| `SpeechToSpeech.Core` | 78 | 消息契约、Handler 基类、取消代际、运行时配置、后端接口、文本与音频工具 |
| `SpeechToSpeech.Realtime` | 6 | 协议事件工厂、连接循环、Kestrel 宿主 |
| `SpeechToSpeech.Backends.ManySpeech` | 5 | VAD / STT 适配 |
| `SpeechToSpeech.Backends.OpenAiCompatible` | 4 | 远程 STT / LLM / TTS |
| `SpeechToSpeech.Backends.TensorSharp` | 2 | 本地 LLM |
| `SpeechToSpeech.Backends.QwenTts` | 2 | 本地 TTS |
| （CLI） | — | 不作公开表面，仅入口 |

- 签名差异 / 运行时差异 / 平台限制：见 `02-differences.md`（32 条，编号 C001–E003、C010–C015、D011–D012）

## 协议锁定

- [x] 关键协议字段名与顺序由测试锁定 —— `ProtocolShapeTests` + `AssistantItemLifecycleTests` 共 19 例覆盖 24 类服务端事件、`event_id` 必带、错误嵌套结构、`response.done` 三终态、转写完成的必填 `usage`、助手消息项完整生命周期；另有 `corpus/protocol` **32 条**差分语料与上游逐字段比对（含编码），锁定的是「与参照实现一致」而非仅「自证一致」
- [x] 服务端事件覆盖 25/26（96%）—— `python tools/refprobe/protocol_coverage.py` 可复跑。唯一缺口是扩展事件 `speech_to_speech.output_audio.visemes`（D009）。原先缺的 9 类（含官方客户端渲染入口 `output_item.added` / `content_part.added`）已补齐并进语料
- [x] 跨语言数字范围已校验（±2^53−1）—— `ServerEvent` 全部数值参数据查为 `int`（`audio_start_ms` / `audio_end_ms` / `output_index`），**无 `long`/`double` 上线**；延迟统计用秒级 `double`，只留在 `TurnLatencyStore` 内部，不出线。故不存在精度越界
- [x] 编码与行尾归一化策略已确定 —— 出线 UTF-8 文本帧 + 紧凑 JSON（`WriteIndented = false`，`separators` 效果等价于上游的 `,`/`:` 无空格），**不含换行**；`Encoder = UnsafeRelaxedJsonEscaping` 保证中文与 base64 的 `+` `/` 不被转义成 `\uXXXX`，与上游 Starlette `send_json`（`ensure_ascii=False`）一致（C012，已验证）。入线同解码，`input_audio_buffer.append` 走 base64 二进制而非文本

## 端到端

- [ ] **容器内** —— 未做（E002）：无 Dockerfile、无发布包
- [ ] **跨平台**（Windows / Linux / macOS）—— 部分：`serve` 纯托管代码，理论跨平台；`talk`/`local` 的音频后端已在 Windows（WASAPI + WinMM）与 Linux（ALSA）实现，macOS 缺（E001）。Windows 已实跑设备创建；**Linux 与 macOS 未在真实设备上跑过**
- [x] **跨传输** —— 仅 WebSocket。WebRTC 未移植（D003），明确记为缺口而非「支持」

## 未闭合风险

| 风险 | 影响 | 处置 |
|---|---|---|
| response 载荷精简（D012） | 客户端读不到 token 用量、`output_modalities`、`audio.voice`、`conversation_id`，按 `response.usage` 计费或按 `response.output` 回放历史的下游会读到空值 | 引入等价的状态聚合后补齐，再把字段进语料 |
| 音频层差分语料为空 | 模型推理受浮点累加顺序影响，不可逐字节比对 | 改锁采样率/声道/位深/段数/时长误差带，不比样本值 |
| `local` / `talk` 未实跑 | 首次真跑可能暴露队列时序与背压问题 | 备齐模型后单机跑一轮，录事件时序对比上游日志 |
| 投机轮次 HOLD/grace 未移植（C006） | 打断与续说场景行为不等价——这是上游 68 例测试覆盖最重的区域 | 定位为「本期不含」；若下游依赖打断体验，须优先补 |
| 实时转写未移植（D005） | `openai-realtime` / `vllm-realtime` 两个 STT 后端不可用 | `BackendUnavailableException` 显式报错，不静默降级 |
| 音频后端跨平台不全（E001） | macOS 无 `talk`/`local`；Linux ALSA 路径未实跑 | Windows 已实跑设备创建；Linux 需在装有 `libasound.so.2` 的机器上验证；macOS 等 `NAudio.MacOS` 出正式版 |
| 语言检测未移植（D006） | 助手输出语言与 STT 语言不一致时，TTS 可能选错音色 | 已由 `TtsInput` 的 `selected_language` 兜底；自动检测缺失 |
| 无容器镜像（E002） | 无法在服务端标准部署 | 服务端可 `dotnet run`，但缺可复现交付物 |
| 模型惰性加载（E003） | 缺模型时首次合成才失败，而非启动即失败 | 可加 `--check-models` 预检子命令 |

---

## 守护命令（进 CI 前必跑）

```bash
# 1. 参照物漂移检查——漂移了先决策再动手
python C:/Users/geffzhang/.workbuddy/skills/cross-lang-porting/scripts/reference_lock.py \
  check --repo C:/workshop/github/speech-to-speech --dest porting/reference-lock.json

# 2. 差异登记账校验——每条必须有编号、原因、决策
python C:/Users/geffzhang/.workbuddy/skills/cross-lang-porting/scripts/diff_register.py lint
python C:/Users/geffzhang/.workbuddy/skills/cross-lang-porting/scripts/diff_register.py render

# 3. 构建与测试
dotnet build SpeechToSpeech.slnx --no-incremental
dotnet test SpeechToSpeech.slnx

# 4. 差分语料（文本层 / 协议层）
python <skill>/scripts/diff_corpus.py verify --corpus corpus/text --json-canonical \
  --cmd "dotnet tools/TextProbe/bin/Debug/net10.0/TextProbe.dll"

python <skill>/scripts/diff_corpus.py verify --corpus corpus/protocol --json-canonical \
  --cmd "dotnet tools/ProtocolProbe/bin/Debug/net10.0/ProtocolProbe.dll"

# 5. 协议覆盖审计（与上游 26 种事件比对，当前 25/26）
python tools/refprobe/protocol_coverage.py
```

重录基线只在参照实现或上游文本层变动时做，且**先逐条看差异再决策**，不要一把 `--update` 全过。
录制环境（nltk / punkt_tab 版本）见 `corpus/text/README.md`。

**结论**：构建、单元、文本层与协议形状层验收通过；端到端未闭合。本实现可作为「四段管线 + Realtime 协议」的可运行基线，但**不能声称与上游行为完全等价**：打断状态机未移植（C006）、真跑链路未验证，两项是主要空白。

协议层本轮已从「客户端可能看不到回复」补到 25/26 覆盖：消息项生命周期（`output_item.added` →
`content_part.added` → delta → `content_part.done` → `output_item.done`）、提交确认、会话项删除、
转写失败的 item 作用域通告都已到位，并有 31 条差分语料与 18 条形状断言双重锁定。
终态也已收口：取消会回 `response.done`（status=cancelled），`status_details.type` 与 status 同域
（原先恒写 `incomplete`，取消时会产出官方 schema 里不存在的组合，见 C015）；
`transcription.completed` 的必填 `usage` 已补齐（D011，原先严格客户端会整条拒收）。
剩下的 `response` 载荷深度（D012）不影响客户端能否渲染，只影响计费与历史回放读到什么。

下一步按影响面排序：C006（打断/续说的 HOLD/grace 状态机）已成为唯一的行为级空白。
