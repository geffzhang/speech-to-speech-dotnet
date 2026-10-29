# Docker Automatic Model Preparation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `docker compose up --build` download and verify all default models into a persistent volume, then start the speech server using its local GGUF LLM.

**Architecture:** Keep the existing pinned manifest and Bash downloader as the single source of model download and verification behavior. Add a small Linux entrypoint to run preparation before `exec`-ing the app; make Compose attach a writable named volume and configure TensorSharp with the local GGUF path. Keep weights out of image layers and document the first-run download and volume behavior.

**Tech Stack:** Docker multi-stage build, Linux shell, Docker Compose, existing Bash/curl/SHA-256 downloader, .NET 10.

## Global Constraints

- The container entrypoint prepares `/models` using a Linux downloader before starting `serve`.
- Downloads use fixed URLs, sizes, and SHA-256 values from `tools/models/models.tsv`.
- Any download or verification failure exits nonzero and does not start the service.
- The Compose `/models` volume is writable and persists across container recreation.
- Compose uses `tensorsharp` with `/models/llm/Qwen3.5-0.8B-Q4_K_M.gguf` and requires no host LLM service.
- The first start downloads all five model groups (about 6.6 GB); later starts reuse verified files.
- Model weights are not embedded in the image or committed to the repository.

---

## File Structure

- Create `docker-entrypoint.sh`: prepare `/models` with the packaged Bash downloader, then replace the shell with the application process.
- Modify `Dockerfile`: install Bash, curl, and SHA-256 tooling in the runtime image; copy in the downloader and manifest; set the entrypoint.
- Modify `docker-compose.yml`: switch from the host bind mount to a writable named volume and set local TensorSharp model options.
- Create `tools/docker/test_docker_config.py`: validate Docker packaging/configuration and entrypoint success/failure order without downloading real weights.
- Modify `README.md`: replace manual Docker model preparation instructions with the automatic startup flow and its requirements.

### Task 1: Add a fail-fast container entrypoint and package downloader assets

**Files:**
- Create: `docker-entrypoint.sh`
- Modify: `Dockerfile`
- Create: `tools/docker/test_docker_config.py`

**Interfaces:**
- Entrypoint consumes `MODEL_DIR` (default `/models`) and optional `MODEL_DOWNLOADER` (default `/usr/local/bin/download-models.sh`) for deterministic testing/operations.
- Entrypoint runs the downloader against the image-packaged manifest, then `exec`s `dotnet /app/speech-to-speech.dll "$@"`.
- The existing Docker `ENTRYPOINT` and Compose command arguments preserve `serve` defaults/overrides.

- [ ] **Step 1: Write entrypoint contract tests**

Create `tools/docker/test_docker_config.py` with temporary fake downloader and fake `dotnet` executables. Test:

1. A successful downloader is called with `--models-dir /models --manifest /usr/local/share/speech-to-speech/models.tsv`; only after it succeeds does the entrypoint invoke `dotnet /app/speech-to-speech.dll serve`.
2. A downloader returning nonzero causes the entrypoint to return nonzero and never invokes the fake `dotnet`.
3. A custom `MODEL_DIR` is passed to the downloader.

Use Python's `unittest`, execute the shell script with `bash`, place a fake `dotnet` in a temporary `PATH`, and record invocations in a temporary log file.

- [ ] **Step 2: Run the tests and verify failure**

Run: `python -m unittest tools.docker.test_docker_config -v`

Expected: tests fail because `docker-entrypoint.sh` does not yet exist.

- [ ] **Step 3: Implement the entrypoint**

Create `docker-entrypoint.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

model_dir="${MODEL_DIR:-/models}"
downloader="${MODEL_DOWNLOADER:-/usr/local/bin/download-models.sh}"
manifest="${MODEL_MANIFEST:-/usr/local/share/speech-to-speech/models.tsv}"

"$downloader" --models-dir "$model_dir" --manifest "$manifest"
exec dotnet /app/speech-to-speech.dll "$@"
```

Ensure line endings are LF. The script must not catch or suppress downloader errors.

- [ ] **Step 4: Install runtime tools and copy the pinned downloader inputs**

In the runtime stage of `Dockerfile`, install `bash`, `curl`, `coreutils` (for `sha256sum`), and CA certificates using the base image's package manager. Copy `download-models.sh` to `/usr/local/bin/download-models.sh`, `tools/models/models.tsv` to `/usr/local/share/speech-to-speech/models.tsv`, and the entrypoint to `/usr/local/bin/docker-entrypoint.sh`. Set the entrypoint to `["/usr/local/bin/docker-entrypoint.sh"]`. Keep the published app files and `/models` volume behavior.

Copy only the required model assets explicitly; do not broaden `.dockerignore` exclusions for all tools or docs.

- [ ] **Step 5: Run entrypoint tests**

Run: `python -m unittest tools.docker.test_docker_config -v`

Expected: all tests pass, including the assertion that downloader failure never starts `dotnet`.

- [ ] **Step 6: Commit**

```bash
git add Dockerfile docker-entrypoint.sh tools/docker/test_docker_config.py
git commit -m "feat: prepare models in Docker entrypoint"
```

### Task 2: Configure Compose for persistent local model inference

**Files:**
- Modify: `docker-compose.yml`
- Modify: `tools/docker/test_docker_config.py`

**Interfaces:**
- Compose service `pipeline` passes `serve` arguments for host, port, VAD, Whisper, local TensorSharp LLM, Qwen3 TTS, and Smart Turn.
- The service mounts named volume `models` at `/models` without read-only mode.
- Compose does not require `host.docker.internal`, `extra_hosts`, or an external OpenAI-compatible LLM URL.

