[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ApkPath,

    [string[]]$ExpectedMod = @(),

    [string[]]$ExpectedDeployment = @()
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$apk = [IO.Path]::GetFullPath($ApkPath)
if (-not (Test-Path -LiteralPath $apk -PathType Leaf)) {
    throw "APK was not found at '$apk'."
}
$expectedMods = @($ExpectedMod | ForEach-Object { $_ -split ',' } | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_)
})
$expectedDeploymentFiles = @(
    $ExpectedDeployment |
        ForEach-Object { $_ -split ',' } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim().Replace('\', '/') }
    $expectedMods | ForEach-Object { "Mods/$([IO.Path]::GetFileName($_))" }
)
foreach ($relativePath in $expectedDeploymentFiles) {
    if ($relativePath.StartsWith('/', [StringComparison]::Ordinal) -or
        $relativePath.Split('/') -contains '..') {
        throw "Expected deployment path must be relative to the deployment root: '$relativePath'."
    }
}

function Get-EntrySha256 {
    param([Parameter(Mandatory)] [IO.Compression.ZipArchiveEntry]$Entry)

    $input = $Entry.Open()
    try {
        return [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($input)).ToLowerInvariant()
    }
    finally {
        $input.Dispose()
    }
}

function Get-PayloadTreeHash {
    param(
        [Parameter(Mandatory)] [IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory)] [string]$Scope
    )

    $prefix = "assets/LemonLoader/$Scope/"
    [string[]]$lines = @(
        foreach ($entry in $Archive.Entries |
            Where-Object {
                -not [string]::IsNullOrEmpty($_.Name) -and
                $_.FullName.StartsWith($prefix, [StringComparison]::Ordinal)
            }) {
            $relativePath = $entry.FullName.Substring("assets/LemonLoader/".Length)
            "$relativePath|$($entry.Length)|$(Get-EntrySha256 -Entry $entry)"
        }
    )
    [Array]::Sort($lines, [StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(
        ((@("layout-version=4", "scope=$Scope") + $lines) -join "`n"))
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

$archive = [IO.Compression.ZipFile]::OpenRead($apk)
try {
    $duplicate = $archive.Entries |
        Group-Object FullName -CaseSensitive |
        Where-Object Count -ne 1 |
        Select-Object -First 1
    if ($null -ne $duplicate) {
        throw "APK contains duplicate ZIP entry '$($duplicate.Name)'."
    }

    $legacy = $archive.Entries | Where-Object {
        $_.FullName -ceq "assets/lemonloader_asset_hash.txt" -or
        $_.FullName.StartsWith("assets/dotnet/", [StringComparison]::Ordinal) -or
        $_.FullName.StartsWith("assets/MelonLoader/", [StringComparison]::Ordinal) -or
        $_.FullName.StartsWith("assets/LemonLoader/Mods/", [StringComparison]::Ordinal)
    } | Select-Object -First 1
    if ($null -ne $legacy) {
        throw "APK contains legacy LemonLoader entry '$($legacy.FullName)'."
    }

    foreach ($required in @(
        "lib/arm64-v8a/libmain.so",
        "assets/LemonLoader/payload.json",
        "assets/LemonLoader/runtime/dotnet/native/openssl/lemcrypto.so",
        "assets/LemonLoader/runtime/dotnet/native/openssl/lemssl.so",
        "assets/LemonLoader/runtime/loader/Il2CppAssemblies/interop-manifest.json")) {
        if ($null -eq $archive.GetEntry($required)) {
            throw "APK is missing required LemonLoader entry '$required'."
        }
    }
    foreach ($reserved in @(
        "lib/arm64-v8a/lemcrypto.so",
        "lib/arm64-v8a/lemssl.so")) {
        if ($null -ne $archive.GetEntry($reserved)) {
            throw "Private .NET dependency '$reserved' leaked into the public native namespace."
        }
    }

    $payloadEntry = $archive.GetEntry("assets/LemonLoader/payload.json")
    $reader = [IO.StreamReader]::new($payloadEntry.Open(), [Text.Encoding]::UTF8)
    try {
        $payload = $reader.ReadToEnd() | ConvertFrom-Json
    }
    finally {
        $reader.Dispose()
    }
    $runtimeHash = Get-PayloadTreeHash -Archive $archive -Scope "runtime"
    $deploymentHash = Get-PayloadTreeHash -Archive $archive -Scope "deployment"
    if ($payload.formatVersion -ne 4) {
        throw "APK payload.json has unsupported format '$($payload.formatVersion)'."
    }
    if ($payload.runtimeSha256 -cne $runtimeHash) {
        throw "APK runtime hash is '$($payload.runtimeSha256)', expected '$runtimeHash'."
    }
    if ($payload.deploymentSha256 -cne $deploymentHash) {
        throw "APK deployment hash is '$($payload.deploymentSha256)', expected '$deploymentHash'."
    }

    foreach ($relativePath in $expectedDeploymentFiles) {
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            $null -eq $archive.GetEntry("assets/LemonLoader/deployment/$relativePath")) {
            throw "Expected packaged deployment file '$relativePath' was not found."
        }
    }

    $interopCount = @($archive.Entries | Where-Object {
        $_.Name.EndsWith(".dll", [StringComparison]::OrdinalIgnoreCase) -and
        $_.FullName.StartsWith(
            "assets/LemonLoader/runtime/loader/Il2CppAssemblies/",
            [StringComparison]::Ordinal)
    }).Count
    if ($interopCount -eq 0) {
        throw "APK contains no generated Interop assemblies."
    }

    Write-Host "Verified LemonLoader APK layout v4:"
    Write-Host "  $apk"
    Write-Host "  Interop assemblies: $interopCount"
    Write-Host "  Expected deployment files: $($expectedDeploymentFiles.Count)"
    Write-Host "  Runtime SHA-256: $runtimeHash"
    Write-Host "  Deployment SHA-256: $deploymentHash"
}
finally {
    $archive.Dispose()
}
