# Patcher scripts

Use PowerShell 7. Scripts resolve repository paths from their own location;
caller-provided relative input paths use the current working directory.

| Entry point | Purpose |
| --- | --- |
| test.ps1 | Run Core/CLI regression tests |
| setup-dependencies.ps1 | Verify/select this product's pinned generator checkout without changing shared sources |
| clean.ps1 | Clean known local build trees; releases/packages require AllOutputs |
| scan-publication.ps1 | Audit product HEAD, explicit producing sources and final archives; no upload |
| test-scripts.ps1 | Product script parsing and path/cleanup/publication helper fixtures |
| test-cleanup.ps1 | Synthetic named-output cleanup and link/source protection |
| test-dependencies.ps1 | Independent generator pins and non-mutating source setup |
| test-publication-scan.ps1 | Scanner preflight fixtures; optional real Gitleaks history/archive tests |
| test-release-packaging.ps1 | Synthetic cross-process Windows/Linux repacking, long filenames, modes and checksums |
| publish.ps1 | Build CLI, GUI and pinned Interop tool into local per-RID outputs |
| package-release.ps1 | Package published outputs and checksums, without uploading |
| verify-apk-layout.ps1 | Validate an explicitly supplied APK layout |
| verify-unstripping.ps1 | Validate restored Unity managed references |
| generate-release-notes.sh | CI/local release-note producer |
| test-release-notes.sh | Release-note baseline, filtering and fallback regression |

Publishing and packaging are distinct stages,
not duplicate release commands. `common/Paths.ps1` shares output containment and
symlink/junction rejection below the trusted output root; aliases at or above
that root are supported. It is not an executable entry point. Product scripts
must not depend on workspace helper files so this repository remains standalone.

Signing and installation are not implicit steps of these scripts. Local
`-AllowDirtySource` publishing is development-only. See [local packages](../CONTRIBUTING.md#local-packages-and-source-ownership)
for commands and [Contributing](../CONTRIBUTING.md) for tag-driven publication.
Script syntax/helper and cleanup/publication fixtures run as part of test.ps1
and do not require a containing workspace.

Linux archives use GNU tar headers with fixed times/ownership and executable
CLI/GUI modes. Same-toolchain repacking of unchanged files is checked across
processes; historical PAX archives remain ordinary readable tar.gz inputs.
Windows ZIP entries use ordinal file order and fixed times. Both archives include
published files, not empty directory scaffolding.

Cleanup is restricted to this product. It preserves source caches, shared
dependencies, diagnostic fixtures, private/unknown Output directories and
published archives by default. See [maintenance commands](../CONTRIBUTING.md#cleanup).
Script tests clean their own unique fixtures in finally through
`common/TestFixtures.ps1`; synthetic Git data and links belong to those fixtures,
and link targets are never followed. This does not broaden product cleanup scope.
