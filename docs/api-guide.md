# API 接入指南（前端 / 第三方）

`speech-to-speech-dotnet` 对外暴露的是一个 **OpenAI Realtime 兼容**的本地/内网语音转语音服务（`serve` 子命令）。本文档面向两类接入方：**浏览器前端**（走 WebRTC 实时对讲）与**后端 / 第三方服务**（走 WebSocket 事件流）。

---

## 1. 概览

### 1.1 服务形态

- 协议与 OpenAI Realtime API 兼容（`session.update` / `input_audio_buffer.*` / `response.*` / `conversation.item.*`），但**运行在你自己掌控的进程里**，模型、语料、延迟都归你。
- **无鉴权**：默认不校验令牌。服务只应监听本地回环或受控内网，不要直接暴露公网（如需鉴权，在外层网关加）。
- 模型**懒加载**：首个转写/生成触发加载，首轮延迟偏高（数秒到数十秒），后续热态正常工作。

### 1.2 两种接入方式

| 方式 | 适用 | 说明 |
|---|---|---|
| **WebSocket** | 后端、第三方服务、脚本 | 走 `/v1/realtime`，音频采集/播放由你掌控，需自行实现 PCM 收发 |
| **WebRTC** | 浏览器前端、实时对讲 | 走 `/v1/realtime/calls`，音频经媒体轨 + `oai-events` 数据通道，回声消除/抖动缓冲由浏览器处理 |

> 也可以直接用仓库自带 CLI：`speech-to-speech talk`（WebSocket 客户端）或 `speech-to-speech local`（进程内四段串联）。它们本身就是这套 API 的参考实现。

### 1.3 启动服务

```bash
S2S="C:/Users/geffzhang/Models/s2s"
./src/SpeechToSpeech.Cli/bin/Debug/net10.0/speech-to-speech.exe serve \
  --port 8765 \
  --vad_model_dir "$S2S/vad5" \
  --whisper_model_dir "$S2S/whisper" \
  --qwen3_model_dir "$S2S/tts" \
  --smart_turn_model_path "$S2S/smart-turn/smart-turn-v3.2-cpu.onnx" \
  --llm_model_path "$S2S/llm35/Qwen3.5-0.8B-Q4_K_M.gguf"
```

启动成功标志：

```
listening on ws://127.0.0.1:8765/v1/realtime
```

> 参数命名：STT/TTS/LLM 相关参数沿用 `{backend}_*` 模板（非上游的 `*_stt_*`，见 `docs/vllm-realtime-d005-integration.md` §5.1）。**缺模型目录时，连接在构建期即被拒**（客户端表现为「连上了但收不到任何事件」）。

---

## 2. 端点总览

| 端点 | 方法 | Content-Type | 用途 |
|---|---|---|---|
| `/healthz` | GET | — | 存活探针，返回 `ok` |
| `/v1/realtime` | **WebSocket 升级** | — | 实时对话主通道（事件流） |
| `/v1/realtime/calls` | POST | `application/sdp` | WebRTC 信令：提交 offer，返回 answer + `Location` |
| `/v1/realtime/calls/{callId}` | DELETE | — | 挂断 WebRTC 通话，释放槽位 |

---

## 3. WebSocket 接入（后端 / 第三方服务）

### 3.1 连接与握手

1. `new WebSocket("ws://127.0.0.1:8765/v1/realtime")`。
2. 连接建立后，服务端立刻推 `session.created`（含 `session.id`，形态 `session_<32hex>`）与 `session.updated`。
3. 客户端可选发 `session.update` 调整会话（voice / instructions / turn_detection / 转写订阅）。

### 3.2 上行音频

- PCM **16-bit 单声道**，默认采样率 **16000**。客户端按 `input_audio_buffer.append` 连续推流，结束时发 `input_audio_buffer.commit`，取消发 `input_audio_buffer.clear`。

```json
{ "type": "input_audio_buffer.append", "audio": "<base64 PCM16>" }
{ "type": "input_audio_buffer.commit" }
```

- 采样率与 `session.update` 的 `format` 声明一致：`16000` 时**省略** `format`（走服务端原生管线速率）；`24000` 时显式声明；其余值报错。

### 3.3 客户端→服务端事件

