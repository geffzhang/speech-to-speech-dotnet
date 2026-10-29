# `local` automatic model preparation

## Intent

When `local` uses a built-in local backend whose pinned model files are missing, download those files before opening the local Realtime endpoint. This prevents a client from connecting to a server that cannot construct its VAD and then receiving no Realtime events.

## Goals and non-goals

- Automatically prepare missing default local models for `local`, without a confirmation prompt.
- Keep the existing PowerShell and Bash download scripts, pinned manifest, SHA-256 checks, and resumable partial files as the source of download behavior.
- Validate selected local model paths before the listener starts; report script failures and missing custom models as errors.
- Keep `serve` and `talk` behavior unchanged.
- Do not overwrite or download user-specified custom model paths.
- Do not download model groups for remote backends that do not consume them.

## Constraints and implementation calls

- The default directory is `models/` under the repository root; the existing scripts receive it explicitly so invocation does not depend on their process working directory.
- Extend both scripts with an optional model-group filter (`vad`, `whisper`, `llm`, `tts`, `smart-turn`); no filter retains the current all-model behavior. Empty, duplicate, unknown, or manifest-empty groups fail before downloads. `local` selects groups for its active built-in local backends. Missing files in selected groups are downloaded; verified files are skipped.
- Before building the backend context or starting Kestrel, `local` resolves its effective model paths, invokes `download-models.ps1` on Windows or `download-models.sh` through Bash elsewhere if pinned default files are missing, waits for completion, and then validates the resulting paths.
- Explicit custom model paths remain user-managed. If one is missing, `local` fails before listening and reports the missing path plus the manual download command; it does not silently substitute default weights.
- Script lookup uses the repository checkout. If the matching script or manifest is unavailable, fail clearly before listening rather than falling back to an unverified download.
- Smart Turn is included only when enabled and using its default local model. The default local pipeline therefore selects all five groups.

## Approaches

1. **Invoke the existing scripts from `local` (chosen):** reuses the already tested curl-based resume and integrity behavior. It requires the checkout's platform script and Bash on non-Windows systems.
2. **Implement a downloader inside the .NET CLI:** works without external shell tools and can ship with the application, but duplicates the script's transfer and integrity implementation.
3. **Make the .NET CLI the sole downloader and turn both scripts into wrappers:** avoids duplicate download logic but makes the scripts require the .NET SDK and routes standalone downloads through a project build.

The user chose approach 1 and direct, unprompted downloads. The group filter is a builder call to avoid downloading irrelevant models when a backend is remote or overridden.

## Acceptance

- With default local model files absent, `local` starts the platform script, waits for verified download completion, and only then opens its endpoint and client.
- With valid files already present, `local` does not invoke a download.
- With a selected default model missing, only its selected manifest group is downloaded.
- With a missing custom model, or a failed downloader process, `local` exits non-zero without starting a listener.
- Integration tests cover script group filtering and CLI startup ordering/failure; existing downloader resume and integrity tests continue to pass.
