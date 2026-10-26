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
- `event_id` 在两侧输出时都归一化为 `"<event_id>"`——两侧生成规则不同（上游 32 位十六进制，本实现 12 位），客户端也不解析它，比它没有意义
- 其余字段一律逐字节比，**包括编码**：中文不得变成 `\uXXXX`，base64 里的 `+` `/` 不得被转义

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

## 32 条覆盖什么

**输入侧（转写链路）**：`speech_started` / `speech_stopped` / `committed` / `item.created` / `transcription.delta` / `transcription.completed` / `transcription.failed` / `item.deleted` / `error`。

**输出侧（渲染链）**：`output_item.added` → `content_part.added` → `output_audio_transcript.delta` / `output_text.delta` → `output_audio.done` / `output_text.done` → `content_part.done` → `output_item.done` → `response.done`，音频与纯文本两种模态各一套。

渲染链那 16 条是本轮补的。它们的意义不是"多支持几个事件"，而是**官方客户端能不能看到回复**：客户端靠 `output_item.added` 建立消息项、靠 `content_part.added` 建立内容片，缺了这两步它不会报错，只会静默不显示。所以每条都有对应的形状断言（`AssistantItemLifecycleTests`），不只靠语料。

**终态三种**：`response.done` 的 `completed` 不发 `status_details`；`cancelled` / `incomplete` 发同名 `type` 加 `reason`；`failed` 发 `type=failed` 加 `error.code` / `error.message`。`status_details.type` 必须与 `status` 同域——这是判别器的取值域，混用会让严格客户端解析失败（C015）。

## 为什么不是 26 条全铺

语料收录的是**已对齐**的事件，作用是防回归。协议层的**覆盖缺口**不适合放进必过语料：一旦某个事件整体未实现，这条语料会永久标红，CI 就失去意义。

缺口由两个可复跑的审计承担：

```bash
# 事件级覆盖：上游 26 种 vs 本实现的构造路径（当前 25/26）
python tools/refprobe/protocol_coverage.py

# 官方 schema：46 种事件的必填字段清单，可作为字段级的比对基准
python tools/refprobe/schema_dump.py
```

缺口的结论记在 `porting/02-differences.md`（D011–D012），不在这里。

## 已知且刻意不比的点

- **`response` 载荷的内部字段**：上游 `_build_response()` 会填 `usage`（完整 token 统计）、`output_modalities`、`conversation_id`、`audio.voice`、`metadata`；本实现只填 `id` / `object` / `status` / `output`。`RealtimeResponse` 在官方 schema 里没有必填字段，所以不算违约，但客户端读不到 token 用量——登记为 D012。
- **`transcription.completed` 的 `usage`**：这条不一样，`usage` 在官方 schema 里是**必填**。已按上游口径补齐（`{type: "duration", seconds}`，秒数取该输入项的 VAD 段时长），并进必过语料——它是「缺了就整条被拒收」，不是「少个统计」。
- **`content_index` / `output_index` 的省略**：上游模型对它们是必填，本实现在 `transcription.delta` 上显式写 `content_index: 0`，上游可选时省略。语料里统一显式给值，避免这条噪声掩盖真差异。
