# LemonLoader.Patcher

This repository owns the build-time managed assembly transformations required by
the current Android LemonLoader dependency stack. It is separate from the loader
runtime and has no device or APK responsibilities.

The command accepts one managed dependency assembly, or
`--interop-directory <path>`. Every transformation is guarded by the exact
assembly identity and expected IL shape and fails closed on unknown input.

```text
dotnet run --project AndroidManagedCompatPatcher.csproj -- \
  --runtime-major 10 path/to/0Harmony.dll
```

The project is framework-dependent and portable across Windows, Linux, and
macOS hosts with .NET SDK 10. It must not declare a host RuntimeIdentifier.

Run `./scripts/test.ps1` for the guarded transform and idempotence regression
suite.
