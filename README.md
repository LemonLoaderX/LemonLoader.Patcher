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

The released executable is `CLI/LemonLoader.Patcher.CLI.exe` on Windows and
`CLI/LemonLoader.Patcher.CLI` on Linux. Run `patch --help` to show the command
contract installed with the current version.

### Patch command

```text
LemonLoader.Patcher.CLI patch <input.apk> --output <output.apk> [options]
```

`<input.apk>` and `--output` are the only unconditional inputs. The input must
be an original ARM64 Unity IL2CPP APK and is never modified in place.

#### Required inputs

| Input | Description |
| --- | --- |
| `<input.apk>` | Positional path to the original APK. Already-patched APKs are rejected. |
| `--output <path>` | Destination APK. It must not resolve to the input path. |

#### Payload options

| Option | Required | Description and default |
| --- | --- | --- |
| `--release <path>` | No | Local `LemonLoader-Android-arm64.zip`. When omitted, Patcher uses a valid cache beside the output and otherwise downloads the latest Release. |
| `--deployment <directory>` | No | Directory mirrored into the runtime MelonLoader root. It may contain `Mods`, `Plugins`, `UserLibs`, `UserData`, or future top-level directories. |
| `--profile <name>` | No | Deployment behavior: `development`, `production`, or `locked`. Defaults to `development`. |
| `--policy <path=policy>` | No | Overrides one file or `directory/**`. Repeatable. Policies are `seed`, `upgrade`, `refresh`, and `enforce`. Every rule must match a packaged file. |

#### Interop options

These are optional. Normally Patcher extracts the game inputs from the APK,
detects the Unity version, downloads the matching Unity managed references, and
uses the fixed Il2CppInterop generator bundled with Patcher.

| Option | Description and default |
| --- | --- |
| `--game-assembly <path>` | Uses this `libil2cpp.so` instead of `lib/arm64-v8a/libil2cpp.so` from the APK. |
| `--metadata <path>` | Uses this `global-metadata.dat` instead of the APK entry. |
| `--unity-version <version>` | Overrides automatic detection from `globalgamemanagers`, for example `6000.3.8f1`. |
| `--unity-libraries <directory>` | Uses an existing directory of Unity managed reference DLLs instead of resolving and caching them. |
| `--interop-output <directory>` | Publishes a copy of the generated game Interop assemblies and `interop-manifest.json` for Mod development. Existing output is replaced only after generation succeeds. |
| `--cpp2il <path>` | Uses a specific Cpp2IL executable instead of the pinned verified download. |
| `--il2cppinterop-cli <path>` | Development override for a built `Il2CppInterop.CLI.dll`. Its adjacent dependencies must remain beside it. Normal releases should omit this option. |

#### Android and signing options

| Option | Required | Description and default |
| --- | --- | --- |
| `--sdk <directory>` | No | Android SDK root. When omitted, Patcher uses `ANDROID_SDK_ROOT`, then `ANDROID_HOME`. One of these sources must resolve to SDK build-tools. |
| `--keystore <path>` | No | Signs the aligned APK with this keystore. Without it, the result is aligned but unsigned. |
| `--key-alias <name>` | With `--keystore` | Alias of the signing key. Supplying it without `--keystore` is an error. |

Signing passwords are not command-line parameters. Set
`LEMONLOADER_KEYSTORE_PASSWORD`; optionally set `LEMONLOADER_KEY_PASSWORD` when
the key password differs. Patcher passes them only through the `apksigner` child
environment.

#### Global options

| Option | Description |
| --- | --- |
| `-h`, `--help` | Shows root or command help. |
| `--version` | Prints the Patcher version. |
| `--verbose` | Adds exception details when a command fails. It can appear anywhere in the command. |

### Examples

Minimal patch using the latest LemonLoader Release and automatic game input
detection:

```powershell
CLI\LemonLoader.Patcher.CLI.exe patch game.apk `
    --output game-lemonloader.apk `
    --sdk D:\Develop\Android\SDK
```

Patch with Mods and persistent UserData using the `production` profile:

```text
Deployment/
  Mods/
    ExampleMod.dll
  UserData/
    ExampleMod/
      defaults.cfg
```

```powershell
CLI\LemonLoader.Patcher.CLI.exe patch game.apk `
    --output game-lemonloader.apk `
    --deployment Deployment `
    --profile production `
    --policy "UserData/ExampleMod/defaults.cfg=upgrade" `
    --sdk D:\Develop\Android\SDK
```

Offline patch with explicit Release, Unity references, and a published Interop
directory:

```powershell
CLI\LemonLoader.Patcher.CLI.exe patch game.apk `
    --output game-lemonloader.apk `
    --release LemonLoader-Android-arm64.zip `
    --unity-version 6000.3.8f1 `
    --unity-libraries UnityDependencies `
    --interop-output GeneratedInterop `
    --sdk D:\Develop\Android\SDK
```

Signed output:

```powershell
$env:LEMONLOADER_KEYSTORE_PASSWORD = "<store-password>"
$env:LEMONLOADER_KEY_PASSWORD = "<key-password>" # Optional
CLI\LemonLoader.Patcher.CLI.exe patch game.apk `
    --output game-lemonloader.apk `
    --sdk D:\Develop\Android\SDK `
    --keystore signing.jks `
    --key-alias release
