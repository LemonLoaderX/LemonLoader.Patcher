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

cleanup_publish() {
    local exit_code=$?
    if [[ "${moved_existing:-false}" == true &&
          -n "${backup:-}" && -e "$backup" &&
          -n "${runtime_output:-}" && ! -e "$runtime_output" ]]; then
        mv -- "$backup" "$runtime_output"
    fi
    if [[ -n "${work_root:-}" && -e "$work_root" ]]; then
        rm -rf -- "$work_root"
    fi
    return "$exit_code"
}

for runtime in "${runtimes[@]}"; do
    case "$runtime" in
        win-x64|linux-x64) ;;
        *) echo "Unsupported runtime: $runtime" >&2; exit 2 ;;
    esac

    work_root="$repository_root/Output/PublishTemp/$runtime"
    runtime_staging="$work_root/release"
    cli_temp="$runtime_staging/CLI"
    gui_temp="$runtime_staging/GUI"
    runtime_output="$release_root/$runtime"
    case "$work_root" in
        "$repository_root/Output/PublishTemp/"*) ;;
        *) printf 'Refusing unsafe work path: %s\n' "$work_root" >&2; exit 1 ;;
    esac
    case "$runtime_output" in
        "$release_root/"*) ;;
        *) printf 'Refusing unsafe release path: %s\n' "$runtime_output" >&2; exit 1 ;;
    esac
    rm -rf -- "$work_root"
    mkdir -p -- "$cli_temp" "$gui_temp"
    backup=""
    moved_existing=false
    trap cleanup_publish EXIT

    dotnet publish "$repository_root/src/LemonLoader.Patcher.CLI/LemonLoader.Patcher.CLI.csproj" \
        --configuration "$configuration" \
        --runtime "$runtime" \
        --output "$cli_temp" \
        --no-self-contained \
        -p:PublishSingleFile=true \
        -p:DebugType=None \
        -p:DebugSymbols=false
    dotnet publish "$repository_root/src/LemonLoader.Patcher.GUI/LemonLoader.Patcher.GUI.csproj" \
        --configuration "$configuration" \
        --runtime "$runtime" \
        --output "$gui_temp" \
        --no-self-contained \
        -p:PublishSingleFile=false \
        -p:DebugType=None \
        -p:DebugSymbols=false

    if [[ -f "$lemon_release" ]]; then
        cp -f -- "$lemon_release" "$runtime_staging/LemonLoader-Android-arm64.zip"
    else
        printf 'Warning: LemonLoader Release was not found at %s. The published Patcher will require a local or downloadable Release.\n' \
            "$lemon_release" >&2
    fi

    cli_executable="$cli_temp/LemonLoader.Patcher.CLI"
    gui_executable="$gui_temp/LemonLoader.Patcher.GUI"
    if [[ "$runtime" == "win-x64" ]]; then
        cli_executable+=".exe"
        gui_executable+=".exe"
    fi
    if [[ ! -f "$cli_executable" || ! -f "$gui_executable" ]]; then
        printf 'Published CLI or GUI executable is missing for %s.\n' "$runtime" >&2
        exit 1
    fi
    if find "$runtime_staging" -type d \( -name .cpp2il -o -name Il2CppAssemblies -o -name .tools \) -print -quit | grep -q .; then
        printf 'Published output contains generated or game-specific directories.\n' >&2
        exit 1
    fi

    backup="$release_root/.$runtime.backup-$$"
    if [[ -e "$backup" ]]; then
        printf 'Refusing to reuse publish backup path: %s\n' "$backup" >&2
        exit 1
    fi
    if [[ -e "$runtime_output" ]]; then
        mv -- "$runtime_output" "$backup"
        moved_existing=true
    fi
    if mv -- "$runtime_staging" "$runtime_output"; then
        if [[ "$moved_existing" == true ]]; then
            rm -rf -- "$backup"
        fi
    else
        rm -rf -- "$runtime_output"
        if [[ "$moved_existing" == true ]]; then
            mv -- "$backup" "$runtime_output"
        fi
        printf 'Could not publish %s; the previous output was restored.\n' "$runtime_output" >&2
        exit 1
    fi
    rm -rf -- "$work_root"
    trap - EXIT

    printf 'Published LemonLoader.Patcher for %s to:\n  %s\n' \
        "$runtime" "$runtime_output"
done
