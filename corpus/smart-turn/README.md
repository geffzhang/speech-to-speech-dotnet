# Smart Turn 端点判定差分语料

守护的是**判定结论到两个期限的映射**：同一组配置与同一个判定结论（说完了 / 没说完 /
抛异常 / 没有判定器），本实现与上游给出的宽限、处理延迟、状态字符串是否相同；
以及判定器的输入窗口是否按同一条规则整理（取尾部 8 秒、短了在前面补零）。

这一层不看状态机本身（那是 `corpus/turns`），只看「状态机拿到的两个毫秒数从哪来」。

## 为什么单独做，而不是并进 corpus/turns

`corpus/turns` 把 `output_hold_ms` / `processing_delay_ms` 当**输入**喂给状态机，
刻意不关心它们是怎么算出来的。上游这两个数的产地是 `_smart_turn_timing_ms`：它读一个
ONNX 分类器的结论，再按四条分支映射成毫秒。

把产地也纳入比对，才能回答「用户说到一半停顿时，宽限到底是 800 还是 2000」。
两个层次分开的原因很实际：状态机那一层能逐值比对（纯内存、可注入时钟），
而判定器那一层**没有模型就不存在**——所以这里用脚本化判定器替换模型，
比的是判定器上下游的那段逻辑，而不是模型本身。

## 关于参照侧的 torch 桩

上游 `vad_handler.py` 顶层 `import torch`，参照环境没装（PyPI 不可达），
所以探针在 `sys.modules` 里塞了一个桩模块让导入通过。

这不影响结论：被测的 `_smart_turn_timing_ms` 与 `SmartTurnAnalyzer._prepare_audio`
都不调用任何 torch API，桩只在导入期被触碰。探针调的是**上游源码里那个方法本身**，
不是它的复制品——一旦上游改了映射规则，重录就会暴露。

## 输入约定

```json
{
  "note": "这条在守什么",
  "payload": {
    "mode": "timing",
    "classifier": "incomplete",
    "probability": 0.21,
    "config": {
      "speculative_reopen_ms": 800,
      "smart_turn_max_wait_ms": 2000,
      "smart_turn_incomplete_delay_ms": 600,
      "smart_turn_threshold": 0.5
    }
  }
}
```

- `mode=timing`：不需要音频。判定器是替身，结论由 `classifier` 与 `probability` 决定
- `mode=window`：`samples` + `pattern` 现场生成音频（`ramp` / `signed`），
  过一遍窗口整理。音频不进文件——128,000 个数写进 JSON 只会让语料变成噪声
- `classifier` 取值：`disabled`（没有判定器）/ `complete` / `incomplete` / `failed`（抛异常）

### 音频生成规则（两侧各写一份，但结果逐位相同）

```
ramp   : sample[i] = ((i * 37) % 1000) / 1000.0
signed : sample[i] = ((i * 37) % 2001 - 1000) / 1000.0
```

只用到整数运算与一次 `double → float32` 转换。IEEE 转换是确定性的，
所以两侧逐位相同——不用把音频存进语料。

## 两条归一化约定

**状态输出上游同名字符串**：`disabled` / `failed` / `complete` / `incomplete`。
上游的 `_last_smart_turn_status` 本来就是字符串（该字段最终会进 `response.metadata`
的 `smart_status`），照抄它才能保证将来接 D016 时两边对得上。

**窗口样本输出 float32 位模式（uint32）而不是十进制**。
理由与 `corpus/turns` 的 6 位小数一致但更严格：浮点的最短十进制表示是两侧运行时
各自的策略（Python 写 `0.037`，C# 写 `0.037` 但指数的写法可能不同），
比它就等于允许漂移。位模式比较同时锁住符号位、指数与尾数，没有容差余地。

为了让「数组不同」这句话可诊断，输出还带三项摘要：`leading_zeros`、`trailing_zeros`、
`head_bits` / `tail_bits`，以及整窗的 FNV-1a 64 位校验（`bits_hash`）。
不用哈希代替数组是为了失败时能看出是「补零补错边」还是「截断截错头」——
只有一个 hash 不同的话，只看得到「不一致」三个字。

## 两侧探针

| 侧 | 命令 | 作用 |
|---|---|---|
| 上游 | `tools/refprobe/smart_turn_probe.py` | 造一个只够跑 `_smart_turn_timing_ms` 的对象（跳过模型加载），调上游方法 |
| 本实现 | `tools/SmartTurnProbe` | 调产品入口 `TurnEndTiming.Decide` / `TurnAudioWindow.Prepare` |

本实现探针刻意**不自己重写一份决策**：产品改了、探针没改的话，diff 永远假绿。

录制与比对：

```bash
python ../.workbuddy/skills/cross-lang-porting/scripts/diff_corpus.py record \
  --corpus corpus/smart-turn \
  --cmd "<python> tools/refprobe/smart_turn_probe.py"

python ../.workbuddy/skills/cross-lang-porting/scripts/diff_corpus.py verify \
  --corpus corpus/smart-turn --json-canonical \
  --cmd "dotnet tools/SmartTurnProbe/bin/Debug/net10.0/SmartTurnProbe.dll"
```

## 13 条在守什么

| 语料 | 不变量 |
|---|---|
| `timing-disabled` | 没有判定器：宽限退回投机窗口、处理不推迟；分析耗时记 null 而非 0 |
| `timing-complete` | 判定说完了：宽限就是投机窗口（800），处理立刻开始 |
| `timing-incomplete` | 判定没说完：宽限拉到 `max_wait`（2000），处理推迟 600 |
| `timing-threshold-boundary` | `probability == threshold` 算**没说完**（上游是严格大于）；差一个等号宽限就从 2000 掉到 800 |
| `timing-incomplete-delay-clamped` | 处理延迟大于最长等待时取 `min(delay, max_wait)`；不钳制会让期限晚于自己的宽限 |
| `timing-incomplete-zero-delay` | 延迟可为 0，宽限仍然拉长——两项独立，不能互相代替 |
| `timing-failed` | 判定器抛异常：退回投机窗口、不推迟，状态记 `failed`（与「关掉了」区分） |
| `timing-narrow-windows` | 非默认配置（300 / 1500 / 400）：三条都取自配置而非写死的默认值 |
| `window-left-pads` | 不足 8 秒在前面补零；往后补会把整句挪到时间轴开头 |
| `window-tail-truncates` | 超过 8 秒取尾部；取头会让说得久的用户永远判不出说完 |
| `window-exact` | 正好 8 秒原样返回，是另两条的对照基准 |
| `window-empty` | 空音频整窗补零，不抛异常（phantom 触发后的空段会走到这里） |
| `window-signed-bits` | 含负值音频锁符号位；只看和或只看绝对值时符号反了也看不出来 |

## 压不住的部分

**模型本身**。分类器的推理没有移植（差异账 D018）：模型是 `pipecat-ai/smart-turn-v3`
的 ONNX 导出，还要 Whisper 的 log-mel 前端。这层语料证明的是「拿到了结论之后怎么办」，
不是「结论本身对不对」。没有模型 → 决策层走 `disabled` 分支 → 与「用户关掉了 Smart Turn」同形。

**真实时延**。`analysis_seconds` 只比「是不是 null」，不比数值——那是墙上时间。
真要测推理耗时，只能在有模型的机器上量。

**判定器内部的采样率转换**。上游非 16 kHz 走 `scipy.resample_poly`，本实现走自带的
`AudioUtil.Resample`，两者不逐样本相同。VAD 阶段送来的音频恒为 16 kHz，这条路只在
外部直接调 `Prepare` 时才走到，故语料只覆盖 16 kHz。
