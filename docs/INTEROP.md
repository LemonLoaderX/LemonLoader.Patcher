# Interop inputs and outputs

Patcher extracts the game's libil2cpp.so/global-metadata.dat, detects the Unity
version and restores matching Unity reference DLLs. --game-assembly, --metadata
and --unity-version override individual inputs; --unity-libraries supplies an
existing reference directory. Overrides must still match the actual game.

```powershell
./LemonLoader.Patcher.CLI.exe patch game.apk --output game-lemonloader.apk `
    --release Loader.zip --unity-version 6000.3.8f1 `
    --unity-libraries UnityDependencies --interop-output GeneratedInterop
./LemonLoader.Patcher.CLI.exe unity-dependencies 6000.3.8f1 --output UnityDependencies
```

Interop output is a published copy of newly generated DLLs and the host-side
interop-manifest.json; it is not an existing assembly input. Replacement occurs
only after successful generation. The manifest records source/tool/CLI/dependency
identity for diagnosis and Mod development; it is not injected into layout 9.
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

Loader owns [runtime ABI and standalone generation](https://github.com/LemonLoaderX/LemonLoader/blob/main/docs/android/INTEROP.md).
Patcher and Loader can pin different Interop source revisions; do not switch a
shared source checkout to resolve that difference.
