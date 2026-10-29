# SenseVoice 设为默认 STT：实施计划

> **供智能体执行：** 必须使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 子技能，逐项执行本计划。使用复选框（`- [ ]`）跟踪步骤。

**目标：** 将 SenseVoice 设为默认本地 STT，并自动下载其固定版本 INT8 模型，同时保留 Whisper 和 Paraformer 的可选能力。

**架构：** 修改 CLI 默认后端及现有模型准备器、清单和下载脚本，使它们按实际使用的后端选择和校验模型组。继续使用现有完整性校验与断点续传逻辑，不改动 STT 推理实现。

**技术栈：** .NET 10、C#、xUnit、Bash、PowerShell、固定版本 TSV 模型清单。

## 全局约束

- 默认 SenseVoice 模型使用固定版本 `csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17` 的 INT8 导出。
- 模型来源指向 FunASR 模型许可；不得将权重描述为 Apache-2.0。
- `--stt whisper` 和 `--stt paraformer` 仍须可用。
- 显式指定的自定义模型目录仍由用户管理；缺少必要文件时，必须在监听前报错。
- 保留下载器的大小/SHA-256 校验、断点续传和清单路径校验。
- 所选后端缺少或未通过校验的文件时，不得静默切换到其他 STT 后端。

---

### 任务 1：补充 SenseVoice / Paraformer 模型组选择测试

**文件：**
- 修改：`tests/SpeechToSpeech.Tests/LocalModelPreparationTests.cs`

**接口：**
- 使用：现有的 `CommandLine.Parse`、`LocalModelPreparation.BuildPlan` 和 `ApplyDefaultPaths`。
- 产出：覆盖默认 SenseVoice、显式选择 Whisper / Paraformer 及其默认模型路径的测试。

- [ ] **步骤 1：添加测试，确认省略 STT 参数时选择 SenseVoice 资源**

添加测试，断言 `CommandLine.Parse(["serve"])` 生成的计划选择 `sense-voice`，而不是 `whisper`；应用默认路径后，STT 模型目录为 `models/sense-voice`。

- [ ] **步骤 2：添加显式选择 Whisper 和 Paraformer 的测试**

添加参数化测试或独立测试，断言 `--stt whisper` 只选择 `whisper` STT 模型组，`--stt paraformer` 只选择 `paraformer` 组。

- [ ] **步骤 3：运行聚焦测试并确认按预期失败**

运行：`dotnet test tests/SpeechToSpeech.Tests --filter FullyQualifiedName~LocalModelPreparationTests`

预期：失败，因为当前模型准备器默认使用 Whisper，且没有 SenseVoice / Paraformer 的自动准备组。

### 任务 2：在固定版本模型清单与下载脚本中加入模型组

**文件：**
- 修改：`tools/models/models.tsv`
- 修改：`download-models.ps1`
- 修改：`download-models.sh`
- 测试：`tools/models/test_download_models.py` 或现有下载脚本测试

**接口：**
- 产出：新增 `sense-voice` 和 `paraformer` 两个模型组；支持通过 `-Groups` / `--groups` 选择；清单仍使用 SHA-256、字节数、相对路径和固定 URL 的 TSV 格式。

- [ ] **步骤 1：检查现有下载器测试与模型组校验**

找到脚本测试文件，确认目前如何断言模型组白名单以及按组筛选清单条目。

- [ ] **步骤 2：添加新模型组的失败测试**

添加测试，确认 `sense-voice` 和 `paraformer` 是有效组名，并且选择其中一组时只选中属于该组的清单项。

- [ ] **步骤 3：运行脚本测试并确认按预期失败**

运行仓库现有 Python 测试命令，指定下载器相关测试。

预期：失败，因为当前白名单未包含这两个后端专属模型组。

- [ ] **步骤 4：添加固定版本 SenseVoice INT8 清单项**

