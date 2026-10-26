# 项目长期约定

## 定位

`huggingface/speech-to-speech` 的 C# 行为等价重写。参照物锁定 `0835002`，`net10.0`。
验收标准是**可观察行为一致**，不是编译通过。任何不一致必须进 `porting/differences.jsonl` 并编号。

## 依赖策略

**一律走 NuGet，不挂本地工程引用。** 即使参考仓库在本机（`C:\workshop\github\*`），也只取 API 形态与行为语义，落地用正式版包。理由：构建可复现，不受参考仓库工作区状态影响。

已用：`ManySpeech.SileroVad 1.1.2` / `ManySpeech.WhisperAsr 1.0.1` / `ManySpeech.AliParaformerAsr 1.1.9` / `TensorSharp.Chat 2026.10.11` / `ElBruno.QwenTTS 1.10.2` / `NAudio 3.3.0` / `NAudio.Alsa 3.3.0`。
**预发布包不进默认构建**（如 `NAudio.MacOS` 只有 preview）——宁可缺功能 + 明确报错，不赌破坏性变更。

## 核心语义纪律

- **错误是数据不是异常**：`TranscriptionFailure` / `EndOfResponse.Error`，不 throw
- **取消代际**：`CancelScope` 用 32 位回绕 `Interlocked.Exchange`；迟到完成不得清 discarding 保护
- **投机会话**：`ResponsePrefetchTransaction` 未认领即丢弃，永不写历史
- **内部事件名 vs 线上协议名分离**：映射只在一处
- 新增后端必须带 `UnsupportedReason`，**不静默降级**

## 音频层（易踩）

- 平台后端集中在 `src/SpeechToSpeech.Cli/Audio/AudioDevices.cs`，其它地方只认 `IWaveIn` / `IWavePlayer`
- **`WasapiRecorder` 未实现 `IWaveIn`**，只能留在 `WaveIn`；播放可用 `WasapiPlayer`
- `ClientWebSocket` 不允许并发 `Send`：麦克风回调只入队，重采样与发送交给单消费者
- WinMM 类型带 `[SupportedOSPlatform("windows")]`，新代码调用需 OS 守卫，否则 CA1416

## 构建纪律

- **不要并行跑 `dotnet build` 与 `dotnet test`**，会互删 `obj/*.AssemblyInfo.cs` 报 CS2001
- 目标是 0 警告 0 错误：`CA2007`/`CA1031` 的抑制是有意识的（与上游异常处理语义冲突），新增告警要逐条判断而非直接加 `NoWarn`

## 账本

`porting/` 四本账：`00-reference.md`（参照物）· `01-baseline.md`（行为基线）· `02-differences.md`（差异，由 jsonl 渲染，**不要手改**）· `03-acceptance.md`（验收）。
动工前先跑 `reference_lock.py check`，漂移了先决策再动手。

## 差分语料

- 目录 `corpus/<层>/`，`<case>.input.json` + `.expected.json`；**基线只能由参照实现 `record`，不能手写**
- 文本层探针两条：`tools/refprobe/text_probe.py`（上游）/ `tools/TextProbe`（本实现），输出同形 JSON
- 探针读 stdin 必须绕开 `Console.In`（Windows 按 OEM 代码页解码，UTF-8 中文会乱码），走原始流 + UTF-8
- 刻意分歧不放进必过语料：用 `payload.split` 这类开关收窄比对面，分歧本身改用**表征单元测试**锁定，并在 README 留上游原始输出作对照
- 参照实现录制环境：Python 3.13 + `nltk 3.10.4` + `punkt_tab`（数据取自 nltk_data 仓库，**4,319,076 字节**）+ `pillow` + `requests` + `openai 2.28.0`（与上游 pyproject 一致）+ `numpy` + `scipy`，venv 在 `~/.workbuddy/binaries/python/envs/default`

## 协议层

- **上游基线不用起服务**：`RealtimeService` 的 handler 是 `(conn_id, event) -> list[ServerEvent]` 纯函数，直接实例化即可调用，无需模型/网络
- 探针：`tools/refprobe/protocol_probe.py`（官方 `openai.types.realtime` 模型）/ `tools/ProtocolProbe`（本实现工厂）。两侧 `event_id` 归一化为 `"<event_id>"`，其余逐字节比
- 审计脚本：`schema_dump.py`（官方 46 种事件 + 必填字段）、`protocol_coverage.py`（事件级覆盖，当前 16/26）
- **`ServerEvent.Serialize` 必须 `Encoder = UnsafeRelaxedJsonEscaping`**：上游 Starlette `send_json` 用 `ensure_ascii=False`，默认编码器会让中文 3 字节变 6 字节、base64 的 `+` 变 `\u002B`（C012）
- 探针必须调**产品序列化入口**，不能自己 `ToJsonString()`，否则编码差异测不出来
- 结构有差异的事件**不进必过语料**（会长期标红），缺口走 `differences.jsonl` + 审计脚本

