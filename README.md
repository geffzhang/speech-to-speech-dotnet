# speech-to-speech-dotnet

**[`huggingface/speech-to-speech`](https://github.com/huggingface/speech-to-speech) 的 C# 行为等价重写。**

一个完全跑在本地的低延迟语音智能体：一条命令就开始对话——没有 API key，没有云服务。
默认管线是 **Silero VAD → Whisper → TensorSharp(本地 GGUF LLM) → Qwen3-TTS**，
每一段都可替换。服务端讲 **OpenAI Realtime 协议**（WebSocket 与 WebRTC 双传输），
所以为 OpenAI Realtime API 写的应用可以把地址指向你自己的机器。

> **本仓库不是上游的官方移植。** 它是一份独立的 C# 重写，验收标准是**可观察行为与上游一致**，
> 而不是源码相似。参照物锁定在 `0835002`（上游 `main`，2026-10-22，`pyproject` 版本 `1.1.0`）。
> 每一处不一致都登记在 `porting/differences.jsonl`（75 条：已解决 58 / 已接受 17 / 缺口 0），
> 并由差分语料与单元测试双向锁住。

---

## 目录

- [快速开始](#快速开始)
- [它是怎么工作的](#它是怎么工作的)
- [命令](#命令)
- [支持的组件](#支持的组件)
- [Realtime API](#realtime-api)
- [浏览器 demo](#浏览器-demo)
- [移植方法学](#移植方法学)
- [已知差异](#已知差异)
- [构建与测试](#构建与测试)
- [仓库结构](#仓库结构)
- [许可与致谢](#许可与致谢)

---

## 快速开始

### 环境要求

| 项 | 要求 |
|---|---|
| .NET SDK | **10.0**（`net10.0`，Nullable 启用） |
| 音频后端 | Windows：WASAPI + WinMM；Linux：ALSA；macOS：PortAudio |
| 模型权重 | 用下载脚本一键准备，约 **6.61 GB**，见下节 |

Linux 上需要 `libasound2`（NAudio.Alsa 的原生依赖）：

```bash
sudo apt-get install libasound2
```

### 构建

```bash
git clone https://github.com/geffzhang/speech-to-speech-dotnet.git
cd speech-to-speech-dotnet
dotnet build SpeechToSpeech.slnx
```

### 准备模型

在仓库根目录执行一条命令，下载默认本地管线的**全部五组模型**（含可选 Smart Turn）：

```powershell
# Windows PowerShell 5.1+ / PowerShell 7
.\download-models.ps1
```

```bash
# Linux / macOS / Git Bash
bash ./download-models.sh
```

脚本需要 **curl**（Windows 使用 `curl.exe`，避免 PowerShell 的同名别名）；Bash 另需
`sha256sum` 或 macOS 自带的 `shasum`。不需要 Python、Hugging Face CLI 或 API key。
如果 Windows 的执行策略阻止脚本，可以使用
`powershell -NoProfile -ExecutionPolicy Bypass -File .\download-models.ps1`（仅本次进程生效）。

默认下载到**脚本所在仓库**的 `models/`，不依赖当前工作目录；权重不入库。
完整 URL、固定提交版本、字节数与 SHA-256 在
[`tools/models/models.tsv`](tools/models/models.tsv)，不在运行时解析 `main` / `master`：

| 段 | 参数 | 下载目标（相对 `models/`） | 来源 |
|---|---|---|---|
| VAD | `--vad_model_dir` | `vad/silero_vad.onnx`（v5） | [Silero v5 ONNX](https://huggingface.co/runanywhere/silero-vad-v5) |
| STT | `--whisper_model_dir` | `whisper/`（多语种 base INT8 encoder + decoder + `conf.json`） | [ManySpeech Whisper](https://modelscope.cn/models/manyeyes/whisper-base-onnx) |
| LLM | `--llm_model_path` | `llm/Qwen3.5-0.8B-Q4_K_M.gguf` | [Unsloth GGUF](https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF) |
| TTS | `--qwen3_model_dir` | `tts/`（0.6B CustomVoice ONNX + 外部数据 + embeddings + tokenizer） | [ElBruno Qwen3-TTS](https://huggingface.co/elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX) |
| 端点判定 | `--smart_turn_model_path` | `smart-turn/smart-turn-v3.2-cpu.onnx` | [Pipecat Smart Turn](https://huggingface.co/pipecat-ai/smart-turn-v3) |

- 共 **39 个文件，6,612,701,771 字节**；预留至少 7 GB 可用空间，替换旧模型时还需临时文件空间。
- 已有文件每次都校验大小和 SHA-256，正确的直接跳过；损坏的重新下载，旧文件在新文件校验成功前保留。
- 下载写入同目录的 `.part` 文件；断网或 Ctrl+C 后**重跑原命令**即可续传。服务端不支持 Range 时明确告警并从头下载。
- 校验成功才改为正式文件名；校验失败返回非零退出码并删除坏的 `.part`，下次重新下载；网络失败则保留 `.part`。
- 清单中大文件的 SHA-256 来自发布方 LFS / ModelScope 元数据，小文件按固定版本内容计算。
  下载不代表接受额外授权，使用前仍应核对各模型原始许可。
- 这些脚本只准备默认五组资产，不包含 Paraformer、说话人分离、声纹或口型等可选后端；
  不要同时对同一个目录运行多个下载进程。

自定义模型根目录（起服务时相应修改 `S2S`）：

```powershell
.\download-models.ps1 -ModelsDir D:\s2s-models
```

```bash
bash ./download-models.sh --models-dir /path/to/models
```

维护或选择其他模型时，两个入口还分别支持 `-Manifest <清单路径>` / `--manifest <清单路径>`；
TSV 每行依次为 SHA-256、正数字节数、相对目标路径、HTTP(S) URL（tab 分隔，`#` 行为注释）。
应使用可信清单并锁定源版本，必须同时更新校验值与大小，不能用普通 Hugging Face Whisper 导出替代
ManySpeech 所需格式；TTS 的 `.onnx.data` 等配套文件也不能遗漏。

### 起服务

```bash
S2S="$PWD/models" # 自定义下载目录时改为对应路径
dotnet run --project src/SpeechToSpeech.Cli -- serve \
  --port 8765 \
  --vad_model_dir "$S2S/vad" \
  --whisper_model_dir "$S2S/whisper" \
  --qwen3_model_dir "$S2S/tts" \
  --smart_turn_model_path "$S2S/smart-turn/smart-turn-v3.2-cpu.onnx" \
  --llm_model_path "$S2S/llm/Qwen3.5-0.8B-Q4_K_M.gguf"
```

看到这行就算起来了：

```
listening on ws://127.0.0.1:8765/v1/realtime
```

模型是**懒加载**的——首个转写/生成才触发加载，所以第一轮延迟偏高（数秒到数十秒），热态后正常。

### 说话

打包的音频客户端（麦克风 + 扬声器）：

```bash
# 连一个已经在跑的 serve
dotnet run --project src/SpeechToSpeech.Cli -- talk --url ws://127.0.0.1:8765/v1/realtime

# 或者：服务端 + 客户端在同一个进程里（一条命令开聊）
dotnet run --project src/SpeechToSpeech.Cli -- local
```

> 用耳机，否则助手会听见自己。`talk` / `local` 支持 `--block-mic-during-playback` 在播放期间闭嘴。

> **真跑必读**：`talk` 必须对着**带全模型路径**起的 `serve` 用。缺 VAD 模型目录时，服务端在
> **连接构建期**就拒绝连接——客户端侧表现为「连上了但收不到任何事件」，看起来像客户端 bug，
> 其实不是（差异账 E003）。

---

## 它是怎么工作的

管线是四段级联，每段在自己的线程上跑，用队列连接：

1. **语音活动检测（VAD）** — Silero VAD v5 判定说话边界与轮次起止。
2. **语音转文本（STT）** — 转写用户这一轮，可选实时逐字转写。
3. **语言模型（LLM）** — 生成回复，流式文本与工具调用。
4. **文本转语音（TTS）** — 合成音频并流式回给客户端。

三段可选扩展：

- **Smart Turn 端点判定** — 用本轮的内容与韵律校验 Silero 的收口判断，决定投机宽限与处理延迟。
- **流式说话人分离** — 在 VAD 之前分轨，给转写补上 `[speaker_N]` 标签。
- **口型提取（STV）** — TTS 之后给出带时间戳的口型区间，供 avatar / robot 客户端渲染。

每一段都有多个可替换后端，用 CLI 开关选；**不可用的后端会带着原因明确报错，不静默降级**。

---

## 命令

| 命令 | 行为 | 什么时候用 |
|---|---|---|
| `serve` | 跑管线服务端，OpenAI Realtime 协议 over WebSocket + WebRTC。 | 你要基于这个 API 做应用或设备。 |
| `talk` | 跑打包的麦克风/扬声器客户端，连一个已有的 Realtime 服务端。 | 你要跟一个已经在跑的服务端说话。 |
| `local` | 进程内同时跑 `serve` 与 `talk`（环回）。 | 你想一条命令就开聊。 |
| `list-backends` | 列出全部后端名字与可用状态。 | 想知道这个构建能跑什么、不能跑什么、为什么。 |

`serve` 默认绑 `127.0.0.1`；要暴露到网络请显式 `--host 0.0.0.0`。
`local` 永远绑回环。

```bash
# 看后端清单（32 个名字：11 可用 / 21 不可用，每个不可用都带原因）
dotnet run --project src/SpeechToSpeech.Cli -- list-backends
```

---

## 支持的组件

### 可用

| 段 | 后端 | 说明 |
|---|---|---|
| STT | `whisper`（默认） | ManySpeech 的 Whisper ONNX，离线整段识别 |
| STT | `paraformer` | ManySpeech 的 Ali Paraformer |
| STT | `sense-voice` | ManySpeech |
| STT | `openai` | OpenAI 兼容 `/v1/audio/transcriptions` |
| STT | `openai-realtime` | 流式：说话过程中逐段发 `transcription.delta` |
| STT | `vllm-realtime` | 流式，16 kHz 直通 |
| LLM | `tensorsharp`（默认） | 本地 GGUF（`.gguf`） |
| LLM | `transformers` | 本地 HF 权重 |
| LLM | `chat-completions` | OpenAI 兼容 `/v1/chat/completions`（llama.cpp / vLLM / LM Studio） |
| TTS | `qwen3`（默认） | Qwen3-TTS（ElBruno.QwenTTS ONNX） |
| TTS | `openai` | OpenAI 兼容 `/v1/audio/speech` |

### 不可用（名字保留，报错时带原因）

上游有 31 个后端名字，本实现的注册表把它们全部保留、另加 C# 专属的 `tensorsharp`（本地 GGUF），
共 32 个。其中 11 个可用，其余 21 个保留名字但**不可用**——选它们会得到一条
明确的 `BackendUnavailableException` 与原因（差异账 D010），不会静默换一条路。典型的几类：

- **Apple Silicon 专属**：MLX 系（`mlx-lm` / `whisper-mlx` / `mlx-audio-whisper`）——C# 侧无对应运行时。
- **NVIDIA 工具链专属**：NeMo 系（`parakeet-tdt` / `parakeet-unified` / `nemotron-streaming` / `orukeet`）、
  `qwen3-asr`、CTranslate2 的 `faster-whisper`。
- **G2P / 独立运行时缺失**：`kokoro`（需 Misaki）、`chatTTS`、`kitten`、`pocket`、`phonon`、
  `omnivoice`、`facebookMMS`、`supertonic`。
- **需要外部服务端**：`drama3`（SGLang Omni）、`minimax`（hosted API）。
- **协议面未接**：`responses-api`（OpenAI Responses API 的流式事件）、`none`（直连音频输入）。

**转写后端怎么选**——分三类，差别在**是否流式**：

| 类别 | 后端 | 采样率 | 行为 |
|---|---|---|---|
| 离线整段 | `whisper`（默认）/ `paraformer` / `sense-voice` | 16 kHz | 每个 VAD 段**结束**才发 `transcription.completed`，**没有** `transcription.delta` |
| 离线整段（远程） | `openai` | 16 kHz | 同前，走 `/audio/transcriptions` |
| 流式实时 | `openai-realtime` / `vllm-realtime` | 24 kHz / 16 kHz | 说话过程中**逐段**发 `transcription.delta` |

要「边说边出字」的实时感、或对中文/方言质量要求更高 → 流式；要完全本地、离线、无外部端点 →
默认 `whisper`。

> `transcription.delta` **不需要客户端订阅**，流式后端在说话过程中就推。
> `speech_to_speech.input_audio_transcription.snapshot` 是另一条通道，需要在 `session.update`
> 的 `extensions` 里订阅（缺省关）。两者别混淆：delta 是边说边出的增量，snapshot 是已提交片段的一句话快照。

---

## Realtime API

协议与 OpenAI Realtime 兼容，但**跑在你自己的进程里**——模型、语料、延迟都归你。
**无鉴权**：只应监听本地回环或受控内网。

| 端点 | 方法 | 说明 |
|---|---|---|
| `/healthz` | GET | 存活探针，返回 `ok` |
| `/v1/realtime` | **WebSocket 升级** | 实时对话主通道（事件流） |
| `/v1/realtime/calls` | POST | WebRTC 信令：提交 offer，返回 answer + `Location` |
| `/v1/realtime/calls/{callId}` | DELETE | 挂断 WebRTC 通话，释放槽位 |

### WebSocket 接入

```csharp
var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri("ws://127.0.0.1:8765/v1/realtime"), CancellationToken.None);
// 服务端立刻推 session.created（含 session.id，形态 session_<32hex>）与 session.updated
// 上行：{ "type": "input_audio_buffer.append", "audio": "<base64 PCM16>" }
//       { "type": "input_audio_buffer.commit" }
// PCM 16-bit 单声道，默认 16000 Hz
```

客户端→服务端事件：`session.update`、`input_audio_buffer.append/commit/clear`、
`conversation.item.create/delete/truncate`、`response.create`、`response.cancel`、
`output_audio_buffer.clear`（仅 WebRTC）、`function_call_output`（工具结果）。

服务端→客户端事件：会话（`session.created` / `session.updated`）、VAD
（`input_audio_buffer.speech_started` / `speech_stopped` / `committed`）、用户转写
（`conversation.item.input_audio_transcription.completed`）、生成（`response.created` +
`output_item.added/done` + `response.content_part.added/done`）、音频
（`response.output_audio.delta` / `output_audio.done`）、文本（transcript / output_text 的 delta/done）、
收尾（`response.done`，含 `status_details` 终态）、错误（`error` / `type_error`）。

**服务端事件覆盖 26/26 = 100%**（上游 26 种事件全部可构造，含扩展事件
`speech_to_speech.output_audio.visemes`）。审计脚本：`python tools/refprobe/protocol_coverage.py`。

JSON 编码口径与上游一致：UTF-8 **不转义**、base64 的 `+` `/` 不转义、BMP 外字符不转义——
转义集合只含引号 / 反斜杠 / 控制字符。

### WebRTC 接入

浏览器前端用 `RTCPeerConnection` 创建 offer（Opus 48 kHz，`sendrecv`），`POST /v1/realtime/calls`
（`Content-Type: application/sdp`），服务端回 **201** + answer SDP + `Location`，
音频走媒体轨、控制事件走 datachannel `oai-events`。

```js
const pc = new RTCPeerConnection({ iceServers: [] });
pc.addTransceiver("audio", { direction: "sendrecv" });
const dc = pc.createDataChannel("oai-events");
const offer = await pc.createOffer();
await pc.setLocalDescription(offer);
const resp = await fetch("http://127.0.0.1:8765/v1/realtime/calls", {
  method: "POST", headers: { "Content-Type": "application/sdp" }, body: offer.sdp,
});
await pc.setRemoteDescription({ type: "answer", sdp: await resp.text() });
```

- 槽位数 = `num_pipelines`（默认 1），打满时 `POST /calls` 返回 **503**。
- 非 `application/sdp` 内容类型返回 **415**；`DELETE` 未知 callId 返回 **404**。
- 本项目**没有** OpenAI 的 `POST /sessions`（临时令牌）。前端直接发 offer 即可。
- ICE 服务器走环境变量 `SPEECH_TO_SPEECH_ICE_SERVERS`。

完整接入文档见 **[`docs/api-guide.md`](docs/api-guide.md)**。

---

## 浏览器 demo

`demo/` 是上游自带的**浏览器语音对话 UI**（走 OpenAI Realtime GA 协议，WebSocket / WebRTC 双传输，
前端用 `@openai/agents` 适配器 + 浏览器端 `@ricky0123/vad-web`）。

它默认以 `ws://127.0.0.1:8765/v1/realtime` 为后端——正是本项目 `serve` 的端点。设
`SPEECH_TO_SPEECH_URL` 后，demo 的服务端会**禁用**加载均衡 / 排队 / 计量 / 登录等 Space 专属逻辑，
浏览器直连我们的 `serve`，免鉴权。

```bash
npm ci --prefix demo
pip install -r demo/requirements.txt
export SPEECH_TO_SPEECH_URL=ws://localhost:8765/v1/realtime
uvicorn --app-dir demo server:app --port 7860
```

### ASP.NET Core 10 替代服务端

同一套前端也可以由本仓库的 ASP.NET Core 10 服务端托管（`src/SpeechToSpeech.Demo.Web`）：

```bash
dotnet run --project src/SpeechToSpeech.Demo.Web --urls http://127.0.0.1:7860
```

它复刻上游 `demo/server.py` 的**直连/本地模式**：静态前端 + `/api/config` + `/api/me` +
`/api/calls` + `/api/search` + `/vendor/*`，裁掉 Space 专属的 auth / limiter / session / queue。
行为对齐由**对拍**保证——`tools/smoke/demo_server_parity.py` 起 Python uvicorn 与 ASP.NET 双侧，
同批 45 条 key 端点请求比对 status / content-type / body，**零差异**。

其中 `PythonJson.cs` 是刻意写的**行为等价层**：按 CPython `json` 模块语义解析与序列化
（NaN/Infinity、int/float 区分、大整数、浮点 repr `1.0` / `1e+16`、错误 msg/pos），
因为 pydantic v2 的 422 校验形状要把这些原样回显。

---

## 移植方法学

行为等价不是「读懂了就重写」，是**建基线、对拍、记账**。四本账在 `porting/`：

| 账 | 文件 | 作用 |
|---|---|---|
| 参照物锁定 | `00-reference.md` | 钉死上游版本与指纹文件；同域参考只取工程形态 |
| 行为基线 | `01-baseline.md` | 把上游行为变成可执行证据（移植的测试 + 差分语料） |
| 差异登记 | `02-differences.md` | 机器可读源 `differences.jsonl`；此文件由 `render` 生成，不手改 |
| 验收 | `03-acceptance.md` | 构建 / 测试 / 语料 / 真跑 / 公开面审计的体检报告 |

### 差分语料

语料只能由**参照实现**生成，不能手写。`corpus/` 下六类，每类都由一对探针驱动
（Python 参照探针 `tools/refprobe/` ↔ 本实现探针 `tools/`）：

| 目录 | 守护的行为 | 条数 |
|---|---|---|
| `text/` | 文本规范化与分句 | 15 |
| `protocol/` | 线路协议事件的形状与编码 | 43 |
| `turns/` | 投机轮次状态迁移（虚拟时钟逐值比对） | 13 |
| `lang/` | 助手输出语言识别（lingua-py ↔ lingua-rs 互证） | 15 |
| `smart-turn/` | 端点判定到宽限/处理延迟的映射 | 13 |
| `latency/` | 响应时延账的状态迁移与 metadata 字符串 | 16 |

```bash
python ~/.workbuddy/skills/cross-lang-porting/scripts/diff_corpus.py verify --corpus corpus/protocol --json-canonical \
  --cmd "dotnet tools/ProtocolProbe/bin/Debug/net10.0/ProtocolProbe.dll"
```

`--json-canonical` 是必须的：探针输出紧凑、基线带空格，不归一化会整片误报。

> 上面命令里的 `~/.workbuddy/skills/...` 是 **cross-lang-porting skill** 的脚本路径，Windows 下用完整路径（`C://Users//<你>//.workbuddy//skills//cross-lang-porting//scripts//diff_corpus.py`）。

### 账本纪律

- **状态翻 resolved 必须同步改四本账的正文**，不只改账本那一行。两个方向都危险：
  正向让人低估实现（以为不支持，实际能用），反向让人高估实现（以为已解决，实际还是缺口）。
  一致性由 `python tools/ledger/check_doc_sync.py --strict` 守着。
- 差异编号一律写账本形态 `D001` / `C009` / `E001`。
- **「accepted 的理由」也要核**：理由常建立在「这不影响可观察行为」的假设上，
  而假设的依据（哪个字段真出线、语料到底覆盖没覆盖）本身可能是错的。

---

## 已知差异

75 条差异（`resolved 58` / `accepted 17` / `gap 0`）。accepted 的意思是**已知、刻意接受**，
不是「还没做」。几条值得先知道的：

| ID | 差异 | 影响 |
|---|---|---|
| `D001` | Qwen3-TTS 整句合成而非逐帧流式 | 打断无法中断在途合成——真权重下被打断那一轮仍会合成完（实测 82–104 s），下一轮首包被它顶住。已按句切分 + 代际检查补偿，但 CPU 上消不掉 |
| `D010` | 上游多数后端无 C# 运行时 | 注册表 32 个名字里 21 个不可用——名字保留、选择即报明确原因，不静默降级 |
| `D008` | ManySpeech 本地 STT 无增量转写 | 走 `whisper` / `paraformer` 时没有 `transcription.delta`；流式路径由 `openai-realtime` / `vllm-realtime` 承担 |
| `D036` | STT 参数命名统一 `{backend}_*` 模板 | 上游是 `vllm_realtime_stt_*`；映射表在 `docs/vllm-realtime-d005-integration.md` §5.1 |
| `E003` | 缺模型时运行期报错而非启动期 | 表现为「连上了但收不到事件」——先看服务端日志 |
| `C005` | 分句器由 Punkt 改为确定性规则 | **刻意改进**：上游 Punkt 用英文模型，中文标点完全不切；本实现按全角标点切，中文长回复的首段音频不拖到最后 |

完整清单见 `porting/02-differences.md`（机器可读源 `porting/differences.jsonl`）。

### 尚未实跑的格子

- **真人麦克风听感**（音色、全双工回声）——协议与时延账已闭合，剩听感。
- **Linux / macOS 真机**——代码与绑定冒烟已过（macOS 走 PortAudio，与上游 sounddevice 同引擎），
  但真机未实跑。
- **流式 STT 真实服务端联调**——47 例测试全基于 fake WebSocket；手册在
  [`docs/vllm-realtime-d005-integration.md`](docs/vllm-realtime-d005-integration.md)，拿到 GPU 或 key 后按节执行。

---

## 构建与测试

```bash
# 1. 账本与文档一致性
python tools/ledger/check_doc_sync.py --strict

# 2. 构建（0 警告 0 错误是硬要求）
dotnet build SpeechToSpeech.slnx --no-incremental

# 3. 单元测试（当前 688 例）
dotnet test tests/SpeechToSpeech.Tests/SpeechToSpeech.Tests.csproj

# 4. 差分语料（六类）
python ~/.workbuddy/skills/cross-lang-porting/scripts/diff_corpus.py verify --corpus corpus/text       --json-canonical --cmd "dotnet tools/TextProbe/bin/Debug/net10.0/TextProbe.dll"
python ~/.workbuddy/skills/cross-lang-porting/scripts/diff_corpus.py verify --corpus corpus/protocol   --json-canonical --cmd "dotnet tools/ProtocolProbe/bin/Debug/net10.0/ProtocolProbe.dll"
python ~/.workbuddy/skills/cross-lang-porting/scripts/diff_corpus.py verify --corpus corpus/turns      --json-canonical --cmd "dotnet tools/TurnsProbe/bin/Debug/net10.0/TurnsProbe.dll"
python ~/.workbuddy/skills/cross-lang-porting/scripts/diff_corpus.py verify --corpus corpus/lang       --json-canonical --cmd "dotnet tools/LanguageProbe/bin/Debug/net10.0/LanguageProbe.dll"
python ~/.workbuddy/skills/cross-lang-porting/scripts/diff_corpus.py verify --corpus corpus/smart-turn --json-canonical --cmd "dotnet tools/SmartTurnProbe/bin/Debug/net10.0/SmartTurnProbe.dll"
python ~/.workbuddy/skills/cross-lang-porting/scripts/diff_corpus.py verify --corpus corpus/latency    --json-canonical --cmd "dotnet tools/LatencyProbe/bin/Debug/net10.0/LatencyProbe.dll"

# 5. 协议覆盖审计（当前 26/26）
python tools/refprobe/protocol_coverage.py

# 6. 模型下载脚本集成测试（本地 HTTP，不下载真实权重；自动测试可用的 PowerShell / Bash）
python -m unittest discover -s tools/models -p test_download_models.py -v
```

> **不要并行跑 build 与 test**（会撞 CS2001）。构建前先停掉正在跑的 `serve`——
> 残留进程锁住 `speech-to-speech.exe` 会让下一次构建报 MSB3027；
> 「当文件已存在时无法创建」则用 `dotnet build-server shutdown`。

### 端到端冒烟

`tools/smoke/` 下是脚本化客户端，把「一次完整说话」变成可复现的几十行输出：

```bash
python tools/smoke/serve_smoke.py --wav speech_6s.wav --url ws://127.0.0.1:8765
python tools/smoke/serve_smoke.py --wav speech_6s.wav --speak-twice --gap 3   # 打断场景
python tools/smoke/webrtc_smoke.py --port 8791                              # aiortc 互证 WebRTC
python tools/smoke/demo_server_parity.py                                   # demo 服务端双侧对拍
```

> HTTP 一律走 `ProxyHandler({})`（Windows 的系统代理会劫持 `127.0.0.1`，差异账 D029）。
> `serve` 指向本机 LLM 服务时记得 `NO_PROXY=127.0.0.1,localhost`。

### Docker

```bash
# 准备容器挂载的模型目录（PowerShell: .\download-models.ps1 -ModelsDir .\cache\models）
bash ./download-models.sh --models-dir ./cache/models
docker compose up --build
```

容器里没有音频设备，`serve` 模式（客户端经 WebSocket 推流）是唯一用法。

---

## 仓库结构

```
src/
  SpeechToSpeech.Core/              管线运行时、消息契约、配置、后端注册表、线路 JSON 编码
  SpeechToSpeech.Realtime/          协议事件工厂、连接循环、Kestrel 宿主
  SpeechToSpeech.Transport.WebRtc/  WebRTC 传输（会话、信令、pacer、重采样）
  SpeechToSpeech.Cli/               serve / talk / local / list-backends
  SpeechToSpeech.Audio.Diarization/ 流式说话人分离状态机（零依赖）
  SpeechToSpeech.Backends.*         ManySpeech / TensorSharp / QwenTts / OpenAiCompatible /
                                    SmartTurn / OpenLipSync / PyannoteDiarization /
                                    SpeakerEmbedding / Lingua
  SpeechToSpeech.Demo.Web/          浏览器 demo 的 ASP.NET Core 10 服务端
  SpeechToSpeech.Evals/             Big Bench Audio 评估套件
tests/SpeechToSpeech.Tests/         单元测试（688 例）
corpus/                             差分语料（六类）
tools/                              探针（参照侧 Python + 本实现 C#）、冒烟脚本、账本检查
porting/                            四本账
docs/                               API 接入指南、流式 STT 联调手册
demo/                               上游浏览器 demo
examples/OpenAIExamples/GetStarted/ SIPSorcery WebRTC 客户端示例（已指向本地 serve）
```

---

## 许可与致谢

本仓库采用 **Apache License 2.0**（见 [`LICENSE`](LICENSE)）。

- 上游项目：[huggingface/speech-to-speech](https://github.com/huggingface/speech-to-speech)（Apache-2.0）。
  本仓库是独立重写，参照物锁定在 `0835002`；上游的模型权重各自遵循其原始许可
  （Silero VAD、Parakeet、Qwen3-TTS 等），商用前请核对。
- `demo/` 是上游自带的前端，随上游许可引入。
- 依赖的 NuGet 包均为正式版：ManySpeech（SileroVad / WhisperAsr / AliParaformerAsr）、
  TensorSharp.Chat、ElBruno.QwenTTS、NAudio(+Alsa)、PortAudioSharp2、SIPSorcery、Concentus。

如果用到了这条管线，也请一并引用你实际运行的组件模型。
