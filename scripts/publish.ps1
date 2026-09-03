[CmdletBinding()]
param(
    [ValidateSet("win-x64", "linux-x64")]
    [string[]]$Runtime = @("win-x64", "linux-x64"),

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Il2CppInteropSourceRoot,

    [switch]$AllowDirtySource
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$releaseRoot = Join-Path $repositoryRoot "Output\Releases"
$publishTempRoot = Join-Path $repositoryRoot "Output\PublishTemp"
$buildPropertiesPath = Join-Path $repositoryRoot "Directory.Build.props"
[xml]$buildProperties = Get-Content -LiteralPath $buildPropertiesPath -Raw
$expectedIl2CppInteropRevision = [string](
    $buildProperties.Project.PropertyGroup.BundledIl2CppInteropRevision)
if ($expectedIl2CppInteropRevision -notmatch '^[0-9a-f]{40}$') {
    throw "Directory.Build.props does not define a valid bundled Il2CppInterop revision."
}
if ([string]::IsNullOrWhiteSpace($Il2CppInteropSourceRoot)) {
    $Il2CppInteropSourceRoot = Join-Path $repositoryRoot "..\dependencies\Il2CppInterop"
}
$Il2CppInteropSourceRoot = [System.IO.Path]::GetFullPath($Il2CppInteropSourceRoot)
$interopProject = Join-Path $Il2CppInteropSourceRoot "Il2CppInterop.CLI\Il2CppInterop.CLI.csproj"
$interopLicense = Join-Path $Il2CppInteropSourceRoot "LICENSE"
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

function Assert-NoRunningPublishedProcess {
    param([Parameter(Mandatory)][string]$Path)

    if (-not $IsWindows -or -not (Test-Path -LiteralPath $Path)) {
        return
    }
    $prefix = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $running = @(Get-CimInstance Win32_Process |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
        } |
        Select-Object ProcessId, Name, ExecutablePath)
    if ($running.Count -ne 0) {
        $details = $running |
            ForEach-Object { "PID $($_.ProcessId) $($_.Name): $($_.ExecutablePath)" }
        throw "Close the published Patcher process before replacing '$Path':`n$($details -join [Environment]::NewLine)"
    }
}

$patcherChanges = @(& git -C $repositoryRoot status --short)
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect the Patcher source state."
}
if ($patcherChanges.Count -ne 0 -and -not $AllowDirtySource) {
    throw "The Patcher source is dirty. Commit the reviewed release source before publishing."
}
if ($patcherChanges.Count -ne 0) {
    Write-Warning "Publishing dirty Patcher source for local validation only; do not upload this output."
}

if (-not (Test-Path -LiteralPath $interopProject -PathType Leaf)) {
    throw "Il2CppInterop CLI source was not found at '$interopProject'."
}
if (-not (Test-Path -LiteralPath $interopLicense -PathType Leaf)) {
    throw "Il2CppInterop license was not found at '$interopLicense'."
}
$revisionOutput = @(& git -C $Il2CppInteropSourceRoot rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $revisionOutput.Count -ne 1) {
    throw "Could not read the Il2CppInterop source revision from '$Il2CppInteropSourceRoot'."
}
$actualIl2CppInteropRevision = $revisionOutput[0].Trim()
if ($actualIl2CppInteropRevision -ne $expectedIl2CppInteropRevision) {
    throw "Il2CppInterop source revision '$actualIl2CppInteropRevision' does not match pinned revision '$expectedIl2CppInteropRevision'."
}
$interopSourcePaths = @(
    "Directory.Build.props",
    "Il2CppInterop.CLI",
    "Il2CppInterop.Common",
    "Il2CppInterop.Generator",
    "Il2CppInterop.StructGenerator"
)
$statusArguments = @("-C", $Il2CppInteropSourceRoot, "status", "--short", "--") + $interopSourcePaths
$interopSourceChanges = @(& git @statusArguments)
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect the Il2CppInterop generator source state."
}
if ($interopSourceChanges.Count -ne 0) {
    throw "The pinned Il2CppInterop generator source is dirty:`n$($interopSourceChanges -join [Environment]::NewLine)"
}

