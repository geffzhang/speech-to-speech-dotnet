# `local` Automatic Model Download Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prepare missing pinned models for `local` before it listens, so clients never connect to a local server that cannot build its VAD.

**Architecture:** Add an optional group filter to the existing PowerShell and Bash download scripts. A CLI preflight selects groups for active built-in local backends, invokes the platform script against the shared manifest and repository `models/` directory, and only then builds backend context and starts Kestrel. Explicit custom paths remain user-managed and are validated before listening.

**Tech Stack:** .NET 10 / C#, PowerShell 5.1+, Bash, curl, Python unittest integration tests, xUnit.

**Spec:** [2026-10-29-local-auto-model-download-design.md](../specs/2026-10-29-local-auto-model-download-design.md)

## Global Constraints

- `local` automatically prepares missing default local models without confirmation.
- Continue using the pinned `tools/models/models.tsv` and existing PowerShell/Bash transfer scripts.
- The default model directory is `models/` under the repository root.
- `serve` and `talk` behavior remains unchanged.
- Never overwrite or automatically substitute a user-specified custom model path.
- Do not select groups for remote backends.
- If download or custom-path validation fails, do not start a listener.

## Review Focus

- Missing VAD must not be deferred until the first WebSocket connection; assert the download process finishes before `RealtimeServer.Build`/`StartAsync`.
- A remote STT, LLM, or TTS backend must not trigger download of its unused local model group; assert the exact selected group list.
- A custom model path must never be replaced with a default path; assert missing custom files fail before the listener.
- Script non-zero exit, missing script, and unsupported platform shell must surface an error and prevent startup.
- An empty, repeated, or unknown group filter must not silently download all models; reject it before any HTTP request.

---

### Task 1: Add explicit model-group filtering to both download scripts

