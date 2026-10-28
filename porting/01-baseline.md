# 行为基线账（第一本账）

把「参照实现的行为」变成可执行证据。四类证据缺一不可。

统计口径：上游 `tests/` 共 **119 个 `test_*.py`**、**2,082 个测试函数**（`evals/` 85 + `tool_call/` 74 + `openai_realtime/` 588 + 根目录 1,335）。
本实现 **60 个测试方法 / 4 个测试文件**。数量不对称是刻意取舍：只锁「语义接缝」，不锁「某个后端在某台机器上的延迟」。

---

## 1. 测试移植

### 1.1 已锁定的语义接缝

| 上游测试 | 本实现测试 | 用例数 | 状态 | 说明 |
|---|---|---|---|---|
| `test_pipeline_messages.py` | `PipelineSemanticsTests.ResponsePrefetchTransactionTests` | 7 | 改写 | 上游 5 例（claim/cleanup/abort/discard），本实现 7 例——补了「弃置后注册的 abort 立即执行」与「无 claim 时 cleanup 挂起」两条边界 |
| `test_speculative_turns.py`（跟踪器部分） | `PipelineSemanticsTests.CancelScopeTests` + `corpus/turns` | 7 + 13 | 改写 | 上游 68 例 pytest 不照抄：代际与弃置保护走 7 例单测，HOLD/grace/复开等状态迁移改由 `corpus/turns` 13 条与上游 `SpeculativeTurnTracker` 逐方法对拍（C006 **已解决**，见「轮次层的实测结论」） |
| `test_runtime_config.py` | `PipelineSemanticsTests.RuntimeConfigTests` | 9 | 改写 | 字段级深合并、未触碰嵌套字段保留、`turn_detection` 默认值与 OpenAI server VAD 对齐；另 4 例锁 `output_modalities` → `WantsAudio` 的四种取值（缺省/空数组/含 audio/text-only） |
| `test_pipeline_messages.py`（TtsInput） | `PipelineSemanticsTests.TtsInputTests` | 3 | 移植 | `selected_language` → TTS 语言的 `"auto"`→null 语义逐条对齐 |
| 上游无对应（F#09 handler 循环契约） | `BaseHandlerTests.BaseHandlerTests` | 5 | 新增 | 逐条覆盖 `BaseHandler` 六条契约：软控制转发不中断、`PipelineEnd` 断循环、异常只记录不终止线程、过期代际静默丢弃、两条路径都写终止哨兵 |
| `test_backend_registry.py` | `BaseHandlerTests.BackendRegistryTests` | 3 | 改写 | 上游 43 例锁 CLI 解析与后端构造；本实现 3 例锁「31 个名字全注册」「不可用带原因」「未知名字列出全部合法值」 |
| `test_llm_utils.py` | `TextAndAudioTests.TextNormalizerTests` | 12 | 移植 + 表征 | Unicode 规范化、Markdown 剥离、确定性分句（含缩写白名单）。末一条 `TreatsCjkTerminatorsAsBoundaries_UnlikeUpstreamPunkt` 是**刻意分歧的表征**，对应 C005 与 `corpus/text` |
| — | `TextAndAudioTests.SentenceAccumulatorTests` | 3 | 新增 | 句级流式切分（上游散落在 `test_tts_input_coalescing.py` 的时序断言里，此处提为独立单元） |
| `openai_realtime/test_output_resampling.py` | `TextAndAudioTests.AudioUtilTests` | 5 | 改写 | PCM16 往返量化误差、定长切块、重采样比、WAV 头标记 |
| `openai_realtime/test_realtime_protocol_contract.py` | `ProtocolShapeTests.ProtocolShapeTests` | 7 | 改写 | 上游 7 例锁响应生命周期交织；本实现 7 例锁线上字段形状（`event_id` 必带、错误嵌套结构、`response.done` 三种终态） |
| `openai_realtime/test_realtime_protocol_contract.py`（渲染链部分） | `ProtocolShapeTests.AssistantItemLifecycleTests` | 10 | 改写 | 助手消息项生命周期：`output_item.added` 开项（content 为空数组）、`content_part.added` 两模态各自的 part 形状、`output_item.done` 带完整 content 与 `completed`/`incomplete`、`transcription.failed` 为 item 作用域、`committed` 首项省略前驱 |
| — | `ProtocolShapeTests.TtsSplitTests` | 3 | 新增 | TTS 合成单元切分（空段丢弃、一句一单元） |

