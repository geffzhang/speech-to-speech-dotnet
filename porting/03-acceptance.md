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
| `SpeechToSpeech.Tests` | 222 | 222 | 0 | 0 |
| `corpus/text`（差分语料） | 15 | 15 | 0 | 0 |
| `corpus/protocol`（差分语料） | 36 | 36 | 0 | 0 |
| `corpus/turns`（差分语料） | 13 | 13 | 0 | 0 |
| `corpus/lang`（差分语料） | 15 | 15 | 0 | 0 |
| `corpus/smart-turn`（差分语料） | 13 | 13 | 0 | 0 |
| `corpus/latency`（差分语料） | 16 | 16 | 0 | 0 |

单元测试耗时约 629 ms（含 7 条阻塞式等待用例，它们会真的等一小段宽限）；差分语料需先构建 `tools/TextProbe` / `tools/ProtocolProbe` / `tools/TurnsProbe` / `tools/LanguageProbe` / `tools/SmartTurnProbe` / `tools/LatencyProbe`，比对在百毫秒级。六类都不依赖模型文件、网络与音频设备——所以能进 CI。`corpus/lang` 是唯一带原生依赖的一组（lingua 的 Rust 库），但只在探针进程内加载一次，不需要任何模型下载；`corpus/smart-turn` 用脚本化判定器，不需要 ONNX 模型；`corpus/latency` 的时间全部由语料给定，不读时钟。

跳过项说明：**无跳过项**。但有 3 类「未覆盖」不等于「跳过」，单列在此以免混淆：

| 测试 | 跳过原因 | 风险 |
|---|---|---|
| `local` 全链路 | 未写成测试，只写成 CLI 子命令。`serve` 已用脚本化客户端实跑（含两轮与打断），`local` 子命令本身未跑 | 已收敛——`serve` 真跑暴露的 C020/C023/C024 修复后，剩余风险与 `local` 共享同一套管线 |
| `talk` 端到端 | 需麦克风/扬声器。TTS 音频已在真链路上合成并经 `output_audio.delta` 流式发出（2026-10-26），`tts_ttfa_s` / `e2e_s` 落地；剩下的是真实设备听感（音色、全双工回声） | 中——仅 Windows 实测 |
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

- 签名差异 / 运行时差异 / 平台限制：见 `02-differences.md`（44 条，编号 C001–E003、C010–C019、D011–D022）

## 协议锁定

- [x] 关键协议字段名与顺序由测试锁定 —— `ProtocolShapeTests` + `AssistantItemLifecycleTests` 共 25 例覆盖 24 类服务端事件、`event_id` 必带、错误嵌套结构、`response.done` 四终态、`response` 载荷完整性（usage / output_modalities / voice / conversation_id / output）、created 与 done 载荷同构、转写完成的必填 `usage`、助手消息项完整生命周期；另有 `corpus/protocol` **41 条**差分语料与上游逐字段比对（含编码，到 BMP 外的字符为止），锁定的是「与参照实现一致」而非仅「自证一致」
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
- [ ] **`vad_settle_s` 未记**（D022）—— 只在日志行里出现，不进 metadata；不要与 `hold_s` 混为一谈
- [ ] **工具输入记账未移植**（D020）—— `s2s_tool_input_call_ids` 不发（客户端的伪造会被删掉），
  且 `pending_function_calls` 参与的 anchor 关闭规则未接

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
      **注意**：ElBruno.QwenTTS 的 ONNX 管线在 CPU 上逐句合成 15–190 s/句，是后端运行时性能（上游用 faster-qwen3-tts/mlx-audio），不是行为等价问题（D-01）。
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
- [ ] **容器内** —— 未做（E002）：无 Dockerfile、无发布包
- [ ] **跨平台**（Windows / Linux / macOS）—— 代码路径齐备：`serve` 纯托管代码，理论跨平台；`talk`/`local` 音频后端三平台均接入——Windows（WASAPI + WinMM）、Linux（ALSA）、macOS（PortAudio，PortAudioSharp2 1.0.6，E001 已解决）。Windows 已实跑设备创建；**Linux 与 macOS 未在真实设备上跑过**（绑定冒烟 3 例通过）
- [ ] **带真实音频设备的一轮对话** —— 仍未做：`local`/`talk` 需要真人对着麦克风说话，本轮用 `serve` + 脚本化客户端替代，验证的是同一套协议与管线
- [x] **跨传输** —— 仅 WebSocket。WebRTC 未移植（D003），明确记为缺口而非「支持」

