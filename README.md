# LemonLoader Patcher

CLI and Avalonia GUI for injecting LemonLoader into original Android ARM64 Unity
IL2CPP APKs or unpacked directories. Both front ends use the same Core pipeline.

Patcher generates game Interop, restores Unity references and adds a validated
layout-9 Loader payload with optional Mods/config. APK ZIP entries are handled
directly; unpacked directories are updated transactionally in place. Android and
Bionic require API26+. Injection adds no DEX. Old layout-8/MonoVM/external-DEX and
already-patched inputs are unsupported.

## Start here

- [Patch an APK or directory](docs/USAGE.md)
- [Interop and Unity references](docs/INTEROP.md)
- [Build, test and maintain](CONTRIBUTING.md)
- [Script catalog](scripts/README.md)
- [Release notes](docs/releases/README.md)
- [Loader installation/runtime guides](https://github.com/LemonLoaderX/LemonLoader/blob/main/docs/README.md)

[Binary releases](https://github.com/LemonLoaderX/LemonLoader.Patcher/releases)
require .NET 10. Download the GUI or CLI archive, then start
LemonLoader.Patcher.GUI.exe or LemonLoader.Patcher.CLI.exe at its root on Windows;
Linux binaries omit .exe. Keep its adjacent Tools directory with the executable.
`patch --help` describes the installed CLI options. Alignment/signing are explicit
operations; the Core patch needs no Android SDK.

## License

[Apache-2.0](LICENSE); bundled tools retain their licenses and source identities.
See [Security](SECURITY.md) for private vulnerability reporting.