**合计 74 例，全绿。**

### 1.2 未移植的上游测试与原因

| 上游范围 | 测试函数 | 未移植原因 | 归属 |
|---|---|---|---|
| 后端处理器（各 TTS/STT/ASR/LLM） | 633 | 每个后端在 C# 侧要么走统一契约（`ISttBackend` 等），要么根本没运行时；照抄单测等于测别人的库 | D010 |
| `openai_realtime/`（WebRTC、Agents SDK、会话删除） | 588 | WebRTC 与 call 端点（含 DELETE 挂断）**已移植**（D003，SIPSorcery + Concentus，不引 aiortc），但上游 pytest 不照抄——行为改以 C# 侧表征单测（pacer/resampler）+ 进程内环回 + aiortc 跨实现冒烟对拍；Agents SDK 是 Python 生态绑定 | D003 |
| VAD 长程状态机（stitching、grace、progressive pause） | 约 200 | 依赖 `SmartTurn` 与 progressive 快照，属 C006 同族 | C006 / D005 |
| `evals/`（Big Bench Audio 评测套件） | 85 | 已移植（`src/SpeechToSpeech.Evals` + `tools/EvalProbe`） | D030 |
| `tool_call/`（函数签名解析） | 74 | 工具调用解析在 C# 侧走 `AssistantToolCallPart` 直通，无独立解析层 | D009 |
| 设备/延迟基准（`test_torch_num_threads` 等） | 约 30 | Python 运行时特有（GIL、torch 线程数） | C001 |
| 口型（STV） | 约 200 | 已移植：引擎 D031（OpenLipSync）+ 管线级/事件/配置 D032 | — |
| 说话人分离（diarization） | 约 45 | 已移植：分离状态机 + powerset 掩码引擎 + 时间线/归因（D009）+ 声纹后端（D033，Kaldi fbank + 3D-Speaker CAM++） | — |

---

## 2. 差分语料

语料只能由参照实现生成，不能手写。

| 语料 | 守护的行为 | 条数 | 结果 |
|---|---|---|---|
| `corpus/text/` | `remove_unspeechable` / `remove_markdown` / 分句（含缩写、连续标点、围栏与行内代码、标题与列表、强调、snake_case、未闭合围栏、URL 与邮箱、空与纯空白） | 15 | **15 通过** |
| `corpus/protocol/` | 线路形状：26 类服务端事件的字段集、值类型、JSON 编码（UTF-8 不转义、base64 特殊字符不转义、BMP 外字符不转义、转义集合只含引号/反斜杠/控制字符），含四条终态、`response` 载荷（created + 四种终态）与转写完成的必填 `usage` | 36 | **36 通过** |
| `corpus/turns/` | 状态迁移：裁决（accept/hold/drop）、轮次标识、复开宽限与处理期限，在虚拟时钟下逐步比对 | 13 | **13 通过** |
| `corpus/lang/` | 语言判定：长度门槛、CJK 放宽、置信度差、`nb`→`no` 归一化、256 码点窗口的累积与截断 | 15 | **15 通过** |
| `corpus/smart-turn/` | 端点判定：结论到宽限/处理延迟的四条分支（含阈值等号、延迟钳制、异常回落）、判定器输入窗口的取尾与左补零 | 13 | **13 通过** |
| `corpus/latency/` | 响应时延账：待定槽位吸收、E2E 名额只发一次、会话清理作用域、等待区间并集、metadata 压缩 JSON 与日志行的逐字符形态 | 16 | **16 通过** |

**状态：文本层、协议形状层、轮次状态机层、语言层、端点判定决策层与时延账层已闭合，音频层仍缺口。**

录制环境（可复现前提）：Python 3.13 + `nltk 3.10.4` + `punkt_tab` + `pillow` + `requests`；
协议层另需 `openai 2.28.0`（与上游 `pyproject.toml` 锁定的版本一致）+ `numpy` + `scipy`。
探针六对：`tools/refprobe/text_probe.py` / `tools/TextProbe`（文本层），
`tools/refprobe/protocol_probe.py` / `tools/ProtocolProbe`（协议层），
`tools/refprobe/turns_probe.py` / `tools/TurnsProbe`（轮次层），
`tools/refprobe/lang_probe.py` / `tools/LanguageProbe`（语言层），
`tools/refprobe/smart_turn_probe.py` / `tools/SmartTurnProbe`（端点判定层），
`tools/refprobe/latency_probe.py` / `tools/LatencyProbe`（时延账层），各自输出同形 JSON。

