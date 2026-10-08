# LemonLoader.Patcher agent instructions

Patcher is optional host tooling for Unity IL2CPP files. CLI and GUI share one Core
pipeline for generation, injection and optional APK processing. This repository
owns its SDK/generator pins and builds without a parent or Loader source checkout.

## Read by task

[Contributing](CONTRIBUTING.md) owns development, design, code style, documentation,
verification and output lifecycle rules. [Usage](docs/USAGE.md),
[file workflows](docs/WORKFLOW.md) and [Interop](docs/INTEROP.md) own public behavior.
[The script catalog](scripts/README.md) identifies maintained operations. Read the
relevant guide and any nested instructions before changing their contract.

## Source map

| Location | Owner |
| --- | --- |
| src/LemonLoader.Patcher.Core/ | Request validation, generation, payloads, transactions and external tools |
| src/LemonLoader.Patcher.CLI/ | Syntax parsing, error-code/output contract and progress rendering |
| src/LemonLoader.Patcher.GUI/ | Presentation, interaction and UI state; no second patch implementation |
| tests/ | Core/CLI fixtures and separate GUI behavior tests |
| scripts/, Directory.Build.props, global.json | Development commands and independent dependency/tool pins |
| docs/, .agents/notes/ | Stable contracts and decision rationale |
| Output/, .dependencies/ | Ignored generated output and exact source caches |

## Commands and verification

Run from this repository root with the SDK selected by global.json. Do not run all
entries for every task; use the smallest one that covers the changed behavior.

| Task | Entry |
| --- | --- |
| Prepare a missing generator source | `pwsh -NoProfile -File scripts/setup-dependencies.ps1` |
| Core/CLI behavior | `dotnet run --project tests/LemonLoader.Patcher.Tests/LemonLoader.Patcher.Tests.csproj -c Release` |
| GUI behavior | `dotnet run --project tests/LemonLoader.Patcher.GUI.Tests -c Release` |
| Script/helper change | `pwsh -NoProfile -File scripts/test-scripts.ps1` |
| Full product boundary | `pwsh -NoProfile -File scripts/test.ps1` |
| Exercise the GUI | `dotnet run --project src/LemonLoader.Patcher.GUI` |
| Publish a local Windows build | `pwsh -NoProfile -File scripts/publish.ps1 -Runtime win-x64` |
| Cleanup preview | `pwsh -NoProfile -File scripts/clean.ps1 -WhatIf` |

Actual Loader archive checks, other publish targets and release packaging are in
Contributing. Local publish creates files; GitHub publication is a separate action.

## Non-negotiable constraints

- Preserve unrelated work. Keep Core responsibilities cohesive; front ends parse
  and present. Normalize requests once in Core and recheck newly resolved tool paths
  before publication; preserve usage-error versus execution-failure semantics.
- Use existing implementations before adding helpers, configuration or interfaces.
  Generation, injection and SDK processing must remain independently usable through
  ordinary files. Do not add automatic Interop caching or binary repair passes.
- Keep input/output protection, ZIP paths/duplicates, hashes/signatures, ABI,
  native-name collisions, runtime completeness, cancellation and rollback intact.
  Tolerate additive metadata; schema versions describe semantics, not release numbers.
- Follow repository attributes and surrounding C# style. Keep Core out of UI code,
  avoid secret-bearing logs and make public errors actionable.
- Keep private inputs, generated game DLLs, signing material, credentials and test
  progress outside Git/releases. Documentation has one topic owner; local evidence
  stays in ignored output rather than public guides or design notes.
- Clean only named owned outputs after retaining necessary results/recovery data.
  Do not follow links, delete source caches/siblings or grow cross-drive test copies.
- Commit, push, tag, publish and device mutation require scoped authorization; reuse
  existing authorization within that scope. Never bypass a policy rejection, reset
  user work, or uninstall/clear/rename an application during routine testing.
