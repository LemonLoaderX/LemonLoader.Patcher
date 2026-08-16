[CmdletBinding()]
param(
    [ValidateSet("win-x64", "linux-x64")]
    [string[]]$Runtime = @("win-x64", "linux-x64"),

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$LemonRelease
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$releaseRoot = Join-Path $repositoryRoot "Output\Releases"
if ([string]::IsNullOrWhiteSpace($LemonRelease)) {
    $LemonRelease = Join-Path $repositoryRoot "..\LemonLoader\Output\Releases\LemonLoader-Android-arm64.zip"
}
$LemonRelease = [System.IO.Path]::GetFullPath($LemonRelease)
$projects = @(
    [pscustomobject]@{
        Name = "CLI"
        Path = Join-Path $repositoryRoot "src\LemonLoader.Patcher.CLI\LemonLoader.Patcher.CLI.csproj"
        SingleFile = $true
    },
    [pscustomobject]@{
        Name = "GUI"
        Path = Join-Path $repositoryRoot "src\LemonLoader.Patcher.GUI\LemonLoader.Patcher.GUI.csproj"
        SingleFile = $false
    }
)

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify '$fullPath' because it is outside '$fullParent'."
    }
}

foreach ($runtimeIdentifier in $Runtime) {
    $runtimeOutput = Join-Path $releaseRoot $runtimeIdentifier
    $runtimeWorkRoot = Join-Path $repositoryRoot "Output\PublishTemp\$runtimeIdentifier"
    $runtimeStaging = Join-Path $runtimeWorkRoot "release"
    Assert-ChildPath -Path $runtimeWorkRoot -Parent (Join-Path $repositoryRoot "Output\PublishTemp")
    Assert-ChildPath -Path $runtimeOutput -Parent $releaseRoot
    if (Test-Path -LiteralPath $runtimeWorkRoot) {
        Remove-Item -LiteralPath $runtimeWorkRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $runtimeStaging | Out-Null

    try {
        foreach ($project in $projects) {
            $publishOutput = Join-Path $runtimeWorkRoot "build\$($project.Name)"
            dotnet publish $project.Path `
                --configuration $Configuration `
                --runtime $runtimeIdentifier `
                --output $publishOutput `
                --no-self-contained `
                -p:PublishSingleFile=$($project.SingleFile.ToString().ToLowerInvariant()) `
                -p:DebugType=None `
                -p:DebugSymbols=false
            if ($LASTEXITCODE -ne 0) {
                throw "Publishing $($project.Name) for $runtimeIdentifier failed with exit code $LASTEXITCODE."
            }
            Move-Item -LiteralPath $publishOutput `
                -Destination (Join-Path $runtimeStaging $project.Name)
        }

        if (Test-Path -LiteralPath $LemonRelease -PathType Leaf) {
            Copy-Item -LiteralPath $LemonRelease `
                -Destination (Join-Path $runtimeStaging "LemonLoader-Android-arm64.zip") `
                -Force
        }
        else {
            Write-Warning "LemonLoader Release was not found at '$LemonRelease'. The published Patcher will require a local or downloadable Release."
        }

    $cliExecutable = Join-Path $runtimeStaging `
        "CLI\LemonLoader.Patcher.CLI$(if ($runtimeIdentifier -eq 'win-x64') { '.exe' })"
    $guiExecutable = Join-Path $runtimeStaging `
        "GUI\LemonLoader.Patcher.GUI$(if ($runtimeIdentifier -eq 'win-x64') { '.exe' })"
    if (-not (Test-Path -LiteralPath $cliExecutable -PathType Leaf) -or
        -not (Test-Path -LiteralPath $guiExecutable -PathType Leaf)) {
        throw "Published CLI or GUI executable is missing for $runtimeIdentifier."
    }
    $forbiddenDirectories = @(Get-ChildItem -LiteralPath $runtimeStaging -Directory -Recurse -Force |
        Where-Object Name -in @(".cpp2il", "Il2CppAssemblies", ".tools"))
    if ($forbiddenDirectories.Count -ne 0) {
        throw "Published output contains generated or game-specific directories: $($forbiddenDirectories.FullName -join ', ')."
    }

    $backup = Join-Path $releaseRoot ".$runtimeIdentifier.backup-$([Guid]::NewGuid().ToString('N'))"
    $movedExisting = $false
    try {
        if (Test-Path -LiteralPath $runtimeOutput) {
            Move-Item -LiteralPath $runtimeOutput -Destination $backup
            $movedExisting = $true
        }
        Move-Item -LiteralPath $runtimeStaging -Destination $runtimeOutput
        if ($movedExisting) {
            Remove-Item -LiteralPath $backup -Recurse -Force
        }
    }
    catch {
        if (Test-Path -LiteralPath $runtimeOutput) {
            Remove-Item -LiteralPath $runtimeOutput -Recurse -Force
        }
        if ($movedExisting -and (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $runtimeOutput
        }
        throw "Could not publish '$runtimeOutput'; the previous output was restored. $($_.Exception.Message)"
    }
        Write-Host "Published LemonLoader.Patcher for $runtimeIdentifier to:"
        Write-Host "  $runtimeOutput"
    }
    finally {
        if (Test-Path -LiteralPath $runtimeWorkRoot) {
            Remove-Item -LiteralPath $runtimeWorkRoot -Recurse -Force
        }
    }
}