```

### Deployment behavior

The deployment directory mirrors the runtime MelonLoader directory. Standard
directory names are case-sensitive on Android:

```text
Deployment/
  Mods/
  Plugins/
  UserLibs/
  UserData/
```

| Profile | Mods, Plugins, UserLibs | UserData | Other directories |
| --- | --- | --- | --- |
| `development` | `seed` | `seed` | `seed` |
| `production` | `refresh` | `upgrade` | `seed` |
| `locked` | `enforce` | `upgrade` | `seed` |

- `seed` installs a missing file and otherwise preserves the destination.
- `upgrade` replaces a file only when it still matches the last packaged copy.
- `refresh` applies changed packaged bytes once per deployment revision, then
  preserves later device-side edits until another revision is installed.
- `enforce` restores the packaged bytes whenever the file is missing or differs.

Exact rules take precedence over directory rules; longer matching directory
rules take precedence over shorter ones. Examples:

```powershell
--policy "Mods/Required.dll=enforce" `
--policy "UserData/Managed/**=refresh"
```

### Unity dependencies command

This command restores the same Unity managed references used for Interop
generation without patching an APK:

```text
LemonLoader.Patcher.CLI unity-dependencies <unity-version> --output <directory> [--cache <directory>]
```

| Input | Required | Description |
| --- | --- | --- |
| `<unity-version>` | Yes | Full Unity version, for example `6000.3.8f1`. |
| `--output <directory>` | Yes | Published Unity reference directory. |
| `--cache <directory>` | No | Download/extraction cache. Defaults to `.tools/UnityDependencies` beside the output directory. |

```powershell
CLI\LemonLoader.Patcher.CLI.exe unity-dependencies 6000.3.8f1 `
    --output UnityDependencies
```

The resolver first uses `MelonLoader.UnityDependencies`, then falls back to
`unity.bepinex.dev`. Downloads and extracted assemblies are verified and
recorded in `lemonloader-unity-dependencies.json`.

### Output and exit codes

Progress and external tool output go to stderr. A successful `patch` writes
machine-readable result lines to stdout:

```text
output: C:\build\game-lemonloader.apk
sha256: <sha256>
unity-version: 6000.3.8f1
```

Exit codes are `0` for success, `1` for execution failure, `2` for invalid
usage, and `130` for cancellation.

Published Patcher packages always include the fixed Il2CppInterop generator built
from fork revision `aecf17eeb5a6edd0b1aa4d1dc6460a83a0716aba`; they never
restore the older NuGet tool. The generation manifest records its source, CLI
hash, and deterministic dependency content hash. The bundled `net6.0` generator
uses .NET major roll-forward, so the runtime already required by Patcher is
sufficient.

The Patcher does not include a LemonLoader Release archive. Without `--release`,
it downloads and caches
`https://github.com/anosu/LemonLoader/releases/latest/download/LemonLoader-Android-arm64.zip`.
Use `--release <path>` for an explicit local or offline build.

## GUI

Start `GUI/LemonLoader.Patcher.GUI.exe` on Windows or
`GUI/LemonLoader.Patcher.GUI` on Linux. The GUI exposes APK patching and Unity
dependency restoration as separate workspaces. Common inputs stay in the main
form; Interop overrides and signing are grouped under advanced sections. Paths
use native file and directory pickers, operations can be cancelled, and
pipeline/tool output is shown in the task log.

For source builds, the Avalonia project and assembly are named
`LemonLoader.Patcher.GUI`:

```powershell
dotnet run --project src/LemonLoader.Patcher.GUI
```

## Build and publish

```powershell
pwsh -NoProfile -File scripts/test.ps1
pwsh -NoProfile -File scripts/publish.ps1 `
  -Runtime win-x64 `
  -Il2CppInteropSourceRoot ..\dependencies\Il2CppInterop
```

Stable outputs use the same acronym casing as the products:

```text
Output/Releases/win-x64/CLI/LemonLoader.Patcher.CLI.exe
Output/Releases/win-x64/GUI/LemonLoader.Patcher.GUI.exe
Output/Releases/win-x64/Tools/Il2CppInterop/Il2CppInterop.CLI.dll
Output/Releases/linux-x64/CLI/LemonLoader.Patcher.CLI
Output/Releases/linux-x64/GUI/LemonLoader.Patcher.GUI
Output/Releases/linux-x64/Tools/Il2CppInterop/Il2CppInterop.CLI.dll
```

Publishing uses a fresh staging directory and atomically replaces the runtime
output. It builds the pinned, clean Il2CppInterop generator source and packages
one shared tool directory beside `CLI` and `GUI`. A LemonLoader Release ZIP is
explicitly rejected from Patcher output. Generated game artifacts such as
`.cpp2il`, `.tools`, and `Il2CppAssemblies` are also rejected.
Formal publishing also requires a clean Patcher worktree. `-AllowDirtySource`
exists only for local output-layout validation and its output must not be
uploaded.

Binary releases are published at
`https://github.com/anosu/LemonLoader.Patcher/releases`.

The current Release contract is asset layout v7 with
`assets/LemonLoader/payload.json`. Runtime loader, dotnet, Interop, and packaged
deployment content use independent hashes. Release and APK validation hash their
complete contents; normal device startup trusts the installed domain marker and
does not rescan the private runtime. `runtime/loader/Documentation` is not valid
Android payload content. Native entry replacement is limited to `libmain.so`;
private .NET native dependencies remain isolated from game-owned libraries.
