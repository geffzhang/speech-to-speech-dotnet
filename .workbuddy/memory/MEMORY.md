# 项目长期约定

## 定位

`huggingface/speech-to-speech` 的 C# 行为等价重写。参照物锁定 `0835002`，`net10.0`。
验收是**可观察行为一致**。任何不一致进 `porting/differences.jsonl` 编号；四本账在 `porting/`（02-differences 由 `diff_register.py render` 生成，**不要手改**）。动工前 `reference_lock.py check`。

## 依赖与参考

- **一律 NuGet 正式版**，不挂本地工程引用；预发布包不进默认构建。已用：ManySpeech.SileroVad 1.1.2 / WhisperAsr 1.0.1 / AliParaformerAsr 1.1.9 / TensorSharp.Chat 2026.10.11 / ElBruno.QwenTTS 1.10.2 / NAudio(+Alsa) 3.3.0 / NAudio.Core 3.3.0（OpenAiCompatible 直引，WDL sinc 重采样内核）/ PortAudioSharp2 1.0.6（macOS 音频，原生库由 org.k2fsa.portaudio.runtime.* 按 RID 提供）/ SIPSorcery 10.0.17 / Concentus 2.2.2（Opus，工厂返回 IOpusEncoder/IOpusDecoder，Span 编码签名带 frame_size）。
- 同域参考（只取工程形态不取行为语义）：`C:\workshop\github\ElBruno.Realtime`（8662db1）、tryAGI.WebRTC（e64acf3）、`C:\workshop\github\Aurio`（重采样内核参考；其 NAudioWdlResampler 用 interp=true 实为线性插值，本实现取托管内核思路改用 sinc 模式）。SIPSorcery.OpenAI.Realtime 仅形态参照（BY-NC-SA 不可作依赖）。

## 核心纪律

- 错误是数据不是异常；取消代际 32 位回绕，丢弃判据=代际合取；新增后端带 `UnsupportedReason` 不静默降级。
- 构建 0 警告 0 错误；**不并行 build+test**（CS2001）。
- 状态机类改动：先核对**调用点**再核对断言；第二轮/打断缺陷先二分「后端吐没吐事件」再查状态机。

## 层级陷阱速查（细节见各日 log 与账本）

- **音频**：`AudioDevices.cs` 集中平台后端（Windows=WASAPI+WinMM、Linux=ALSA、macOS=PortAudio，E001 resolved）；WasapiRecorder 非 IWavIn；ClientWebSocket 禁并发 Send（入队+单消费者）；NAudio 3.x `IWaveProvider.Read`/`ISampleProvider.Read` 都是 Span 签名（三参重载不存在）；ManySpeech OnlineVad 每段一条流（C023），驱动 `IsFinished(isEndpoint:true)`+`GetResult(stream)`；脚本推流三要素=真实时长 20ms 帧+尾补 1.5s 静音+proxy=None。
- **协议**：`ServerEvent.Serialize` 用 `WireJson`（自写 RelaxedJsonEncoder，不用 UnsafeRelaxedJsonEscaping——emoji/+/中文会偏，C012/C017）；探针必须调产品序列化入口；`ResolveVoice` 一处定义喂两处；BuildResponse 唯一产地；status_details 任何终态都发；HashSet 无序，FIFO 要 +Queue。
- **turns/时延**：语料三前提=自包含+时钟可注入（`Func<double>`）+纯量返回；浮点 6 位定长字符串绝不用容差；复开窗口=三值取 max；时延账按 response_key；hold_s 区间并集；metadata 是逐字符字符串（Utf8JsonWriter 手写）；round 9 只用于 vad_decision_s/hold_s；先 build 再 pop；日志走 stderr。
- **HOLD 宽限**：算出来≠生效——输出侧逐片阻塞等、输入侧只等 ProcessingDeadline；陈旧收尾照发 done(Succeeded=false)；丢弃修订要 DiscardPendingTurn。
- **测量点**：vad_decision_s 锚=最后有声窗；disabled 只写 status；接线不进语料（单测锁「谁写账本」）。
- **Smart Turn**：ORT 钉 1.31.0（版本敏感差 0.12）；CT 跨步 25 取样；do_normalize=零均值单位方差；窗口尾 8s 前补零；判定与决策分离。
- **语言层**：lingua-py ↔ lingua-rs 互证；一轮只解析一次；关掉检测仍观察写回。

