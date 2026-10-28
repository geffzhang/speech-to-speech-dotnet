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
  - `dotnet run --project tools/ApiProbe -- --self` —— 反射导出本实现公开表面（112 个类型，7 个程序集）
  - `speech-to-speech list-backends` —— 交互跑通，三族计数 15 / 5 / 12
  - 差分语料 `corpus/text` —— **15/15 通过**（基线由上游参照实现录制，非手写）
  - 差分语料 `corpus/protocol` —— **39/39 通过**（基线由官方 `openai.types.realtime` 模型构造，与上游 emit 同类；含 function_call 输出项与混合 output 排序 3 条）
  - 差分语料 `corpus/turns` —— **13/13 通过**（基线由上游 `SpeculativeTurnTracker` 在虚拟时钟下录制，逐值比对状态迁移）
  - 差分语料 `corpus/lang` —— **15/15 通过**（基线由上游 `language_detection.py` + `lingua-py` 录制，与 `lingua-rs` 封装逐值比对）
  - 差分语料 `corpus/smart-turn` —— **13/13 通过**（基线由上游 `_smart_turn_timing_ms` / `_prepare_audio` 在脚本化判定器下录制）
  - 差分语料 `corpus/latency` —— **16/16 通过**（基线由上游 `TurnLatencyStore` / `TurnLatencyTracker` 录制，metadata 字符串与日志行逐字符比对）
  - 协议覆盖审计 `tools/refprobe/protocol_coverage.py` —— 上游 26 种事件，本实现可构造 **26 种（100%）**（扩展事件 visemes 于 D032 补上）
  - 官方 schema `tools/refprobe/schema_dump.py` —— 导出 `openai 2.28.0` 的 46 种服务端事件及其必填字段
  - `speech-to-speech talk` —— 音频后端创建已验（Windows：`WasapiPlayerBuilder` 共享模式 + `WaveIn`），失败点在 `ConnectAsync` 而非设备初始化，说明 NAudio 调用链正常

## 测试

| 项目 | 总数 | 通过 | 失败 | 跳过 |
|---|---|---|---|---|
| `SpeechToSpeech.Tests` | 688 | 688 | 0 | 0 |
| `corpus/text`（差分语料） | 15 | 15 | 0 | 0 |
| `corpus/protocol`（差分语料） | 43 | 43 | 0 | 0 |
| `corpus/turns`（差分语料） | 13 | 13 | 0 | 0 |
| `corpus/lang`（差分语料） | 15 | 15 | 0 | 0 |
| `corpus/smart-turn`（差分语料） | 13 | 13 | 0 | 0 |
| `corpus/latency`（差分语料） | 16 | 16 | 0 | 0 |

单元测试耗时约 12 s（含阻塞式等待用例，它们会真的等一小段宽限——`ToolFollowupAccountingTests.Stt_RecordsVadSettleWhenTheGateActuallyWaited` 断言闸门真的挡了 ≥0.1 s，机器负载高时偶发失败，单跑必过，属已知的时序敏感用例）；差分语料需先构建 `tools/TextProbe` / `tools/ProtocolProbe` / `tools/TurnsProbe` / `tools/LanguageProbe` / `tools/SmartTurnProbe` / `tools/LatencyProbe`，比对在百毫秒级。六类都不依赖模型文件、网络与音频设备——所以能进 CI。`corpus/lang` 是唯一带原生依赖的一组（lingua 的 Rust 库），但只在探针进程内加载一次，不需要任何模型下载；`corpus/smart-turn` 用脚本化判定器，不需要 ONNX 模型；`corpus/latency` 的时间全部由语料给定，不读时钟。

跳过项说明：**无跳过项**。但有 3 类「未覆盖」不等于「跳过」，单列在此以免混淆：

| 测试 | 跳过原因 | 风险 |
|---|---|---|
| `local` 全链路 | 客户端参数族、`session.update` 协议面与真跑已闭合（D035，2026-10-28，见「talk 客户端真跑」一节）；`local` 与 `talk` 共用同一 `AudioClient` | 已收敛——与 `serve` 真跑共享同一套管线，剩听的只有真人说话的听感 |
| `talk` 端到端 | 2026-10-28 真跑已走完握手与转写链路（见下）；剩下的是真实设备听感（音色、全双工回声） | 低——仅 Windows 实测 |
| 上游 2,082 例中的后端专属部分 | 不移植（见 `01-baseline.md` §1.2） | 低——统一契约已锁 |

## 公开 API 审计

- 参照实现导出数：`src/` 对外暴露的是 CLI 与模块级函数，无稳定 API 契约（Python 项目常态）。故本项不按「函数逐个对齐」验收，按**语义接缝对齐**验收。
- 本实现公开表面：**112 个类型 / 7 个程序集**

| 程序集 | 类型数 | 角色 |
|---|---|---|
| `SpeechToSpeech.Core` | 89 | 消息契约、Handler 基类、取消代际、运行时配置、后端接口、文本/语言/音频工具、线路 JSON 编码、端点判定决策层 |
| `SpeechToSpeech.Realtime` | 9 | 协议事件工厂、连接循环、Kestrel 宿主 |
| `SpeechToSpeech.Backends.ManySpeech` | 5 | VAD / STT 适配 |
| `SpeechToSpeech.Backends.OpenAiCompatible` | 4 | 远程 STT / LLM / TTS |
| `SpeechToSpeech.Backends.TensorSharp` | 2 | 本地 LLM |
| `SpeechToSpeech.Backends.QwenTts` | 2 | 本地 TTS |
| `SpeechToSpeech.Backends.Lingua` | 1 | 文本语言识别（lingua 原生库） |
| （CLI） | — | 不作公开表面，仅入口 |

- 签名差异 / 运行时差异 / 平台限制：见 `02-differences.md`（74 条，编号 C001–C037、D001–D035、E001–E003；resolved 58 / accepted 16 / gap 0）

## 协议锁定

- [x] 关键协议字段名与顺序由测试锁定 —— `ProtocolShapeTests` + `AssistantItemLifecycleTests` 共 25 例覆盖 24 类服务端事件、`event_id` 必带、错误嵌套结构、`response.done` 四终态、`response` 载荷完整性（usage / output_modalities / voice / conversation_id / output）、created 与 done 载荷同构、转写完成的必填 `usage`、助手消息项完整生命周期；另有 `corpus/protocol` **43 条**差分语料与上游逐字段比对（含编码，到 BMP 外的字符为止），锁定的是「与参照实现一致」而非仅「自证一致」
- [x] 服务端事件覆盖 26/26（100%）—— `python tools/refprobe/protocol_coverage.py` 可复跑。扩展事件 `speech_to_speech.output_audio.visemes` 于 D032 补上（开启 `--enable_visemes` 时发）。原先缺的 9 类（含官方客户端渲染入口 `output_item.added` / `content_part.added`）已补齐并进语料
- [x] 跨语言数字范围已校验（±2^53−1）—— `ServerEvent` 的计数/索引类参数均为 `int`（`audio_start_ms` / `audio_end_ms` / `output_index`），不出 `long`；延迟统计用秒级 `double`，只留在 `TurnLatencyStore` 内部。**唯一带 `double` 上线的是 D032 的口型事件**（`visemes[].start_s/end_s`），值域 [0, 响应音频时长]、量化到 10 ms，与上游 float 载荷同形，远离 2^53，不存在精度越界
- [x] 编码与行尾归一化策略已确定 —— 出线 UTF-8 文本帧 + 紧凑 JSON（`WriteIndented = false`，`separators` 效果等价于上游的 `,`/`:` 无空格），**不含换行**；`Encoder = UnsafeRelaxedJsonEscaping` 保证中文与 base64 的 `+` `/` 不被转义成 `\uXXXX`，与上游 Starlette `send_json`（`ensure_ascii=False`）一致（C012，已验证）。入线同解码，`input_audio_buffer.append` 走 base64 二进制而非文本

## 轮次状态机锁定

打断与续说这一层没有线路形状可比，比的是**状态迁移**：给定一串操作，两侧的裁决（accept / hold / drop）、轮次标识、期限是否逐步相同。

- [x] 状态机逐方法对齐 —— `SpeculativeTurnTracker` 与上游同形：`Gate` / `WaitForGate` / `SpeechStarted` / `SpeechCandidateStarted` / `SegmentFinalized` / `Close` / `BeginReopenCandidate` / `ConfirmReopenCandidate` / `StartReopenGrace` / `ProcessingDeadline`，共 20 个操作可脚本驱动
- [x] 时钟可注入 —— 上游所有期限都是 `time.monotonic()` 上的绝对时刻。本实现改为 `Func<double>`，探针注入虚拟时钟；**不注入就无法确定性比对**，用真实时钟两侧数字必然不同
- [x] 三条不变量各有语料守着 —— 挂起不等于丢弃（`hold-not-drop`）、已提交的不可挂起（`committed-never-held`）、复开不推进序号（`revision-supersede`）
- [x] 浮点不做容差 —— 所有期限输出 6 位小数的字符串，比较退化为字符串相等。**有容差就等于允许漂移**
- [x] 压不住的部分改由单测锁 —— 阻塞式等待（`WaitForGate`）会让探针真的睡在期限上，改用 7 条用例锁「等待者怎么被叫醒」：被取代的修订立刻返回 false、宽限到期回来、等待时长报给观察者
- [x] 接合点已接上并锁住 —— 状态机对了不等于用得对。三处调用各有测试：开口先 `SpeechCandidateStarted` 再 `SpeechStarted`（少了前一步，换气时的输出会抢在判定前发出）、定稿调 `SegmentFinalized`、空段调 `SegmentDiscarded`；响应收口与取消两条路径都 `Close`（不释放提交态，这一轮会永远停在 ANSWERING）。见 `VadTurnWiringTests` 6 例
- [x] 复开窗口取 `max(宽限, 未应答上限)` —— 照上游口径；宽限比上限长时窗口跟着变宽，否则宽限还没过就判成不能复开

## 端点判定锁定

这一层要回答的是「用户说到一半停顿时，输出到底等多久」。上游的答案来自一个 ONNX 分类器，
所以它天然不可差分——除非把「判定」与「拿到判定之后怎么办」拆开。拆开之后后者可以比：

- [x] 四条分支逐条对齐 —— `TurnEndTiming.Decide`：没有判定器 / 抛异常 / 说完了 → 宽限取投机窗口且不推迟；
  没说完 → 宽限取 `smart_turn_max_wait_ms`、处理延迟取 `min(incomplete_delay_ms, max_wait_ms)`。
  状态字符串与上游 `_last_smart_turn_status` 逐字相同，将来接 `response.metadata` 的 `smart_status` 不必再翻译
- [x] 阈值是严格大于 —— `probability == threshold` 算**没说完**。差一个等号，宽限就从 2000 掉到 800
- [x] 两个期限独立 —— 宽限与处理延迟分别传给 `SegmentFinalized`。合成一个期限会让「VAD 慢」与
  「用户可能续说」互相覆盖：宽限没变则回复提前放行，延迟没变则 STT/LLM 白跑一次随后被作废的修订
