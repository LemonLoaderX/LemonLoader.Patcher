# Contributing

The CLI and GUI must use the same `LemonLoader.Patcher.Core` pipeline. Keep APK
ZIP handling, directory handling, Interop generation, payload assembly, and
optional post-processing as distinct responsibilities; do not duplicate patch
behavior in a front end.

CLI parsing constructs requests; Core pipeline constructors normalize and validate
them once. CLI maps construction errors to usage errors before running the pipeline,
so execution failures retain their separate exit code. After resolving SDK tools,
recheck their paths against APK/export destinations without revalidating the whole
request. Keep the existing input, link and overwrite protections.

Directory injection shares payload validation through an uncompressed temporary
ZIP, extracted directly into the transaction's staging directory on the target
volume. Validate every destination before publishing files. APK injection keeps its
distribution compression policy. Preserve output bytes, cancellation and rollback
when optimizing either path.

## Repository organization

Core owns file formats, input validation, generation, payload composition,
transactions and tool execution. CLI owns syntax/output/exit codes; GUI owns
interaction and presentation. Both use the same Core operations. Keep code in the
existing src/<project>/ and tests/<project>/ layout; the responsibility table below
identifies the Core modules. Do not create a service layer or project for one wrapper.

scripts/ owns reusable preparation/build/test/cleanup operations. Directory.Build.props
and global.json own this product's generator and SDK choices; no parent manifest is
required. Generated files belong in ignored Output/ or established bin/obj paths;
.dependencies/ is a source cache, not disposable test scratch.

## Design and code style

- Prefer cohesive modules, explicit request/result types and small interfaces.
  Expose only intended integration contracts; keep implementation internal.
- Require a real caller, varying behavior or measured benefit before adding an
  interface, configuration switch, cache, retry or compatibility path. Reuse a local
  implementation first; do not build a general framework for a single operation.
- Maintain ordinary file interchange and independent generation/injection/processing.
  Do not add mandatory intermediate formats, output caches or generated-DLL repairs.
- Make resource ownership, async cancellation and the publication commit point clear.
  Use scoped disposal and propagate failures. Catch at an appropriate boundary to
  translate context or restore state; never turn an unknown failure into success.
- Follow .gitattributes and nearby C#/.NET conventions. New C# files normally match
  their principal type; types/methods/properties use PascalCase and locals use
  descriptive camelCase. Keep the existing four-space C# indentation. Do not rename
  unrelated members or format entire projects during a behavioral change.
- Respect nullable annotations and use Async names for Task-returning operations.
  Reserve async void for required UI event handlers with explicit error handling.
  Avoid broad warning suppression, hidden mutable state and untyped option bags.
- PowerShell uses declared parameters and Verb-Noun functions; check external exit
  codes. Bash quotes paths and passes argument arrays. Do not interpolate untrusted
  input into executable command text or put passwords on command lines.
- Comments explain contracts, reasons and non-obvious constraints. Logs describe
  progress or actionable failures, not implementation inventories or credentials.

## Documentation

Keep public guides and code comments in English, matching the repository. Local
handoff notes may use the maintainer's language. Use concrete, concise prose and
copyable commands with shell, working directory and prerequisites stated.

| Location | Authority |
| --- | --- |
| README.md | Product introduction and task navigation |
| AGENTS.md | Concise agent entry, selected commands and hard constraints |
| CONTRIBUTING.md | Development/design/style/testing/cleanup and publication workflow |
| docs/USAGE.md | Inputs, options, deployment behavior and user-visible errors |
| docs/WORKFLOW.md | Stage composition, file mappings and equivalent script operations |
| docs/INTEROP.md | Generation inputs, Unity references and export behavior |
| docs/releases/<tag>.md | Version-specific breaking changes, migration and limitations |
| .agents/notes/ | Nontrivial decisions, genuine alternatives and consequences |
| Output/<task>/ | Private inputs/identities, logs, measurements and one current checkpoint |

Maintain one owner per contract and link to it; do not copy options or large command
lists into multiple overviews. Loader owns installed/runtime contracts, while this
repository owns host tooling. Update affected guides with the code change. Keep
paths and links valid and describe superseded behavior as historical.

Update an existing decision topic for factual changes. A decision reversal needs
explicit supersession; remove fully absorbed duplicate notes after preserving useful
rationale and repairing links. Mechanical edits need no new design note. Do not
commit local build/job progress, device identifiers, private paths or acceptance
chronology to guides or decisions. AGENTS.md stays short; detailed procedures live here.

## Development workflow

Use PowerShell 7 and a stable .NET 10 SDK. The product's global.json selects the
latest installed .NET 10 feature band, matching CI's 10.0.x policy rather than
inheriting an SDK choice from a parent checkout.

