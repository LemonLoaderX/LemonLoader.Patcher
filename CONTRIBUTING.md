# Contributing

The CLI and GUI must use the same `LemonLoader.Patcher.Core` pipeline. Keep APK
ZIP handling, directory handling, Interop generation, payload assembly, and
optional post-processing as distinct responsibilities; do not duplicate patch
behavior in a front end.

## Development workflow

For publication, commit `docs/releases/<tag>.md` with the version change before
pushing the version tag. Write the release body without a duplicate title.
The tag workflow creates a draft, uploads CI assets, then publishes it. Do not
manually create another Release. Retries replace draft assets only; binary
changes after publication require a new version. Notes-only corrections do not
require rebuilding or moving an existing tag.

```powershell
pwsh -NoProfile -File scripts/test.ps1
pwsh -NoProfile -File scripts/publish.ps1 `
    -Runtime win-x64 `
    -Il2CppInteropSourceRoot "<path-to-Il2CppInterop>"
```

Tests use generated fixtures. Do not commit APKs, decoded applications, Interop
output, Mods, signing material, logs, device identifiers, or local paths.

Path traversal, duplicate ZIP entries, hash mismatches, ABI mistakes, native
name collisions, and incomplete payloads are functional or security failures.
Do not weaken those checks to accept a malformed input. Manifest readers should
validate fields they consume while tolerating additive metadata.

Use scoped imperative commits and explain non-obvious ZIP, signing, rollback, or
format decisions in the commit body. A format version changes only when an
existing consumer cannot safely interpret the new semantics.
