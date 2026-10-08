# File-based workflow

Patcher is optional. Its executables, host manifests, source pins and cache keys
are not needed by the installed Loader. Replace stages with scripts or call only
the commands you need. Loader's
[artifact contract](https://github.com/LemonLoaderX/LemonLoader/blob/main/docs/android/ARTIFACTS.md)
owns installed semantics; [deployment](https://github.com/LemonLoaderX/LemonLoader/blob/main/docs/android/DEPLOYMENT.md)
owns policies and runtime extraction. This page covers host preparation.

## Generate Interop

Inputs are the exact game's ARM64 libil2cpp.so, global-metadata.dat, Unity engine
version and matching managed Unity references. APK defaults are
`lib/arm64-v8a/libil2cpp.so` and `assets/bin/Data/Managed/Metadata/global-metadata.dat`.
Version discovery reads headers as described in [Interop](INTEROP.md).
Standalone generation needs no native main/Unity startup layout or Loader Release.

```powershell
./LemonLoader.Patcher.CLI.exe generate-interop game.apk --output GameInterop
./LemonLoader.Patcher.CLI.exe generate-interop UnpackedGame --output GameInterop
./LemonLoader.Patcher.CLI.exe generate-interop --game-assembly libil2cpp.so `
    --metadata global-metadata.dat --unity-version 2022.3.62f2 `
    --unity-libraries UnityReferences --output GameInterop
```

For your own script, put the binary and metadata under those basenames in Inputs:

```powershell
./Cpp2IL.exe --game-path Inputs --force-binary-path Inputs/libil2cpp.so `
    --force-metadata-path Inputs/global-metadata.dat --force-unity-version 2022.3.62f2 `
    --output-as dummydll --output-to DummyDlls `
    --use-processor attributeanalyzer,attributeinjector
dotnet --roll-forward Major ./Tools/Il2CppInterop/Il2CppInterop.CLI.dll generate `
    --input DummyDlls --output GameInterop --unity UnityReferences `
    --game-assembly Inputs/libil2cpp.so --no-xref-cache --use-opt-out-prefixing
```

The generator pin lives in Directory.Build.props. Custom generators must produce
valid DLLs compatible with Loader/Mods; Patcher performs no metadata repair.
interop-manifest.json accompanies explicit exports for development but is never
required for injection. Only top-level DLLs are injected. Unity references can
come from unity-dependencies or your own tooling; explicit paths avoid downloads.
Interop is never cached automatically. Successful exports replace the whole
output directory transactionally, so choose a dedicated directory.

## Inject files

```powershell
./LemonLoader.Patcher.CLI.exe inject game.apk --output patched.apk `
    --release Loader.zip --interop GameInterop
./LemonLoader.Patcher.CLI.exe inject UnpackedGame --release Loader.zip --interop GameInterop
```

Injection needs no Unity version, reference libraries, Cpp2IL or generator.
DLLs must match the exact game. No Patcher manifest/game identity is required.
inject rejects generation options; patch --interop uses the same injection path.

Current Patcher source accepts Release manifest formats 2 and 3 with installed
layout 9. Format 3 removes redundant build/layout declarations; the payload,
runtime RID and verified file inventory supply those facts. Published Patcher
2.0.0 accepts format 2 only. Use a Patcher supporting format 3 for newly staged
Loader archives; existing format-2 archives remain supported.

For scripts or ZIP-entry editors, retain an original APK and apply this mapping:

| Source | APK destination |
| --- | --- |
| Release `lib/arm64-v8a/libmain.so` | Replace `lib/arm64-v8a/libmain.so` |
| Release `assets/LemonLoader/runtime/**` | Same APK asset paths |
| `GameInterop/*.dll` | `assets/LemonLoader/runtime/interop/<name>.dll` |
| Optional deployment tree | `assets/LemonLoader/deployment/<relative-path>` |
| Release `assets/LemonLoader/payload.json` | Same path, or minimal configuration below |

Preserve the game's libunity.so, libil2cpp.so, DEX/Java, manifest and ordinary assets.
Root Release manifests, tools and Interop records are not runtime destinations.
Retain legal material with distributions. Preserve safe, unique case-sensitive
ZIP paths: full APK unpack/repack on case-insensitive filesystems can lose entries.
Directory injection preserves DEX/apktool source; your own tool owns rebuilding.

Android can omit payload JSON or use `{}`. Bionic needs:

```json
{"runtimeRid":"linux-bionic-arm64"}
```

Optional non-seed policies can be written directly:

```json
{
  "runtimeRid": "linux-bionic-arm64",
  "deploymentFiles": [{"path":"Mods/Example.dll","policy":"refresh"}]
}
```

Paths are relative to deployment; undeclared files use seed. If formatVersion is
present it must be 9. No revision/hash regeneration is needed. Profiles/wildcards
are Patcher conveniences expanded to this ordinary path/policy list.
Patcher omits the policy list when all files use the default seed behavior.

Validate downloads/Release, ZIP paths, native-name collisions and runtime
completeness before mutation. Use supported ARM64 libmain startup layout and
16 KiB-compatible native libraries. Start from an original game, not an existing
Loader installation. Patcher provides these checks, cancellation-safe output
publication and directory rollback. Other installers must provide equivalent
care for their operations.

## Align and sign

```powershell
./LemonLoader.Patcher.CLI.exe process-apk patched.apk --output final.apk `
    --align --keystore signing.jks --key-alias release
```

Patcher reads LEMONLOADER_KEYSTORE_PASSWORD and optional LEMONLOADER_KEY_PASSWORD,
defaulting the latter to the store password. Input/output must differ; alignment
or signing is required. This command accepts any APK without injecting a Loader,
downloading generation inputs or installing an app.

Equivalent Android SDK commands:

```powershell
zipalign -P 16 -f 4 patched.apk aligned.apk
zipalign -P 16 -c 4 aligned.apk
apksigner sign --ks signing.jks --ks-key-alias release `
    --ks-pass env:LEMONLOADER_KEYSTORE_PASSWORD --key-pass env:LEMONLOADER_KEY_PASSWORD `
    --v4-signing-enabled false --out final.apk aligned.apk
zipalign -P 16 -c 4 final.apk
apksigner verify --verbose final.apk
```

For direct SDK use, set both password variables (equal values if needed). Preserve
package/signing identity for replacement updates. Align before signing; later
byte changes invalidate signatures. Your chosen device workflow owns installation.

## Composition and ownership

patch combines generation and injection, then explicitly requested SDK operations.
GUI's operation selector exposes the same four tasks and a separate Unity reference
export. InteropRequest/InteropPipeline own generation; PatchRequest/ApkPatchPipeline
own injection/composition; ApkProcessingRequest/ApkProcessingPipeline own independent
SDK processing. PayloadAssembler owns installed path mapping and policy descriptors.
Core types use `LemonLoader.Patcher.Core`. `PatchRequest.Generation` composes an
`InteropRequest`; `PatchRequest.PostProcessing` shares `ApkPostProcessingOptions`
with independent APK processing. CLI options keep the same file workflow.
No separate repositories, plugin system or mandatory intermediate package format
is introduced. Interchange uses ordinary DLL directories and APKs.