## 未闭合风险

| 风险 | 影响 | 处置 |
|---|---|---|
| 音频层差分语料为空 | 模型推理受浮点累加顺序影响，不可逐字节比对 | 改锁采样率/声道/位深/段数/时长误差带，不比样本值 |
| 有声窗位置由能量门限判定（D019 / D002） | `vad_decision_s` 的锚点是「最后一个有声窗」，本实现按块能量判有声，上游按 Silero 概率判。噪声底高的环境里两者会差一两个窗（约 32–64 ms） | 粒度与上游一个 VAD 窗同量级；缺口比「用段尾」小一个数量级——段尾含闭合静音，会把延迟整段抹成 0 |
| `vad_settle_s` 的判定依赖 `Mode="final"` | 本实现无 progressive 产出，所有定稿段都带 `final`，口径与上游一致；但上游 progressive 转写路径本身未移植（D005） | 低——D005 补齐时需一起核对 `vad_settle_s` 的分段记账 |
| 函数调用未进 `response.output`（D023，**已解决 2026-10-27**） | 输出项状态机与 `ResponseOutput.Merge` 合并排序已就位，协议语料 39/39、真跑回归通过；**未经真实工具链路验证**——本实现的测试 LLM（qwen35）没有稳定的工具调用产出，`AssistantToolCallReadyEvent` 端到端只由假后端走过 | 需要一个会发工具调用的 LLM 后端（如 OpenAI 兼容端）跑一轮真链路 |
| ~~带外响应未实现（D024）~~ **已解决 2026-10-27** | 侧问会污染会话历史 | 四处一起补齐：metadata 不挂工具输入、不建时延账、输出不写回历史（哨兵词+item.delete 双证）、`conversation_id` 报 null；协议语料 40/40，真跑 `oob_leak_probe.py` 闭环 |
| 每响应音色覆盖未实测（D017 已补） | 代码路径已打通（`ResolveVoice` → `TtsInput.Voice` → 后端 `Speaker`），但只由假后端验证过「值确实传到了」 | 低——真实 Qwen3-TTS 对不支持的音色会忽略，这层校验在模型侧；缺的是端到端听感验证 |
| `local` / `talk` 未实跑（真实音频设备） | 队列时序与背压已经 `serve` + 脚本化客户端验证过两轮与打断；剩下的是真实全双工声学场景（回声、设备延迟） | 带麦克风/扬声器跑 `local`，听感验证 TTS 音色（顺带闭合 D017 的端到端验证） |
| 接合点的时序与真实后端的交互 | `VadHandler` 与真实 VAD 后端的两轮/打断交互已用脚本化客户端实跑（C023/C024 由此暴露并修复）；剩余的是真实麦克风/扬声器下的全双工行为（回声、半双工时序） | 低——协议与管线层面已闭合，剩下的是声学环境问题，不影响行为等价判定 |
| Smart Turn 判定的「incomplete」路径未在真实音频上触发 | 判定器概率已与参照链路逐值对齐（合成音频 <1e-4），但手头的测试音频截断处恰好语义完整，参照侧也判 complete | 低——incomplete 之后的决策（推迟 600ms、max_wait 2000ms、复开）由 `corpus/smart-turn` 13 条语料与 `TurnEndTiming` 单测锁定；真实对话中自然会出现 |
| 实时转写未移植（D005） | `openai-realtime` / `vllm-realtime` 两个 STT 后端不可用 | `BackendUnavailableException` 显式报错，不静默降级 |
| 音频后端跨平台不全（**E001 已解决 2026-10-27**） | macOS 已接入 PortAudio（PortAudioSharp2 1.0.6，与上游 sounddevice 同引擎）；Linux ALSA 路径仍未实跑 | Windows 已实跑设备创建；Linux 需在装有 `libasound.so.2` 的机器上验证；macOS 需真机跑一轮 `talk`（发布时带 osx-x64/osx-arm64 原生库） |
| 语言识别的原生库覆盖不全（D006 已补） | lingua 原生库只有 win-x64 / linux-x64 / osx-arm64；其余平台（Linux arm64、macOS x64）该后端抛 `BackendUnavailableException` | 与 E001 同一类平台缺口，不静默降级；补包即可，不影响决策层（决策层与检测器解耦，已有 25 例单测） |
| 语言识别结果未在真实对话上验证 | `corpus/lang` 证明与上游逐值一致，但那只覆盖决策层；「TTS 是否真的换了发音」需要听 | 低——参数已按上游口径接到 `TtsRequest.Language`，剩下是后端行为 |
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