- [x] 判定看整轮而不是最后一段 —— 用户分两次说完同一句时，第二次的判定输入是累积音频。
  单测用「第二次调用拿到 5 个样本而不是 3 个」锁住；只在开新轮时清空累积（复开是同轮的下一个修订）
- [x] 复开窗口含 `smart_turn_max_wait_ms` —— 上游把这一项也算进可复开上限。漏掉的话，判定刚说完
  「没说完」，窗口却已经关了，用户接着说的那几个字会被判成新的一轮
- [x] 非法配置在构造期拒绝 —— `MaxWait` 必须为正、`IncompleteDelay` 不得为负。0 会让「没说完」的
  宽限比「说完了」还短，语义倒置
- [x] 窗口整理有语料 —— 取尾部 8 秒、短了在前面补零，与上游 `_prepare_audio` 按 float32 位模式逐位比对。
  取头会让说得久的用户永远判不出说完；往后补会把整句挪到时间轴开头
- [x] **判定器本身**（D018，2026-10-26 补齐）—— `SpeechToSpeech.Backends.SmartTurn`：
  `Fft400`（400 = 16×25 两级 Cooley–Tukey，float64）+ Whisper log-mel 前端（周期 Hann、
  center 反射填充、slaney mel 80 维、log10、丢尾帧、max-8 钳制、(x+4)/4）+
  ONNX Runtime 1.31.0 推理。对齐方式：FFT / mel / 全量 64000 个特征值由参照实现
  （transformers 5.20.2 numpy 路径）录制 fixture 逐值比对（max diff 8.6e-7），
  端到端概率与参照链路差 <1e-4；真跑 `smart_status=complete` 与参照判定一致。
  缺模型仍走 `disabled` 分支并打印启动告警（上游缺模型直接启动失败，此差异保留为告警）。
  **ORT 版本敏感**：同模型同特征，1.27.0 与 1.31.0 的概率差 0.12——包版本必须与参照录制环境一致

## 时延账锁定

这一层要回答的是「这一轮到底慢在哪」。上游把它拆成八个字段，其中三项容易理解错：

- [x] 账本的形状按 `response_key` 而不是轮次 —— 同一轮里工具调用与它的口头回答是两条响应，
  按轮次记会把两者的 `llm_s` 挤进同一个格子。待定轮次槽位只服务「STT 早于响应键存在」这一个场景
- [x] 三项不是加总分解 —— 各阶段互相重叠；`e2e_s` 在一条链上只取一次（名额由 `SpeechLatencyAnchor`
  消费，用完即失效）；`hold_s` 记的是等待区间的**并集**，四个 worker 同时等 0.2 秒是 0.2 秒
- [x] 挂起 ≠ 丢弃 —— 最终转写跑完时挂在待定槽位上，响应建立时 `absorb_pending` 并进来。
  就地丢弃会让 `stt_s` 永久为 null
- [x] 记账不复活 —— `pop` / `discard_response` / `clear_session` 之后同一条键再查必须为空。
  worker 只查不建，否则被取消的 worker 会给一条没人要的响应留下永远不结算的账
- [x] 会话清理有作用域 —— 只摘自己那条会话的在途测量；最后一个会话摘完才顺带清待定槽位
  （待定槽位不带会话标记，这是单会话管线模型下的近似，与上游一致）
- [x] metadata 里的字符串逐字符对齐 —— 走 `Utf8JsonWriter` 手写而不是产品序列化入口，
  因为要匹配 `json.dumps(sort_keys=True, separators=(",",":"))`：键按字母序、整值写成 `2.0` 而不是 `2`、
  小指数写成 `1e-07` 而不是 `1E-07`。**metadata 里存的就是这段字符串，数字相等不等于对客户端相等**
- [x] 合并规则五条是纯函数 —— 客户端键全保留且键序不动；两个保留键客户端说了不算；
  终态才挂服务端测量；16 槽位与 512 字符两个上限都按「不删客户端键」处理；空结果发 null 不发空对象
- [x] 日志走 stderr —— 上游 `logging` 的默认 handler 就是 stderr，stdout 留给 `local`/`talk` 的转写。
  写错地方会让重定向输出的人分不清哪行是日志
- [x] 四项测量点已接线（D019）—— `stt_s` 只量转写本身；`vad_decision_s` 从「最后一个有声窗」
  反推（段尾含闭合静音，拿段尾算会把延迟算成 0）；`smart_status` 与宽限/处理延迟成组写；
  `hold_s` 由等待观察者折算成区间并集
- [x] 宽限与处理延迟真的挡住工作（D021）—— 这是本轮查出的更大缺口：状态机的判定原先没有任何
  阶段去等，陈旧轮次的文字照样说出口。输出侧阻塞等闸门、输入侧按 `ProcessingDeadline` 等
- [x] `vad_settle_s` 已记（D022 **已解决 2026-10-26**）—— 整段输入闸门的等待单独记账，不要与 `hold_s` 混为一谈
- [x] 工具输入记账已接（D020 **已解决 2026-10-26**）—— `s2s_tool_input_call_ids` 会发（客户端伪造的会被删掉），
  且 `pending_function_calls` 参与的 anchor 关闭规则一并接上

## 语言层锁定

助手输出语言这一层可以真差分：`detect_language_from_text` 是纯函数，检测器换成 C# 侧的同源实现后，
两侧跑同一串文本就能逐值比。这也是本项目第一次「同一个算法的两种语言实现互证」。

- [x] 决策层与检测器解耦 —— 长度门槛（20 码点）、含 CJK/假名/谚文时放宽到 4、置信度差 0.1、`nb`→`no` 归一化，全部在 `LanguageDetection` 里，与用哪个检测器无关。好处是这层能被 25 例单测覆盖，不必依赖原生库
- [x] 检测器同源 —— 上游 `lingua-py`，本实现 `Panlingo.LanguageIdentification.Lingua`（lingua 的 Rust 实现的 .NET 封装）。15 条语料（英/中/日/韩/俄/德/法/西/挪威、模糊短句、纯数字、纯 emoji、中英混排、分片累积、256 窗口截断）**逐值一致**，包括 `windows` 数组
- [x] 窗口规则单独锁 —— `corpus/lang` 输出每片观察后的窗口，而不只输出结论：结论只有「判出/没判出」两种，窗口错了可能碰巧结论一样，到长回复才暴露
- [x] 浮点不做容差 —— 置信度差用二进制可精确表示的值做边界用例（0.75/0.5）；`0.6-0.5` 在双精度下是 0.0999…98，两侧同样掉到阈值下，属于必须一致的行为而非缺陷
- [x] 解析「一轮只一次」 —— `ResponseLanguageResolver` 锁住：同一响应内首片决定 TTS 语言，后续分片不改。否则流式输出里语言一波动，同一段回复就换音色
- [x] 开关语义照上游 —— `--detect_llm_output_language` 关掉时**仍观察**并写回 `last_assistant_language`，只是不拿它选本轮语言。照「关掉就什么都不做」实现会连带改变下一轮行为
- [x] 原生库缺失不静默降级 —— 抛 `BackendUnavailableException`（与 E003 同一策略）。静默返回「判不出」会让音色选错却查不出原因

## 端到端

- [x] **真跑一轮**（`serve` + 脚本化客户端）—— 2026-10-26 首次跑通。模型：Silero VAD v5（ModelScope `manyeyes/silero-vad-v5-onnx`）、
      Whisper tiny ONNX、Qwen3.5-0.8B Q4_K_M（TensorSharp）、Qwen3-TTS 0.6B ONNX。客户端是 `tools/smoke/serve_smoke.py`：
      把一段 6 秒 16 kHz 语音按 24 kHz / 20 ms 推上去（尾部补 1.5 秒静音），逐事件打相对时间线。
      实测时间线（单轮，首轮含模型加载）：

      ```
      +  568ms  input_audio_buffer.speech_started
      + 6266ms  speech_stopped / committed / conversation.item.created
      + 8015ms  conversation.item.input_audio_transcription.completed
      + 8020ms  response.created
      +11181ms  response.output_item.added → content_part.added → output_audio_transcript.delta×7
      +13021ms  content_part.done → output_item.done
      +13023ms  response.done  status=completed output_items=1
                metadata: stt_s=1.738 llm_s=4.999 vad_decision_s=0.0688 smart_status=disabled source=speech
      ```

      三个数字都对得上口径：`stt_s` 只含转写本身（不含闸门等待）、`llm_s` 按 response_key 记、`vad_decision_s` 从最后一个有声窗反推。
      该轮 TTS 模型未就位，`e2e_s` / `tts_ttfa_s` 为 null（合成失败被记录成失败、响应照常收口）——这是「失败是数据」的应有表现。
- [x] **真跑 TTS 完整收口**（2026-10-26）—— Qwen3-TTS 模型（5.9 GB）就位后补跑。`response.done status=completed`，
      metadata 全账落地：`e2e_s=47.55 tts_ttfa_s=43.31 stt_s=1.76 llm_s=3.42 vad_decision_s=0.186 smart_status=complete hold_s=0.0`。
      首跑抓出 C025（`e2e_s` 锚链在 `TtsInput.Create` 断裂）并修复；`tts_ttfa_s` 与 `e2e_s` 是不同的量（首包 vs 首包-语音结束），各自独立记账。
      **注意**：ElBruno.QwenTTS 的 ONNX 管线在 CPU 上逐句合成 15–190 s/句，是后端运行时性能（上游用 faster-qwen3-tts/mlx-audio），不是行为等价问题（D001）。
      冒烟脚本新增 `--instructions` 与 `--max-output-tokens`（session.update 覆盖），用于控制回复长度。
- [x] **脚本化客户端可复现** —— 推流按真实时长走（一次性灌完会让 VAD 看到的时间轴与实际不符），尾部必须补静音，
      否则 VAD 永远不知道用户说完了（第一次跑就卡在 `speech_started` 之后）。
- [x] **后端冒烟探针** —— `tools/VadProbe`：默认走适配层；`--offline` 用整段模式对照（分清「模型问题」还是「我们的用法问题」）；
      `--online-raw` 直连库并打印每块的 `IsFinished` 与段数；`--online-renew` 验证「每段换新流」（C023 的修复依据）。
      C020、C023 都是靠它定位的。
- [x] **真跑同连接第二轮**（2026-10-26）—— `--speak-twice --gap 25`：第一轮完整收口（`response.done status=completed`）约 25 秒后再推第二段。
      修复前（C023）第二轮 `speech_started` 有、`speech_stopped` 永远不来，整句丢失；修复后两轮各自完整走完
      `speech_started → speech_stopped → transcription → response.done`，第二轮 `turn_2 rev=0`，时延账各自独立记账。
      期间顺手证伪了一个直觉：第二轮丢失与投机轮次状态机（`_committed` 阻断复开）**无关**——状态机与上游一致，根因在 VAD 后端流死了。
