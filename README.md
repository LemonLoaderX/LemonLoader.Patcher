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
  --deployment Deployment `
  --mod ExampleMod.dll `
  --plugin ExamplePlugin.dll `
  --user-lib SharedLibrary.dll `
  --user-data UserData `
  --align
```

Omit `--release` to download
`LemonLoader-Android-arm64.zip` from the latest LemonLoader GitHub release.
`--libil2cpp`, `--metadata`, and `--unity-version` override APK discovery.
The Patcher downloads and caches the matching Unity managed libraries and uses
them to restore stripped Unity API wrappers. `--unity-libs` can supply an
offline directory containing `UnityEngine.CoreModule.dll` instead.
Signing is enabled when `--keystore`, `--ks-pass`, and `--ks-alias` are supplied.

Unity libraries are resolved from the Android IL2CPP packages published by
`MelonLoader.UnityDependencies`, with `unity.bepinex.dev` as a fallback. A full
Unity version such as `6000.3.8f1` maps to package `6000.3.8`. Downloads use a
staging file, invalid caches are repaired, and the extracted DLL set is recorded
in `lemonloader-unity-dependencies.json` with source and content hashes. Restore
the same developer reference set without patching an APK with:

```powershell
dotnet run --project src/LemonLoader.Patcher -- unity-dependencies `
  --unity-version 6000.3.8f1 `
  --output UnityDependencies
```

Generated Interop output includes `interop-manifest.json`, which records the
game inputs, tool versions, Unity dependency provenance, and every output hash.
The manifest is also placed beside the Interop DLLs in the APK. Unstripping can
restore managed wrappers and method bodies; it cannot create a native Unity
implementation that the Android player does not contain.

`LemonLoader.ManagedCompat` remains the separate build-time transformer for the
pinned Harmony/MonoMod/Il2CppInterop runtime dependencies. Run
`./scripts/test.ps1` for the guarded transform, Release validation, Unity cache,
fallback, and output-publication regression suites. For a real generated pair,
`scripts/verify-unstripping.ps1` compares stripped and unstripped output and can
assert required Unity methods.

## Desktop GUI

The Avalonia GUI source is under `src/LemonLoader.Patcher.Gui`. During local
development, launch it with:

```powershell
dotnet run --project src/LemonLoader.Patcher.Gui
```

Use `scripts/publish.ps1` on Windows or `scripts/publish.sh` on Linux to publish
the CLI and GUI for both supported desktop platforms. The CLI is a single-file
application; Avalonia's platform dependencies remain beside the GUI executable.
Published executables are
kept at stable paths instead of inside project-local `bin` directories:

```text
Output/Releases/win-x64/cli/LemonLoader.Patcher.exe
Output/Releases/win-x64/gui/LemonLoader.Patcher.Gui.exe
Output/Releases/linux-x64/cli/LemonLoader.Patcher
Output/Releases/linux-x64/gui/LemonLoader.Patcher.Gui
```

Pass one runtime to publish only that platform, for example
`./scripts/publish.ps1 -Runtime win-x64` or `./scripts/publish.sh linux-x64`.
The published applications require the .NET 10 desktop runtime, and Interop
generation additionally requires a .NET SDK for the pinned tool restore.

The publish scripts also copy the workspace's current
`LemonLoader-Android-arm64.zip` beside the platform's `cli` and `gui`
directories. Both applications discover that bundled Release automatically.
Use `-LemonRelease <path>` in PowerShell or set `LEMONLOADER_RELEASE=<path>` for
the shell script to bundle a different build. Without a bundled or explicit
Release, the Patcher falls back to the latest GitHub release download.
Each runtime is assembled in a fresh staging directory, checked for generated
`.cpp2il`, `.tools`, and `Il2CppAssemblies` directories, then replaces the stable
output with rollback protection. Old game-specific files cannot survive a new
publish.

The Release manifest is validated as patcher input but is not copied into an
APK. APK payload merging is restricted to the Release `assets` and `lib` trees.
The patcher requires asset layout v4 and `assets/LemonLoader/payload.json`, so an
older Release is rejected instead of producing a mixed-layout APK. All runtime,
Interop, and deployment inputs live under `assets/LemonLoader`; legacy
`assets/dotnet`, `assets/MelonLoader`, and `assets/LemonLoader/Mods` entries are
removed during migration.

The deployment tree mirrors the MelonLoader base directory. `--deployment`
recursively merges an entire mirror, including arbitrary future top-level
directories. `--mod`, `--plugin`, `--user-lib`, and `--user-data` are convenience
inputs that merge files or directory contents into `Mods`, `Plugins`, `UserLibs`,
and `UserData` respectively. For example, `--user-data UserData` can package
`UserData/Fonts/font.ab`. On launch, the bootstrap copies each deployment file to
the same relative runtime path.
Existing user or ADB-deployed files are preserved, and duplicate APK target paths
are rejected instead of choosing one input silently. The Patcher recomputes the
independent runtime and deployment hashes after all inputs are merged.

Only `libmain.so` may replace an original APK native entry. The bootstrap links
libc++ statically; OpenSSL is stored in the private .NET asset tree. An APK that
already owns `libssl.so` or `libcrypto.so`, any other Release native collision,
or duplicate ZIP entry names is rejected with an explicit error.