- [ ] **Step 1: Add Compose configuration assertions**

Extend `tools/docker/test_docker_config.py` to load `docker-compose.yml` with the Python standard library only (parse the required YAML shape using a focused text/config assertion if no YAML library is already installed). Assert:

1. `/models` is mounted from a named volume and is writable.
2. `--llm_backend` is followed by `tensorsharp`.
3. `--llm_model_path` is followed by `/models/llm/Qwen3.5-0.8B-Q4_K_M.gguf`.
4. The compose config has no `extra_hosts` or `host.docker.internal:8080`.

- [ ] **Step 2: Run the tests and verify the new assertions fail**

Run: `python -m unittest tools.docker.test_docker_config -v`

Expected: entrypoint tests pass; Compose assertions fail against the existing read-only bind mount and remote LLM settings.

- [ ] **Step 3: Update the Compose service**

In `docker-compose.yml`, replace the chat-completions arguments with:

```yaml
      - --llm_backend
      - tensorsharp
      - --llm_model_path
      - /models/llm/Qwen3.5-0.8B-Q4_K_M.gguf
```

Remove `--llm_base_url` and its host URL. Change the mount to `models:/models` and remove `extra_hosts`. Add a top-level named volume:

```yaml
volumes:
  models:
```

Keep all other server/model directory arguments and port mappings unchanged.

- [ ] **Step 4: Run Docker configuration tests**

Run: `python -m unittest tools.docker.test_docker_config -v`

Expected: all entrypoint and Compose assertions pass.

- [ ] **Step 5: Commit**

```bash
git add docker-compose.yml tools/docker/test_docker_config.py
git commit -m "feat: use persistent local models in Docker Compose"
```

### Task 3: Document Docker first-run and persistent-volume behavior

**Files:**
- Modify: `README.md`
- Modify: `tools/docker/test_docker_config.py`

**Interfaces:**
- README Docker instructions require only Docker Compose, describe the initial download of five groups (~6.6 GB), explain that models persist in the named volume, and state that no host LLM service or pre-populated `./cache/models/` is required.
- README notes model weights retain their upstream licenses and are verified against the pinned manifest.

- [ ] **Step 1: Add README content assertions**

Extend the config test to read README's Docker section and assert it documents `docker compose up --build`, the approximate first-run size, persistent named-volume behavior, local LLM operation, and no manual host model preparation.

- [ ] **Step 2: Run the targeted test and verify documentation assertion fails**

Run: `python -m unittest tools.docker.test_docker_config -v`

Expected: runtime/config tests pass and the new README assertion fails against the old manual download instructions.

- [ ] **Step 3: Replace manual Docker model preparation instructions**

Update the Docker section to provide:

```bash
docker compose up --build
```

Explain that the first run downloads and verifies all five groups (~6.6 GB) into the Compose `models` named volume before `serve` starts; later starts reuse verified models. Explain that users can remove the named volume explicitly when they want a clean redownload and that the model upstream licenses still apply. Do not include destructive volume-removal commands in the default path.

- [ ] **Step 4: Run targeted and existing downloader tests**

Run:

```powershell
python -m unittest tools.docker.test_docker_config -v
python -m unittest discover -s tools/models -p test_download_models.py -v
git --no-pager diff --check
```

Expected: Docker tests and existing Bash/PowerShell downloader tests pass; diff check is clean.

- [ ] **Step 5: Commit**

```bash
git add README.md tools/docker/test_docker_config.py
git commit -m "docs: explain automatic Docker model setup"
```

### Task 4: Validate the complete Docker build and Compose model

**Files:**
- No additional files; run the checks against `Dockerfile`, `docker-compose.yml`, `docker-entrypoint.sh`, and the README changes.

**Interfaces:**
- Docker build produces a runnable image with a default entrypoint that downloads/validates assets and then starts the app.
- Compose configuration resolves without requiring an external model directory or LLM service.

- [ ] **Step 1: Run config tests and check Docker availability**

Run:

```powershell
python -m unittest tools.docker.test_docker_config -v
docker version
docker compose config
```

Expected: tests pass, Docker daemon is available, and `docker compose config` exits zero with the named `models` volume and local TensorSharp model path.

- [ ] **Step 2: Build the runtime image**

Run: `docker compose build pipeline`

Expected: build succeeds; runtime stage contains `/usr/local/bin/download-models.sh`, `/usr/local/share/speech-to-speech/models.tsv`, and executable `/usr/local/bin/docker-entrypoint.sh`.

- [ ] **Step 3: Validate image packaging without downloading models**

Run:

```powershell
docker run --rm --entrypoint bash speech-to-speech-dotnet-pipeline -lc 'command -v bash && command -v curl && command -v sha256sum && test -s /usr/local/share/speech-to-speech/models.tsv && test -x /usr/local/bin/download-models.sh && test -x /usr/local/bin/docker-entrypoint.sh'
```

Expected: all commands exit zero. If the generated image name differs, use the image name printed by `docker compose build pipeline`.

- [ ] **Step 4: Run final repository checks**

Run:

```powershell
python -m unittest tools.docker.test_docker_config -v
python -m unittest discover -s tools/models -p test_download_models.py -v
docker compose config
git --no-pager diff --check
```

Expected: all checks pass. Do not run `docker compose up` against the real manifest for routine validation because it downloads several gigabytes; first-run behavior is covered by entrypoint tests with a fake downloader.

- [ ] **Step 5: Commit any final corrections**

```bash
git add Dockerfile docker-entrypoint.sh docker-compose.yml README.md tools/docker/test_docker_config.py
git commit -m "test: validate Docker model startup"
```