仅添加固定提交 `2365baeacb507f821a0c8120fcee3d484dba7a07` 中的 `model.int8.onnx` 与 `tokens.txt`：

| 文件 | 字节数 | SHA-256 |
|---|---:|---|
| `model.int8.onnx` | 239233841 | `c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51` |
| `tokens.txt` | 315894 | `f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc` |

使用该固定提交对应的 Hugging Face resolve URL，目标路径放在 `sense-voice/` 下。最终确定清单项前，须确认 URL 返回的文件大小和 SHA-256 与上表一致。

- [ ] **步骤 5：添加固定版本 Paraformer 清单项**

为固定版本 `def027084691107096b5ebba69785756d63de6c5` 的 `csukuangfj/sherpa-onnx-paraformer-zh-2023-09-14` 添加 `model.int8.onnx`、`config.yaml`、`tokens.txt` 和 `am.mvn`。添加前，逐项从来源获取并验证确切字节数与 SHA-256。目标路径放在 `paraformer/` 下。

验证数据：

| 文件 | 字节数 | SHA-256 |
|---|---:|---|
| `model.int8.onnx` | 243371218 | `f36a0433bcf096bd6d6f11b80a3ac8bed110bdca632fe0d731df8d1a84475945` |
| `config.yaml` | 56799 | `6244fb74572573abcf7127283c9c20b440a2f43d4891a5d8ede0eee351855d87` |
| `tokens.txt` | 75756 | `59aba8873a2ed1e122c25fee421e25f283b63290efbde85c1f01a853d83cb6e6` |
| `am.mvn` | 11203 | `29b3c740a2c0cfc6b308126d31d7f265fa2be74f3bb095cd2f143ea970896ae5` |

- [ ] **步骤 6：在两个下载脚本中加入新组名**

更新 `download-models.ps1` 和 `download-models.sh` 的模型组白名单，加入 `sense-voice` 和 `paraformer`；保留现有参数写法及不指定组时下载全部模型的行为。

- [ ] **步骤 7：运行下载器聚焦测试**

运行现有脚本测试命令。

预期：新模型组筛选测试及现有断点续传、完整性校验测试全部通过。

### 任务 3：设置默认后端为 SenseVoice，并准备匹配的模型资源

**文件：**
- 修改：`src/SpeechToSpeech.Cli/CommandLine.cs`
- 修改：`src/SpeechToSpeech.Cli/LocalModelPreparation.cs`
- 测试：`tests/SpeechToSpeech.Tests/LocalModelPreparationTests.cs`
- 测试：`tests/SpeechToSpeech.Tests` 中的 CLI 选项构建测试

**接口：**
- 产出：省略 STT 参数时使用 `sense-voice`；模型准备器添加对应默认路径，并只下载当前所选后端的模型组。

- [ ] **步骤 1：让现有模型准备测试清单包含每个模型组**

更新 `LocalModelPreparationTests` 中的临时清单，为所有支持的组各加入一个测试条目：`vad`、`whisper`、`sense-voice`、`paraformer`、`llm`、`tts` 和 `smart-turn`，使组选择测试能验证真实的计划行为。

- [ ] **步骤 2：运行聚焦测试确认失败**

运行：`dotnet test tests/SpeechToSpeech.Tests --filter FullyQualifiedName~LocalModelPreparationTests`

预期：新增的默认后端和显式选择断言失败。

- [ ] **步骤 3：修改 CLI 默认 STT 后端**

将 `OptionsBuilder.Build` 中的 STT 默认值从 `"whisper"` 改为 `"sense-voice"`。保留显式 `--stt` 参数解析。

- [ ] **步骤 4：在模型准备器中添加后端专属分支**

在 `LocalModelPreparation.BuildPlan` 中解析后端对应模型目录参数，例如 `sense_voice_model_dir`、`paraformer_model_dir`、`whisper_model_dir`，并继续兼容共用的 `stt_model_dir`。

