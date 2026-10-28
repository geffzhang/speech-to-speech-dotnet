# 项目长期约定

## 定位

`huggingface/speech-to-speech` 的 C# 行为等价重写（参照 `0835002`，`net10.0`）。验收=**可观察行为一致**。不一致进 `porting/differences.jsonl`；四本账在 `porting/`（`02-differences.md` 由 `render` 生成，**不手改**）。动工前 `reference_lock.py check`。
状态翻 resolved 必须同步改四本账**正文**，不只账本那一行。两向漂移都危险：正向让人低估实现，反向让人高估。自查 `python tools/ledger/check_doc_sync.py --strict`（按 ID 关联措辞；历史叙述用行尾 `<!-- doc-sync: ignore -->` 豁免）。
引用差异编号一律写账本形态 `D001`/`C009`/`E001`——`D-01` 这类连字符写法在账本里搜不到（曾散落 13 处，已统一）。

## 核心纪律

- 错误是数据不是异常；取消代际 32 位回绕，丢弃判据=代际合取；新后端带 `UnsupportedReason` 不静默降级。
- **参数被接受却静默忽略是最隐蔽的差异**（D034）：接线必须查清「谁真的消费它」。
- 构建 0 警告 0 错误；**不并行 build+test**（CS2001）。构建前先停 serve（dll 锁 → MSB3027）；「当文件已存在时无法创建」→ `dotnet build-server shutdown`。
- 状态机改动：先核对**调用点**再核对断言；第二轮/打断缺陷先二分「后端吐没吐事件」。
- 反证实验 `git checkout` 恢复源文件必须在**同一条命令**里 `cp` 回来，否则「新测试全挂」会骗过自己。
- 新测试要在**旧实现上全挂**才算有牙；只跑「通过」证明不了。
- **账本的自述也要核**：accepted 的理由常建立在「这不影响可观察行为」的假设上，而假设的依据（哪个字段真出线、语料到底覆盖没覆盖）本身可能是错的。C029 三条自述全不准——把内部量当时线上量、声称语料已归一化覆盖而那格根本没语料、把「缺字段」写成「前缀不同」。

## 依赖

NuGet 正式版，不挂本地工程引用。已用：ManySpeech.SileroVad 1.1.2 / WhisperAsr 1.0.1 / AliParaformerAsr 1.1.9、TensorSharp.Chat 2026.10.11、ElBruno.QwenTTS 1.10.2、NAudio(+Alsa) 3.3.0、PortAudioSharp2 1.0.6、SIPSorcery 10.0.17、Concentus 2.2.2（Opus，Span 编码带 frame_size）。NAudio 3.x 的 `Read` 全是 Span 签名，三参重载不存在。
同域参考只取工程形态：ElBruno.Realtime(`8662db1`)、tryAGI.WebRTC(`e64acf3`)、Aurio（重采样思路）。**例外**：BasisVR/Basis(`acfca6b`) 的 com.basis.openlipsync 是 D031 的**移植来源**（行为参与对拍）。

## 层级陷阱速查

- **音频**：`AudioDevices.cs` 集中平台后端（Win=WASAPI+WinMM、Linux=ALSA、macOS=PortAudio），设备索引贯通三后端（WinMM DeviceNumber / WASAPI `EnumerateAudioEndPoints` 大写 P / ALSA `plughw:<n>,0` / PortAudio 直通），越界显式报错；WasapiRecorder 非 IWavIn；ClientWebSocket 禁并发 Send（入队+单消费者）；ManySpeech OnlineVad 每段一条流（C023）；脚本推流三要素=真实时长 20ms 帧+尾补 1.5s 静音+proxy=None。
- **客户端（D035）**：`talk`/`local` 参数族与协议面已对齐上游——`RealtimeAudioClientOptions.BuildSessionUpdate`（16k **省略 format**/24k 显式/其余报错 + extensions 快照订阅 + interrupt_response）、`PlaybackBuffer`（首包门槛/finish 放行/欠读回 idle/150ms 活动窗）、麦克风按 send_rate 直通不重采样；`FromTalkArgs`/`FromLocalArgs` 工厂，local 缺省 buffer openai→196。**talk 真跑必须带全模型路径**（缺 VAD 时 serve 连接构建期即拒，客户端「连上但收不到」像 bug 不是 bug）；`timeout` 杀 Windows 进程不彻底，残留锁 exe → MSB3027。留待 accepted：tool-module/箭头切 voice/renderer 细节/连接重试。
- **协议**：`ServerEvent.Serialize` 用 `WireJson`（自写 RelaxedJsonEncoder，不用 UnsafeRelaxedJsonEscaping，C012/C017）；探针必须调产品序列化入口；`ResolveVoice` 一处定义喂两处；`BuildResponse` 唯一产地；status_details 任何终态都发；FIFO 用 Queue。**会话 id `session_<32hex>` 三处同一值**（session.created/updated 的 session.id、WebRTC Location、DELETE 路径，C029）——id 由 `RealtimeConnection._sessionId` 一处生成，事件构造器注入时 DeepClone 不回写配置；客户端自造的 id 必须被覆盖。
- **turns/时延**：语料三前提=自包含+时钟可注入(`Func<double>`)+纯量返回；浮点 6 位定长字符串绝不用容差；复开窗口三值取 max；时延账按 response_key；hold_s 区间并集；metadata 逐字符字符串；round 9 只用于 vad_decision_s/hold_s；日志走 stderr。
- **HOLD 宽限**：算出来≠生效——输出侧逐片阻塞等、输入侧只等 ProcessingDeadline；陈旧收尾照发 done(Succeeded=false)；丢弃修订要 `DiscardPendingTurn`。
- **测量点**：vad_decision_s 锚=最后有声窗；disabled 只写 status；接线不进语料。
- **Smart Turn**：ORT 钉 1.31.0（版本敏感差 0.12）；CT 跨步 25 取样；do_normalize=零均值单位方差；窗口尾 8s 前补零；判定与决策分离。
- **语言层**：lingua-py ↔ lingua-rs 互证；一轮只解析一次；关掉检测仍观察写回。
- **STT language**：只有 whisper 系消费（默认 `en`，`auto`=完全不传）；ManySpeech 无运行期入口，只能写**临时 conf 副本**（原件不动、语言小写规整、文件名带源 conf SHA256 前 8 位，D034）；paraformer 不消费。

