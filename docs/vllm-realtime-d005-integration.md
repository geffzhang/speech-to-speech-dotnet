# D005 流式 STT 真实服务端联调手册

> **状态（2026-10-28）**：执行手册，尚未跑——手头没有 GPU 设备。文档自包含，拿到
> GPU 或 API key 后按节执行即可，不必回翻对话。
> 关联账本：`porting/differences.jsonl` 的 D005（resolved，fake 表征已闭合）；
> 本文覆盖的是 03-acceptance 里「真实服务端联调」那一格。

## 1. 背景与现状

D005 已移植上游 `streaming_handler.py` 的两条有状态流式转写后端
（`openai-realtime` / `vllm-realtime`），测试 47 例（协议 28 + 会话 14 + 后端全链 5）
**全部基于 fake WebSocket**——协议字段逐字对齐上游源码，但从未与真实服务握过手。

fake 验不了、必须在真实端点上验的五项：

| # | 验什么 | 为什么 fake 验不了 |
|---|---|---|
| 1 | 真实握手序列与 `session.updated` 的实际返回时机 | fake 里是按上游源码假定的顺序 |
| 2 | 真实事件流：`delta` 切分粒度、final 收口形状 | 服务端行为不受我方控制 |
| 3 | 错误路径：无效 model / 限流 / 半途断线的真实报错形状 | fake 只复刻了上游代码处理过的那些 |
| 4 | **vLLM discard 断连兜底**（「vLLM 无 clear 事件 → 断连重开」） | 依据是上游源码注释，非实测 |
| 5 | `connect_timeout` / `final_timeout` 在真网延迟下的合理性；代际与 partial 发布锁在真实乱序/重连下是否成立 | fake 无网络抖动 |

## 2. 路径选择

| 路径 | 需要 | 覆盖范围 | 适用 |
|---|---|---|---|
| A. 自建 vLLM Realtime | NVIDIA GPU：**约 4 GB**（Qwen3-ASR-0.6B）/ ≥16 GB（Voxtral），Linux/WSL2/Docker | 全部 5 项 | 有 GPU 时首选 |
| B. 容器镜像 | 同上 GPU，省去配环境 | 同 A | 不想配 CUDA 环境 |
| C. OpenAI hosted | `OPENAI_API_KEY`，无 GPU | 1/2/3/5（缺 vLLM 特有的第 4 项） | **当前无 GPU 时的推荐起点** |

C 验证的是同一条客户端状态机（握手 / 代际 / partial 发布 / 超时），只有 vLLM 特有的
discard 断连兜底一项留待 GPU 到位补测。

## 3. 方案 A：自建 vLLM Realtime

### 3.1 安装（WSL2 或 Linux）

```bash
uv pip install -U vllm --torch-backend=auto \
  --extra-index-url https://wheels.vllm.ai/nightly/cu130
```

国内下载设 `HF_ENDPOINT` 镜像。

### 3.2 模型选型与启动

vLLM 官方 supported models 的 **Realtime Transcription** 表只有两个架构
（其余 ASR 模型如 Qwen3-ASR-1.7B / Whisper 只支持 `/v1/audio/transcriptions`
非流式端点，**不**挂 `/v1/realtime`）：

| 架构 | 模型 | 显存 | serve 注意 |
|---|---|---|---|
| `Qwen3ASRRealtimeGeneration` | `Qwen/Qwen3-ASR-0.6B` | **约 4 GB 即可**（0.6B BF16 ≈ 1.2 GB 权重 + KV/激活） | 架构**不能从 config.json 自动识别**，必须 `--hf-overrides '{"architectures":["Qwen3ASRRealtimeGeneration"]}'` |
| `VoxtralRealtimeGeneration` | `mistralai/Voxtral-Mini-4B-Realtime-2602` | ≥16 GB（BF16 4B） | 需 `mistral-common[audio]` + `--tokenizer-mode mistral` |

