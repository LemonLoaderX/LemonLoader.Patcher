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