$toolWorkRoot = Join-Path $publishTempRoot "Il2CppInterop"
$toolOutput = Join-Path $toolWorkRoot "publish"
Assert-ChildPath -Path $toolWorkRoot -Parent $publishTempRoot
if (Test-Path -LiteralPath $toolWorkRoot) {
    Remove-Item -LiteralPath $toolWorkRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $toolOutput | Out-Null

try {
    dotnet publish $interopProject `
        --configuration Release `
        --output $toolOutput `
        --no-self-contained `
        -p:GeneratePackageOnBuild=false `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing the pinned Il2CppInterop generator failed with exit code $LASTEXITCODE."
    }
    foreach ($requiredToolFile in @(
        "Il2CppInterop.CLI.dll",
        "Il2CppInterop.CLI.deps.json",
        "Il2CppInterop.CLI.runtimeconfig.json"
    )) {
        $requiredToolPath = Join-Path $toolOutput $requiredToolFile
        if (-not (Test-Path -LiteralPath $requiredToolPath -PathType Leaf)) {
            throw "Published Il2CppInterop generator is missing '$requiredToolFile'."
        }
    }
    Copy-Item -LiteralPath $interopLicense -Destination (Join-Path $toolOutput "LICENSE")
    [ordered]@{
        formatVersion = 1
        revision = $actualIl2CppInteropRevision
        upstream = "https://github.com/BepInEx/Il2CppInterop.git"
        upstreamCommit = "f03c8f4ae507d47ea814f3d11d1ec6b0391c1576"
    } | ConvertTo-Json | Set-Content `
        -LiteralPath (Join-Path $toolOutput "lemonloader-il2cppinterop.json") `
        -Encoding utf8

    foreach ($runtimeIdentifier in $Runtime) {
        $runtimeOutput = Join-Path $releaseRoot $runtimeIdentifier
        $runtimeWorkRoot = Join-Path $publishTempRoot $runtimeIdentifier
        $runtimeStaging = Join-Path $runtimeWorkRoot "release"
        $backupPattern = ".$runtimeIdentifier.backup-*"
        Assert-ChildPath -Path $runtimeWorkRoot -Parent $publishTempRoot
        Assert-ChildPath -Path $runtimeOutput -Parent $releaseRoot
        Assert-NoRunningPublishedProcess -Path $runtimeOutput
        $existingBackups = @(Get-ChildItem `
            -LiteralPath $releaseRoot `
            -Directory `
            -Filter $backupPattern `
            -Force `
            -ErrorAction SilentlyContinue)
        foreach ($existingBackup in $existingBackups) {
            Assert-NoRunningPublishedProcess -Path $existingBackup.FullName
        }
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

            $bundledToolDirectory = Join-Path $runtimeStaging "Tools\Il2CppInterop"
            New-Item -ItemType Directory -Force -Path $bundledToolDirectory | Out-Null
            Get-ChildItem -LiteralPath $toolOutput -Force |
                Copy-Item -Destination $bundledToolDirectory -Recurse -Force
            foreach ($legalFile in @("LICENSE", "NOTICE")) {
                $legalPath = Join-Path $repositoryRoot $legalFile
                if (-not (Test-Path -LiteralPath $legalPath -PathType Leaf)) {
                    throw "Patcher legal file '$legalFile' is missing."
                }
                Copy-Item -LiteralPath $legalPath -Destination $runtimeStaging -Force
            }

            $cliExecutable = Join-Path $runtimeStaging `
                "CLI\LemonLoader.Patcher.CLI$(if ($runtimeIdentifier -eq 'win-x64') { '.exe' })"
            $guiExecutable = Join-Path $runtimeStaging `
                "GUI\LemonLoader.Patcher.GUI$(if ($runtimeIdentifier -eq 'win-x64') { '.exe' })"
            if (-not (Test-Path -LiteralPath $cliExecutable -PathType Leaf) -or
                -not (Test-Path -LiteralPath $guiExecutable -PathType Leaf)) {
                throw "Published CLI or GUI executable is missing for $runtimeIdentifier."
            }
            if (-not (Test-Path -LiteralPath `
                    (Join-Path $bundledToolDirectory "Il2CppInterop.CLI.dll") `
                    -PathType Leaf)) {
                throw "Published Patcher is missing the fixed Il2CppInterop generator."
            }
            $bundledLemonRelease = @(Get-ChildItem `
                -LiteralPath $runtimeStaging `
                -Filter "LemonLoader-Android-arm64.zip" `
                -File `
                -Recurse `
                -Force)
            if ($bundledLemonRelease.Count -ne 0) {
                throw "Published Patcher must not contain a LemonLoader Release archive."
            }
            $forbiddenDirectories = @(Get-ChildItem -LiteralPath $runtimeStaging -Directory -Recurse -Force |
                Where-Object Name -in @(".cpp2il", "Il2CppAssemblies", ".tools"))
            if ($forbiddenDirectories.Count -ne 0) {
                throw "Published output contains generated or game-specific directories: $($forbiddenDirectories.FullName -join ', ')."
            }

            $backup = Join-Path $releaseRoot ".$runtimeIdentifier.backup-$([Guid]::NewGuid().ToString('N'))"
            $movedExisting = $false
            $publishedNew = $false
            try {
                if (Test-Path -LiteralPath $runtimeOutput) {
                    Move-Item -LiteralPath $runtimeOutput -Destination $backup
                    $movedExisting = $true
                }
                Move-Item -LiteralPath $runtimeStaging -Destination $runtimeOutput
                $publishedNew = $true
                if ($movedExisting) {
                    Remove-Item -LiteralPath $backup -Recurse -Force
                }
            }
            catch {
                $publishError = $_.Exception.Message
                $rollbackErrors = [System.Collections.Generic.List[string]]::new()
                if ($publishedNew -and (Test-Path -LiteralPath $runtimeOutput)) {
                    try {
                        Remove-Item -LiteralPath $runtimeOutput -Recurse -Force
                    }
                    catch {
                        $rollbackErrors.Add("could not remove new output: $($_.Exception.Message)")
                    }
                }
                if ($movedExisting -and (Test-Path -LiteralPath $backup)) {
                    if (Test-Path -LiteralPath $runtimeOutput) {
                        $rollbackErrors.Add("could not restore the previous output because the new output still exists")
                    }
                    else {
                        try {
                            Move-Item -LiteralPath $backup -Destination $runtimeOutput
                        }
                        catch {
                            $rollbackErrors.Add("could not restore previous output: $($_.Exception.Message)")
                        }
                    }
                }
                $rollbackMessage = if ($rollbackErrors.Count -eq 0) {
                    "The previous output was restored."
                }
                else {
                    "Rollback was incomplete: $($rollbackErrors -join '; ')."
                }
                throw "Could not publish '$runtimeOutput'. $rollbackMessage $publishError"
            }
            $staleBackups = @(Get-ChildItem `
                -LiteralPath $releaseRoot `
                -Directory `
                -Filter $backupPattern `
                -Force)
            foreach ($staleBackup in $staleBackups) {
                Assert-ChildPath -Path $staleBackup.FullName -Parent $releaseRoot
                Remove-Item -LiteralPath $staleBackup.FullName -Recurse -Force
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
}
finally {
    if (Test-Path -LiteralPath $toolWorkRoot) {
        Remove-Item -LiteralPath $toolWorkRoot -Recurse -Force
    }
}