实测结论——这层比预期更干净：

- `markdown` 与 `unspeakable` 两栏在 **全部 15 条上与上游逐字节相同**；
- 分歧**只**出现在 `sentences`，且**只**由 CJK 标点触发：上游 Punkt 用英文模型，对
  `一句话。第二句话！` 完全不切分，整段算一个句子；本实现按全角标点切（C005）。

因此语料加了 `split` 开关：中文用例置 `split: false`，只比两个纯函数；切分这个刻意分歧
改由单元测试 `SentenceTokenize_TreatsCjkTerminatorsAsBoundaries_UnlikeUpstreamPunkt`
表征。上游的原始输出留在 `corpus/text/README.md` 作为对照。

音频层仍不可比对：模型推理受浮点累加顺序影响，只能锁「采样率、声道、位深、段数、时长误差带」，不锁样本值。

**协议层的实测结论**（`corpus/protocol/`，36 条）：

- 转写链路与渲染链路的全部事件**字段集、值类型、编码逐字节一致**，含中文与
  含 `+` `/` 的 base64；
- 发现并修掉七个真实缺陷：
  1. 本实现原先用默认 `JavaScriptEncoder` 序列化，中文被转成 `\uXXXX`（汉字 3 字节变 6 字节）、
     base64 里的 `+` 被转成 `\u002B`；上游走 Starlette 的 `send_json`（`ensure_ascii=False`）不转义。
     已改 `UnsafeRelaxedJsonEscaping`（C012）；
  2. 助手消息项的生命周期整段缺失，官方客户端因此建不出消息项（D013）。已按上游顺序补齐；
  3. 转写失败原先借用全局 `error`，会让客户端误判整条连接出错。已改为
     `conversation.item.input_audio_transcription.failed`（C014）；
  4. `response.done` 的 `status_details.type` 恒写 `incomplete`，取消时产出官方 schema 里
     不存在的组合（C015）。已改为跟随 `status`；
  5. `transcription.completed` 缺 `usage`——这不是「少个统计」，是整条事件被严格客户端拒收
     （D011，官方 schema 必填）。已按上游口径发 `{type: "duration", seconds}`；
  6. `response` 载荷只填了 `id`/`object`/`status`，缺 `usage`/`output_modalities`/
     `conversation_id`/`output`（D012）。已由 `ResponseSnapshot` + `ServerEvent.BuildResponse`
     单点组装，created 与 done 共用同一份口径；token 记账抽成 `ResponseUsageLedger`
     （按 `response_key` 挂起、收口结转、已关闭 key 丢弃迟到用量），9 条用例锁定；
  7. `status_details` 原先只在异常终态出现，`completed` 完全不带——上游对任何终态都发；
     且 `failed` 的 `error` 上游只带 `type`，本实现多发 `code`/`message`（C016）。已修正；
  8. `response.create` 里的 `audio.output.voice` 原先被忽略，音色只认会话配置（D017）。
     现在响应级优先、会话级兜底，且同一个 `ResolveVoice` 同时喂协议载荷与 `TtsRequest.Speaker`。
     这类缺陷有个共同特征：字段看着对，行为没跟上——所以锁它的是单测（值是否真的到了后端），
     不是语料（语料只能证明字段在）。
- 曾记的最后一类**结构性差异**已闭合：`response.metadata` 现在会发（D016 **已解决 2026-10-26**），
  终态时延四项由 D019 接线，工具输入 `s2s_tool_input_call_ids` 由 D020 补齐。当初那条判断仍然成立
  ——不发而不是发空对象，因为空对象会表示「这轮没有遥测」，缺字段才表示「本实现不上报」——只是两侧
  现在口径一致了。

覆盖缺口由 `tools/refprobe/protocol_coverage.py` 量化：上游 26 种事件，本实现可构造 **26 种（100%）**。
扩展事件 `speech_to_speech.output_audio.visemes` 由口型管线补齐（引擎 D031 + 管线级 D032，D013 翻 26/26）。

**轮次层的实测结论**（`corpus/turns/`，13 条）：

这层比的是状态迁移，不是字节。三个前提让逐值比对成立：状态机自包含（不碰模型/网络/磁盘）、
期限全在单调时钟上（换成虚拟时钟即可确定性复现）、返回值是纯量（裁决 + 轮次 + 期限）。