See [usage](docs/USAGE.md) and [Interop](docs/INTEROP.md) for user contracts.
Nontrivial decisions belong in .agents/notes by topic; preserve alternatives and
supersession links, and remove fully absorbed duplicate drafts.

See [automatic release notes](docs/releases/README.md) for commit conventions,
baseline selection, optional additions, and local/CI preview commands.

For publication, update the version and push reviewed source followed by its tag.
CI generates the release body from commits. Only commit `docs/releases/<tag>.md`
when extra upgrade or compatibility information is needed; it is prepended to
the automatic changes.
The tag workflow creates a draft, uploads CI assets, then publishes it. Do not
manually create another Release. Retries replace draft assets only; binary
changes after publication require a new version. Notes-only corrections do not
require rebuilding or moving an existing tag.

```powershell
pwsh -NoProfile -File scripts/test.ps1
dotnet run --project src/LemonLoader.Patcher.GUI
```

Tests use generated fixtures. Do not commit APKs, decoded applications, Interop
output, Mods, signing material, logs, device identifiers, or local paths.
The normal test entry includes script parsing, path/cleanup helper tests,
publication scanner orchestration and generator source-selection fixtures.
Use scripts/test-scripts.ps1 for script-only checks. Bash parsing defaults off
on Windows and on elsewhere; provide a working native Bash when enabling it.

Select the smallest relevant entry before running tests:

| Change | Verification |
| --- | --- |
| Documentation/comments | Facts, links, command paths and diff; no product build |
| Core or CLI behavior | `dotnet run --project tests/LemonLoader.Patcher.Tests/LemonLoader.Patcher.Tests.csproj -c Release` |
| GUI behavior | `dotnet run --project tests/LemonLoader.Patcher.GUI.Tests -c Release` |
| Script/dependency selection | The affected script fixture; use the script suite for shared helper changes |
| Payload format or release | Producer/consumer archive checks and the maintained full CI/release gates |

Tests assert behavior through the appropriate interface: outputs, failure types,
exit codes, cancellation, input preservation and rollback. Prefer a regression that
fails on the original bug. Do not copy implementation branches into tests or require
a test for every mechanical edit. Use generated inputs and mock network/tool boundaries;
real process, ZIP or serialization behavior needs an integration fixture when mocks
cannot establish it. Tests own their disposable directories and processes.

Reuse passing results for identical source, dependency pins, toolchains and fixtures.
Repeat or broaden checks only for a relevant change, unresolved failure, explicit
request or required CI gate. Do not repeat builds or device tests to close a turn.
Performance claims require fixed inputs, baseline measurements and output equivalence;
fewer calls or copies alone do not establish a measured speedup. Host tests do not
qualify a device, TLS service, Unity version or every external tool installation.

Path traversal, duplicate ZIP entries, hash mismatches, ABI mistakes, native
name collisions, and incomplete payloads are functional or security failures.
Do not weaken those checks to accept a malformed input. Manifest readers should
validate fields they consume while tolerating additive metadata.

Use scoped imperative commits and explain non-obvious ZIP, signing, rollback, or
format decisions in the commit body. A format version changes only when an
existing consumer cannot safely interpret the new semantics.

## Local packages and source ownership

```powershell
pwsh -NoProfile -File scripts/setup-dependencies.ps1
pwsh -NoProfile -File scripts/publish.ps1
[xml]$properties = Get-Content Directory.Build.props -Raw
pwsh -NoProfile -File scripts/package-release.ps1 `
    -Version "v$($properties.Project.PropertyGroup.Version)"
