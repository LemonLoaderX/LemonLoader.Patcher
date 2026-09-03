# LemonLoader Patcher

Cross-platform CLI and Avalonia GUI for injecting LemonLoader into Android ARM64
IL2CPP APKs or unpacked APK directories. Both front ends call the same typed
patch pipeline; the GUI does not shell out to or emulate the CLI.

For APK input, Patcher works directly with ZIP entries; directory input is
updated in place without copying the original tree. It generates game-specific
Interop assemblies, restores the matching Unity managed references, and merges
the current LemonLoader Release and deployment tree. APK alignment and signing
are explicit, independent post-processing operations; the core patch does not
require an Android SDK. An input that already contains a loader payload is
rejected rather than migrated.

## CLI

The released executable is `CLI/LemonLoader.Patcher.CLI.exe` on Windows and
`CLI/LemonLoader.Patcher.CLI` on Linux. Run `patch --help` to show the command
contract installed with the current version.

Release packages are framework-dependent and require the .NET 10 runtime.

### Patch command

```text
LemonLoader.Patcher.CLI patch <input.apk> --output <output.apk> [options]
LemonLoader.Patcher.CLI patch <input-directory> [options]
```

`<input>` is the only unconditional input. An APK input requires `--output` and
is never modified in place. An unpacked directory is modified directly and must
not be given `--output`.

#### Required inputs

| Input | Description |
| --- | --- |
| `<input>` | Positional path to the original APK or unpacked APK directory. Already-patched inputs are rejected. |
| `--output <path>` | Required for APK input only. Destination APK; it must not resolve to the input path. |

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

#### APK post-processing options

| Option | Required | Description and default |
| --- | --- | --- |
| `--align` | No | Requests 16 KiB APK ZIP alignment. Without it, the patched APK is published without running `zipalign`. |
| `--zipalign <path>` | With `--align` only | Explicit `zipalign` executable. When omitted, Patcher searches `PATH`. Supplying it without `--align` is an error. |
| `--keystore <path>` | No | Requests APK signing with this keystore. Signing does not implicitly request alignment. |
| `--key-alias <name>` | With `--keystore` | Alias of the signing key. Supplying it without `--keystore` is an error. |
| `--apksigner <path>` | With `--keystore` only | Explicit `apksigner` executable or script. When omitted, Patcher searches `PATH`. Supplying it without `--keystore` is an error. |

Signing passwords are not command-line parameters. Set
`LEMONLOADER_KEYSTORE_PASSWORD`; optionally set `LEMONLOADER_KEY_PASSWORD` when
the key password differs. Patcher passes them only through the `apksigner` child
environment. If alignment or signing is requested and its tool cannot be found,
the operation fails before publishing the output. Directory input rejects every
APK post-processing option.

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
    --output game-lemonloader.apk
```

Patch an unpacked directory directly. No output directory is copied or created:

```powershell
CLI\LemonLoader.Patcher.CLI.exe patch UnpackedGame `
    --release LemonLoader-Android-arm64.zip
```

Directory mode accepts raw `classes*.dex` files and apktool-decoded
`smali`/`smali_classesN` source directories. CoreCLR adds its helper as the next
unused DEX index without materializing synthetic source DEX files.

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
    --policy "UserData/ExampleMod/defaults.cfg=upgrade"
```

Offline patch with explicit Release, Unity references, and a published Interop
directory:

```powershell
CLI\LemonLoader.Patcher.CLI.exe patch game.apk `
    --output game-lemonloader.apk `
    --release LemonLoader-Android-arm64.zip `
    --unity-version 6000.3.8f1 `
    --unity-libraries UnityDependencies `
    --interop-output GeneratedInterop
```

Aligned and signed output using tools discovered on `PATH`:

```powershell
$env:LEMONLOADER_KEYSTORE_PASSWORD = "<store-password>"
$env:LEMONLOADER_KEY_PASSWORD = "<key-password>" # Optional
CLI\LemonLoader.Patcher.CLI.exe patch game.apk `
    --output game-lemonloader.apk `
    --align `
    --keystore signing.jks `
    --key-alias release
```

Tool paths can instead be supplied explicitly:

