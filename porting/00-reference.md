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
| 音频设备 I/O | `C:\workshop\github\NAudio`、`C:\workshop\github\PortAudioSharp2` | `54cb8188`（`VersionPrefix` 3.3.1，未发布） | NuGet `NAudio 3.3.0`、`NAudio.Alsa 3.3.0`、`PortAudioSharp2 1.0.6` |

五者均以 NuGet 包引用，不挂本地工程引用——构建可复现，也不受参考仓库工作区状态影响。

> NAudio 的特殊之处：本地仓库在 3.3.1 开发线上，NuGet 上最新正式版是 3.3.0，故取 3.3.0。
> 跨平台后端里 `NAudio.Alsa` 有 3.3.0 正式版，而 `NAudio.MacOS` 只有 `3.3.0-preview.34`，
> 官方 README 明写 "still in pre-release phase and breaking changes may be performed"，
> 因此 macOS 不走 NAudio，改以 **PortAudioSharp2 1.0.6**（csukuangfj/PortAudioSharp2，
> Apache-2.0，NuGet 正式版）补齐——上游 `sounddevice` 底层就是 PortAudio，同引擎对齐，
> 见差异账 E001。

### 同域参考（设计参考，不参与行为等价，也不是运行时来源）
| 项 | 值 |
|---|---|
| 仓库 | `C:\workshop\github\ElBruno.Realtime`（github.com/elbruno/ElBruno.Realtime，MIT） |
| 锁定版本 | `8662db1da92b0f53fdc312093bfdb4054218cfcd`（v0.11.0，2026-08-02） |
| 指纹文件 | `porting/reference-lock.elbruno-realtime.json`（322 文件，tree `7151763e…`；check 已通） |
| NuGet | `ElBruno.Realtime 0.11.0` 及其生态包（QwenTTS / KokoroTTS / VibeVoice 适配器、Blazor 组件） |
| 定位 | 与本工程**问题域同构**的 .NET 实时语音会话框架：Silero VAD → Whisper STT → MEAI `IChatClient` → 可插拔 TTS |

参考点（仅取 C# 工程形态与工程实践，**不取行为语义**——行为基线只有上游一处）：

- **MEAI 抽象形态**：`IVoiceActivityDetector` / `ITextToSpeechClient` / `IConversationSessionClient` 等接口切分方式，可对照本实现 `Core/Backends` 契约的粒度
- **SileroVad 并发纪律**：`SileroVadDetector` 用 `SemaphoreSlim _inferenceLock` 串行化 ONNX 推理会话（0.10.0 修的多调用方并发缺陷）——与本工程「ManySpeech OnlineVad 每段一条流」的语义互为印证，接入多消费者时必查
- **会话隔离**：`IConversationSessionStore`（0.10.0 防多用户历史泄漏）——对应本实现 `ChatSession` 按连接隔离的口径
- **延迟观测**：`LatencyMonitor` 组件的指标切分——对照本实现 `TurnLatency` / `response.metadata` 的账目项
- **DI 装配**：`DependencyInjection` 的 builder 扩展模式（`AddRealtimePipeline` 风格）——本实现保持手工装配，仅在需要对外开放嵌入能力时回头参考

纪律：当本仓库的形态与上游行为冲突时，**上游为准**；该仓库只回答「C# 生态惯用形状是什么」。

### WebRTC 同域参考（tryAGI.WebRTC）

| 项 | 值 |
|---|---|
| 仓库 | `C:\workshop\github\WebRTC`（github.com/tryAGI/WebRTC，MIT） |
| 锁定版本 | `e64acf345fc6b3d9703653d535aac77e72dcb335`（0.x 实验线，2026-10-27） |
| 指纹文件 | `porting/reference-lock.tryagi-webrtc.json`（311 文件，tree `98a5b0bb…`；check 已通） |
| NuGet | `tryAGI.WebRTC 0.2.10-dev.65`（仅预发布） |
| 定位 | .NET 10 纯托管 WebRTC 传输（自研于 RFC：ICE/DTLS-SRTP/SCTP 全自研，媒体编解码留给应用层），Pion 互测通过，文档示例即 `oai-events` 场景 |

