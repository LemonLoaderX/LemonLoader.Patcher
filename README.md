# LemonLoader Patcher

Cross-platform CLI and Avalonia GUI for producing LemonLoader-enabled Android
ARM64 IL2CPP APKs. Both front ends call the same typed patch pipeline; the GUI
does not shell out to or emulate the CLI.

The Patcher works directly with ZIP entries, generates game-specific Interop
assemblies, restores the matching Unity managed references, merges the current
LemonLoader Release and deployment tree, zipaligns the result for 16 KiB pages,
and optionally signs it. Use an original game APK as input. An APK that already
contains a loader payload is rejected rather than migrated.

## CLI

```powershell
dotnet run --project src/LemonLoader.Patcher.CLI -- patch game.apk `
  --output game-lemonloader.apk `
  --release LemonLoader-Android-arm64.zip `
  --deployment Deployment `
  --profile production `
  --policy "UserData/Managed/**=refresh" `
  --sdk D:\Develop\Android\SDK
```

The deployment directory mirrors the runtime MelonLoader directory:

```text
Deployment/
  Mods/
  Plugins/
  UserLibs/
  UserData/
```

Future top-level directories are preserved. Standard directories must use the
shown Android casing. `development` seeds missing files, `production` updates
packaged code while preserving user-modified data, and `locked` enforces
packaged code on every launch. Repeat `--policy path=policy` only for exceptions.

Every APK is zipaligned. The Android SDK is resolved from `--sdk`,
`ANDROID_SDK_ROOT`, or `ANDROID_HOME`. Signing uses `--keystore` and
`--key-alias`; passwords are read from `LEMONLOADER_KEYSTORE_PASSWORD` and the
optional `LEMONLOADER_KEY_PASSWORD`, so they do not appear in the process list.

Progress and external tool output are written to stderr. Successful result
fields are written to stdout:

```text
output: C:\build\game-lemonloader.apk
sha256: <sha256>
unity-version: 6000.3.8f1
```

Exit codes are `0` for success, `1` for an execution failure, `2` for invalid
usage, and `130` for cancellation. Errors are concise by default; add
`--verbose` for exception details.

Restore the same Unity reference set for a Mod project without patching an APK:

```powershell
dotnet run --project src/LemonLoader.Patcher.CLI -- unity-dependencies `
  6000.3.8f1 --output UnityDependencies
```

The resolver first uses `MelonLoader.UnityDependencies`, then
`unity.bepinex.dev`. Downloads and extracted assemblies are verified and
recorded in `lemonloader-unity-dependencies.json`.

## GUI

The Avalonia project and assembly are named `LemonLoader.Patcher.GUI`:

```powershell
dotnet run --project src/LemonLoader.Patcher.GUI
```

The GUI exposes two workspaces: APK patching and Unity dependency restoration.
Common inputs stay in the main form; Interop overrides and signing are grouped
under advanced sections. Paths use native file and directory pickers, operations
can be cancelled, and pipeline/tool output is shown in the task log.

## Build and publish

```powershell
pwsh -NoProfile -File scripts/test.ps1
pwsh -NoProfile -File scripts/publish.ps1 `
  -Runtime win-x64 `
  -LemonRelease ..\LemonLoader\Output\Releases\LemonLoader-Android-arm64.zip
```

Stable outputs use the same acronym casing as the products:

```text
Output/Releases/win-x64/CLI/LemonLoader.Patcher.CLI.exe
Output/Releases/win-x64/GUI/LemonLoader.Patcher.GUI.exe
Output/Releases/linux-x64/CLI/LemonLoader.Patcher.CLI
Output/Releases/linux-x64/GUI/LemonLoader.Patcher.GUI
```

Publishing uses a fresh staging directory and atomically replaces the runtime
output. A bundled `LemonLoader-Android-arm64.zip` is placed beside `CLI` and
`GUI`; without it, the Patcher downloads the current Release. Generated game
artifacts such as `.cpp2il`, `.tools`, and `Il2CppAssemblies` are rejected from
published output.

The current Release contract is asset layout v7 with
`assets/LemonLoader/payload.json`. Runtime loader, dotnet, Interop, and packaged
deployment content use independent hashes. Release and APK validation hash their
complete contents; normal device startup trusts the installed domain marker and
does not rescan the private runtime. `runtime/loader/Documentation` is not valid
Android payload content. Native entry replacement is limited to `libmain.so`;
private .NET native dependencies remain isolated from game-owned libraries.