| 事件 | 作用 |
|---|---|
| `session.update` | 改 voice / instructions / turn_detection / 转写订阅 |
| `input_audio_buffer.append` / `commit` / `clear` | 音频上行、提交、丢弃 |
| `conversation.item.create` / `delete` / `truncate` | 对话历史增删、截断 |
| `response.create` | 触发一次生成 |
| `response.cancel` | 打断在途生成 |
| `output_audio_buffer.clear` | 仅 WebRTC 有意义；WebSocket 下为协议错误 |
| `function_call_output` / `message` | 工具结果（`--tool-module` 相关，可选） |

未知的 `type` 会回 `unknown_or_invalid_event` 错误事件。

### 3.4 服务端→客户端事件

| 类别 | 事件 |
|---|---|
| 会话 | `session.created` / `session.updated` |
| VAD | `input_audio_buffer.speech_started` / `speech_stopped` / `committed` |
| 用户转写 | `conversation.item.input_audio_transcription.completed`；订阅后还可能收到 `...transcription.delta` / `transcription.failed` / `speech_to_speech.input_audio_transcription.snapshot` |
| 生成 | `response.created` + `output_item.added/done` + `response.content_part.added/done` |
| 音频 | `response.output_audio.delta` / `output_audio.done` |
| 文本 | `response.output_audio_transcript.delta/done`、`response.output_text.delta/done` |
| 收尾 | `response.done`（含 `status_details` 终态） |
| 错误 | `error`、`type_error`、各类 `*_not_found` / `invalid_*` |

JSON 编码口径（与上游一致）：UTF-8 **不转义**、base64 特殊字符不转义、BMP 外字符不转义，转义集合只含引号/反斜杠/控制字符。

**`previous_item_id` 字段要点**：`input_audio_buffer.committed` 与 `conversation.item.created` 都带 `previous_item_id`，表示新会话项接在哪个已有项之后。该键**始终存在**——会话首个项为 `null`（与上游 `event.model_dump()` 不 `exclude_none` 对齐，也满足按 OpenAI 形状建模的客户端）。

### 3.5 最小示例（消息外壳）

完整可跑的客户端见仓库 `src/SpeechToSpeech.Cli` 的 `talk` 子命令，或用 `tools/smoke/` 的脚本。下面是最小自包含 WebSocket 骨架：

```csharp
var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri("ws://127.0.0.1:8765/v1/realtime"), CancellationToken.None);
// 读循环：按帧收文本 JSON；"type" 字段区分事件
// 上行：把 PCM16 转 base64 后发 input_audio_buffer.append；说完发 commit
```

---

## 4. WebRTC 接入（浏览器前端 / 实时对讲）

### 4.1 信令流程

1. 前端用 `RTCPeerConnection` 创建 **offer**，添加音频轨（**Opus 48kHz**，`sendrecv`）。
2. `POST /v1/realtime/calls`，`Content-Type: application/sdp`，body 为 offer SDP。
3. 服务端返回 **201**，body 为 **answer SDP**，`Location: /v1/realtime/calls/session_<32hex>`。
4. 用 answer 设远端描述；音频走媒体轨，控制事件走 **datachannel `oai-events`**。
5. 结束：`DELETE /v1/realtime/calls/{callId}`。

### 4.2 无 ephemeral token 注意

本项目**没有** OpenAI 的 `POST /sessions`（临时令牌）。前端直接拿本地服务的地址发 offer 即可，无需先取 token。若你参考的代码先调了 `/sessions`，需去掉该步骤。

### 4.3 浏览器示例（原生，无依赖）

```js
const base = "http://127.0.0.1:8765/v1/realtime";
const pc = new RTCPeerConnection({ iceServers: [] });

pc.addTransceiver("audio", { direction: "sendrecv" });
const dc = pc.createDataChannel("oai-events");

dc.onmessage = (e) => { /* 解析 JSON server 事件 */ };

const offer = await pc.createOffer();
await pc.setLocalDescription(offer);

const resp = await fetch(`${base}/calls`, {
  method: "POST",
  headers: { "Content-Type": "application/sdp" },
  body: offer.sdp,
});
const answerSdp = await resp.text();      // 201
await pc.setRemoteDescription({ type: "answer", sdp: answerSdp });

// 说话：往音频轨写 Opus 帧；触发首轮：
dc.send(JSON.stringify({ type: "response.create",
  response: { voice: "ryan", instructions: "Say Hi!" } }));
```

### 4.4 会话槽位与状态码