**选型记录（2026-10-27，用户决策）**：D003 实现走 **SIPSorcery 稳定版**（NuGet 正式版，遵守「预发布包不进默认构建」纪律；Opus 编码走 SIPSorceryMedia.Encoders）。tryAGI.WebRTC 仅作**同域形态参照**：

- `SendOpusAsync(payload, rtpTimestamp, marker)` / `ReceiveAudioAsync` 的**边界划分**（库管 RTP 帧化+SRTP，应用管编码/时钟/pacing）——对照 SIPSorcery `AudioSink`/`AudioSource` 的取舍
- RFC 3550 静音抑制下的**时钟语义**（时钟原点保持、整帧抑制、下一帧截止期）——与上游 PipelineAudioTrack 的 pacing 相同问题域，两处参照互证
- 数据频道生命周期（`DataChannelsReady` 与 `MediaReady` 分离、borrowed owner）——对照上游 datachannel open/close 事件处理

### WebRTC 同域参考（SIPSorcery.OpenAI.Realtime）

| 项 | 值 |
|---|---|
| 包 | `SIPSorcery.OpenAI.Realtime`（NuGet，10.0.18-pre，2026-10-24；github.com/sipsorcery-org/sipsorcery 仓库 examples 旁路） |
| 许可 | BSD 3-Clause **+ BY-NC-SA 附加限制（非商业）**——**不可作依赖**，仅形态参照 |
| 定位 | SIPSorcery 官方的 OpenAI Realtime WebRTC **客户端**（REST 签名 + `WebRTCEndPoint` + `DataChannelMessenger`） |


### STV 同域参考（BasisVR/Basis——D031 口型引擎的移植来源）

| 项 | 值 |
|---|---|
| 仓库 | `github.com/BasisVR/Basis`（Apache-2.0；开源 VR 社交框架，Unity/C#） |
| 参考路径 | `Basis/Packages/com.basis.openlipsync/Runtime/Ported/`（MIT，纯 C# 推理核，无 Unity 依赖） |
| 锁定版本 | developer 分支 `acfca6b`（2026-10-27） |
| 模型 | `model.onnx.bytes`（10.7 MB，TCN 流式口型模型）+ `config.json`（mel 几何/流式 cache 形状/校准指标），随包发布；资产不入库，落 `models/viseme/` |
| 定位 | OVR LipSync 兼容的**音频→口型**推理：环形缓冲 → 48 tap Blackman-sinc 多相重采样 → Hann/HTK mel 前端 → 缓存式 TCN（ONNX Runtime，cache_* 乒乓 IoBinding，一 hop 一帧）→ 双极点平滑器（趋势外推消群延迟） |

参照点（**行为语义参与移植**——mel/FFT/重采样/平滑与参照逐值对拍，不同于仅取工程形态的同域参考）：
音频分块语义、mel 滑动窗因果对齐、multi-label sigmoid 激活、`StateUnchanged` 静默跳帧。
口型集合差异（OVR 15 → MS 21）与产出方式差异登记 C033，wire 契约保持上游 MS21 区间形状（D009）。

参照点（SIPSorcery）：SIPSorcery 10.x 的 PeerConnection 组装与 Opus 收发的**官方用法**；**Opus 编解码用 Concentus 2.2.2（纯托管）**——本实现沿用此选型替代 SIPSorceryMedia.Encoders（原生 libopus，容器需多平台原生资产）；`DataChannelMessenger` 的事件序列化分层。另注意它是 OpenAI **客户端**（浏览器 ← ASP.NET 桥 → OpenAI 的形态在 BrowserBridge 示例），本实现是对端**服务端**，方向相反。

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
