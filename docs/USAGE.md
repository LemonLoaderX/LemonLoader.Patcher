# Patching games

Packages require .NET 10. Windows executable: ./LemonLoader.Patcher.CLI.exe;
Linux: ./LemonLoader.Patcher.CLI. Use `patch --help` for the complete option
contract. The separately packaged GUI exposes the same Core pipeline, with
LemonLoader.Patcher.GUI.exe (Windows) or LemonLoader.Patcher.GUI (Linux) at archive root.
GUI managed dependencies are bundled into its executable; adjacent native graphics
libraries and Tools/Il2CppInterop remain required. Move the whole extracted directory.

## Inputs

Use an original ARM64 Unity IL2CPP APK or unpacked directory and a current
layout-9 Loader Release. Already-patched inputs, layout 8, MonoVM and external-DEX
releases are rejected. APK input requires a distinct output path; directory
input is updated in place without an output copy.

Android is the default runtime download; select --runtime bionic when needed.
A local --release without --runtime selects that archive. With both options,
the RIDs must match. Cache archives are isolated by runtime.
Android helpers are embedded in libmain.so; neither profile adds DEX or smali.
Directory mode preserves existing raw DEX/apktool source and does not require
primary DEX files to be present.

```powershell
./LemonLoader.Patcher.CLI.exe patch game.apk --output game-lemonloader.apk
./LemonLoader.Patcher.CLI.exe patch UnpackedGame --release Loader.zip
```

Use generate-interop to export DLLs without modifying a game, inject --interop
to use existing DLLs without generation, or process-apk to align/sign only.
patch --interop also skips generation. Injection needs no Patcher Interop manifest;
DLLs must match the exact game. Generation-only accepts APK/directory or explicit
binary + metadata + Unity version, without requiring the libmain startup layout.
See [stage commands and script replacements](WORKFLOW.md). GUI's Tasks operation
selector exposes these workflows and hides unrelated inputs.

Patcher does not bundle a Loader archive. An omitted --release resolves the
selected latest Loader distribution. Fully offline work requires a local Release,
Unity references and the verified Cpp2IL tool already cached or explicitly supplied.
Cpp2IL, Unity references and Loader downloads stay in `.tools` beside the Patcher
executable, independent of game/output paths and the current working directory.
The Patcher directory must be writable for downloads; it does not fall back to
creating caches beside games. Separate GUI/CLI installations own separate caches.
Valid cached Releases are reused offline; remove the runtime ZIP from that
directory to request a newer download. Invalid caches are validated and replaced
automatically. Existing output-adjacent `.tools` directories are no longer read or
written. To avoid downloading again, move their Cpp2IL, UnityDependencies and
runtime ZIP entries into the executable's `.tools`; the normal validation remains.
Old `Interop` cache entries can be deleted; they are no longer used.

## Deployment

The deployment directory mirrors the runtime MelonLoader root. Standard
Mods/Plugins/UserLibs/UserData names are case-sensitive on Android.

```powershell
./LemonLoader.Patcher.CLI.exe patch game.apk --output game-lemonloader.apk `
    --deployment Deployment --profile production `
    --policy "UserData/Example/defaults.cfg=upgrade"
```

| Profile | Mods, Plugins, UserLibs | UserData | Other paths |
| --- | --- | --- | --- |
| development (default) | seed | seed | seed |
| production | refresh | upgrade | seed |
| locked | enforce | upgrade | seed |

Seed installs missing files on an APK update and preserves edits. Upgrade replaces
only an unchanged previously owned copy. Refresh applies once per APK update;
enforce restores missing/changed bytes each launch. Unchanged-package launches
skip seed/upgrade/refresh. These are deployment choices, not tamper protection.

--policy accepts a file or directory/** and is repeatable. Exact rules win over
directory rules; longer directory prefixes win. Every rule must match a packaged
file. Layout 9 emits only non-seed path/policy overrides. Manual additions default
to seed; changes require no revision/hash regeneration. Full extraction/rollback
semantics belong to the Loader [deployment contract](https://github.com/LemonLoaderX/LemonLoader/blob/main/docs/android/DEPLOYMENT.md).

## Alignment and signing

APK post-processing is opt-in and unavailable for directory input.
--align requests 16 KiB ZIP alignment. --keystore with --key-alias requests signing;
neither operation implies the other. --zipalign/--apksigner override PATH discovery
and are valid only when their operation is requested. Missing tools fail before
output publication.

```powershell
$env:LEMONLOADER_KEYSTORE_PASSWORD = "<store-password>"
$env:LEMONLOADER_KEY_PASSWORD = "<key-password>"
./LemonLoader.Patcher.CLI.exe patch game.apk --output game-lemonloader.apk `
    --align --keystore signing.jks --key-alias release
```

The key password variable is optional when equal to the store password. Passwords
are passed only through the signing child's environment, never command arguments.
Maintain the existing application/signing identity for replacement updates.

## Results

Progress/tool output goes to stderr as plain text, with terminal control sequences
removed. GUI display, copying and saving use the same plain tool output.
Successful APK patch writes output, sha256
and, when generation ran, unity-version lines to stdout. Directory mode reports its modified input path
and omits sha256. Exit codes: 0 success, 1 execution failure, 2 invalid usage,
130 cancellation. --verbose adds exception details.

Cancellation before commit preserves the previous APK or restores directory
changes. Once committed, the result is successful even if cancellation arrives.
A requested Interop export can complete before APK injection; cancelling the
patch does not remove that independently published directory. Backup cleanup
warnings preserve the committed result and identify remaining recovery files.

[Interop](INTEROP.md) covers input overrides, export and Unity reference restoration.
Manual Loader installation and runtime/Mod behavior live in the
[Loader guides](https://github.com/LemonLoaderX/LemonLoader/blob/main/docs/README.md).