## 真跑与权重

- `tools/smoke/`：serve_smoke（完整说话时间线）、oob_smoke/oob_leak_probe、tool_call_smoke、error_shape_smoke、webrtc_smoke（aiortc 互证）。HTTP 一律 `ProxyHandler({})`（Windows 代理劫持 127.0.0.1，D029）；后台服务用 run_in_background。
- **权重位置**：仓库 `models/` 只有 pyannote / speaker-embedding / viseme；四段管线全在 `C:\Users\geffzhang\Models\s2s\`（`vad5/`、`whisper/` tiny、`tts/` 5.5G、`smart-turn/`、`llm35/Qwen3.5-0.8B-Q4_K_M.gguf`）。**「补模型」=把 serve 指过去，不是下载。**
- 端到端：`serve --vad_model_dir <s2s>/vad5 --whisper_model_dir <s2s>/whisper --whisper_language zh --qwen3_model_dir <s2s>/tts --smart_turn_model_path <s2s>/smart-turn/smart-turn-v3.2-cpu.onnx --llm_model_path <s2s>/llm35/Qwen3.5-0.8B-Q4_K_M.gguf` + `serve_smoke.py --url ws://127.0.0.1:8791/v1/realtime`。真权重下 `smart_status=complete`。
- 时延：TTS 首轮 ~95s（含 5.5G 加载）/ 热态 ~30s/段；qwen35 CPU per-op decode ~3.1s/轮。**TensorSharp 只认 qwen35**：收 qwen2 在**流里**抛 `Unsupported architecture`（表现为 output_items=0），启动时不报。这份 whisper 是 tiny，中文转写质量受限。
- **打断**（`--speak-twice --gap 3`）：桩后端毫秒返回，打断窗口几乎打不着；真权重下 TTS 30–104s/句，第二句必落在音频中段。**取消只停发不停算**——被作废那轮仍把整句合成完（实测 104s），下一轮首包被它顶住 94.8s（D001 结构代价，CPU 消不掉）。陈旧音频不发、历史回滚、下一轮照常收口——这三条是对的。

## WebRTC（D003 resolved）

`SessionTransport` 抽象+TransportGate；SIPSorcery `WebRtcSession`（datachannel "oai-events" + Opus 48k RTP + ICE env `SPEECH_TO_SPEECH_ICE_SERVERS`）；`WebRtcCallEndpoints`（415 是**子串包含**，503 槽位默认 1=num_pipelines）。
Pacer 对齐上游 `recv()`：首帧即时、不足补零不限首帧、追赶用 **ceil** 且只认「等过数据」、Sleep 在途缓存 timestamp（重算会 ceil 跳帧死循环）、结束补 5 帧排空。Resampler 绝对输出游标+整数上取整（分块==整块）。
session.created：WS 握手后发，WebRTC 由 datachannel onOpen 发（幂等）。环回测试：offer 的 track 必须与 answer 侧 codec/clock 有交集。

## 评估套件（D030 resolved）

`src/SpeechToSpeech.Evals` + `tools/EvalProbe`（run/compare/show/build-subset），HF 端点尊重 `HF_ENDPOINT`。**PythonRandom**：CPython MT19937 逐位复刻，同种子选题与上游逐位一致。陷阱：`MatchCollection` 要 `foreach (Match m in ...)`；上游 redact 对无 `=` 的敏感 flag 保留名、下一值脱敏；`--push-to-hub`/job.py 不移植（C032）。

## macOS 音频（E001 resolved）

