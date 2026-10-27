# 协议层差分语料

守护的是**线路形状**：同一组业务参数，本实现发出的事件与上游是否字段一致、值一致、编码一致。

## 输入约定

```json
{
  "note": "这条在守什么",
  "payload": {
    "event": "<线上事件名>",
    "args": { "<snake_case 业务字段>": <值> }
  }
}
```

- `args` 只放业务字段；`type` 与 `event_id` 由探针各自填充
- `args` 的形状刻意与官方模型参数同形：`item` / `part` 直接给完整对象，而不是拆成散字段。这样上游探针可以原样展开传给 Pydantic 模型，本实现探针从同一份对象里取它需要的部分（`item.id`、`item.role`、`part.type` 等）
- 模态不从入参读布尔，而是由 `part.type` / `item.content[0].type` 推出——多一个布尔就多一处可能与 part 自相矛盾的来源
- `event_id` 在两侧输出时都归一化为 `"<event_id>"`——它是随机值，客户端也不解析它，比它没有意义。其余 id（`resp_` / `item_` / `conv_`）同样是随机值，语料里用固定字面量；两侧的生成格式已对齐为 `前缀_ + 32 位十六进制`
- 其余字段一律逐字节比，**包括编码**：中文不得变成 `\uXXXX`，base64 里的 `+` `/` 不得被转义，
  BMP 之外的字符（emoji）不得写成代理对转义 `\uD83D\uDE00`（C017）
- 转义集合只有三样：`"`、`\`、0x00–0x1F（`\b \f \n \r \t` 用短转义，其余 `\u00xx` 小写）。
  DEL(0x7F) 与 NO-BREAK SPACE(0xA0) 上游不转义——多转一个就多一次字节差异，
  而这类字符在转写里真实存在（复制粘贴、OCR）。`output-text-delta-escaping` 就是拿它们当探针

## 两侧探针

| 侧 | 命令 | 基线来源 |
|---|---|---|
| 上游 | `tools/refprobe/protocol_probe.py` | 官方 `openai.types.realtime` 模型类——上游 `service.py` emit 的就是这批类，其契约测试也用同一个联合类型校验（`assert_openai_schema`） |
| 本实现 | `tools/ProtocolProbe` | `ServerEvent.*` 工厂 + `ServerEvent.Serialize`（产品序列化路径，不是探针自建） |

跑法：

```bash
# 录基线（只在参照实现或上游协议层变动时做）
python <skill>/scripts/diff_corpus.py record --corpus corpus/protocol \
  --cmd "C:/Users/geffzhang/.workbuddy/binaries/python/envs/default/Scripts/python.exe tools/refprobe/protocol_probe.py"

# 比对
python <skill>/scripts/diff_corpus.py verify --corpus corpus/protocol --json-canonical \
  --cmd "dotnet tools/ProtocolProbe/bin/Debug/net10.0/ProtocolProbe.dll"
