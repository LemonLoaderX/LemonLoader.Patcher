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
            ForEach-Object { $_.FullName.Replace('\\', '/') }
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
            & tar -C $runtimeRoot -czf $asset .
            if ($LASTEXITCODE -ne 0) {
                throw "Creating the Linux release archive failed with exit code $LASTEXITCODE."
            }
            $entries = @(& tar -tzf $asset) |
                ForEach-Object { $_.TrimStart('.', '/').Replace('\\', '/') } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            if ($LASTEXITCODE -ne 0) {
                throw "Reading the Linux release archive failed with exit code $LASTEXITCODE."
            }
            foreach ($required in Get-RequiredEntries $runtimeIdentifier) {
                if ($required -cnotin $entries) {
                    throw "Archive '$asset' is missing '$required'."
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
    Set-Content -LiteralPath $checksumPath -Value $checksumLines -Encoding Ascii
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