上游 sounddevice 底层即 PortAudio → macOS 走 `PortAudioAudioDevices`（**同引擎对齐**），Win/Linux 维持 NAudio。`PortAudioWaveIn/WavePlayer` 实现 NAudio 接口（`Volume` 在回调里 int16 就地缩放）；Pa_Initialize/Terminate 逐设备配对；播放 framesPerBuffer=Unspecified，录制对齐上游 blocksize。绑定冒烟 3 例过，**真机未实跑**（与 Linux ALSA 同口径）。

## 口型（D031/D032 resolved）

`Backends.OpenLipSync`：48tap Blackman-sinc 多相重采样 / FFT / Hann+HTK mel(80×100Hz) / TCN 流式 ONNX（cache_* 乒乓 IoBinding）/ 双极点平滑，ORT 1.31.0。
**mel 滑动窗是因果对齐**（prev=240 起步，prev=window[160..400]），不是 librosa 居中分帧；重采样 1:1 **不是恒等直通**（0.45·fs 低通+半样本相移），尾部 23 样本延迟线。
`StvHandler`：0.5s 整窗一次推理、偏移=已推理样本/管线率、**visemes 只挂首块**、终态前冲尾、`cleanup_only` 不冲、陈旧代际整窗丢弃；`EmitAudioAsync` **先发事件后发音频**。逐帧回调走 `SetFrameObserver(context)`，**别用后端级 VisemeFrameComputed**（多连接串帧）。`--enable_visemes` 默认关，关时 TTS 直连（逐字节相同）。C034：整窗连续（静音也发 sil）vs 上游音素稀疏；时间量化 10ms。

## 流式说话人分离（D009 / D033 / C035–C037）

参照 voxa `feat/streaming-diarization` @ `cc60257`。两包：`Audio.Diarization`（纯 C# 状态机）+ `Backends.PyannoteDiarization`（ORT）。
**powerset 语义**：`p[s]=Σ_c softmax[c]·mapping[c][s]`，边缘和**故意 ≠1** → 一帧两项非零=重叠。压平成 speech/silence 会同时丢掉谁/重叠/置信度。
**几何从帧网格反推**：模型声明窗口向下取整（159840=592 帧=9.99s），默认跳长 58 帧；`Analyze` 只接受引擎声明长度 → `SnapToFrameGrid` 一处定义（C035：参照实现会拒掉它自己钉的模型）。
**会话结束 ≠ 开新会话**：`OnSessionEnd` 只冲尾，`Reset()` 才清空+时钟归零；`DrainBlocking` 退让重试到 `FlushTimeout`。
**归因落点 `SttHandler`**（转写与音频区间只在这一处同时在场）：区间最多者胜、并列取更早、超出已提交时间线按 continuity 借前一位、无线索 Unknown。线上事件保持原话，只给 LLM 历史加 `[speaker_N]`。
**非阻塞**：`DiarizationHandler` 在 VAD 之前；有界 Channel+单 worker，**队列满=时间轴缺口**（`NotifyGap`）。
**声纹（D033）**：`KaldiFbank`（25/10ms、povey、**snip_edges=false 单边镜像**、80mel 20~7600、log 前用 **`FLT_EPSILON` 不是 `float.Epsilon`**、global-mean）+ `OnnxSpeakerEmbedding`（CAM++，输出 L2 单位化、**不做内部重采样**）。模型 `models/speaker-embedding/`、`models/pyannote-segmentation/` 不入库。
**跨窗口认亲（C037）**：身份**只按音频时间重叠**——起点帧抖 3–8 帧、powerset 通道号排列无关（同一句 2→1→2→1）。次级判据：同窗同时=真重叠；换通道要**验声**（`CosineDistance` ≤ `MatchThreshold`）；**碎片判据必须排在「同时」判据之前**（否则长轨道中 2 帧亮点成假说话人）。`IsReReport` 余量 `8×frameShift`。真跑 16 段/29.09s → **6 段/14.44s、重叠 0**。
**三个真缺陷教训**：① scratch 复用 + `CopyTo` 按**目标长度**复制 → 声纹夹带后续音频（向量趋同→只剩 1 人）；② `Associate` 只在 `StartFrame==0` 查映射 → 同轮次被多窗重复建段（段总时长 > 音频本身）；③ C036 凹陷判据 `0.7×onset` 在真实概率（换人处约 0.40）上永不成立 → 双人缝成一段。

## 当前状态（2026-10-28）

差异账 75 条：resolved 58 / accepted 17 / **gap 0**。协议语料 43 条（事件覆盖 26/26）。测试 503/503。剩余：真人麦克风听感一轮、Linux/macOS 真机、D005 流式联调（手册 docs/vllm-realtime-d005-integration.md，等 GPU/key；无 GPU 可先走 OpenAI hosted 覆盖五项中四项）。D036：上游 STT 参数带 _stt 段 vs 本实现 {backend}_* 模板，映射表在手册 §5.1。Python 参照环境 `~/.workbuddy/binaries/python/envs/default/Scripts/python.exe`（aiortc 1.15、numpy、kaldi-native-fbank、onnxruntime）。
