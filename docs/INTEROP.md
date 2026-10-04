# Interop inputs and outputs

Patcher extracts the game's libil2cpp.so/global-metadata.dat, detects the Unity
version and restores matching Unity reference DLLs. --game-assembly, --metadata
and --unity-version override individual inputs; --unity-libraries supplies an
existing reference directory. Overrides must still match the actual game.
Automatic version detection reads the SerializedFile metadata version from
globalgamemanagers, then mainData, or the engine revision from data.unity3d's Bundle
header. APK and directory inputs use the same order; unreadable/unsupported headers
fall through to the next source. Only bounded header fields are read, directly
from the input, without copying game assets or decompressing Bundle blocks.
There is no whole-file version-string scan or libunity.so scan. Modified/stripped
headers and unknown formats require --unity-version (Unity version in GUI).

```powershell
./LemonLoader.Patcher.CLI.exe generate-interop game.apk --output GeneratedInterop `
    --unity-version 6000.3.8f1 --unity-libraries UnityDependencies
./LemonLoader.Patcher.CLI.exe unity-dependencies 6000.3.8f1 --output UnityDependencies
```

Interop output is a published copy of newly generated DLLs and the host-side
interop-manifest.json. inject --interop or patch --interop consumes an existing
DLL directory without this manifest, version detection or generation tools.
It cannot be combined with generation overrides or --interop-output. Replacement occurs
only after successful generation. The manifest records Unity/tool versions,
dependency origins and assembly names/sizes for Mod development; it is not injected
into layout 9 and does not hash game inputs, generators or generated DLLs.
The entire export directory is replaced. It must not overlap game, deployment,
dependency or other input/output paths; linked destinations are rejected.

Each patch generates Interop in a temporary workspace removed after the run.
There is no persistent Interop cache. Only `--interop-output` retains an explicit
export for Mod development.

Packages bundle the generator built from Patcher's own Directory.Build.props pin.
The net6 generator permits major runtime roll-forward to Patcher's required
runtime. --il2cppinterop-cli is an explicit development override requiring its
adjacent dependencies; --cpp2il chooses an executable instead of the verified
pinned download. Use command help for all options.

Unity reference restoration tries MelonLoader.UnityDependencies, then
unity.bepinex.dev. Downloads/extracted assemblies are checked and recorded in
lemonloader-unity-dependencies.json. unity-dependencies accepts --cache; its default
is `.tools/UnityDependencies` beside the Patcher executable, shared with patching.
Local references and cached/explicit
Cpp2IL are necessary for offline generation.
Custom generators must produce valid DLLs; Patcher no longer repairs generated
metadata. Lightweight generation records are produced only for explicit
exports. [Workflow](WORKFLOW.md) covers generation without a Loader and direct
binary/metadata inputs.

Loader owns [runtime ABI and standalone generation](https://github.com/LemonLoaderX/LemonLoader/blob/main/docs/android/INTEROP.md).
Patcher and Loader can pin different Interop source revisions; do not switch a
shared source checkout to resolve that difference.