- 上游 488 行的 `SpeculativeTurnTracker` 逐方法对齐，20 个操作可脚本驱动；
- 首次运行即 **13/13 一致**，包括 HOLD 期限的绝对秒值（`"1001.000000"` 这类）；
- 三条不变量各有语料守着：挂起不等于丢弃、已提交的不可挂起、复开不推进序号；
- 压不住的两块改由单测锁：阻塞式等待（探针会真睡在期限上，比对变成比谁更慢）、
  并发（`threading.Condition` 与 `Monitor` 语义等价但调度不可复现）。

**语言层的实测结论**（`corpus/lang/`，15 条）：

这是唯一一层「同一个算法的两种语言实现互证」：上游 `lingua-py`，本实现用
`Panlingo.LanguageIdentification.Lingua`（lingua 的 Rust 实现 `lingua-rs` 的 .NET 封装）。
同模型、同算法、不同宿主机语言，所以两侧一致才有意义。

- **15/15 逐值一致**，包括置信度差边界、`nb`→`no` 归一化、256 码点窗口的截断点；
- 决策层（长度门槛 20、含 CJK/假名/谚文放宽到 4、置信度差 0.1）与检测器解耦，
  因此能在没有原生库的机器上跑单测；
- 窗口规则单独作为断言输出（`windows` 数组），不只比结论——结论只有两种取值，
  窗口错了可能碰巧结论一样；
- 一个必须接受的反直觉结果：`window-truncates` 里英文重复被判成 `pt`。那是截断后窗口的
  真实判定，两侧相同。**验收标准是一致，不是合理**。

**端点判定层的实测结论**（`corpus/smart-turn/`，13 条）：

这一层比的是上游 `_smart_turn_timing_ms` 的映射规则：判定结论 → （输出宽限 ms，处理延迟 ms）。
判定器在这一层被替换成脚本化替身——不是因为缺，而是因为这一层要钉的是「拿到判定之后怎么办」：
后者是纯函数、能逐值比，前者（ONNX 分类器，D018 **已解决 2026-10-26**）另由概率对拍与单测锁。
这也正是当初主张「判定」与「决策」必须拆开的理由。

- **13/13 一致**，含阈值等号（`probability == threshold` 算没说完，上游是严格大于）、
  延迟大于最长等待时的钳制、判定器抛异常时的回落、以及窗口整理的取尾与左补零；
- 参照侧没有 torch（PyPI 不可达），探针塞了一个桩模块让 `vad_handler` 导入通过——
  被测的两个方法都不碰 torch，调的是上游源码本身而不是复制品；
- 窗口样本按 float32 位模式比对而不是十进制：浮点的最短表示是两侧运行时各自的策略，
  比它等于允许漂移。为了让失败可诊断，另带首尾样本、补零计数与整窗 FNV-1a 校验；
- 音频不进语料文件，按 `((i*37) % 1000) / 1000.0` 现场生成——只用整数运算与一次
  `double → float32` 转换，IEEE 转换确定性保证两侧逐位相同。

**判定器已补齐**（D018 **已解决 2026-10-26**）：ONNX 实现，`ITurnEndClassifier` 按当初的约定注入，
`VadHandler` 一行没动——「决策层与判定器分离」这条设计在真补的时候省了事。判定器概率与参照链路逐值
对齐（合成音频 <1e-4），语料仍只覆盖纯函数那半边（那才是能逐值比对的部分）。

**时延账层的实测结论**（`corpus/latency/`，16 条）：

这一层比的不是字节，是**状态迁移**加上两个逐字符的产物（metadata 里那段压缩 JSON、以及那行 INFO 日志）。时间全部由语料给定，所以没有抖动、也不需要容差。

- **16/16 一致**，含待定槽位吸收、`pop` 之后不再复活、会话清理的作用域、E2E 名额在工具续接链上只发一次；
- `hold_s` 记的是等待区间**并集**：两段重叠区间是 0.4 秒而不是相加的 0.6 秒；
- 浮点写法是这一层最容易静默漂的地方。动手前先拿 12 个值在两侧各跑一遍格式化，确认 `.2f` 与 `F2` 连中点舍入都一致（`0.125 → 0.12`、`0.135 → 0.14`），而最短表示的默认写法真的不同：`2.0` vs `2`、`1e-07` vs `1E-07`。metadata 里存的就是那段字符串，数字相等不等于对客户端相等。
- 上游对 `vad_decision_s` / `hold_s` 做 `round(x, 9)`，其余字段原样写。这条不对称容易被「统一保留几位小数」的直觉抹平。