```

## 41 条覆盖什么

**输入侧（转写链路）**：`speech_started` / `speech_stopped` / `committed` / `item.created` / `transcription.delta` / `transcription.completed` / `transcription.failed` / `item.deleted` / `error`。

**输出侧（渲染链）**：`output_item.added` → `content_part.added` → `output_audio_transcript.delta` / `output_text.delta` → `output_audio.done` / `output_text.done` → `content_part.done` → `output_item.done` → `response.done`，音频与纯文本两种模态各一套。

渲染链那 16 条是本轮补的。它们的意义不是"多支持几个事件"，而是**官方客户端能不能看到回复**：客户端靠 `output_item.added` 建立消息项、靠 `content_part.added` 建立内容片，缺了这两步它不会报错，只会静默不显示。所以每条都有对应的形状断言（`AssistantItemLifecycleTests`），不只靠语料。

**终态四种**：`response.done` 的 `status_details` 对**任何终态都发**（`completed` 也发 `type=completed`，不是只有出问题才发）；`cancelled` / `incomplete` 另带 `reason`；`failed` 另带 `error.type`（恒 `response_failed`），失败详情走独立的 `error` 事件。`status_details.type` 必须与 `status` 同域——这是判别器的取值域，混用会让严格客户端解析失败（C015、C016）。

**`response` 载荷（5 条）**：`response.created` 与 `response.done` 的 `completed` / `cancelled` / `incomplete` / `failed`；外加 `response-done-out-of-band`——带外响应（`conversation="none"`）的 `conversation_id` 报 null，官方模型 `exclude_none` 会把整个键剔掉，线路上一律没有这个键（D024）。带外的其余字段与带内终态完全一致：不接进会话不等于降级的事件形状。这四条守的是同一件事——**两次载荷同构**。客户端用 `created` 建立「当前响应」、用 `done` 替换它，中间任何字段缺失都要到 done 才补，按字段取值的地方就会踩空。所以断言里有专门一条比两次的字段集（唯一合法差异是只在终态出现的 `status_details`）。

**`error` 形状（1 条）**：`error-make-error-shape` 锁上游 `make_error`（`build_error_event`）形状——error 只有 `message` + `type` 两个字段，无 `code` / `param`；`error.event_id` 是 `send_correlated` 写入的客户端事件关联 id，事件级 `event_id` 仍是服务端新生。它与 schema 校验路径（`invalid_request_error` + `code` + `param`）是两条构造线，产品里对应 `ServerEvent.TypeError` 与 `ServerEvent.Error` 两个工厂（D027）。

载荷里几个字段值得单独说：

| 字段 | 为什么不能省 |
|---|---|
| `conversation_id` | 连接级、全生命周期不变。缺了或每轮新生成，历史回放会把同一段对话当成多条会话 |
| `output_modalities` | `audio` / `text` 互斥，`audio` 已隐含带转写文本。客户端据此决定渲染成音频气泡还是文本气泡 |
| `output` | 终态里的消息项必须与 `output_item.done` 那份**逐字段相同**：客户端把它们当同一条消息的两份快照，不一致时后到的 done 会把先渲染的内容盖掉 |
| `usage` | 下游按 `response.usage` 计费。`audio_tokens` / `cached_tokens` / `image_tokens` 在纯文本管线里恒 0，但字段要在（schema 里有），值恒 0（这几维确实没被支持） |
| `audio.output` | 没配音色时发 `{"output": {}}` 而不是省掉整个 `audio` 键——省键会让解析器在解引用 `audio.output.voice` 时踩空 |

## 为什么不是 26 条全铺

语料收录的是**已对齐**的事件，作用是防回归。协议层的**覆盖缺口**不适合放进必过语料：一旦某个事件整体未实现，这条语料会永久标红，CI 就失去意义。

缺口由两个可复跑的审计承担：

```bash
# 事件级覆盖：上游 26 种 vs 本实现的构造路径（当前 25/26）
python tools/refprobe/protocol_coverage.py

# 官方 schema：46 种事件的必填字段清单，可作为字段级的比对基准
python tools/refprobe/schema_dump.py
```

缺口的结论记在 `porting/02-differences.md`（D015–D016），不在这里。

## 已知且刻意不比的点

- **`response.metadata`**：上游会填 `speech_to_speech.turn_latency`（每轮时延分解）与 `s2s_tool_input_call_ids`（本轮到达模型的工具结果 id）。本实现整个字段不发——发空对象会表示「这轮没有遥测」，缺字段才表示「本实现不上报」。登记为 D016。
- **每响应音色覆盖**：已打通（D017）。`response.create` 的 `audio.output.voice` 现在既进载荷也进 `TtsRequest.Speaker`，两处共用同一个 `ResolveVoice`（响应级 → 会话级）。这条**不能**只改载荷——字段对、声音错只能靠耳朵发现，所以由 `VoiceOverrideTests` 5 例锁住「值确实传到了后端」，而不是靠语料比字节。
- **`transcription.completed` 的 `usage`**：这条不一样，`usage` 在官方 schema 里是**必填**。已按上游口径补齐（`{type: "duration", seconds}`，秒数取该输入项的 VAD 段时长），并进必过语料——它是「缺了就整条被拒收」，不是「少个统计」。
- **`content_index` / `output_index` 的省略**：上游模型对它们是必填，本实现在 `transcription.delta` 上显式写 `content_index: 0`，上游可选时省略。语料里统一显式给值，避免这条噪声掩盖真差异。
