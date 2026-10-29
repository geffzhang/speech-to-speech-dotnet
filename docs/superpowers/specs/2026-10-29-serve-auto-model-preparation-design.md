# Standalone `serve` Automatic Model Preparation

## Intent

When `serve` uses built-in local backends, prepare the required pinned model groups before starting the Realtime listener. A standalone server should not accept connections that cannot create a usable pipeline because its default models are absent.

## Behavior

- `serve` and `local` use the existing `LocalModelPreparation` workflow before building backend context or starting Kestrel.
- Only model groups used by enabled built-in local backends are selected. Remote backends do not cause their model groups to download.
- Default paths use the repository's pinned manifest and existing PowerShell/Bash download scripts, including integrity checks, resumable downloads, and verification caching.
- Explicit custom model paths remain user-managed. Missing custom assets cause a clear error before listening; they are never replaced with defaults.
- Downloader errors or invalid downloaded assets prevent the listener from starting.
- `talk` behavior remains unchanged.
- README startup guidance documents automatic preparation for `serve` and `local`.

## Approach

Reuse `LocalModelPreparation.BuildPlan`, `ApplyDefaultPaths`, and `PrepareAsync` for both server-starting commands. This preserves existing validation and download behavior and avoids creating a second downloader or duplicating backend selection logic.

## Acceptance

- A plan built for `serve` selects default assets exactly as a `local` plan does.
- Existing valid default assets do not invoke the downloader.
- Missing default assets invoke the platform script only for the selected groups.
- Missing custom assets and downloader failures are surfaced before listener startup.
- README no longer states that standalone `serve` does not download models.