- [x] **真跑打断**（2026-10-26）—— `--speak-twice --gap 3`：第二段在第 1 轮响应进行中推入，触发 `response.cancel`。
      修复前（C024）第一轮的取消把整条连接的丢弃保护永久打开，第二轮响应只有 `response.created` 与空的 `response.done`；
      修复后第二轮完整出转写、分片与终态，被取消的第一轮照常发 done（`status=cancelled`）并回滚历史。
      **2026-10-27 真权重下复验**：桩后端时代 TTS 毫秒返回，打断窗口只有几毫秒，几乎打不着；真权重下一句话合成
      30–104 s，第二句必然落在音频流中段，这才是打断的真实工况。同连接两轮各自收口（`cancelled` / `completed`），
      顺带第一次量出 D001 的代价。详见「打断与同连接两轮」小节
- [x] **四后端真权重端到端**（2026-10-27）—— VAD / ASR / TTS / Smart Turn 全部用真实权重跑通，
      `smart_status=complete` 首次成立（此前都是缺模型的 `disabled` 降级）。见「真实模型端到端」小节
- [x] **容器内**（E002 **已解决**）—— `Dockerfile`（多阶段：`sdk:10.0` restore/publish → `aspnet:10.0`，EXPOSE 8765，模型卷 `/models`）+ `.dockerignore` + `docker-compose.yml`。
      实跑：镜像 1.14 GB 构建成功、容器内 `serve` 干净启动、宿主冒烟客户端经容器协议层打到 `host.docker.internal` 上的 llama-server，`tool_call_smoke` 与 `error_shape_smoke` 双绿。
      实跑中修掉两处打包缺陷：`ENTRYPOINT` 用错程序集名（`AssemblyName` 是 `speech-to-speech`）；compose 的 `llm_base_url` 带 `/v1` 后缀会拼成 `/v1/v1/chat/completions`。
      NuGet 发布不在范围（行为等价重写，无对外包面）
- [ ] **跨平台**（Windows / Linux / macOS）—— 代码路径齐备：`serve` 纯托管代码，理论跨平台；`talk`/`local` 音频后端三平台均接入——Windows（WASAPI + WinMM）、Linux（ALSA）、macOS（PortAudio，PortAudioSharp2 1.0.6，E001 已解决）。Windows 已实跑设备创建；**Linux 与 macOS 未在真实设备上跑过**（绑定冒烟 3 例通过）
- [ ] **带真实音频设备的一轮对话** —— 参数族、协议面与真跑握手已闭合（D035，2026-10-28）：`serve` + `talk --print-json` 真跑走完麦克风 16k 直通上行 → VAD commit（`audio_end_ms=4736`）→ 转写完成 → `response.created` → transcript delta，全程无 error 事件。**仍未做的只剩真人对着麦克风说话的听感验证**
- [x] **跨传输** —— WebSocket 与 WebRTC 双通路（D003 **已解决 2026-10-27**）。`SessionTransport` 抽象分出
      `send_events` / `send_audio_chunk` / `discard_pending_audio` / `close` 四面，WebSocket 承接原 `socket` 路径；
      WebRTC 侧 RTP 音频走 SIPSorcery `WebRtcSession`（Opus 48 kHz，Concentus 编解码），事件走 `oai-events` datachannel，
      HTTP 面 `POST/DELETE /v1/realtime/calls` 已接（415/503 `session_limit_reached`/500/400/201+Location/404 逐字对齐）。
      音频节拍与上游 `PipelineAudioTrack.recv()` 逐行对齐（首帧即时、RFC 7587 §3.1.3 整帧抑制、追赶用 ceil、
      `waited_for_audio` 判据、5 帧编码器排空、重采样绝对输出游标使分块喂入等价于整块）。
      验证：pacer/resampler 表征单测 10、门控 6、registry+503 体 9、SIPSorcery 进程内环回 5（真 HTTP + 真 SDP/ICE/DTLS/datachannel），
      以及 `tools/smoke/webrtc_smoke.py` 用 aiortc 1.15 跨实现互证 PASS（415 / 201 / 连接 / `session.created` / append 门控 / DELETE）。
      会话 id 也已对齐（C029 **已解决 2026-10-27**）：统一为 `session_<32hex>`，同一个值贯穿
      `session.created`/`session.updated` 的 `session.id`、`Location` 头、DELETE 路径参数。
      **2026-10-27 复跑确认链路此刻可用**（不只是「代码在位」）：`serve --port 8791` + `webrtc_smoke.py` 五项全绿，
      ICE/DTLS 真的连上（`pc state: connected` → `closed`），`call_id=session_822b7e48…` 走完 201 → datachannel
      `session.created` → append 门控 → DELETE 200 → 重复 DELETE 404 整条线

## 未闭合风险

| 风险 | 影响 | 处置 |
|---|---|---|
| 音频层差分语料为空 | 模型推理受浮点累加顺序影响，不可逐字节比对 | 改锁采样率/声道/位深/段数/时长误差带，不比样本值 |
| 有声窗位置由能量门限判定（D019 / D002） | `vad_decision_s` 的锚点是「最后一个有声窗」，本实现按块能量判有声，上游按 Silero 概率判。噪声底高的环境里两者会差一两个窗（约 32–64 ms） | 粒度与上游一个 VAD 窗同量级；缺口比「用段尾」小一个数量级——段尾含闭合静音，会把延迟整段抹成 0 |
| `vad_settle_s` 的判定依赖 `Mode="final"` | 本实现定稿段都带 `final`，口径与上游一致；progressive 转写路径已由 D005 补齐（流式后端边收边出 partial） | 低——progressive 真正打开后需按 streaming 路径复核 `vad_settle_s` 的分段记账，留给真实流式后端联调一并做 |
| 函数调用未进 `response.output`（D023，**已解决 2026-10-27**） | 输出项状态机与 `ResponseOutput.Merge` 合并排序已就位，协议语料 39/39、真跑回归通过；**未经真实工具链路验证**——本实现的测试 LLM（qwen35）没有稳定的工具调用产出，`AssistantToolCallReadyEvent` 端到端只由假后端走过 | 需要一个会发工具调用的 LLM 后端（如 OpenAI 兼容端）跑一轮真链路 |
| ~~带外响应未实现（D024）~~ **已解决 2026-10-27** | 侧问会污染会话历史 | 四处一起补齐：metadata 不挂工具输入、不建时延账、输出不写回历史（哨兵词+item.delete 双证）、`conversation_id` 报 null；协议语料 40/40，真跑 `oob_leak_probe.py` 闭环 |
| 每响应音色覆盖未实测（D017 已补） | 代码路径已打通（`ResolveVoice` → `TtsInput.Voice` → 后端 `Speaker`），但只由假后端验证过「值确实传到了」 | 低——真实 Qwen3-TTS 对不支持的音色会忽略，这层校验在模型侧；缺的是端到端听感验证 |
| `local` / `talk` 听感实跑 | 客户端参数族与协议面已对齐（D035 resolved 2026-10-28），`talk --print-json` 真跑走完 VAD commit → 转写 → response 创建；剩下的是真实全双工声学场景（回声、设备延迟） | 带麦克风/扬声器说一轮，听感验证 TTS 音色（顺带闭合 D017 的端到端验证） |
| 接合点的时序与真实后端的交互 | `VadHandler` 与真实 VAD 后端的两轮/打断交互已用脚本化客户端实跑（C023/C024 由此暴露并修复）；剩余的是真实麦克风/扬声器下的全双工行为（回声、半双工时序） | 低——协议与管线层面已闭合，剩下的是声学环境问题，不影响行为等价判定 |
| Smart Turn 判定的「incomplete」路径未在真实音频上触发 | 判定器概率已与参照链路逐值对齐（合成音频 <1e-4），但手头的测试音频截断处恰好语义完整，参照侧也判 complete | 低——incomplete 之后的决策（推迟 600ms、max_wait 2000ms、复开）由 `corpus/smart-turn` 13 条语料与 `TurnEndTiming` 单测锁定；真实对话中自然会出现 |
| 实时转写（D005 **已解决**） | `openai-realtime` / `vllm-realtime` 两个 STT 后端已实现——wire dialect（六方法 + 端点 URL/setup 校验逐条对齐）、worker 的命令队列/代际/毒轮/partial 发布/握手、VAD 侧 `StartTurn`/`AppendAudio`/`CommitBoundary`/`DiscardUtterance` 全部接线，`StreamingSttProtocolTests` 28 + `StreamingSttSessionTests` 14 + `StreamingSttBackendTests` 5 | 剩下的是**真实服务端联调**（手头没有这两家的流式端点，账本只覆盖到 fake WS 表征）；失败仍走 `BackendUnavailableException`，不静默降级 |
| 音频后端跨平台不全（**E001 已解决 2026-10-27**） | macOS 已接入 PortAudio（PortAudioSharp2 1.0.6，与上游 sounddevice 同引擎）；Linux ALSA 路径仍未实跑 | Windows 已实跑设备创建；Linux 需在装有 `libasound.so.2` 的机器上验证；macOS 需真机跑一轮 `talk`（发布时带 osx-x64/osx-arm64 原生库） |
| 语言识别的原生库覆盖不全（D006 已补） | lingua 原生库只有 win-x64 / linux-x64 / osx-arm64；其余平台（Linux arm64、macOS x64）该后端抛 `BackendUnavailableException` | 与 E001 同一类平台缺口，不静默降级；补包即可，不影响决策层（决策层与检测器解耦，已有 25 例单测） |
| 语言识别结果未在真实对话上验证 | `corpus/lang` 证明与上游逐值一致，但那只覆盖决策层；「TTS 是否真的换了发音」需要听 | 低——参数已按上游口径接到 `TtsRequest.Language`，剩下是后端行为 |
| ~~无容器镜像（E002）~~ **已解决** | 曾有「无法在服务端标准部署」的风险 | `Dockerfile` + `docker-compose.yml` 已入库并实跑（容器内 `serve` + 宿主客户端全链 PASS）；NuGet 发布仍不在范围 |
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

# 2b. 账本状态与文档措辞一致性——状态翻了 resolved，叙述层不能还写着「未实现」
#     一个状态翻掉的条目会牵动四本账的叙述，靠人读不成立；2026-10-27 一次查出 10 处
python tools/ledger/check_doc_sync.py --strict

# 3. 构建与测试
dotnet build SpeechToSpeech.slnx --no-incremental
dotnet test SpeechToSpeech.slnx

# 4. 差分语料（文本层 / 协议层 / 轮次层）
python <skill>/scripts/diff_corpus.py verify --corpus corpus/text --json-canonical \
  --cmd "dotnet tools/TextProbe/bin/Debug/net10.0/TextProbe.dll"

python <skill>/scripts/diff_corpus.py verify --corpus corpus/protocol --json-canonical \
  --cmd "dotnet tools/ProtocolProbe/bin/Debug/net10.0/ProtocolProbe.dll"