```powershell
CLI\LemonLoader.Patcher.CLI.exe patch game.apk `
    --output game-lemonloader.apk `
    --align `
    --zipalign <android-sdk>\build-tools\<version>\zipalign.exe `
    --keystore signing.jks `
    --key-alias release `
    --apksigner <android-sdk>\build-tools\<version>\apksigner.bat
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

Directory mode reports the modified input path as `output` and omits `sha256`,
because it does not create a separate APK artifact.

Exit codes are `0` for success, `1` for execution failure, `2` for invalid
usage, and `130` for cancellation.

Published Patcher packages always include the fixed Il2CppInterop generator built
from the revision pinned in `Directory.Build.props`; they never restore the older
NuGet tool. The generation manifest records its source, CLI hash, and deterministic
dependency content hash. The bundled `net6.0` generator uses .NET major roll-forward,
so the runtime already required by Patcher is sufficient.

The Patcher does not include a LemonLoader Release archive. Without `--release`,
it downloads and caches
`https://github.com/LemonLoaderX/LemonLoader/releases/latest/download/LemonLoader-Android-arm64.zip`.
Use `--release <path>` for an explicit local or offline build.

## GUI

Start `GUI/LemonLoader.Patcher.GUI.exe` on Windows or
`GUI/LemonLoader.Patcher.GUI` on Linux. The GUI accepts either an APK or unpacked
directory and exposes Unity dependency restoration as a separate workspace.
Selecting a directory disables output, alignment, and signing controls because
the directory is patched in place. APK alignment and signing are grouped under
the optional post-processing section. Paths use native pickers, operations can
be cancelled, and pipeline/tool output is shown in the task log.

For source builds, the Avalonia project and assembly are named
`LemonLoader.Patcher.GUI`:

```powershell
dotnet run --project src/LemonLoader.Patcher.GUI
```

## Source layout

`LemonLoader.Patcher.Core` keeps the public patch interface small while the
implementation is divided by responsibility:

- `ApkPatchPipeline` coordinates one patch run and owns temporary workspace cleanup.
- `GameInteropGenerator` reads either input form and produces verified game Interop.
- `PayloadAssembler` builds the canonical payload for both APK and directory targets.
- `DirectoryInjector` applies directory overlays transactionally with rollback.
- `ApkPostProcessor` performs only explicitly requested alignment and signing.
- `ProcessRunner` and the resolver modules contain bounded external tool execution.

The CLI parser and application runner are separate modules. The GUI code-behind is
split into operation, request, picker, and logging partials; neither front end
duplicates Core patch behavior.

## Build and publish

```powershell
pwsh -NoProfile -File scripts/test.ps1
pwsh -NoProfile -File scripts/publish.ps1 `
  -Runtime win-x64 `
  -Il2CppInteropSourceRoot ..\dependencies\Il2CppInterop
pwsh -NoProfile -File scripts/package-release.ps1 -Version v1.0.4
```

Stable outputs use the same acronym casing as the products:

```text
Output/Releases/win-x64/CLI/LemonLoader.Patcher.CLI.exe
Output/Releases/win-x64/GUI/LemonLoader.Patcher.GUI.exe
Output/Releases/win-x64/Tools/Il2CppInterop/Il2CppInterop.CLI.dll
Output/Releases/linux-x64/CLI/LemonLoader.Patcher.CLI
Output/Releases/linux-x64/GUI/LemonLoader.Patcher.GUI
Output/Releases/linux-x64/Tools/Il2CppInterop/Il2CppInterop.CLI.dll
Output/Packages/v1.0.4/LemonLoader.Patcher-win-x64.zip
Output/Packages/v1.0.4/LemonLoader.Patcher-linux-x64.tar.gz
Output/Packages/v1.0.4/SHA256SUMS.txt
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
`https://github.com/LemonLoaderX/LemonLoader.Patcher/releases`.

The current Release contract is asset layout v8 with
`assets/LemonLoader/payload.json`. Runtime loader, dotnet, Interop, and packaged
deployment content use independent hashes. Release and APK validation hash their
complete contents; normal device startup trusts the installed domain marker and
does not rescan the private runtime. Published payloads contain the minimal
`runtime-identity.json`; full build provenance and build commands remain outside
the Release. Validators require the fields they consume and tolerate additive
JSON metadata and files instead of maintaining content blacklists. Android
staging, rather than Patcher, decides whether desktop-only material is published.
Native entry replacement is limited to `libmain.so`;
private .NET native dependencies remain isolated from game-owned libraries.

## Contributing and license

See [CONTRIBUTING.md](CONTRIBUTING.md) for repository and validation rules and
[SECURITY.md](SECURITY.md) for private vulnerability reporting. LemonLoader
Patcher is licensed under Apache-2.0; bundled tools retain their own license files
and source provenance.
