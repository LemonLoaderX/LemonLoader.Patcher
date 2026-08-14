#!/usr/bin/env bash
set -euo pipefail

configuration="${CONFIGURATION:-Release}"
if (($# == 0)); then
    runtimes=(win-x64 linux-x64)
else
    runtimes=("$@")
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_dir/.." && pwd)"
release_root="$repository_root/Output/Releases"
lemon_release="${LEMONLOADER_RELEASE:-$repository_root/../LemonLoader/Output/Releases/LemonLoader-Android-arm64.zip}"

for runtime in "${runtimes[@]}"; do
    case "$runtime" in
        win-x64|linux-x64) ;;
        *) echo "Unsupported runtime: $runtime" >&2; exit 2 ;;
    esac

    cli_temp="$repository_root/Output/PublishTemp/$runtime/cli"
    gui_temp="$repository_root/Output/PublishTemp/$runtime/gui"
    rm -rf -- "$cli_temp" "$gui_temp"

    dotnet publish "$repository_root/src/LemonLoader.Patcher/LemonLoader.Patcher.csproj" \
        --configuration "$configuration" \
        --runtime "$runtime" \
        --output "$cli_temp" \
        --no-self-contained \
        -p:PublishSingleFile=true \
        -p:DebugType=None \
        -p:DebugSymbols=false
    dotnet publish "$repository_root/src/LemonLoader.Patcher.Gui/LemonLoader.Patcher.Gui.csproj" \
        --configuration "$configuration" \
        --runtime "$runtime" \
        --output "$gui_temp" \
        --no-self-contained \
        -p:PublishSingleFile=false \
        -p:DebugType=None \
        -p:DebugSymbols=false

    mkdir -p -- "$release_root/$runtime/cli" "$release_root/$runtime/gui"
    cp -Rf -- "$cli_temp/." "$release_root/$runtime/cli/"
    cp -Rf -- "$gui_temp/." "$release_root/$runtime/gui/"

    if [[ -f "$lemon_release" ]]; then
        cp -f -- "$lemon_release" "$release_root/$runtime/LemonLoader-Android-arm64.zip"
    else
        printf 'Warning: LemonLoader Release was not found at %s. The published Patcher will require --release or a working online Release.\n' \
            "$lemon_release" >&2
    fi

    printf 'Published LemonLoader.Patcher for %s to:\n  %s\n' \
        "$runtime" "$release_root/$runtime"
done