python <skill>/scripts/diff_corpus.py verify --corpus corpus/lang --json-canonical \
  --cmd "dotnet tools/LanguageProbe/bin/Debug/net10.0/LanguageProbe.dll"

python <skill>/scripts/diff_corpus.py verify --corpus corpus/turns --json-canonical \
  --cmd "dotnet tools/TurnsProbe/bin/Debug/net10.0/TurnsProbe.dll"

python <skill>/scripts/diff_corpus.py verify --corpus corpus/smart-turn --json-canonical \
  --cmd "dotnet tools/SmartTurnProbe/bin/Debug/net10.0/SmartTurnProbe.dll"

python <skill>/scripts/diff_corpus.py verify --corpus corpus/latency --json-canonical \
  --cmd "dotnet tools/LatencyProbe/bin/Debug/net10.0/LatencyProbe.dll"

# 5. 协议覆盖审计（与上游 26 种事件比对，当前 26/26）
python tools/refprobe/protocol_coverage.py
```

重录基线只在参照实现或上游文本层变动时做，且**先逐条看差异再决策**，不要一把 `--update` 全过。
录制环境（nltk / punkt_tab 版本）见 `corpus/text/README.md`。

**为什么要有 2b**：账本翻状态时，叙述层常常只改了「账本」那一处，四本账里的正文没跟上。2026-10-27 的
巡检（由「WebRTC 未移植（D003）」这句话引起）一次查出 **10 处**：WebRTC 与 `Dockerfile` 明明已经落地、<!-- doc-sync: ignore -->
两个流式 STT 后端已有 47 例测试，文档却分别写着「未移植」和「不可用」。两个方向都危险——正向让人
低估实现（按文档判断「不支持」，实际能用），反向让人高估实现（按文档判断「已解决」，实际仍是缺口）。
脚本按差异 ID 关联行内措辞；**历史叙述**（「当时写成 X，后来改掉」这类必须保留原话的句子）在该行末尾
加 `<!-- doc-sync: ignore -->` 豁免。

**结论**：构建、单元、文本层、协议形状层、轮次状态机层与语言层验收通过；端到端经 `serve` + 脚本化客户端实跑闭合——单轮、同连接第二轮（gap 25 s）、打断（gap 3 s）与 TTS 音频完整收口四个场景都完整走通，队列时序、时延账（含 `tts_ttfa_s` / `e2e_s`）与投机轮次在真实模型链上得到验证；Smart Turn 判定器（D018）已补齐并经 fixture 逐值对齐 + 真跑验证；函数调用的输出项生命周期（D023）已补齐并经协议语料与真跑回归。本实现可作为「四段管线 + Realtime 协议 + 投机轮次 + Smart Turn + TTS + 工具调用输出项 + 带外响应」的可运行基线。剩余空白集中在真实音频设备的 `local`/`talk` 听感验证。工具调用链已闭环：chat-completions 后端补上 delta.tool_calls 流式聚合（D028），真实 LLM 端到端以脚本化 mock 服务验证全链 PASS——后续接任何 OpenAI 兼容服务（llama.cpp server / vLLM）即为真实模型端到端。error 事件的 make_error 类形状归位（D027）已解决——错误事件双构造线（make_error 的 error={message,type} vs schema 校验的 invalid_request_error+code）分流完毕，`send_correlated` 的 error.event_id 关联全量接上；空历史口径（D026）、instructions 回落（C027）与 `response.create` 的 `input` 数组（D025）此前已解决。至此 response.create 的全部入参语义（instructions/temperature/max_output_tokens/modalities/voice/conversation/input）与错误面都与上游对齐。

协议层已从「客户端可能看不到回复」补到 26/26 覆盖（最后一格 visemes 由 D032 补齐）：消息项生命周期（`output_item.added` →
`content_part.added` → delta → `content_part.done` → `output_item.done`）、提交确认、会话项删除、
转写失败的 item 作用域通告都已到位，并有 43 条差分语料与 27 条形状断言双重锁定。
终态全部收口：取消回 `response.done`（status=cancelled）、`status_details` 对**任何终态都发**
（原先只在异常时发，completed 完全不带，见 C015/C016）、`status_details.type` 与 status 同域、
`failed` 的 `error` 只带 `type`。
`response` 载荷也已补齐（D012）：`usage` / `output_modalities` / `audio.output.voice` /
`conversation_id` / `output`，created 与 done 由同一个 builder 产出，字段集不会漂。
token 记账按 `response_key` 挂起、收口结转、已关闭 key 丢弃迟到用量——被丢弃的投机轮次不会
把账算到活的那一轮头上（`ResponseUsageLedger`，9 条用例）。
`transcription.completed` 的必填 `usage` 已补齐（D011，原先严格客户端会整条拒收）。
线路编码也修到与 Python 的 `json.dumps(ensure_ascii=False)` 逐字节一致：BMP 外的字符（emoji）
原样输出而不是代理对转义（C017）；另外纯文本响应不再白跑一次合成模型（C018）。

逐响应音色覆盖（D017）已打通到合成器：`response.create` 里的 `audio.output.voice` 现在
既进载荷也进 `TtsRequest.Speaker`，两处共用同一个 `ResolveVoice`（响应级 → 会话级）判断。
刻意不做「只改载荷」——那种改法字段对、声音错，只能靠耳朵发现。

轮次状态机这一层本轮补齐：上游 488 行的 `SpeculativeTurnTracker` 原样移植（C006），
并首次引入**虚拟时钟差分比对**——13 条脚本化语料逐步比裁决、轮次标识与期限，浮点不做容差。
这是本项目第一个「比状态迁移而非比字节」的语料层，做法写在 `corpus/turns/README.md`。
接合点也一并接上：VAD 的三处调用、响应收口与取消两条路径的 `Close`，由 6 条用例锁住。
端点判定这一层本轮补齐（D015）：上游 `_smart_turn_timing_ms` 的四条分支原样移植
（说完了 → 投机窗口且不推迟；没说完 → `max_wait` 且推迟 `min(delay, max_wait)`；
抛异常 / 无判定器 → 退回投机窗口），
并首次把「判定」与「判定之后怎么办」拆开——后者是纯函数，因此 13 条语料能在没有 ONNX 模型的
机器上逐值比对（`corpus/smart-turn`）。接合点也补了三条容易写错的：判定输入是**本轮累积音频**
而不是最后一段、只在开新轮时清空累积、复开窗口把 `smart_turn_max_wait_ms` 一并纳入取 max。
**判定器本身已补齐**（D018，2026-10-26）：`SpeechToSpeech.Backends.SmartTurn` 手写 Whisper log-mel 前端
（float64，400 点混合基 FFT）+ ONNX Runtime 推理，由参照实现录制 fixture 逐值对齐（全量特征 max diff 8.6e-7），
端到端概率与参照链路差 <1e-4；真跑 `smart_status=complete`、`vad_decision_s≈0.17s`。细节见「端点判定层」小节。

带外响应（D024）、空历史口径（D026）、instructions 回落（C027）、`response.create` 的 `input` 数组（D025）与 error 形状归位（D027）均已于 2026-10-27 解决（各自有单测 + 专用冒烟脚本真跑闭环）。协议面缺口清零，剩余 gap 为已接受的架构性分歧与待 OpenAI 兼容后端的项目。D023 已于 2026-10-27 解决（协议侧），**工具调用的真实 LLM 端到端已于 2026-10-27 补验**（见下）。带 TTS 音频的端到端已于 2026-10-26 补齐（`tts_ttfa_s` / `e2e_s` 落地，C025 修复），最后是真实音频设备的 `local` / `talk` 听感验证。
真跑一轮（`serve` + 脚本化客户端）已于 2026-10-26 完成，单轮时序、两轮与打断三场景见「端到端」小节——它当轮就暴露并修掉了 C020/C021/C022/C023/C024 五个缺陷，验证了「差分语料全绿 ≠ 真机能跑」。

### 真实模型端到端（2026-10-27，四个后端全用真权重）

此前所有端到端记录的 `smart_status` 都是 `disabled`（缺权重时退回投机宽限）。这次 VAD / ASR /
TTS / Smart Turn 四类权重全部就位，第一次让**判定器真的参与端点判定**：

```bash
serve --port 8791 \
  --vad_model_dir <s2s>/vad5 --whisper_model_dir <s2s>/whisper --whisper_language zh \
  --qwen3_model_dir <s2s>/tts --smart_turn_model_path <s2s>/smart-turn/smart-turn-v3.2-cpu.onnx \
  --llm_model_path <s2s>/llm35/Qwen3.5-0.8B-Q4_K_M.gguf
python tools/smoke/serve_smoke.py --wav speech_6s.wav --url ws://127.0.0.1:8791/v1/realtime
```

时间线（相对开播）：

| 时刻 | 事件 |
|---|---|
| +396 ms | `session.created` / `session.updated` |
| +423 ms | `input_audio_buffer.speech_started`（VAD 开口 192 ms） |
| +6276 ms | `speech_stopped` → `committed` → `conversation.item.created` |
| +8314 ms | `transcription.completed` |
| +8314 ms | `response.created` |
| +88326 ms | `output_item.done` → `response.done status=completed`，`output_items=1` |

时延账：`stt_s=1.95 llm_s=3.07 tts_ttfa_s=29.55 e2e_s=34.37 vad_decision_s=0.122 hold_s=0.0
smart_status=complete`。**`smart_status=complete` 是这轮唯一新增的验证面**——其余各项此前都有记录。

真跑当轮暴露并修掉 **D034**：whisper 的 `language` 参数被静默忽略（日志每轮刷
`This tokenizer does not have language token configured`）。参数在、行为不在，属于最难发现的一类差异。

三条环境事实（后端性质，不是行为等价问题）：

- **TensorSharp 不支持 qwen2 架构**：用 `qwen2.5-0.5b` 直接抛 `Unsupported architecture: qwen2`，
  整轮 LLM 空产出（`output_items=0` + `language model produced no output`）。必须用 `qwen35`
  （Qwen3.5-0.8B）——这条已在工作记忆里记过，仍然值得在文档重复一次，因为报错发生在流里而不是启动时。
- CPU 上没有 `full-decode` 融合图，qwen35 走 per-op decode（运行时自己打印「roughly 10x slower」，
  实测 3.1 s/轮）；Qwen3-TTS 首轮 94.8 s（含 5.5 GB 权重加载）、热态 29.6 s/段。
- whisper 这份导出是 **tiny**（4 层 / 384 维 / 80 mel），中文转写质量有限（同一段音频稳定给出
  一串谐音字）。换更大模型只需换 `--whisper_model_dir`，与实现无关。

### 打断与同连接两轮（2026-10-27，真权重实测 D001 的代价）

打断（barge-in）这条路径以前只在**桩后端**上验过，那时 TTS 是毫秒级返回，打断窗口窄得几乎打不着。
真权重下不一样：一句话要合成 30–104 秒，第二句话必然落在第一轮音频流的中段。这才是打断的真实工况。

```bash
# serve 用真实权重起（同上一节），先跑一轮热身把 5.5 GB TTS 载入内存
python tools/smoke/serve_smoke.py --wav speech_6s.wav --url ws://127.0.0.1:8791/v1/realtime --timeout 300
# 再跑两轮，gap=3 让第二句话插进第一轮回答里
python tools/smoke/serve_smoke.py --wav speech_6s.wav --url ws://127.0.0.1:8791/v1/realtime \
  --speak-twice --gap 3 --timeout 900