---

## 3. 线路形状测试

| 协议 / 接口 | 锁定的内容 | 测试位置 |
|---|---|---|
| `session.created` | `event_id` + `session` 对象骨架 | `ProtocolShapeTests` |
| `session.updated` | 深合并后回显 | `ProtocolShapeTests` / `RuntimeConfigTests` |
| `input_audio_buffer.speech_started` / `speech_stopped` | 线上名与内部事件名分离 | `ProtocolShapeTests` / C007 |
| `input_audio_buffer.committed` | 首项整字段省略 `previous_item_id`（非 null），后续项串起顺序链 | `AssistantItemLifecycleTests.InputAudioBufferCommitted_*` |
| `conversation.item.created` | `item.content` 是完整数组（语音为 `[input_audio]`，文本原样回带）、`previous_item_id` 指向链上前驱 | `AssistantItemLifecycleTests.ConversationItemCreated_*` / D014 |
| `conversation.item.deleted` | 只带 `item_id`；删的是链尾时前驱回退 | `RealtimeConnection.HandleConversationItemDeleteAsync` |
| `conversation.item.input_audio_transcription.failed` | item 作用域失败，`error.{type:transcription_error,code:transcription_failed,message}`，**不是**全局 `error` | `AssistantItemLifecycleTests.TranscriptionFailed_*` / C014 |
| `response.created` | 与 `response.done` 同构：`id` / `object` / `status` / `audio.output.voice` / `conversation_id` / `output_modalities` / `output`(空) / `usage`(全 0)。唯一差异是终态才有的 `status_details` | `ProtocolShapeTests.Response_CreatedAndDoneShareTheSameFieldSet` |
| `response.output_item.added` | 开一个 assistant 消息项：`status=in_progress`、`content` 为空数组 | `AssistantItemLifecycleTests.OutputItemAdded_*` |
| `response.content_part.added` | 音频模态 part 为 `{type:audio,audio:"",transcript:""}`，纯文本模态为 `{type:text,text:""}` | `AssistantItemLifecycleTests.ContentPartAdded_*` |
| `response.output_text.delta` / `.done` | 纯文本模态的增量与终稿（音频模态走 `output_audio_transcript.*`） | `ProtocolShapeTests` |
| `response.content_part.done` | 音频模态保留 `audio:""` 不回灌音频，`transcript` 带完整文本 | `AssistantItemLifecycleTests.ContentPartDone_*` |
| `response.output_item.done` | `content` 从空数组变成 `[{type:output_audio|output_text,…}]`；`status` 分 `completed` / `incomplete` | `AssistantItemLifecycleTests.OutputItemDone_*` |
| `response.output_audio.delta` | `response_id` + `item_id` + `output_index` + base64 `delta` | `ProtocolShapeTests.AudioDelta_*` |
| `response.output_audio.done` | 与 delta 同 identity | `ProtocolShapeTests` |
| `response.output_audio_transcript.delta` / `.done` | 转写增量与终稿分列 | `ProtocolShapeTests` |
| `response.function_call_arguments.done` | 工具名 + 完整参数字符串 | `ProtocolShapeTests` |
| `response.done` | 四终态，`status_details` 一律发：`completed` 只带 `type`、`incomplete`/`cancelled` 另带 `reason`、`failed` 另带 `error.type`；`output` 里的消息项与 `output_item.done` 逐字段相同 | `ProtocolShapeTests.ResponseDone_*` / `Response_TerminalOutputItemMatchesOutputItemDone` |
| `error` | 嵌套 `error.{type,code,message}`，非扁平 | `ProtocolShapeTests.Error_*` |
| 上行 `input_audio_buffer.append` / `commit` / `clear` | 解析形状 | `RealtimeConnection.DispatchAsync` |
| 上行 `session.update` / `response.create` / `response.cancel` | 解析形状 | 同上 |
| 上行 `conversation.item.create` / `delete` / `truncate` | 解析形状 | 同上 |

上表是**本实现自证**的形状断言；能否与上游真正对齐，由 `corpus/protocol/` 的 36 条差分语料回答。
两者互补：单元测试挡住内部漂移，差分语料挡住与上游的偏离。

渲染链那几条尤其只有靠这两层同时守住才安全：`output_item.added` 缺失时客户端不报错、
只是不显示，单靠日志和编译都发现不了。

---

## 4. 示例入编译