- 槽位数 = `num_pipelines`（默认 1），打满时 `POST /calls` 返回 **503**。
- 非 `application/sdp` 内容类型返回 **415**。
- `DELETE` 未知 callId 返回 **404**。
- offer 的音频 codec/clock 必须与 answer 侧有交集，否则协商失败。

---

## 5. 仓库内参考示例（.NET）

`examples/OpenAIExamples/GetStarted/` 是一个**自包含、可直接运行**的 SIPSorcery WebRTC 客户端，已改造为指向本地 `serve`（原上游示例硬编码 OpenAI 地址，不可直接用）：

```bash
# 先按 §1.3 起 serve
cd examples/OpenAIExamples/GetStarted
dotnet run
```

关键改造点（也是第三方接入的要点）：

- 库通过 `OpenAIRestClientFactory` 把 `BaseAddress` 硬编码为 `https://api.openai.com/v1/realtime/`。要指向本地，需**自定义 `IHttpClientFactory`**，经 `WebRTCEndPoint` 的 DI 构造函数注入（见 `Program.cs` 的 `LocalHttpClientFactory`）。
- 环境变量 `SPEECH_TO_SPEECH_REALTIME_URL` 可覆盖 REST 基址（默认 `http://127.0.0.1:8765/v1/realtime/`），`S2S_VOICE`（默认 `marin`）、`S2S_PROMPT`（默认 `Say Hi!`）。
- **兼容性注**：SIPSorcery 用 C# `required` 声明 `previous_item_id`，本项目早期对会话首个项省略该键，导致 SIPSorcery 反序列化告警并降级 `RealtimeUnknown`。现已对齐上游：`previous_item_id` **键始终存在**（首个项为 `null`，与上游 `model_dump()` 不 `exclude_none` 一致），SIPSorcery 可正常解析，不再告警。

### 5.1 已实测的行为

用该示例对真实 `serve`（8765 端口）联调：SDP 协商成功、`oai-events` 数据通道建立、`session.created` / `session.updated` / `input_audio_buffer.committed` / `conversation.item.created` 事件按序到达，无协议错误。模型懒加载下的首轮延迟与真实语音触发请按 §1.1 理解。

---

## 6. 错误契约

| 情形 | 表现 |
|---|---|
| 缺模型目录 / 后端不可用 | **连接构建期**即被拒，客户端「连上但收不到事件」 |
| 未知事件类型 / 缺 `type` | 回 `error` / `type_error`（`unknown_or_invalid_event`） |
| 会话槽位打满 | `POST /calls` 返回 503 |
| 内容类型不对 | `POST /calls` 返回 415 |
| 删除未知通话 | `DELETE /calls/{id}` 返回 404 |
| WebRTC 下发 `append` / WebSocket 下发 `output_audio_buffer.clear` | 各回对应的 `type_error`（传输特化） |

---

## 7. 与 OpenAI 的兼容度与限制

- **协议面**：事件名与字段集同 OpenAI Realtime，但**字段集与必填约束有取舍**（以 `porting/differences.jsonl` 为准；`previous_item_id` 已对齐——键始终存在，首个项为 `null`）。
- **无鉴权 / 无 `/sessions` 临时令牌**。
- **「语义接缝」差异**以 `porting/differences.jsonl` 为准（`02-differences.md` 是渲染产物）。关键差异编号：`D035`（`talk`/`local` 客户端参数族）、`D008`（ManySpeech 本地 STT 无增量转写，流式实时转写走 `openai-realtime`/`vllm-realtime` 后端）、`D036`（STT 参数命名模板）、`E003`（缺模型运行期报错）。
- **转写后端二选一**：默认 ManySpeech 离线整段（`completed` 事件）；选 `--stt openai-realtime` / `vllm-realtime` 才收 `transcription.delta` 增量（需外部端点，见 `docs/vllm-realtime-d005-integration.md`）。

---

## 8. 常见问题

- **连上了但收不到 `session.created`**：几乎总是缺模型路径——serve 在连接构建期已拒，先看 serve 日志。
- **首轮很慢**：模型懒加载。热态后正常。
- **SIPSorcery 反序列化告警**：若用旧版示例或旧服务端，可能因 `previous_item_id` 缺失而告警；现服务端该键始终带（首个项为 `null`），无需处理。
- **浏览器直连**：无需 /sessions；若前端框架封装了该步，去掉即可。
