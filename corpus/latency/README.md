# 响应时延账差分语料

守护的是**时延账的状态迁移与对外的那个字符串**：同一串操作喂给上游 `TurnLatencyStore`
与本实现 `TurnLatencyStore`，每一步的可观察结果、终态 `metadata` 里那段压缩 JSON、
以及那行人类可读日志是否逐字符相同。

这一层不看协议对象长什么样（那是 `corpus/protocol`），只看「时延记录是怎么攒出来、
怎么被摘掉、最后写成了什么」。

## 为什么单独做，而不是并进 corpus/protocol

`corpus/protocol` 的输入是**事件**（`response.create` / `audio.done` …），驱动的是
整条事件分发链。时延账的输入是**一串时序操作**：STT 先于响应键存在、待定槽位被
响应吸收、同一轮的多条响应各自记账、会话清理只清自己的。这些都不是事件能表达的，
而且需要精确控制每个时间点——用真实时钟就没有可复现性。

所以这里的输入是操作脚本，时间全部由语料给定。这是第三层「比状态迁移不比字节」的
语料（前两层是 `corpus/turns` 与 `corpus/smart-turn`）。

## 三条不变量

1. **挂起 ≠ 丢弃**。STT 跑完时还没有 response_key，测量先挂在按轮次的待定槽位上，
   响应建立时 `absorb_pending` 并进来。就地丢弃会让 `stt_s` 永久为 null。
2. **worker 只查不建**。响应键由服务端在入队之前创建。被取消的 worker 若能自己新建，
   就给一条没人要的响应留下永远不结算的账，还会把已经清掉的表重新撑起来。
3. **记账不复活**。`pop` / `discard_response` / `clear_session` 之后，同一条响应键
   再查必须是空——不是「查到了但内容为空」。后者会让客户端以为这轮有遥测。

## 两个逐字符点

`hold_s` 记的是等待区间的**并集**，不是各段相加：四个 worker 同时等 0.2 秒是 0.2 秒，
相加会算成 0.8 秒。`hold-union-overlapping` 用两段重叠区间锁这一条。

终态 `metadata` 里那段字符串逐字符比 —— 数字相等不等于对客户端相等：

| 值 | 客户端看到的 | 不是 |
| --- | --- | --- |
| `2.0` | `2.0` | `2` |
| `1e-07` | `1e-07` | `1E-07` |
| `1.2345678901234`（`vad_decision_s`） | `1.23456789` | `1.2345678901234` |

前两行是浮点最短表示在两套运行时里的默认写法差异，第三行是上游对这四项新字段做了
`round(x, 9)`。三者都写错了也不会抛异常，只是客户端拿到的字符串不同。

## 输入约定

```json
{
  "ops": [
    {"op": "stt", "turn_id": "turn_1", "turn_revision": 0, "seconds": 0.18},
    {"op": "for_response", "response_key": "resp_1", "turn_id": "turn_1", "turn_revision": 0},
    {"op": "payload", "response_key": "resp_1", "status": "completed"}
  ]
}
```

操作与上游 API 一一对应，不发明聚合操作：

| 操作 | 对应 |
| --- | --- |
| `for_turn` / `pending` / `discard_pending` | `get_or_create_for_turn` / `pending_for_turn` / `discard_pending_turn` |
| `stt` / `vad_settle` / `smart` | 在待定槽位上写 `record_stt` / `record_vad_settle` / 三个 smart 字段 |
| `smart_wait` | `store.record_smart_wait`（区间并集） |
| `for_response` | `get_or_create_response`（触发 absorb） |
| `record_llm` / `record_ttfa` / `record_e2e` / `set_vad_decision` | 响应账上的同名方法 |
| `record_resp_ttfa` / `record_resp_e2e` | `record_response_tts_ttfa` / `record_response_e2e` |
| `anchor_request` / `anchor_attach_existing` / `anchor_close` / `anchor_available` | `SpeechLatencyAnchor` 的四件事 |
| `pop` / `discard_response` / `clear_session` | 同名方法 |
| `payload` | `metadata_payload` + `json.dumps(sort_keys=True, separators=(",",":"))` |
| `log` | `format_log_line` |

`anchor_attach_existing` 对应工具续接链：同一条链上的第二条响应共用第一份名额。
上游在 `bind_response_latency_tracker` 里从 `speech_latency_anchors[(turn_id, revision)]`
查出来后挂上，探针把这一步显式写成操作，免得「名额共享」这件事藏在夹具里。

## 录制

```bash
python <skill>/scripts/diff_corpus.py record --corpus corpus/latency \
  --cmd "python tools/refprobe/latency_probe.py"

python <skill>/scripts/diff_corpus.py verify --corpus corpus/latency --json-canonical \
  --cmd "dotnet tools/LatencyProbe/bin/Debug/net10.0/LatencyProbe.dll"
```

基线只能由参照实现录。`float-rendering` 那条是从**实测差异**里来的，不是猜的：
动手前先拿 12 个值在两侧各跑一遍格式化，确认 `.2f` / `F2` 连中点舍入都一致
（`0.125 → 0.12`、`0.135 → 0.14`），然后才确认最短表示的这两处真的不同
（`2.0` vs `2`、`1e-07` vs `1E-07`）。没有这一步，`RenderFloat` 里那两个补丁就是凭印象写的。

## 刻意不放进来的

- `mlx_lock_wait` / `mlx_lock_hold`：只在 macOS 终态日志里出现，Windows 上恒不打印，
  没有可比对的基线（与 E001 macOS 音频缺口同一片区）。
- 出带外响应（`conversation="none"`）：本实现尚未建模这个概念，`OutOfBand` 恒为 false。
  合并规则里那一支由 `tests/SpeechToSpeech.Tests/ResponseMetadataTests.cs` 单测锁住。

- **测量点怎么接线**：`stt_s` / `vad_decision_s` / `smart_status` 的写入分散在三个 handler 里，
  上游则是内联在 900 行的 `_process_realtime` 与各 STT 后端里，没有可调用的单元。
  把这些抄进探针等于**手写基线**，所以这一层不进语料，改由
  `tests/SpeechToSpeech.Tests/TurnLatencyWiringTests.cs` 与 `VadTurnWiringTests.cs` 用断言锁值
  （含 `vad_decision_s` 的算术、`disabled` 时三项不写、metadata 字符串里的实际数值与键序）。
  语料锁「账本怎么算」，单测锁「谁往账本里写」——两件事分开，是因为前者能差分、后者不能。
