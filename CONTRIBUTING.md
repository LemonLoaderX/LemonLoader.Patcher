# Contributing

The CLI and GUI must use the same `LemonLoader.Patcher.Core` pipeline. Keep APK
ZIP handling, directory handling, Interop generation, payload assembly, and
optional post-processing as distinct responsibilities; do not duplicate patch
behavior in a front end.

## Development workflow

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
pwsh -NoProfile -File scripts/setup-dependencies.ps1
pwsh -NoProfile -File scripts/publish.ps1 -Runtime win-x64
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