**联调首选 Qwen3-ASR-0.6B**——门槛低一个量级，且中文正是它的强项（30 语种 + 22 中文方言）：

```bash
VLLM_DISABLE_COMPILE_CACHE=1 vllm serve Qwen/Qwen3-ASR-0.6B \
  --hf-overrides '{"architectures":["Qwen3ASRRealtimeGeneration"]}' \
  --gpu-memory-utilization 0.85 --host 0.0.0.0 --port 8000
```

Voxtral 路径（对照/英文场景）：

```bash
uv pip install "mistral-common[audio]"
VLLM_DISABLE_COMPILE_CACHE=1 vllm serve mistralai/Voxtral-Mini-4B-Realtime-2602 \
  --compilation_config '{"cudagraph_mode": "PIECEWISE"}' \
  --tokenizer-mode mistral --config-format mistral --load-format mistral \
  --gpu-memory-utilization 0.85 --host 0.0.0.0 --port 8000
```

启动成功判据（两模型相同）：日志出现 `Route: /v1/realtime`；
`curl http://localhost:8000/v1/models` 返回 served 模型名——
**Realtime 协议要求 model 标识与 `/v1/models` 一致**（含 serve 时的自定义名）。

> 与上游账面的一个偏差要记住：上游 `vllm_realtime_stt_model` 默认值是
> `Qwen/Qwen3-ASR-1.7B`，但 1.7B 在 vLLM 里只有非流式架构——**上游这个默认值
> 在 Realtime 后端下本身就不能直接用**（必须 serve 了对应 id 才行，上游文档也是
> 这么说的）。本实现默认值同上游；联调时显式传 `Qwen/Qwen3-ASR-0.6B` 即可。

### 3.3 方案 B：容器替代 3.1/3.2

```bash
docker run --rm --gpus all --shm-size 4g -p 8000:8000 \
  -e VLLM_DISABLE_COMPILE_CACHE=1 \
  registry.redhat.io/rhaiis-preview/vllm-cuda-rhel9:voxtral-realtime \
  --model mistralai/Voxtral-Mini-4B-Realtime-2602 \
  --tokenizer-mode mistral --config-format mistral --load-format mistral \
  --gpu-memory-utilization 0.85 --host 0.0.0.0 --port 8000
```

跑 Qwen3-ASR-0.6B 时用通用 vLLM 镜像（或同一镜像），`--model` 换掉并带上
§3.2 的 `--hf-overrides` 即可，`--tokenizer-mode mistral` 三件套不要带。

## 4. 方案 C：OpenAI hosted（无 GPU）

```bash
export OPENAI_API_KEY=...
speech-to-speech local --stt openai-realtime --openai-realtime_model gpt-live-transcribe
```

- hosted 端点用 transcription session（`intent=transcription`），model 在
  session 里配、不在 URL 里
- hosted 必须 24 kHz：本实现 `--openai-realtime_sample_rate` 默认 24000，
  其它值在 setup 校验即拒（与上游一致）；管线 16 kHz 音频由后端重采样
- API key：`--openai-realtime_api_key` 显式传，或缺省读 `OPENAI_API_KEY`
  （仅 hosted 默认端点隐式回退，自定义端点不带）

## 5. 本项目对接与参数映射

vLLM 路径跑起来后：

```bash
speech-to-speech local \
  --stt vllm-realtime \
  --vllm-realtime_base_url ws://localhost:8000/v1 \
  --vllm-realtime_model <3.2 查到的 served 名>
# 服务端要鉴权时：--vllm-realtime_api_key <token>
```

### 5.1 参数名映射（本实现 vs 上游）

**上游文档的命令不能照抄**——上游 STT 参数带 `_stt` 段，本实现统一用
`{backend}_*` 模板（差异账 D036，accepted）。连字符/下划线两写法本实现都接受。

