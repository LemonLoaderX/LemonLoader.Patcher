# LemonLoader.Patcher

Cross-platform Android IL2CPP patching application for Windows and Linux. It
provides a CLI and an Avalonia desktop GUI over the same patch pipeline.

The patcher reads the APK as ZIP data; apktool is not required. It can extract
`libil2cpp.so`, `global-metadata.dat`, and the Unity version directly from a
standard ARM64 Unity APK, generate game-specific Interop assemblies, merge a
game-independent LemonLoader Release, add Mod DLLs, align native entries, and
sign the result.

```powershell
dotnet run --project src/LemonLoader.Patcher -- patch `
  --apk game.apk `
  --release LemonLoader-Android-arm64.zip `
  --output game-lemon.apk `
  --interop-output GeneratedInterop `
  --mod ExampleMod.dll `
  --align
```

Omit `--release` to download
`LemonLoader-Android-arm64.zip` from the latest LemonLoader GitHub release.
`--libil2cpp`, `--metadata`, and `--unity-version` override APK discovery.
Signing is enabled when `--keystore`, `--ks-pass`, and `--ks-alias` are supplied.

`LemonLoader.ManagedCompat` remains the separate build-time transformer for the
pinned Harmony/MonoMod/Il2CppInterop runtime dependencies. Run
`./scripts/test.ps1` for its guarded transform regression suite.