## 真跑

- `serve`+`tools/smoke/`：serve_smoke（完整说话时间线）、oob_smoke/oob_leak_probe（历史污染两招）、tool_call_smoke（llama-server 真链）、error_shape_smoke、**webrtc_smoke（aiortc 互证，D003）**。
- 冒烟脚本的 HTTP 用 ProxyHandler({})（Windows 系统代理劫持 127.0.0.1，D029 镜像）；后台服务用 run_in_background。
- 模型：VAD=ModelScope `silero-vad-v5-onnx`；TTS/LLM 走 hf-mirror resolve/main+Range 断点；TensorSharp 用 `qwen35`（无 qwen2）。

## WebRTC（D003 resolved，2026-10-27）

- 架构：`SessionTransport` 抽象+门控（TransportGate）；SIPSorcery `WebRtcSession`（datachannel "oai-events"+Opus 48k RTP+ICE env `SPEECH_TO_SPEECH_ICE_SERVERS`）；`WebRtcCallEndpoints`（POST/DELETE /v1/realtime/calls，415 是**子串包含**；503 槽位默认 1=num_pipelines）。
- Pacer 对齐上游 recv()：首帧即时、不足补零**不限首帧**、追赶用 **ceil** 且只认「等过数据」、Sleep 在途缓存 timestamp（重算会 ceil 跳帧死循环）、音频结束补 5 帧排空。Resampler 绝对输出游标+整数上取整（分块==整块）。
- session.created：WS 握手后发、WebRTC 由 datachannel onOpen 发（SendSessionCreatedAsync 幂等）。
- 环回测试教训：offer 的 track 必须与 answer 侧 codec/clock 有交集（PCMU vs OPUS → AudioIncompatible）。

## 评估套件（D030 resolved，2026-10-27）

- `src/SpeechToSpeech.Evals`（BigBenchAudio）+ `tools/EvalProbe`（run/compare/show/build-subset）。数据面：metadata.jsonl 1000 条、MP3 音频，HF 端点尊重 `HF_ENDPOINT`（可指 hf-mirror）。
- **PythonRandom**：CPython MT19937 逐位复刻（init_by_array/getrandbits/_randbelow/shuffle），同种子 build_subset 与上游 Python 选题逐位一致（互证过）。getrandbits 是同一流顺序消耗。
- 陷阱：NAudio 3.x `ISampleProvider.Read(Span<float>)`；MatchCollection 非泛型枚举要 `foreach (Match m in ...)`；上游 redact 无 `=` 的敏感 flag 保留名、下一值脱敏；`--push-to-hub`/job.py/prepare_eval_space 不移植（C032，报告本地 JSON）。
- 测试缝：`Dataset.MetadataOverride`（离线 metadata）、Runner `ConsumeAsync`/`FinishCapture` internal。

## macOS 音频（E001 resolved，2026-10-27）

- `PortAudioAudioDevices`（Cli/Audio/PortAudioDevices.cs）：上游 sounddevice 底层就是 PortAudio——macOS 路径**同引擎对齐**；Windows/Linux 维持 NAudio 后端不动。
- `PortAudioWaveIn`/`PortAudioWavePlayer` 实现 NAudio `IWaveIn`/`IWavePlayer`（含 `OutputWaveFormat`；`Volume` 是回调里 int16 就地缩放）；Pa_Initialize/Terminate 官方配对可重叠，逐设备配对即可；播放侧 framesPerBuffer=Unspecified 取最优块长，录制侧对齐上游 blocksize。
- 上游 talk 的 sounddevice 形态：`RawInputStream/RawOutputStream`，int16/单声道/blocksize 定块。
- 绑定冒烟 3 例（原生库解析/嵌套引用计数/枚举），macOS 真机未实跑（与 Linux ALSA 同口径）。

## 当前状态（2026-10-27）

差异账 64 条：resolved 48 / accepted 15 / gap 1（仅剩 D009 口型/说话人分离——架构性）。测试 381/381。参照环境：Python 3.13 venv 在 `~/.workbuddy/binaries/python/envs/default/Scripts/python.exe`（含 aiortc 1.15）。