| 示例 | 是否在构建内 | 覆盖的接缝 |
|---|---|---|
| `list-backends` 子命令 | 是（`SpeechToSpeech.Cli`） | 后端注册表三族 15/5/12、可用性原因、未知名字报错 |
| `serve` 子命令 | 是 | Kestrel + `/v1/realtime` + `/healthz`，已交互跑通 |
| `local` 子命令 | 是 | 进程内四段串联。参数族与协议面对齐 D035（2026-10-28）：`--local-audio-*` 全套接线、`session.update` 走服务端原生速率并订阅转写快照；真跑证据见 03-acceptance「talk 客户端真跑」。仍需真人对麦克风说话的一轮听感验证 |
| `talk` 子命令 | 是 | WebSocket 客户端 + NAudio 设备 I/O，参数族与协议面已按 D035 对齐（2026-10-28）。平台矩阵：Windows 走 WASAPI 播放 + WinMM 录制（已真跑：连接 + 握手 + VAD commit + 转写完成 + response 创建）；Linux 走 ALSA（仅编译期校验，未在真实设备上跑；`--input-device` 映射为 `plughw:<n>,0`）；macOS 走 PortAudio（设备索引直通） |
| `tools/TextProbe` | 是（`/tools/` 目录） | 文本层探针，为 `corpus/text/` 输出 `unspeakable` / `markdown` / `sentences` 三视图 |
| `tools/LanguageProbe` | 是（`/tools/` 目录） | 语言层探针，按片驱动 `AssistantLanguageProbe`，输出判出语言与每片后的窗口 |
| `tools/ProtocolProbe` | 是（`/tools/` 目录） | 协议层探针，走产品序列化入口构造 26 类服务端事件 |
| `tools/TurnsProbe` | 是（`/tools/` 目录） | 轮次层探针，按脚本驱动状态机并注入虚拟时钟 |
| `tools/ApiProbe` | 是（`/tools/` 目录） | 反射导出依赖包与本实现的公开表面（`--self`） |

`/tools/` 下三个探针都进了解决方案，因此「0 警告 0 错误」覆盖它们，不会悄悄腐烂。

诊断工具 `tools/ApiProbe` 同样在解决方案内，用反射导出五个程序集的公开表面，供「公开 API 审计」复核。

---

## 覆盖缺口

| 未覆盖部分 | 原因 | 风险 | 计划 |
|---|---|---|---|
| 音频层差分语料为空 | 模型推理受浮点累加顺序影响，无法逐字节比对 | 中——只能靠采样率/段数/时长误差带间接验证 | 用固定音频跑一遍四段，比段落数、总时长与事件序列，不比样本值 |
| 协议层只比单事件形状，不比顺序 | 语料是「一事件一例」，没有覆盖一整轮的上行/下行时序 | 中——事件顺序错乱（例如 delta 先于 item.added）测不出来 | 复用上游 `realtime_contract.py` 的契约检查器，重放同一事件序列 |
| 轮次状态机的接合点未实测 | 状态机自身与上游逐值一致，但 `VadHandler` 何时调 `SpeechCandidateStarted` / `SegmentDiscarded` 没有真跑验证 | 中——接错时机仍会退化成「卡住」或「停顿即两轮」 | 真跑一轮带打断的对话，对照上游日志的轮次序列 |
| `local` / `talk` 听感实跑 | 参数族、协议面与真跑握手已闭合（D035，2026-10-28）：`serve` + `talk --print-json` 真跑走完 VAD commit → 转写完成 → response 创建；**剩下的只有真人对着麦克风说话的听感**（音色、回声、设备延迟） | 低——队列时序、协议握手与转写链路均已实测 | 找时间人对麦克风说一轮，顺带验证 `--local-audio-*` 参数 |
| 12 个后端无运行时 | D010 | 低——`BackendUnavailableException` 显式报错，不静默降级 | 按需逐步补齐 |
| 跨平台音频 I/O | E001 已解决 —— Windows（WASAPI+WinMM）/Linux（ALSA）/macOS（PortAudioSharp2 1.0.6，与上游 sounddevice 同引擎）三后端齐备 | 低 —— Linux ALSA 与 macOS 路径均未在真实设备上验证；服务端 `serve` 不受影响 | Windows 已实跑设备创建；Linux 需在装 `libasound.so.2` 的机器上跑一轮；macOS 需真机跑一轮 talk |
| 惰性加载的模型路径 | E003 运行期报错 | 低——首次合成才失败，非启动即失败 | 可加 `--check-models` 预检 |