# 5. 协议覆盖审计（与上游 26 种事件比对，当前 25/26）
python tools/refprobe/protocol_coverage.py
```

重录基线只在参照实现或上游文本层变动时做，且**先逐条看差异再决策**，不要一把 `--update` 全过。
录制环境（nltk / punkt_tab 版本）见 `corpus/text/README.md`。

**结论**：构建、单元、文本层、协议形状层、轮次状态机层与语言层验收通过；端到端经 `serve` + 脚本化客户端实跑闭合——单轮、同连接第二轮（gap 25 s）、打断（gap 3 s）与 TTS 音频完整收口四个场景都完整走通，队列时序、时延账（含 `tts_ttfa_s` / `e2e_s`）与投机轮次在真实模型链上得到验证；Smart Turn 判定器（D018）已补齐并经 fixture 逐值对齐 + 真跑验证；函数调用的输出项生命周期（D023）已补齐并经协议语料与真跑回归。本实现可作为「四段管线 + Realtime 协议 + 投机轮次 + Smart Turn + TTS + 工具调用输出项 + 带外响应」的可运行基线。剩余空白集中在真实音频设备的 `local`/`talk` 听感验证与容器内实跑（E002 三件套就绪、静态验证已过，待 Docker 引擎）。工具调用链已闭环：chat-completions 后端补上 delta.tool_calls 流式聚合（D028），真实 LLM 端到端以脚本化 mock 服务验证全链 PASS——后续接任何 OpenAI 兼容服务（llama.cpp server / vLLM）即为真实模型端到端。error 事件的 make_error 类形状归位（D027）已解决——错误事件双构造线（make_error 的 error={message,type} vs schema 校验的 invalid_request_error+code）分流完毕，`send_correlated` 的 error.event_id 关联全量接上；空历史口径（D026）、instructions 回落（C027）与 `response.create` 的 `input` 数组（D025）此前已解决。至此 response.create 的全部入参语义（instructions/temperature/max_output_tokens/modalities/voice/conversation/input）与错误面都与上游对齐。

协议层已从「客户端可能看不到回复」补到 25/26 覆盖：消息项生命周期（`output_item.added` →
`content_part.added` → delta → `content_part.done` → `output_item.done`）、提交确认、会话项删除、
转写失败的 item 作用域通告都已到位，并有 41 条差分语料与 27 条形状断言双重锁定。
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

遗留（不在本轮范围）：模型在两个本地通道之间会抖动，同一区域可能被报成 2–3 个短段
（标签相同，只多几次修订）。它不影响归因结果，但会让时间线出现重叠段——属于分割模型的
后处理问题，不是接缝或状态机的错误。

账本动作：**D033 翻 resolved**（声纹后端落地）、新增 **C036**（凹陷判据，参照缺陷不复现）。
差异账 **71 条（resolved 54 / accepted 17 / gap 0）**。测试 **467/467**，全解决方案 0 警告 0 错误。