| 上游 | 本实现（下划线写法） | 默认值 |
|---|---|---|
| `vllm_realtime_stt_base_url` | `vllm_realtime_base_url` | `ws://localhost:8000/v1` |
| `vllm_realtime_stt_api_key` | `vllm_realtime_api_key` | 无 |
| `vllm_realtime_stt_model` | `vllm_realtime_model` | `Qwen/Qwen3-ASR-1.7B`（上游默认；1.7B 无 Realtime 架构，联调实际传 `Qwen/Qwen3-ASR-0.6B`） |
| `vllm_realtime_stt_audio_sample_rate` | `vllm_realtime_sample_rate` | 16000 |
| `vllm_realtime_stt_connect_timeout` | `vllm_realtime_connect_timeout` | 10.0 s |
| `vllm_realtime_stt_final_timeout` | `vllm_realtime_final_timeout` | 60.0 s |

OpenAI 路径同理：`openai_realtime_stt_model` → `openai_realtime_model`，余同。

### 5.2 协议要点（联调时的对照基准）

- vLLM Realtime 收 **16 kHz mono PCM16**，base64 装帧——与本管线速率一致，零重采样直通
- vLLM dialect `requires_session_updated = False`：`session.update {model}` 发出后
  不等回执即开始；`start_utterance` = `commit {final:false}`，`finish_utterance` =
  `commit {final:true}`；**无 clear 事件**（discard 的断连兜底即针对此）
- OpenAI dialect 相反：等 `session.updated`、`start_utterance` 为空、discard 有 clear
- 服务端事件：`transcription.delta`（部分转写）/ `transcription.done`（终稿）/
  `error`——与本实现 `parse_event` 的三分支一一对应

## 6. 联调执行清单

按 §1 表逐项记录，结果回写 `porting/03-acceptance.md` 的 D005 段：

1. **握手**：连上后按顺序收到什么？vLLM 是否真发 `session.created`？（协议测试假定发）
2. **事件流**：对麦克风说一句 10 秒话，`delta` 应在说话过程中逐段到达
   （对照：ManySpeech 是整段结束才出 `completed`）；`done` 带 `text` 收口
3. **错误形状**：故意传错 model 名 → 真实 `error` 事件的 `message` 形状；
   半途杀掉服务端 → 客户端 `ensure_connection` 是否按 `connect_timeout` 重连
4. **discard 兜底**（仅 vLLM）：打断对话后确认被弃段的 PCM 不漏进下一轮
   （`retired_item_ids` / 断连重开路径）
5. **超时标定**：真网延迟下 `connect_timeout=10` / `final_timeout=60` 是否够用；
   若 final 收口在长句下超时，放宽而非改判据

## 7. 对拍（可选但最有说服力）

同一音频分别喂上游 Python 实现与本实现，比三样：**delta 切分点、终稿文本、
事件序列**。推流用 `tools/smoke/` 既有脚本改造，三要素别丢：
真实时长 20 ms 帧 + 尾补 1.5 s 静音 + `proxy=None`（Windows 代理劫持 127.0.0.1 的老坑，D029）。
Python 参照环境见工作记忆（`~/.workbuddy/binaries/python/envs/default`）。

## 8. 风险预期

风险定级「低」：协议与状态机已被 47 例 fake 表征 + 上游源码逐字段对齐钉住，
联调大概率暴露的是时序参数与错误形状微调，不是结构性返工。唯一真正的未知数
是第 4 项——上游注释说 vLLM「无 clear」，若实际已加 clear 事件，断连兜底可简化
（简化也要先过 fake 测试再改）。

## 9. 参考

- 上游文档：参照仓库 `docs/openai-compatible-stt.md`（Stateful streaming STT 节）
- vLLM Realtime API：https://docs.vllm.ai/en/stable/serving/online_serving/speech_to_text/#realtime-api
- OpenAI Realtime transcription：https://developers.openai.com/api/docs/guides/realtime-transcription
- 本实现代码：`src/SpeechToSpeech.Backends.OpenAiCompatible/StreamingStt/`（四文件）
- 本实现测试：`tests/SpeechToSpeech.Tests/StreamingStt*.cs`（47 例）