```

Publishing builds both RIDs by default; pass the same Runtime to publishing and
packaging to select one. Per-RID output under Output/Releases contains independent
CLI and GUI directories, each with its own Tools/Il2CppInterop and legal files.
Packaging emits separate GUI/CLI archives with the executable at archive root;
each extracted directory can move independently. Archives/checksums go to Output/Packages/<tag>.
Both front ends bundle managed assemblies and runtime configuration into their
executables. GUI native graphics libraries remain adjacent files, avoiding
startup extraction. Bundled generator dependencies remain in Tools/Il2CppInterop.
Staging is fresh and replacement atomic. Formal publish requires clean product
and generator sources; AllowDirtySource is private local development only.

Directory.Build.props owns the generator URL/revision. Setup/publishing reuse a
matching sibling or .dependencies/Il2CppInterop/<revision>, never fetch/checkout
an existing shared source. Il2CppInteropSourceRoot selects an explicit checkout.
Builds are offline after preparation and do not need parent pins or Loader sources.
Game artifacts and Loader ZIPs are not Patcher publication inputs.

Test an actual Loader ZIP with scripts/test.ps1 -ReleaseArchive; multiple paths
are accepted. This uses production validation without games, signing or installation.

| Responsibility | Core module |
| --- | --- |
| One patch run/temporary workspace | ApkPatchPipeline |
| Independent generation request/export | InteropRequest, InteropPipeline |
| Game inputs and generator invocation | GameInteropGenerator |
| Existing DLL inputs | InteropInput |
| APK/directory payload | PayloadAssembler |
| Transactional directory replacement | DirectoryInjector |
| Explicit alignment/signing | ApkPostProcessor |
| Independent APK processing | ApkProcessingPipeline |
| External execution | ProcessRunner and resolvers |

External-tool deadlines cover exit and stdout/stderr draining. Cancellation kills
the process tree while the direct child remains alive; already detached/reparented
descendants cannot be discovered after its exit. The normal tests exercise the
inherited-pipe case. Front ends never duplicate Core behavior.
Headless GUI tests cover automatic output names, preserving explicit output and
prior logs on validation errors, bounded log rows and command layout at minimum
window size. Rendering screenshots and real tool sessions complement these tests.

## Publication audit

Use a reviewed Gitleaks executable and the exact generator source used for the
release. Run from this repository root after committing reviewed source:

```powershell
. ./scripts/common/Dependencies.ps1
$generator = Get-InteropSourceRoot
pwsh -NoProfile -File scripts/scan-publication.ps1 -GitleaksPath "<gitleaks>" `
    -SourceRepository $generator -ArchivePath "<final-patcher-archive>"
```

The product HEAD is always scanned. Explicit sources and initialized nested
dependencies are scanned at their own HEAD; private backup refs are not inputs.
Missing/mismatched nested checkouts or tracked edits reject the scan. Untracked
and ignored source files are not scanned by Git history mode. Supply every
producing source and final archive; a product-only scan is not a dependency or
binary audit. Patcher owns its .gitleaksignore and has no default exceptions.
Use exact reviewed fingerprints only, never broad exclusions. Scanner failures
remain fatal; results do not publish anything.

Synthetic preflight tests run with the normal script tests. To also exercise the
real scanner's HEAD/range/archive detection with generated fixture markers:

```powershell
pwsh -NoProfile -File scripts/test-publication-scan.ps1 -GitleaksPath "<gitleaks>"
```

## Cleanup

Choose an owned output root and check free space before large generation/publish
runs. Keep canonical game inputs and use one ignored task directory for diagnostics.
Reuse inputs and process variants sequentially where possible; do not multiply game
copies or spill them to another drive to postpone cleanup.

After a verification boundary completes, remove reproducible intermediates,
obsolete package variants and finished temporary downloads. Retain current deliverables,
minimal identity, unresolved-failure evidence and any backup needed for recovery.
Ignore rules prevent commits, not disk growth. Cleanup is part of completing the task.

Stop builds and close published Patcher processes first:

```powershell
pwsh -NoProfile -File scripts/clean.ps1 -WhatIf
pwsh -NoProfile -File scripts/clean.ps1
# Also remove known releases/packages:
pwsh -NoProfile -File scripts/clean.ps1 -AllOutputs -WhatIf
```

Routine cleanup removes PublishTemp, src/tests bin/obj, generated ManagedCompat
and TestResults. Releases and Packages require AllOutputs. Diagnostic fixtures,
private/unknown Output directories, dependency caches and sibling repositories
are preserved in both modes. Nested repositories are not traversed. Links on or
inside a selected tree reject cleanup before deletion; there is no Deep mode.

Resolve absolute targets before recursive cleanup and stop owned processes first.
An explicitly owned disposable Git test fixture is not a source cache, but needs
its own scoped cleanup; never treat all nested repositories as disposable. Preserve
unique APKs, keys, user data and recovery evidence. If policy blocks cleanup, record
the exact leftover and reason; do not repeat the deletion through a different tool.

## Change delivery and permissions

Preserve unrelated worktree changes. Do not reset, stash, rewrite history or switch
shared checkouts for convenience. Commits, pushes, tags, releases and device changes
require authorization for their scope. Continue approved actions without asking
again, but do not apply a finished release's authorization to unrelated new changes.

Patcher uses its own semantic version: incompatible public CLI/Core/distribution
contracts require an appropriate version increase and concrete migration notes.
Do not raise a payload schema merely because the product version changes. Publish
only after pinned dependency sources are remotely obtainable; use the existing tag
workflow, not a second manual Release. Never move a published tag, replace public
binaries or relabel development packages as a formal release.

This tool modifies files, not installed devices. Routine validation never uninstalls
apps, clears data, changes package names or changes signing identity. Credentials and
signing inputs remain private; public archives contain product runtime inputs and
minimal identity only. Runtime tool policy remains the actual permission boundary.

Handoff reports the change, relevant checks and limitations, outputs and cleanup
status. Keep one current local checkpoint and references to supporting evidence;
do not create a second report hierarchy or rerun the full suite just to finish.