```

时间线（同一条连接，相对开播）：

| 时刻 | 事件 |
|---|---|
| +211 ms | `session.created` / `session.updated` |
| +367 ms | `speech_started`（VAD 开口 192 ms） |
| +6246 ms | `speech_stopped` → `committed` → `item.created`（turn_1） |
| +8100 ms | `transcription.completed` → `response.created` |
| +9633 ms | `output_item.added`（LLM 文本已定，TTS 还没出音） |
| **+10856 ms** | **`response.done status=cancelled`——第二轮开口的同一毫秒** |
| +10856 ms | `speech_started`（turn_2） |
| +18781 ms | `transcription.completed` → `response.created`（turn_2） |
| +113646 ms | `output_item.added`（等前一轮被废弃的合成跑完，见下） |
| +153598 ms | `output_audio.done` → `response.done status=completed output_items=1` |

两轮的时延账：

| 轮次 | stt | llm | tts_ttfa | e2e | vad_decision | 状态 |
|---|---|---|---|---|---|---|
| turn_1 | 1.82 s | 1.54 s | **n/a** | **n/a** | 0.109 s | cancelled |
| turn_2 | 1.86 s | 1.40 s | 39.96 s | 136.84 s | 0.202 s | completed |

这一轮锁住的六件事：

1. **打断在第二个 `speech_started` 的同一毫秒收口**，且 `response.done` 先于 `speech_started` 发出
   （`EmitAsync` 里先 `HandleResponseCancelAsync()` 再发事件）——客户端先知道「上一轮作废」，再知道「你开口了」。
2. **被打断那轮记 `n/a` 而不是 0**。没有音频就没有首包，填 0 等于让时延账凭空变好。
3. **被打断那轮一个 `output_audio.delta` 都没有**：陈旧音频在 `RealtimeConnection.cs:1455` 按代际丢弃。
4. **第二轮在同一条连接、同一个 `conv_` 下收口 `completed`**，`output_items=1`——打断没有把会话搞坏。
5. **两轮 `smart_status` 都是 `complete`**：判定器在打断路径上照常参与，没有退化成 disabled。
6. **被作废那轮的助手文本不入历史**：打断复用 `HandleResponseCancelAsync()`，其中 `RollbackToCommit()`
   撤回提交点之后追加的消息。侧证是第二轮 prompt 长度 273 vs 第一轮 210——差的只有第二条用户话语。

#### D001 的代价，第一次被量出来

同一份日志里能读出被打断那轮的合成并没有停：

```
Turn turn_1 ... status=cancelled
Generated 92 audio frames
TtsHandler: 104.001 s        ← 已被作废的那一轮仍在合成
Generated 47 audio frames
TtsHandler: 39.961 s         ← turn_2 的合成，等它跑完才开始
```

对得上账：turn_2 的 `response.created`（18.8 s）到 `output_item.added`（113.6 s）相隔 **94.8 s**，
而被废弃的合成在打断发生时已经跑了约 9.6 s，104.0 − 9.6 = **94.4 s**，与 94.8 s 相差不到半秒。
换句话说，**第二轮的等待就是第一轮那段白烧的合成**，不是别的原因。

这是 D001（整句合成而非逐帧流式）的结构性代价，不是新缺陷：上游 `_stream` 是生成器，每个 chunk
查一次 `is_stale`，命中即返回，毫秒级收敛；`IsProgressive=false` 的 ONNX 管线一旦开始就只能读完这一句。
CPU 上没法消掉——ONNX 会话不是线程安全的，抢跑只会更慢；GPU 上同样的结构代价约 1 秒量级。
正确性没有受损（陈旧音频被丢弃、历史被回滚、下一轮正常收口），损失的是被作废那一轮的 CPU 与下一轮的等待。

#### 一处已知噪音（不修）

会话在 TTS 合成中途关闭时，日志出现 `RunLoop: TtsHandler: Error in Process(): OperationCanceledException`。
这是正常关停路径（`Stopping` 触发的取消），被 `BaseHandler.RunLoop` 的 `catch (Exception)` 当成错误上报。
不影响协议行为，也不影响任何一条事件；`TranscriptLog` 没有可观测入口，为一行日志加测试缝不划算，故记录不修。

### 容器实跑（2026-10-27，E002 收口）

`docker build` 成镜像（1.14GB）；容器内 serve 干净启动（Smart Turn 模型经 /models 卷加载，
无告警）；宿主冒烟客户端 → 容器化协议层 → host.docker.internal → 宿主 llama-server 全链：
`tool_call_smoke.py` PASS（工具调用全链，二轮正确消化工具结果）+ `error_shape_smoke.py` 4/4。
实跑修掉两处打包缺陷：ENTRYPOINT 程序集名（AssemblyName 是 speech-to-speech）与
compose 的 `llm_base_url` 多余 `/v1` 后缀（会拼成 `/v1/v1/chat/completions`）。

### 工具调用真实模型端到端（2026-10-27，D028 收口）

后端：llama-server b11913（CPU，`--jinja`）+ Qwen2.5-0.5B-Instruct Q4_K_M（hf-mirror）。
链路：serve `--llm_backend chat-completions --chat_completions_base_url http://127.0.0.1:8080`
→ session.tools 序列化 → 真实 LLM 吐 `delta.tool_calls` 分片 → 聚合 → function_call 事件
→ 客户端回填 function_call_output → 二轮真实生成。`tools/smoke/tool_call_smoke.py` 全 PASS：
工具名/参数 JSON/`call_id`（`call_`+27hex）逐项核对，二轮回答正确消化工具结果
（「北京今天天气晴，温度25℃。」）。

这次真跑当轮暴露并修掉两个环境触发的问题：D029（HttpClient 默认读 Windows 系统代理，
上游 httpx 只认环境变量——挂代理的机器上连本机 LLM 都会被隧道化 502）与
C028（连字符后端名拼出的 `chat-completions_base_url` 键匹配不到下划线参数，
BaseUrl 静默落空回落 api.openai.com，错误在远离根因处爆发）。

### 评估套件（2026-10-27，D030 收口）

上游 `evals/big_bench_audio`（Big Bench Audio vibe check）全链移植为 `src/SpeechToSpeech.Evals`
+ `tools/EvalProbe`（run / compare / show / build-subset 四个子命令）。验收分四层：

1. **CPython MT19937 复刻互证（最强证据）**：`PythonRandom` 逐位复刻 CPython `_randommodule`
   （init_by_array 播种、getrandbits 低位拼字、_randbelow Fisher-Yates shuffle）。getrandbits
   参照值由上游 venv CPython 3.13 生成；关键行为学证据是 `build_subset(40, seed=0)` 在与上游
   conftest 同构的 1000 条离线池上，**与上游 Python 抽出的 40 题序列逐位一致**
   （[216, 348, 720, 862, ...]）——同种子跑 main 与换后端 PR 的对比口径从此可跨实现共享。
2. **纯逻辑表证**：scoring 的 marker/scan 两路提取、否定窗口、有效/无效识别与数字词表按上游
   测试逐条参数化移植（含「correct/negative 是普通词不猜」的窄口径）；report 的聚合口径
   （错误与不可解析都记在模型头上）与双报告对比（题集/修订/judge 不一致警告）锁定。
3. **runner 捕获状态机**：fake WS 驱动 split_turn（两个不同 input item）、同 item 复开不算分裂、
   非完成状态 → `response_<status>`、error 事件透传、response_timeout、握手（session.created →
   session.update → session.updated）与 session_limit_reached 容量重试。
4. **CLI 参数面**：redact（无 `=` 的敏感 flag 保留参数名、下一个值脱敏）、`--host/--port` 三种
   写法推导 URL、`--` 分隔、裸 flag 默认 `run`。

账本动作：D009 缩窄为「口型 + 说话人分离」（gap 维持）；新增 D030（评估套件，resolved）、
C031（音频解码/重采样内核差异，accepted）、C032（HF 云工作流 push-to-hub/job/prepare_eval_space
不移植，报告本地 JSON、上游报告文件可被 C# compare 直接读取，accepted）。
差异账 **64 条（resolved 47 / accepted 15 / gap 2）**，gap 仅剩口型/分离（D009）与 macOS 音频（E001）。
测试 **378/378**（+52），全解决方案 0 警告 0 错误。

### macOS 音频后端（2026-10-27，E001 收口）

`talk`/`local` 的音频设备 I/O 补齐第三平台。验收分三层：

1. **同引擎对齐（选型证据）**：上游 `talk` 用 `sounddevice`（`RawInputStream/RawOutputStream`，
   int16 / 单声道 / blocksize 定块）收放音频，其底层是 PortAudio。macOS 路径取
   **PortAudioSharp2 1.0.6**（NuGet 正式版，Apache-2.0；原生库由配套 `org.k2fsa.portaudio.runtime.*`
   按 RID 提供，含 osx-x64 / osx-arm64）——设备枚举、默认设备选择、`framesPerBuffer` ↔
   `blocksize` 语义与上游一致。Windows/Linux 维持 NAudio 后端不动。
2. **抽象接线**：`PortAudioWaveIn` / `PortAudioWavePlayer` 实现 NAudio 的 `IWaveIn` /
   `IWavePlayer`（补齐 `OutputWaveFormat`、`Volume` 软件缩放），`AudioClient` 与
   `AudioDevices.Create()` 无感知切换；回调只拷贝入队（守住 ClientWebSocket 禁并发 Send 的
   约束）；`Pa_Initialize`/`Pa_Terminate` 按官方「配对可重叠」语义逐设备配对。
3. **绑定冒烟（设备无关，3 例）**：原生库随测试宿主解析成功（Windows 上加载 portaudio.dll）、
   版本信息非空、嵌套 Initialize/Terminate 配对后枚举 API 仍可用、设备数可以为 0 不炸。
   真实 macOS 硬件的开流行为留真机验证（与 Linux ALSA 同口径）。

账本动作：E001 翻 resolved。差异账 **64 条（resolved 48 / accepted 15 / gap 1）**，
gap 仅剩口型/分离（D009）。测试 **381/381**（+3），全解决方案 0 警告 0 错误。

### 口型提取引擎（2026-10-27，D031 收口）

D009 的口型部分拆层推进：推理引擎层本轮落地（D031），管线接线与说话人分离留在 D009
（gap 维持，显式缺口不静默）。验收分三层：

1. **跨实现互证（前端数值）**：mel/FFT 参照值由参照环境（CPython 3.13 + NumPy）按 Basis
   config 的前端几何逐项直算——同一确定性信号下，C# 前端与 NumPy 参照的 dB 值逐 bin 一致
   （≤0.05 dB，含近底噪 bin）。关键语义差异被测试钉死：Basis 的 mel 滑动窗是因果对齐
   （prev=240 零起步，窗口 = prev 240 + 新 hop 160），不是 librosa 式居中分帧。
