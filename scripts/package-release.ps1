[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:[-.][A-Za-z0-9.-]+)?$')]
    [string]$Version,

    [ValidateSet("win-x64", "linux-x64")]
    [string[]]$Runtime = @("win-x64", "linux-x64")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Formats.Tar

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$releaseRoot = Join-Path $repositoryRoot "Output\Releases"
$packageBase = Join-Path $repositoryRoot "Output\Packages"
$packageRoot = Join-Path $packageBase $Version

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify '$fullPath' because it is outside '$fullParent'."
    }
}

function Get-RequiredEntries([string]$RuntimeIdentifier) {
    $suffix = if ($RuntimeIdentifier -eq "win-x64") { ".exe" } else { "" }
    return @(
        "LICENSE",
        "NOTICE",
        "CLI/LemonLoader.Patcher.CLI$suffix",
        "GUI/LemonLoader.Patcher.GUI$suffix",
        "Tools/Il2CppInterop/Il2CppInterop.CLI.dll",
        "Tools/Il2CppInterop/lemonloader-il2cppinterop.json"
    )
}

function Assert-PublishTree([string]$Path, [string]$RuntimeIdentifier) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Published runtime '$RuntimeIdentifier' was not found at '$Path'."
    }
    foreach ($relativePath in Get-RequiredEntries $RuntimeIdentifier) {
        $required = Join-Path $Path ($relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Published runtime '$RuntimeIdentifier' is missing '$relativePath'."
        }
    }
    $bundledRelease = Get-ChildItem -LiteralPath $Path -Recurse -File -Force |
        Where-Object Name -eq "LemonLoader-Android-arm64.zip" |
        Select-Object -First 1
    if ($null -ne $bundledRelease) {
        throw "Patcher release must not contain '$($bundledRelease.FullName)'."
    }
    $forbiddenDirectory = Get-ChildItem -LiteralPath $Path -Recurse -Directory -Force |
        Where-Object Name -in @(".cpp2il", ".tools", "Il2CppAssemblies") |
        Select-Object -First 1
    if ($null -ne $forbiddenDirectory) {
        throw "Patcher release contains generated directory '$($forbiddenDirectory.FullName)'."
    }
}