未指定自定义路径时，只添加当前后端的模型组并设置其默认目录。自定义目录的必需文件校验规则如下：

- SenseVoice：`model*.onnx`、`tokens.txt`
- Paraformer：`model*.onnx`、`config.yaml`、`tokens.txt`
- Whisper：`encoder*.onnx`、`decoder*.onnx`、`conf.json`

- [ ] **步骤 5：运行模型准备及选项聚焦测试**

运行：`dotnet test tests/SpeechToSpeech.Tests --filter FullyQualifiedName~LocalModelPreparationTests`

预期：通过默认 SenseVoice、显式 Whisper、显式 Paraformer、远程后端和自定义路径相关测试。

### 任务 4：更新默认 STT、替代后端和许可说明

**文件：**
- 修改：`README.md`

- [ ] **步骤 1：更新模型准备说明**

说明固定版本 SenseVoice 模型组及约 228.5 MiB 的大小，并链接 FunASR Model Open Source License。保留总下载量说明，并明确自动准备的模型取决于所选后端。

- [ ] **步骤 2：更新模型表格与手动下载命令**

将默认 STT 表项改为 SenseVoice，并列出可显式选择的 Whisper、Paraformer，包括各自模型目录和模型组。添加下载 SenseVoice 默认模型、选择并下载替代后端的命令。

- [ ] **步骤 3：搜索 README 中过期的默认值说明**

运行：`rg -n "default.*Whisper|Whisper.*default|whisper-base|五组|39 个文件|6,612,701,771|whisper_model_dir" README.md`

预期：剩余命中仅用于描述显式选择的 Whisper；有关默认后端、模型组数量及总资源量的描述均已更新。

### 任务 5：完整验证功能

**文件：**
- 验证：`tools/models/models.tsv`
- 验证：`download-models.ps1`
- 验证：`download-models.sh`
- 验证：`README.md`

- [ ] **步骤 1：验证每条新增清单项**

逐条确认 SenseVoice / Paraformer 固定 URL 可访问、下载字节数与 TSV 一致、SHA-256 匹配。验证后不要将下载的模型文件留在仓库中。

- [ ] **步骤 2：运行 .NET 聚焦测试**

运行：`dotnet test tests/SpeechToSpeech.Tests --filter FullyQualifiedName~LocalModelPreparationTests`

预期：通过。

- [ ] **步骤 3：运行下载器测试**

运行仓库现有的 Python 下载器聚焦测试命令。

预期：通过。

- [ ] **步骤 4：构建 CLI**

运行：`dotnet build src/SpeechToSpeech.Cli`

预期：退出码为 0，构建成功。

- [ ] **步骤 5：运行完整 .NET 测试套件**

运行：`dotnet test tests/SpeechToSpeech.Tests`

预期：全部通过。如果无关的时序敏感测试失败，需单独重跑并同时报告两次运行结果。

---

## 执行记录

- [x] 下载脚本组白名单、模型组过滤测试及默认模型清单期望资产。
- [x] CLI 默认 SenseVoice，模型准备按后端选组并校验自定义目录。
- [x] Paraformer/SenseVoice 资源固定到上述提交并补齐清单哈希、大小。
- [x] README 默认后端、替代后端、许可证和资源总量说明。
- [x] `LocalModelPreparationTests` 聚焦验证通过（25/25）。
- [x] 下载器 Python 全套、CLI build、完整 .NET 测试：分别通过（14/14、0 警告/错误、713/713）。
- [x] SenseVoice ONNX 的 pinned LFS 元数据与大小确认；SenseVoice tokens 及 Paraformer 三个小文件已下载并重算 SHA-256。Paraformer ONNX 的 SHA-256 来自 pinned Hugging Face LFS 元数据，当前网络未能完整下载该 243 MB 文件以作独立重算。
- [ ] SenseVoice 真实 ONNX 推理加载尚未验证；不应声称 `OfflineRecognizer` 空 config 真实推理通过。
