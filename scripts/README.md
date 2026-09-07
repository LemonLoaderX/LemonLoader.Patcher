# Patcher scripts

Use PowerShell 7. Scripts resolve repository paths from their own location;
caller-provided relative input paths use the current working directory.

| Entry point | Purpose |
| --- | --- |
| test.ps1 | Run Core/CLI regression tests |
| publish.ps1 | Build CLI, GUI and pinned Interop tool into local per-RID outputs |
| package-release.ps1 | Package published outputs and checksums, without uploading |
| verify-apk-layout.ps1 | Validate an explicitly supplied APK layout |
| verify-unstripping.ps1 | Validate restored Unity managed references |

All five workflows are retained. Publishing and packaging are distinct stages,
not duplicate release commands. `common/Paths.ps1` shares output containment and
symlink/junction rejection below the trusted output root; aliases at or above
that root are supported. It is not an executable entry point. Product scripts
must not depend on workspace helper files so this repository remains standalone.

Signing and installation are not implicit steps of these scripts. Local
`-AllowDirtySource` publishing is development-only. See ../README.md for command
examples and the release contract. Script syntax/helper regression tests are also
available from the containing workspace's scripts/test-scripts.ps1.