2. **行为学单测**：重采样块边界不变性（任意块边界==整块，位级）与频率保持（过零率）；
   平滑器对常值输入无偏收敛、Reset 回 sil 静息位、α 钳制；multi-label sigmoid 与 softmax
   两种激活；OVR→MS 映射表锁定；帧序列→区间合并（argmax + 相邻同 id 合并 + 偏移）。
3. **ONNX 端到端（模型门控）**：流式不变性——整段喂与任意块边界喂的逐帧概率必须一致
   （TCN 缓存语义）；由 `SPEECH_TO_SPEECH_VISEME_MODEL_DIR` 门控（模型资产 10.7 MB 不入库，
   落 `models/viseme/`，来源与许可见 00-reference 的 Basis 小节）。

账本动作：D009 缩窄为「口型管线接线 + 流式说话人分离」（gap 维持）；新增 D031（口型引擎，
resolved）、C033（口型集合 OVR15→MS21 与产出方式差异，accepted）。
差异账 **66 条（resolved 49 / accepted 16 / gap 1）**，gap 仅剩 D009（接线 + 分离）。
测试 **401/401**（+20），全解决方案 0 警告 0 错误。Core 侧 `Viseme` record 修正为上游形状
`{viseme 0-21, start_s, end_s}`（此前是错误脚手架 `(Value, OffsetSeconds, DurationSeconds)`，
无消费者，安全改形）。

### 口型管线级接线（2026-10-27，D032 收口）

D009 的口型部分收口：引擎（D031）之外，管线级、事件与配置面本轮补齐。验收分四层：

1. **时序语义（与上游逐条对齐）**：0.5 秒整窗一次推理（窗口 = 管线率 × 0.5）、区间时间 =
   窗口内相对时间 + 已推理样本数 / 管线率、窗口音频按 32 ms 等价块（上游 512@16k）重发、
   口型只挂首块、终态事件前先把尾巴冲出来、`cleanup_only` 不冲、上下文（response_key + 代际）
   切换与会话结束都重置偏移、陈旧代际整窗丢弃。以上每条都有单测（`StvHandlerTests` 14 例）。
2. **失败不改音频**：提取抛异常只丢口型，音频照发（上游 `speech_to_visemes` 内部 except 同口径）；
   奇数字节整块丢弃并记日志，线程不死。
3. **线上形状**：`speech_to_speech.output_audio.visemes` 带 `response_id/item_id/output_index/
   content_index=0/visemes[]`，数组元素 `{viseme, start_s, end_s}`，键名 snake_case 由测试锁定；
   顺序是**先事件后音频块**，发完事件重新校验响应仍在（陈旧音频不得借事件把已收口的响应重新打开）。
   协议覆盖审计 `tools/refprobe/protocol_coverage.py` 从 25/26 升到 **26/26 = 100%**。
4. **开关行为**：`--enable_visemes` 默认关；关时管线里根本没有这一级（TTS 直连发送队列，
   发送路径与接线前逐字节相同），`AudioOutput.Visemes` 无生产者、visemes 事件永不发出。
   打开却缺模型时启动即失败并给出明确路径（上游 FileNotFoundError 同口径）。

真跑：`serve --enable_visemes --stv_model_dir models/viseme` 启动正常（模型按连接懒加载）；
引擎级端到端（真实 ONNX 模型下的窗口覆盖与流式不变性）由 `SPEECH_TO_SPEECH_VISEME_MODEL_DIR` 门控。

账本动作：D009 缩窄为「流式说话人分离」（gap 维持，唯一剩余架构性缺口）；D013 从 25/26 更新为
26/26；新增 D032（口型管线级，resolved）、C034（区间密度：上游音素稀疏 vs 本引擎整窗连续，accepted）。
差异账 **68 条（resolved 50 / accepted 17 / gap 1）**，测试 **415/415**（+14），全解决方案 0 警告 0 错误。

### 流式说话人分离（2026-10-27，D009 收口）

参照实现是 voxa 的 `feat/streaming-diarization`（`cc60257`，锁定于
`porting/reference-lock.voxa.json`，工作区干净）。本轮按本仓库的管线形态（`BaseHandler` 线程链 +
`BlockingCollection` 队列）重装，而不是把对方的处理器搬过来。验收分五层：

1. **掩码解码：不要过早压平。** pyannote segmentation-3.0 每帧输出 `num_classes` 个 logits
   （3 说话人、最多 2 并发 ⇒ 1+3+3=7 类 powerset）。旧解码把它压成 speech/silence，丢掉三件事：
   谁、重叠、置信度。现在 softmax 后按 powerset 映射边缘化到本地 speaker，
   `p[s] = Σ_c softmax[c]·mapping[c][s]`。**边缘和故意 ≠ 1**：双人类别把一份概率同时给它那两个
   成员，所以「一帧上两项非零」本身就是重叠的判据。单测钉死四条：silence 类全零、单人类只亮
   一个、`(0,2)` 人类两个都 >0.99、一个单人类加一个双人类各占一半时和是 **1.5**（不是 1），
   宽度不匹配抛 `ArgumentException`。
2. **本地通道语义：身份不能按通道跨窗累积。** 模型的 speaker 通道只在窗口内有效——窗口 A 的
   channel 0 与窗口 B 的 channel 0 不是同一个人。所以把「某本地通道的一段连续帧」当 *local turn*，
   对它那一段音频**取一次声纹**，身份由声纹携带、而不是由通道号携带。单测
   `An_embedding_is_taken_per_turn_not_per_window` 用计数器把这条钉死（否则窗口数一多，
   embedding 调用次数就会露馅）。
3. **增量音频状态：收尾要能回溯。** 定容环形缓冲按**绝对样本索引**寻址（`Oldest/End/Count`、
   `CopyTo(absStart, dest)` 越界零填充、`DiscardBefore`），保留策略
   `DiscardBefore(min(nextWindowStart, SamplesSeen − MaxBufferedSeconds·sr))`——`Flush` 时给
   「还没定稿的那一轮」补声纹，必须能回溯到它的起点；窗口滑动又不能无限长。长流下缓冲区有界、
   推流分块方式不改变结果（`Chunking_does_not_change_the_result` 逐条比对更新序列）。
4. **会话级状态与修订协议。** speaker 原型是 L2 归一化质心 + 证据权重：`Weight` 受
   `PrototypeWeightCap` 冻结（防止老说话人把质心钉死），`TotalEvidence` 不设上限（可合并性要靠
   它判断）。id 按创建顺序、永不复用；合并只退让「较新且证据 < `MergeEvidence`」的一方。
   证据不足（语音秒数 < `MinNewSpeakerEvidence` 或平均活动度 < `MinTurnConfidence`）时不新建：
   在 `ContinuityWindow` 内借用前一位，否则 `UnknownSpeaker(-1)`；标签置信度分别是
   新建 1.0 / 匹配 1−d / 借用减半 / Unknown 0。修订按 `Id` upsert、`Revision` 单调、`IsFinal`
   只表示几何（end）已定；合会是另一条修订源，受 `RevisionWindow` 约束。时间线侧还有三条
   硬约束：旧修订不回滚、新修订替换、跟踪条数有界。
5. **不阻塞主循环 + 统一音频时钟。** `DiarizationHandler` 放在 VAD **之前**——它要的是连续音频，
   不是 VAD 挑出来的段。音频重采样到 16 kHz 后 `TryWrite` 进有界 `Channel<Work>`，单 worker 消费；
   **队列满算时间轴缺口**（`NotifyGap` 让绝对时间继续走），绝不背压音频线程。时钟统一到会话绝对秒：
   `VadFrame.AudioStartMs/EndMs` → `VadAudio.AudioStart/EndSeconds` → `Transcription.Start/EndSeconds`，
   再与分离段做区间 join。归因落在 `SttHandler`（转写与它的音频区间只在这一处同时在场，与上游
   `base_stt_handler` 解析 `speaker_pending` 同位置）：区间重叠最多者胜、按 speaker 累加、
   并列取更早起者、超出已提交时间线按 continuity 借用。线上事件保持原话，只给 LLM 历史加
   `[speaker_N]` 前缀。

**测试替身**：200 Hz / 400 Hz 双音（Goertzel 整数 bin 正交）同时伪造分割与声纹两个接缝，
无模型、无网络，状态机全部行为在默认测试道里可复现。`StreamingDiarizerTests` 14 例（跨窗口稳定 id、
重叠双段保留、短轮借用、无历史为 Unknown、说话人上限改为附着、同音频同更新、修订单调且
final 最后、每轮一次 embedding、几何/采样率非法即拒），管线三组 18 例（转发不改字节且驱动时钟、
会话结束冲尾可归因、**卡死 worker 变时间轴缺口而非背压**、24 kHz 输入重采样到 16 kHz 时钟、
非音频项直通；时间线最大重叠/按人累加/并列更早/continuity 借用/合并重映射/旧修订不回滚/
新修订替换/有界；powerset 解码五条）。归因接线另有 6 例
（`SpeakerAttributionWiringTests`）——这是「谁说的这句话」的全部实现，落点必须在 STT 这一级：
区间覆盖最多者胜、前缀在此处拼、无线索时宁可无标签、没有时间轴区间就不归因、后端已带归属时不被覆盖。

**测试逼出来的两个真缺陷**：

1. `DiarizationHandler` 原先把缺口上报写成 `Interlocked.Exchange(ref _droppedSamples, 0)`，把累计指标
   一起清零——指标再也读不出真实历史。改成 `_pendingGap` 只承担「还没上报的缺口」，
   `_droppedSamples` 保持累计。
2. **「会话结束」与「开新会话」被混成了一件事，而且是间歇性失败才暴露的**：`OnSessionEnd`
   先冲尾（目的正是让 STT 队列里剩下的音频还能用上尾部段），紧接着又 `Clear()` 掉时间线——
   自己把刚准备好的东西删了，尾句于是永远归因到 Unknown；之所以不是每次都失败，是因为
   `DrainBlocking` 在队列满时直接放弃重试，worker 的迟到写入又会把时间线补回来一半。
   修法是拆开这两个语义：会话结束**只冲尾不清空**，清空与时钟归零归 `Reset()`（新会话复用同一条管线时用，
   此前它是不可达的死代码，现在有了公开入口与测试）；`DrainBlocking` 改成退让重试到期限——
   收尾路径上放弃重试会让「已经冲完」变成谎话，而调用方正拿它当归因完成的依据。

**真实权重冒烟（`tools/DiarizationProbe`）**。替身跑得再绿也证明不了「真实 metadata 读得对、
真实输出宽度对得上 num_classes」，所以补一个吃真模型的探针。权重取参照实现钉的 sherpa 导出
（`model.onnx`，5,992,913 字节，SHA-256 `220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079`，
与参照实现声明的值逐位一致），落 `models/pyannote-segmentation/`（不入库）。