function Assert-Zip([string]$Path, [string]$RuntimeIdentifier) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = $archive.Entries |
            Where-Object { -not [string]::IsNullOrEmpty($_.Name) } |
            ForEach-Object { $_.FullName.Replace('\', '/') }
        foreach ($required in Get-RequiredEntries $RuntimeIdentifier) {
            if ($required -cnotin $entries) {
                throw "Archive '$Path' is missing '$required'."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-LinuxArchive([string]$SourceRoot, [string]$Destination) {
    $regularMode = [System.IO.UnixFileMode](
        [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite -bor
        [System.IO.UnixFileMode]::GroupRead -bor [System.IO.UnixFileMode]::OtherRead)
    $executableMode = [System.IO.UnixFileMode](
        $regularMode -bor [System.IO.UnixFileMode]::UserExecute -bor
        [System.IO.UnixFileMode]::GroupExecute -bor [System.IO.UnixFileMode]::OtherExecute)
    $executables = [Collections.Generic.HashSet[string]]::new(
        [string[]]@(
            "CLI/LemonLoader.Patcher.CLI",
            "GUI/LemonLoader.Patcher.GUI"),
        [StringComparer]::Ordinal)
    $timestamp = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

    $fileOutput = [IO.File]::Create($Destination)
    try {
        $gzip = [IO.Compression.GZipStream]::new(
            $fileOutput,
            [IO.Compression.CompressionLevel]::Optimal,
            $true)
        try {
            $writer = [System.Formats.Tar.TarWriter]::new(
                $gzip,
                [System.Formats.Tar.TarEntryFormat]::Pax,
                $true)
            try {
                foreach ($path in Get-ChildItem -LiteralPath $SourceRoot -Recurse -File -Force |
                             Sort-Object {
                                 [IO.Path]::GetRelativePath($SourceRoot, $_.FullName).Replace('\', '/')
                             }) {
                    $relativePath = [IO.Path]::GetRelativePath(
                        $SourceRoot,
                        $path.FullName).Replace('\', '/')
                    $entry = [System.Formats.Tar.PaxTarEntry]::new(
                        [System.Formats.Tar.TarEntryType]::RegularFile,
                        $relativePath)
                    $entry.Mode = if ($executables.Contains($relativePath)) {
                        $executableMode
                    }
                    else {
                        $regularMode
                    }
                    $entry.ModificationTime = $timestamp
                    $entry.Uid = 0
                    $entry.Gid = 0
                    $entry.UserName = "root"
                    $entry.GroupName = "root"
                    $input = [IO.File]::OpenRead($path.FullName)
                    try {
                        $entry.DataStream = $input
                        $writer.WriteEntry($entry)
                    }
                    finally {
                        $input.Dispose()
                    }
                }
            }
            finally {
                $writer.Dispose()
            }
        }
        finally {
            $gzip.Dispose()
        }
    }
    finally {
        $fileOutput.Dispose()
    }
}

function Read-LinuxArchive([string]$Path) {
    $fileInput = [IO.File]::OpenRead($Path)
    try {
        $gzip = [IO.Compression.GZipStream]::new(
            $fileInput,
            [IO.Compression.CompressionMode]::Decompress,
            $true)
        try {
            $reader = [System.Formats.Tar.TarReader]::new($gzip, $true)
            try {
                while ($null -ne ($entry = $reader.GetNextEntry($false))) {
                    [pscustomobject]@{
                        Name = $entry.Name.Replace('\', '/')
                        Mode = $entry.Mode
                    }
                }
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $gzip.Dispose()
        }
    }
    finally {
        $fileInput.Dispose()
    }
}

Assert-ChildPath -Path $packageRoot -Parent $packageBase
if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null

$assets = [Collections.Generic.List[string]]::new()
try {
    foreach ($runtimeIdentifier in $Runtime) {
        $runtimeRoot = Join-Path $releaseRoot $runtimeIdentifier
        Assert-PublishTree -Path $runtimeRoot -RuntimeIdentifier $runtimeIdentifier

        if ($runtimeIdentifier -eq "win-x64") {
            $asset = Join-Path $packageRoot "LemonLoader.Patcher-win-x64.zip"
            Compress-Archive -Path (Join-Path $runtimeRoot "*") `
                -DestinationPath $asset `
                -CompressionLevel Optimal
            Assert-Zip -Path $asset -RuntimeIdentifier $runtimeIdentifier
        }
        else {
            $asset = Join-Path $packageRoot "LemonLoader.Patcher-linux-x64.tar.gz"
            New-LinuxArchive -SourceRoot $runtimeRoot -Destination $asset
            $archiveEntries = @(Read-LinuxArchive -Path $asset)
            $entries = @($archiveEntries | ForEach-Object Name)
            foreach ($required in Get-RequiredEntries $runtimeIdentifier) {
                if ($required -cnotin $entries) {
                    throw "Archive '$asset' is missing '$required'."
                }
            }
            foreach ($executable in @(
                "CLI/LemonLoader.Patcher.CLI",
                "GUI/LemonLoader.Patcher.GUI")) {
                $entry = $archiveEntries | Where-Object Name -CEQ $executable
                if ($null -eq $entry -or
                    ($entry.Mode -band [System.IO.UnixFileMode]::UserExecute) -eq 0) {
                    throw "Archive '$asset' does not mark '$executable' executable."
                }
            }
        }
        $assets.Add($asset)
    }

    $checksumPath = Join-Path $packageRoot "SHA256SUMS.txt"
    $checksumLines = $assets |
        Sort-Object { [IO.Path]::GetFileName($_) } |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $([IO.Path]::GetFileName($_))"
        }
    [IO.File]::WriteAllText(
        $checksumPath,
        ($checksumLines -join "`n") + "`n",
        [Text.Encoding]::ASCII)
    $assets.Add($checksumPath)

    Write-Host "Packaged LemonLoader.Patcher $Version release assets:"
    foreach ($asset in $assets) {
        Write-Host "  $asset"
    }
}
catch {
    if (Test-Path -LiteralPath $packageRoot) {
        Remove-Item -LiteralPath $packageRoot -Recurse -Force
    }
    throw
}
