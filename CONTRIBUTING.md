# Contributing

The CLI and GUI must use the same `LemonLoader.Patcher.Core` pipeline. Keep APK
ZIP handling, directory handling, Interop generation, payload assembly, and
optional post-processing as distinct responsibilities; do not duplicate patch
behavior in a front end.

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
| Game inputs and generated Interop | GameInteropGenerator |
| APK/directory payload | PayloadAssembler |
| Transactional directory replacement | DirectoryInjector |
| Explicit alignment/signing | ApkPostProcessor |
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
