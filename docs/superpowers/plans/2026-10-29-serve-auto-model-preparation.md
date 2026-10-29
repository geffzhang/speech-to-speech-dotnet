# Standalone `serve` Automatic Model Preparation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prepare required default local model assets before a standalone `serve` starts its listener.

**Architecture:** Reuse the existing model-preparation plan and downloader for both server-starting commands, using the CLI command name only to initialize the plan. Preserve current backend filtering, custom-path validation, downloader integrity behavior, and talk behavior.

**Tech Stack:** .NET 10, C#, xUnit, PowerShell/Bash model scripts, Markdown.

## Global Constraints

- Use the existing pinned manifest and downloader scripts; do not introduce a second downloader.
- Do not replace explicit custom model paths.
- Do not prepare model groups unused by remote backends.
- Complete preparation before backend startup/listening.
- Keep `talk` startup behavior unchanged.

---

### Task 1: Share the automatic preparation startup path

**Files:**
- Modify: `src/SpeechToSpeech.Cli/Program.cs`
- Test: `tests/SpeechToSpeech.Tests/LocalModelPreparationTests.cs`

**Interfaces:**
- Consumes: `LocalModelPreparation.BuildPlan(CommandLine, string)`, `ApplyDefaultPaths`, and `PrepareAsync`.
- Produces: `serve` and `local` both create their plan and prepare assets before building `BackendContext`.

- [ ] **Step 1: Add a failing test that a serve plan selects the default local backend groups**

Add a test adjacent to `BuildPlan_selects_default_local_backend_groups`:

```csharp
[Fact]
public void BuildPlan_selects_default_server_backend_groups()
{
    var line = CommandLine.Parse(["serve"]);

    var plan = LocalModelPreparation.BuildPlan(line, _root);

    Assert.Equal(["vad", "whisper", "llm", "tts", "smart-turn"], plan.SelectedGroups);
}
```

- [ ] **Step 2: Run the focused test**

Run: `dotnet test tests/SpeechToSpeech.Tests --filter FullyQualifiedName~BuildPlan_selects_default_server_backend_groups`
Expected: FAIL because `BuildPlan` currently accepts only `local`.

- [ ] **Step 3: Permit both server commands in the plan builder**

In `LocalModelPreparation.BuildPlan`, validate `line.Command` is `local` or `serve`; for any other command, throw an `ArgumentException` that names the unsupported command. Continue using the same backend/path selection logic for both.

- [ ] **Step 4: Run preparation tests**

Run: `dotnet test tests/SpeechToSpeech.Tests --filter FullyQualifiedName~LocalModelPreparationTests`
Expected: PASS.

- [ ] **Step 5: Route serve through preparation before options construction**

In `Program.Main`, change the existing `if (line.Command == "local")` preparation guard to include `serve`. Keep the current sequence: build plan, apply default paths, await `PrepareAsync`, then construct backend context. Do not include `talk`.

- [ ] **Step 6: Run focused test suite**

Run: `dotnet test tests/SpeechToSpeech.Tests --filter FullyQualifiedName~LocalModelPreparationTests`
Expected: PASS.

### Task 2: Update startup documentation and verify

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update preparation guidance**

Replace the text saying only `local` prepares models and that `serve` does not download. State that both `serve` and `local` prepare missing/invalid pinned model groups required by selected built-in local backends before listening; custom paths remain user-managed and missing custom models fail before listening.

- [ ] **Step 2: Update the `serve` quick-start sample**

Make the `S2S` export and explicit default model-path arguments unnecessary in the quick-start example. Retain the port option and explain that `serve` adds default paths and prepares the selected local model groups automatically. Continue to describe custom model-root configuration for users who choose to manage downloads themselves.

- [ ] **Step 3: Run focused tests and build**

Run: `dotnet test tests/SpeechToSpeech.Tests --filter FullyQualifiedName~LocalModelPreparationTests`
Expected: PASS.

Run: `dotnet build src/SpeechToSpeech.Cli`
Expected: Build succeeds with exit code 0.

- [ ] **Step 4: Review changed startup behavior**

Confirm by inspection that the `serve`/`local` preparation block precedes `OptionsBuilder.Build`, the `talk` path remains excluded, and README statements consistently describe the new behavior.