**Files:**
- Modify: `download-models.ps1`
- Modify: `download-models.sh`
- Modify: `tools/models/test_download_models.py`
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-10-29-local-auto-model-download-design.md`

**Interfaces:**
- PowerShell accepts `-Groups vad,whisper,llm,tts,smart-turn`.
- Bash accepts `--groups vad,whisper,llm,tts,smart-turn`.
- Omitted group option preserves current behavior (download all manifest entries).
- A provided group selects rows whose first path segment matches a requested group. Unknown, empty, or duplicate group names fail before any file or network side effects.

- [ ] **Step 1: Write failing integration tests**

Add these tests to `tools/models/test_download_models.py`:

1. `test_group_filter_downloads_only_selected_group` — include `vad/one.onnx`, `whisper/one.onnx`, and `tts/one.onnx` against a local server; request `vad,tts`; assert only the two selected files are requested and published.
2. `test_group_filter_rejects_empty_unknown_and_duplicate_groups` — for `""`, `unknown`, and `vad,vad`, assert nonzero exit, no HTTP requests, and no files.
3. Keep the existing no-filter tests proving that omitting the option still downloads all manifest entries.

- [ ] **Step 2: Run the tests and verify they fail**

Run: `python -m unittest discover -s tools/models -p test_download_models.py -k group_filter -v`

Expected: both supported runtimes reject the new arguments; failures are caused by missing group filtering.

- [ ] **Step 3: Implement filtering in both scripts**

Add validated group parsing and filter the already-validated manifest entries before downloads. Do not change checksum, resume, or atomic-promotion behavior.

- [ ] **Step 4: Run script integration tests**

Run: `python -m unittest discover -s tools/models -p test_download_models.py -v`

Expected: all tests pass for each available PowerShell/Bash runtime.

- [ ] **Step 5: Document the group options**

Update the README command examples and model-preparation section to distinguish the unchanged all-model default from filtered downloads used by `local`.

- [ ] **Step 6: Commit**

```bash
git add download-models.ps1 download-models.sh tools/models/test_download_models.py README.md docs/superpowers/specs/2026-10-29-local-auto-model-download-design.md
git commit -m "feat: filter model downloads by group"
```

### Task 2: Prepare and validate models before `local` starts

**Files:**
- Create: `src/SpeechToSpeech.Cli/LocalModelPreparation.cs`
- Modify: `src/SpeechToSpeech.Cli/Program.cs`
- Modify: `src/SpeechToSpeech.Cli/CommandLine.cs`
- Create: `tests/SpeechToSpeech.Tests/LocalModelPreparationTests.cs`
- Modify: `README.md`

**Interfaces:**
- `LocalModelPreparation.BuildPlan(CommandLine line, string repositoryRoot) -> LocalModelPlan` determines active groups, model paths, manifest entries, and platform script invocation without starting processes.
- `LocalModelPreparation.PrepareAsync(LocalModelPlan plan, Func<ProcessStartInfo, CancellationToken, Task<int>> runProcess, CancellationToken cancellationToken) -> Task` runs the platform script only for missing selected default model groups and validates their files afterward.
- `LocalModelPlan` carries the resolved model directory, selected groups, custom paths to validate, and the script invocation (`FileName` plus `ArgumentList`).
- `CommandLine.SetIfAbsent(string name, string value)` applies default model paths without replacing user-specified values.
- Only `local` calls `PrepareAsync`, before `OptionsBuilder.Build`; `serve` and `talk` retain their existing code path.
- Default group mapping: Silero VAD → `vad`; default `whisper` STT → `whisper`; `tensorsharp` LLM → `llm`; `qwen3` TTS → `tts`; enabled Smart Turn with no custom path → `smart-turn`. Do not select manifest groups for remote backends.
- Custom model paths are not passed through as replacements. Validate them at their backend's existing file contract and fail before listener startup if missing.

- [ ] **Step 1: Write failing tests for group selection and path ownership**

Add tests:

1. `BuildPlan_selects_default_local_backend_groups` — default `local` selects `vad`, `whisper`, `llm`, `tts`, and `smart-turn`.
2. `BuildPlan_omits_remote_backend_groups` — remote STT/LLM/TTS omit their model groups while VAD remains selected.
3. `BuildPlan_does_not_replace_explicit_model_paths` — supplied paths are kept as custom validation paths and are not rewritten to `models/`.
4. `BuildPlan_omits_custom_smart_turn_path` — a supplied Smart Turn path does not select the default `smart-turn` group.
5. `PrepareAsync_skips_process_when_required_default_files_are_valid` — a fixture manifest with correct, complete files starts no downloader process.
6. `PrepareAsync_runs_process_for_missing_group_and_rechecks_files` — a fake process runner receives only the missing group; after it writes the fixture manifest assets, preparation succeeds.
7. `PrepareAsync_returns_error_when_script_exits_nonzero` — failed script invocation propagates failure and does not report preparation success.
8. `PrepareAsync_fails_for_missing_custom_model_before_listener` — missing user-specified model path is reported before listener creation.

- [ ] **Step 2: Run the focused tests and verify they fail**

Run: `dotnet test tests/SpeechToSpeech.Tests/SpeechToSpeech.Tests.csproj --filter FullyQualifiedName~LocalModelPreparationTests`

Expected: the tests fail because `LocalModelPreparation` and the local preflight do not exist yet.

- [ ] **Step 3: Implement the pure local model plan**

Implement `LocalModelPreparation.BuildPlan` and `LocalModelPlan` with explicit backend-to-group mapping and custom-path preservation. Resolve script and manifest locations from the repository root found by walking upward from the current directory and `AppContext.BaseDirectory`; fail explicitly if the checkout assets cannot be found.

- [ ] **Step 4: Implement script invocation and model-path checks**

Run PowerShell on Windows with `-NoProfile -ExecutionPolicy Bypass -File`; run Bash elsewhere with `download-models.sh`. Pass absolute `models/` path and validated group names, inherit script output, await its exit code, and report a non-zero result. Check required VAD/Whisper/LLM/TTS/Smart Turn files for the active backends before returning.

- [ ] **Step 5: Wire preflight ahead of backend construction**

In `Program.Main`, for `local` only, await `LocalModelPreparation.PrepareAsync` before `OptionsBuilder.Build` and before constructing or starting the Realtime server. Set default model paths for selected pinned groups only when no explicit path was supplied. Return a non-zero exit on script/path errors; do not start Kestrel or the audio client.

- [ ] **Step 6: Verify focused and full checks**

Run:

```powershell
dotnet test tests/SpeechToSpeech.Tests/SpeechToSpeech.Tests.csproj --filter FullyQualifiedName~LocalModelPreparationTests
python -m unittest discover -s tools/models -p test_download_models.py -v
python tools/ledger/check_doc_sync.py --strict
git --no-pager diff --check
```

Expected: all focused tests pass, all available downloader runtimes pass, documentation stays synchronized, and the diff check is clean.

- [ ] **Step 7: Commit**

```bash
git add src/SpeechToSpeech.Cli/LocalModelPreparation.cs src/SpeechToSpeech.Cli/Program.cs src/SpeechToSpeech.Cli/CommandLine.cs tests/SpeechToSpeech.Tests/LocalModelPreparationTests.cs README.md
git commit -m "feat: prepare local models before listening"
```