第一次跑就炸了，而且炸出两件事：

1. **参照实现的几何假设在它自己的模型上不成立（C035）。** metadata 是 `window_size=160000` /
   `receptive_field_shift=270`（592.6 帧），而参照实现要求窗口是帧步长的整数倍——真权重加载即被拒，
   说明它的真模型路径从未跑通。本实现把**模型声明的窗口**向下取整到帧网格（592 帧 = 159840 样本 =
   9.99 秒），默认跳长取 58 帧（15660）而不是 59，因为 own 区起点 `(window − hop)/2` 也要落在帧网格上，
   窗口与跳长的帧数必须同奇偶；**显式给的**窗口/跳长仍然严格拒绝（配置错误不该被静默改写）。
2. **`--diarization_window_ms` 是死的。** 窗口只写进配置、没喂给引擎，而 `Analyze` 只接受引擎自己声明的
   长度——改窗口长度会让每一帧都抛参数异常。取整规则现在一处定义
   （`DiarizationOptions.SnapToFrameGrid`），CLI 用它算出长度再建正式引擎。

冒烟结论（同一份权重、同一段素材）：

| 观测 | 结果 |
|---|---|
| 引擎几何 | 16 kHz、窗口 159840（9.99 s）、帧步长 270、帧偏移 495、本地说话人 3 |
| 一窗帧数 | **589**（= ⌊(159840 − 991)/270⌋ + 1） |
| 数字静音 | 0 活动帧、峰值 0.009 |
| 真人语音（英语 + 西语交替 18.7 s，一窗内含两人） | 501/589 帧活跃、**2 个本地通道同时点亮**、峰值 0.9968 |
| 同一窗两次推理 | 逐位相同 |
| 流式状态机（真实几何、100 ms 块） | 62 次更新 / 18 次声纹调用 / 缓冲未越界；此处用的是能量签名**替身**声纹，把两人并成 1 个说话人——正是 D033 那条缺口的现场（接上真实声纹后是 2 个，见下一节） |
| 缩短到 2.99 s 窗 | 174 帧、前瞻 4.51 s → 1.35 s，图接受动态长度 |

诚实边界：这次冒烟验的是**工程事实**（metadata 键名、输出宽度、确定性、真实几何下状态机不崩），
不是分离准确率——准确率是模型性质，而且没有真实声纹引擎时说话人身份本来就无从谈起（D033）。

账本动作：D009 翻 resolved（缩窄为「声纹后端」并入 D033）；新增 D033（`ISpeakerEmbedding` 只有
接缝、无模型实现——参照实现同样只有接缝，按参照边界接受）、C035（窗口几何，参照缺陷不复现）、
`porting/reference-lock.voxa.json`。差异账 **70 条（resolved 52 / accepted 18 / gap 0）**——**首次 gap 归零**。
测试 **456/456**（+41），全解决方案 0 警告 0 错误。

### 声纹后端（2026-10-27，D033 收口）

D033 之前 `ISpeakerEmbedding` 只有接缝：分离能给出「谁在什么时候说话」，给不出「这是不是刚才那个人」。
本轮把它补成真实引擎——**参照实现没有这一层**（voxa 的装配点传 null），所以口径取自 sherpa-onnx
（`SpeakerEmbeddingExtractor` + `FeatureExtractorConfig`），这样能直接吃它发布的权重。

1. **前端不是「差不多就行」。** Kaldi 风格 80 维 log-mel：25 ms/10 ms、povey 窗、预加重 0.97、
   逐帧去直流后**再**预加重（顺序反过来会把直流放大成斜坡）、`snip_edges=false`（帧中心对齐、
   边界**单边镜像** `−1→0`、`n→n−1`，不是重复首样本）、512 点 FFT、80 个三角 mel 滤波器
   （20 Hz ~ Nyquist−400 Hz、自然对数 mel 轴）、log 前按 **`FLT_EPSILON`** 截断。
   最后一条最容易写错：`float.Epsilon` 是反规格化最小值 1.4e-45，差了 38 个数量级，
   数字静音的帧会从 −15.94 变成 −103——不抛任何异常，只是声纹悄悄失真。
2. **对拍两层。** 用 `kaldi-native-fbank` 以同一组参数对同一段 3 秒真实语音生成参照矩阵（300×80）：
   逐元素最大差 **1.8e-4**，且全部落在能量已接近 floor 的低频 bin（`log` 在 1e-7 量级的能量上对
   float 舍入极敏感，这是精度不是口径）；再用「knf + onnxruntime」跑同一权重，与 C# 的
   端到端 embedding 余弦 **> 0.9999**。定点值以**合成信号**固化进 `KaldiFbankTests`，
   不需要音频素材入库。
3. **阈值靠实测而不是猜。** 双人素材（英语 `TheGreatGatsby` + 西语 `es-ES` 交替 18.7 秒）：
   同人余弦距离 0.14–0.29、异人 0.71–0.84。配置的 `MatchThreshold=0.45` / `NewSpeakerThreshold=0.62`
   正好落在两者之间，不是贴着数据调的。

**真跑逼出三处缺陷，都是「替身全绿、真权重必现」的类型：**

- **声纹夹带了后面的话。** `EmbedSpan` 复用一块只增不减的取音缓冲，而 `CopyTo` 是按**目标长度**复制的：
  先来一段 6 秒、再来一段 0.6 秒，短的那次会把上一段的残留一起算进去。所有向量于是朝「整条音轨的
  平均」收敛，谁跟谁都像——表现是整场只剩一个说话人。修：只把本段的长度传进去。回归用例
  `An_embedding_sees_only_its_own_turn` 用「短段取样必须 ≤1.2 秒」钉死它。
- **同一轮次被十来个窗口反复认领。** `Associate` 只在 `turn.StartFrame == 0` 时查「上一窗口这个通道
  在续接哪个段」；轮次起点落在 own 区中间时它只去 `_open` 里找，而那段早已 `Finalize` 移出。
  窗口 10 秒、跳长不到 1 秒，于是一段 3 秒的话被反复建成新段（观测：18.7 秒音频切出 14 个段、
  段总时长 31 秒——超过音频本身，段在时间上互相咬住）。修：不论起点在哪，都先按通道映射认亲。
- **凹陷判据在真实概率上永不成立（C036）。** 参照实现要求概率掉到 `0.7×onset = 0.35` 以下才计静音，
  而真实 pyannote 在换人处给的是约 **0.40** 的持续弱响应——既低于 onset 又高于 offset，凹陷永远
  不成立，两个人的话被缝成一段，声纹取到混合声后连锁塌成 1 个说话人。修：凹陷只按 `Onset` 判定，
  删掉 `Offset` 配置项。宁可多切几段（同一人的相邻段共享标签，对外无损），也不能缝。

**修复后的真跑（真实权重 + 双人素材 18.7 秒）**：

| 观测 | 结果 |
|---|---|
| 说话人数 | **2** |
| `speaker_0` | 0.03–3.69 / 7.05–10.51 / 14.12–17.26 s（= 素材的 A 段） |
| `speaker_1` | 4.05–6.36 / 11.19–13.43 s（= 素材的 B 段） |
| 声纹调用 | 15 次 / 14 个段——随轮次走，不随窗口走 |
| `serve --diarization` | 正常监听；`--speaker_embedding_model` 指错路径时启动即失败并给出明确指引 |

遗留（**已于下一节修掉**）：模型在两个本地通道之间会抖动，同一区域可能被报成 2–3 个短段。

账本动作：**D033 翻 resolved**（声纹后端落地）、新增 **C036**（凹陷判据，参照缺陷不复现）。
差异账 **71 条（resolved 54 / accepted 17 / gap 0）**。测试 **467/467**，全解决方案 0 警告 0 错误。

### 跨窗口认亲：按时间重叠，而不是按起点帧（2026-10-27，C037 收口）

上一节记下的「遗留」——模型在两条本地通道之间抖动、同一区域被报成 2–3 个短段——不是模型的
后处理问题，是**状态机认错了身份**。本轮把它当缺陷修掉。

**病灶。** 参照实现（以及照抄它的初版）用两样东西判定「这个轮次就是上一窗口那个轮次」：
起点帧相差 ≤ 1 帧，以及本地通道号。真实权重上这两样都不成立：

| 依据 | 真实权重上的表现 |
|---|---|
| 起点帧 | 同一句话在两个窗口里报出 9.67 与 9.59（差 8 帧）。窗口 10 秒、跳长 0.98 秒，一句话被十来个窗口看到，抖一次就多一条新段 |
| 本地通道号 | 11.2–13.4 秒那句 B 的话，在窗口 1.96 落在通道 2，窗口 2.94 落到通道 1，窗口 3.92 又回通道 2，窗口 7.83 再回通道 1——powerset 输出对通道编号是排列无关的，另一个人不在场时轨道就排到前面去了 |

两者叠加的真跑结果：18.7 秒素材切出 **16** 条段，段总时长 **29.09 秒**（超过音频本身，说明段在时间上互相咬住）。

**判据换了。** 跨窗口稳定的只有一件事：**这段时间已经被谁占着**。所以一个轮次续接哪条段，
按音频时间上的重叠量决定（`Associate` / `FindChannelHandoff`），与窗口怎么切、轨道排在第几条通道无关。
另有两处必须同样稳定的次级判据：

1. **同窗同时 = 真重叠。** 同一个窗口里两条轨道在时间上同时存在，那是模型在说「两个人同时说话」，
   各成一段；只有首尾相接（含被窗口右沿截断的 1–2 帧）才可能是同一个人换了条通道。
2. **换通道要验声。** 「换通道接着说」与「两个人紧挨着说」在时间上长得一样，只有声纹能分开：
   取这一批帧的向量，与那条已认领的段比一次余弦（阈值复用 `MatchThreshold`）；比不过就各自成段。
   短于 `MinSpeechDuration` 的碎片另算——它本来就不够判一个说话人，被整段盖住时并进去即可
   （这条顺带让 `MinSpeechDuration` 这个参照声明了却从未使用的配置项真正上岗）。

**真跑（同一素材、同一权重、同一探针）：**

| 观测 | 修前 | 修后 |
|---|---|---|
| 段数 | 16 | **6** |
| 段总时长 | 29.09 s（互相咬住） | **14.44 s**，加间隙 2.78 s + 尾静音 1.44 s + 头 0.03 s = **18.7 s** |
| 时间线重叠 | 多处 | **0** |
| 声纹调用 | 15 | 11 |
| 说话人 | 2 | 2 |

落点：0.03–3.69 / 4.05–6.36 / **7.09–9.30 + 9.70–10.54** / 11.15–13.43 / 14.12–17.26 秒。
7.09–9.30 与 9.70–10.54 是同一人（A）的两段——模型在那 0.4 秒里确实把概率压到了 onset 之下，
按 C036 的决定「宁可多切」：两段标签相同，对归因无损。这是**最后一个已知的段数多于人数**的情形，
不是认亲缺陷。

