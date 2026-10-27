# 投机轮次差分语料

守护的是**状态迁移**：同一份操作脚本，本实现的 `SpeculativeTurnTracker` 与上游是否
逐步给出同样的裁决（accept / hold / drop）、同样的轮次标识、同样的期限。

这一层不看线路形状（那是 `corpus/protocol` 的事），也不看文本切分（`corpus/text`）。
它看的是"用户停顿一下"这类场景里最容易被写错、又最容易在真实对话里炸的那部分。

## 为什么要单独做

上游 `SpeculativeTurnTracker` 是 488 行、纯内存、无 IO 的状态机。它有三个特点让它
特别适合做逐值比对：

- **自包含**：不碰模型、不碰网络、不碰磁盘，输入是方法调用，输出是返回值
- **可注入时钟**：所有期限都是单调时钟上的绝对时刻，换成虚拟时钟就能确定性复现
- **错误代价高**：这里判错一次，表现是"回复卡住不播"或"用户停顿一下就变成两轮"，
  都是很难从日志反推的故障

纯靠单元测试只能证明"我认为它对"，靠这套语料才能证明"跟上游一致"。

## 输入约定

```json
{
  "note": "这条在守什么",
  "payload": {
    "clock_start": 1000.0,
    "steps": [
      {"op": "speech_started", "audio_ms": 0}
    ]
  }
}
```

- `clock_start` 是虚拟时钟起点（秒）。两侧从同一个值出发，脚本用 `advance` 推时间
- `steps` 是一串操作，两侧按同一顺序执行，逐步输出结果
- `note` 只给人看，不进比对

### 操作全集

| op | 参数 | 说明 |
|---|---|---|
| `advance` | `seconds` | 推进虚拟时钟 |
| `speech_started` | `audio_ms` | 用户开口：复开上一轮还是开新轮 |
| `speech_candidate_started` | `audio_ms` | VAD 疑似续说，先压住输出 |
| `speech_candidate_cancelled` | — | 判定不是语音，恢复放行 |
| `has_speech_candidate` | — | 是否有待确认候选 |
| `segment_finalized` | `audio_ms`, `output_hold_ms`, `processing_delay_ms` | 一段语音定稿，记两个独立期限 |
| `segment_discarded` | — | 这一段放不出去 |
| `processing_deadline` | `turn_id`, `revision` | 查询处理期限 |
| `gate` | `turn_id`, `revision`, `hold_for_grace`, `hold_until`, `commit` | 不带等待的裁决 |
| `is_latest` / `is_current_turn` / `is_committed` | `turn_id`, `revision` | 相关性查询 |
| `close` | `turn_id`, `revision` | 响应终态后释放提交态 |
| `begin_reopen_candidate` | `turn_id`, `revision` | 开复开候选 |
| `confirm_reopen_candidate` | `turn_id`, `base_revision`, `candidate_revision` | 确认复开 |
| `cancel_reopen_candidate` | `turn_id`, `candidate_revision` | 撤候选 |
| `start_reopen_grace` | `turn_id`, `revision`, `grace_seconds` | 单独开宽限（只延长） |
| `start_turn` / `configure_reopen` / `reset` | — / `unanswered_reopen_ms` / — | 游标与配置 |
| `observe` | — | 快照：相位、当前轮、是否有候选 |

## 两条归一化约定

**枚举输出线上值**：`soft_ended` 而不是 `SoftEnded`。上游的 `TurnPhase` 是 `str` 枚举，
本实现是 C# 枚举，只有约定成同一个字符串才能比。

**浮点输出 6 位小数的字符串**。期限是 `起点 + delta/1000.0` 这种浮点累加的结果，
两侧的十进制最短表示可能不同（Python 写 `1000.0`，C# 写 `1000`）。统一格式化为
`"1000.400000"` 之后，比较就退化成字符串相等，不需要容差——有容差就等于允许漂移。

## 两侧探针

| 侧 | 命令 | 作用 |
|---|---|---|
| 上游 | `tools/refprobe/turns_probe.py` | 把模块里的 `time` 换成虚拟时钟，驱动上游实现 |
| 本实现 | `tools/TurnsProbe` | 用 `Func<double>` 注入同一个虚拟时钟 |

录制与比对：

```bash
python ../.workbuddy/skills/cross-lang-porting/scripts/diff_corpus.py record \
  --corpus corpus/turns \
  --cmd "<python> tools/refprobe/turns_probe.py"

python ../.workbuddy/skills/cross-lang-porting/scripts/diff_corpus.py verify \
  --corpus corpus/turns --json-canonical \
  --cmd "dotnet tools/TurnsProbe/bin/Debug/net10.0/TurnsProbe.dll"
```

## 13 条在守什么

| 语料 | 不变量 |
|---|---|
| `reopen-window` | 复开窗口按**流过的音频毫秒**算，不是墙上时间；上限含等号（7000 可、7001 不可） |
| `hold-not-drop` | 挂起不等于丢弃：VAD 复查期间只压住，候选取消后原样放行 |
| `committed-never-held` | 已提交的轮次永不再挂起；之后再来的 grace 与定稿一律被忽略 |
| `close-keeps-answered` | 收口后仍是当前轮且仍算已应答，但不能复开；下一轮才陈旧 |
| `revision-supersede` | 复开是同一轮 revision+1（序号不动），旧修订的带标签工作必须 DROP |
| `pending-hold-expires` | 候选挂起 2 秒自动过期：输出放行，候选仍留着等 VAD |
| `grace-only-extends` | 宽限只延长不缩短，后来的短 grace 不能提前放行 |
| `segment-discarded` | 丢弃的段撤候选并退回 SOFT_ENDED，正在听时也要退 |
| `reset-zeroes` | 重置把序号归零，新会话第一轮仍叫 `turn_1` |
| `configure-reopen-zero` | 上限可配；0 表示只有同一音频位置能复开，负值按 0 |
| `confirm-stale-candidate` | 候选确认按三元组严格匹配，对不上就 false，绝不糊弄认下 |
| `processing-deadline-scope` | 处理期限只对当前轮的当前修订有效，提交即清 |
| `legacy-no-turn-label` | 无标签工作一律放行且不参与提交记账 |

## 压不住的部分

**阻塞式等待**（`WaitForGate`）不在语料里：它会让探针真的睡在期限上，比对就变成
比谁更慢。改用 `tests/SpeechToSpeech.Tests/WaitForGateTests.cs` 的 7 条用例锁
"等待者怎么被叫醒"——被取代的修订要立刻返回 false、grace 到期要回来、等待时长要能
报给观察者。

**并发**：上游用 `threading.Condition`，本实现用 `Monitor`，两者语义等价但调度顺序
不可复现。语料只覆盖单线程可观察结果，并发正确性靠锁的粒度设计（所有状态只在
`_lock` 内读写）保证，不靠测试撞运气。