**回归测试。** 音调替身造不出这三种形态（过阈逐位稳定、通道从不换手、也从不零星亮帧），
所以新增 `ScriptedMaskSegmentation`：按窗口序号脚本化地造出起点抖动、通道换手、长轨道中的 20 ms 亮点。
三条用例 `A_drifting_onset_stays_one_segment` / `A_voice_that_switches_local_channel_stays_one_segment` /
`A_two_frame_blip_on_the_other_channel_is_not_a_speaker` 在**修改前的实现上全挂、修改后全过**（已双向验证）。

探针同时补齐了排查入口：`--trace`（逐窗 NEW / EXTENDED / FINAL，按跳长喂入使一次 Push 正好跑一个窗口）、
`--tracks-at <s,...>`（指定开窗位置的逐通道活动区间）、`--window-at <s>`（该窗所有通道的逐帧活动 run）——
「两条轨道同时报同一个人」与「一个轮次被起点抖动切碎」在段列表上长得一样，只有后两个视图能分开。

账本动作：新增 **C037**（认亲判据，参照缺陷不复现）。
差异账 **72 条（resolved 55 / accepted 17 / gap 0）**。测试 **470/470**，全解决方案 0 警告 0 错误。

### 会话 id 的三处可见面（2026-10-27，C029 收口）

起因是复核一条 `accepted` 的旧记录。它写着「本实现会话 id 是 `conn_<32hex>`，与上游 `session_` 前缀不同」，
理由是「id 对客户端是不透明回传令牌，格式差异不影响行为等价」。**这句记录本身就不准**，实测三层都不对：

| 面 | 上游 | 本实现（修前） |
|---|---|---|
| `session.created` 的 `session.id` | `session_<32hex>` | **字段完全不存在** |
| `session.updated` 的 `session.id` | 同上（`model_copy` 覆盖同一个值） | **字段完全不存在** |
| WebRTC `Location` 头 | `/v1/realtime/calls/session_<32hex>` | `/v1/realtime/calls/<32hex>`（无前缀） |
| `conn_<32hex>` | 无此形态 | 只存在于私有字段里，仅供时延账分作用域，**从不出线** |

**为什么旧记录判定为 accepted 而没人发现。** 两条原因叠在一起：

1. `ServerEvent.SessionCreated(string sessionId, JsonObject session)` 的 `sessionId` 参数**方法体从未使用**。
   写了一半就停了——编译不报错（参数没用不会警告），调用点看起来也在正确传值。
2. 协议语料根本没有覆盖这一格：`corpus/protocol/` 里没有 `session.created` 样例，
   `protocol_probe._normalize` 只把 `event_id` 换成 `<event_id>`。所以「基线绿」从来没被检验过。

**修法。** 会话 id 只在 `RealtimeConnection` 生成一次，形态 `session_<32hex>`：

- `SessionId` 属性直接返回它，不再借用 `ChatSession.SessionId`（那是 LLM 上下文容器的内部标识，没有线上前缀）。
- `ServerEvent.SessionCreated` / `SessionUpdated` 收下这个 id 写进 `session.id`，写入前 `DeepClone`，
  **绝不回写**调用方传入的配置快照；客户端在 `session.update` 里自造的 id 会被覆盖（上游 `model_copy(update=...)` 同口径）。
- WebRTC 的 `Location` 与 DELETE 路径本来就用 `connection.SessionId`，形态一改自然对齐，无需另动。

**验证。** 单测 3 例（带 id 且保留其它字段 / updated 同 id 且不改快照 / 覆盖客户端自造 id）；
`WebRtcLoopbackTests` 把 `Location` 断言从「匹配路径前缀」收紧为
`^/v1/realtime/calls/session_[0-9a-f]{32}$`，并新增「datachannel 里的 `session.id` == Location 末段」——
只断言前缀的话，一个空 id 也能过，这正是上一版的漏洞。

真跑：WS 侧拿到 `session_89721de91d3d4cc68970fff2516bc3ed`，发 `session.update` 伪造 `session_forged` 后
`session.updated` 仍是原值；`webrtc_smoke.py` 五项全 PASS，`call_id=session_822b7e48c25648049864b00da06d2935`
（对比修复前的 `6e9aac6ebb4148168418fbc202faa097`）。

**双向验证。** 去掉两处 id 注入后三条单测全挂（NRE / 字符串不等），环回测试挂在 `Location` 正则上；
恢复后 482/482 全过。

账本动作：`C029` 由 accepted 翻 **resolved**（差异账 73 条：resolved 57 / accepted 16 / gap 0）。

### talk 客户端真跑（2026-10-28，D035 收口）

**发现。** 上一轮收口 C029 后巡检「还没验过的部分」时发现：`talk`/`local` 的音频客户端是 367 行精简版，
而上游 `audio_client.py` 有 1237 行。差距不在「能不能连」，在**参数与协议面**：
上游参数族（`--send-rate`/`--recv-rate`/`--playback-buffer-ms`/`--chunk-size`/`--input-device`/
`--output-device`/`--voice`/`--instructions`/`--print-json`/`--verbose`/`--block-mic-during-playback`，
local 侧带 `--local-audio-*` 前缀）本实现一个都没解析——CommandLine 接受任意 `--xxx` 后无人消费，
比 D034「接受却忽略」还要早一步；`session.update` 硬编码双向 24000 并无条件声明 format、缺
`extensions`、缺 `interrupt_response`，麦克风 16k 被强制上采样 24k 再发（上游默认 16k 直通）。
`extensions` 缺省 false 是真功能缺失：服务端只给订阅者发实时转写快照，不订阅 local/talk 下用户
永远看不到逐字转写。

**修法。** 新增 `RealtimeAudioClientOptions`（含 `BuildSessionUpdate` 静态构造，16k 省略 format /
24k 显式 / 其余报错，与上游 `maybe_pcm_format` 同口径）与 `PlaybackBuffer`（首包攒满才开播、
`finish()` 放行短响应、欠读回 idle、150ms 活动窗，逐条对齐上游）；麦克风按 send_rate 直通不再重采样；
设备索引贯通三后端（WinMM `DeviceNumber` / WASAPI `EnumerateAudioEndPoints` 枚举 / ALSA 映射
`plughw:<n>,0` / PortAudio 索引直通），越界显式报错不静默回落；参数装配提成 `FromTalkArgs` /
`FromLocalArgs` 静态工厂（可测），local 的 playback_buffer_ms 缺省按 TTS 后端分支 openai→196 否则 0。
留待并记 accepted：`--tool-module`（import 语义无 C# 等价物）、箭头切 voice、renderer 原地刷新细节、
连接重试 30s。

**验证。** 单测 21 例（协议形状 10 + 播放缓冲 11 + 参数装配含在协议 10 例内），旧实现上因类型不存在
编译失败；全量 503/503。真跑：真权重 serve + `talk --print-json` 12 秒——麦克风环境音经 16k 直通
上行被 VAD commit（`audio_end_ms=4736`）→ 转写完成 → `response.created` → transcript delta，
`session.created`/`session.updated` 均到达、全程零 error 事件：新 `session.update` 被服务端完整接受，
快照订阅链路生效。

**两个环境事实，下次真跑别再踩：**
1. `talk` 真跑必须带全模型路径起 serve——缺 VAD 模型目录时服务端在**连接构建期**即报
   `BackendUnavailableException`，`session.created` 都不会发出（E003 行为，客户端侧表现为
   「连上了但什么都收不到」，很像客户端 bug）。
2. `timeout` 杀 Windows 进程不彻底：残留的 talk/serve 会锁住 `speech-to-speech.exe`，
   下次构建 MSB3027。跑完先确认进程清干净再 build。

### accepted 条目有效期核查（2026-10-28，C029 教训的延伸：accepted 的理由也要核）

对全部 16 条 accepted 逐条重验「接受理由是否仍与代码/上游一致」。结论：**15 条维持，1 条修订（D008）**。

| ID | 核验结论 |
|---|---|
| C005 分句规则 | 维持。刻意改进有明确动机（CJK 长回复首段音频不拖尾），单测锁定 |
| C008 Parts 优先 | 维持。上游 `pipeline/messages.py:131` 复核确认：parts 显式设置时 text 由 parts 重算，账本描述与上游一致 |
| C009 / C030 / C031 重采样与解码内核 | 维持。均只影响送入下游的音频波形细节，不改线上消息形状；C030/C031 有 ≥20 dB 带外压制与语料验证背书 |
| C010 WASAPI/WinMM 双 API | 维持。平台实现选择，代价（热插拔行为差异）已在账本写明 |
| C032 HF 云工作流 | 维持。报告纯 JSON 落盘即可用 compare 对比，schema 兼容已验证 |
| C033 / C034 口型 | 维持。wire 契约 `{viseme 0-21, start_s, end_s}` 锁定，密度差异是「闭口信息」而非噪声 |
| D001 整句合成 | 维持。三重补偿有单测，代价已实测记账（2026-10-27：废弃合成 104 s、下一轮被顶 94.8 s） |
| D002 能量门限近似开口 | 维持。分段权威仍是 Silero，只影响极轻语音的打断延迟 |
| D004 Paraformer 无语言字段 | 维持。`language_code=null` 下游按未判定处理，TTS 语言由 Qwen3-TTS 自判 |
| **D008 本地 STT 无增量转写** | **修订范围**。原表述「客户端收不到 transcription.delta」是全称，已被 D005（2026-10-26 收口的 openai-realtime / vllm-realtime 流式 STT）推翻——选流式后端时 `IStreamAwareSttBackend → IStreamingSttSink → PartialTranscription → transcription.delta` 全链贯通，serve 侧发送点在 `RealtimeConnection.cs` 的 `PartialTranscriptionEvent` 分支。D008 收窄为「ManySpeech 本地路径无流式入口（该库无运行期流式接口，与 D034 的 language 同类约束）」，维持 accepted |
| D007 LLM 单并发 | 维持。信号量串行化，多客户端排队不崩溃 |
| D010 后端注册表 | 维持并修正计数。注册表实际 32 个名字（上游 31 个 + C# 专属 `tensorsharp`），实测 **11 可用 / 21 不可用**——原条目自述「12 个后端 / 31 个名字」两处都不准（`what` 字段枚举 20 个且把已实现的 `openai-realtime` / `vllm-realtime` 算作缺失），2026-10-28 已按注册表与 `list-backends` 实测改写条目并重新渲染。行为不变：未实现者带 `UnsupportedReason` 报错不静默 |
| E003 缺模型运行期报错 | 维持。D035 真跑再次确认该行为（连接构建期即拒，客户端侧「连上但收不到」） |

**方法备注**：核查动作 = ① 账本 decision 里的每个事实主张找代码/实测对应物；② 对「上游会怎么做」的主张回上游源码验证（如 C008）；③ 对「不影响可观察行为」的主张检查其后的移植工作是否引入了反例（D008 即此类）。账本修订仅 D008 一条，`02-differences.md` 已重新渲染。
