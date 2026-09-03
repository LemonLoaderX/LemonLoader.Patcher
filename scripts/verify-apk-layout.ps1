[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ApkPath,

    [string[]]$ExpectedDeployment = @(),

    [ValidateSet("development", "production", "locked")]
    [string]$ExpectedDeploymentProfile,

    [ValidateSet("monovm-sgen", "coreclr")]
    [string]$ExpectedManagedRuntimeBackend,

    [string[]]$ExpectedDeploymentPolicy = @()
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$apk = [IO.Path]::GetFullPath($ApkPath)
if (-not (Test-Path -LiteralPath $apk -PathType Leaf)) {
    throw "APK was not found at '$apk'."
}
$expectedDeploymentFiles = @(
    $ExpectedDeployment |
        ForEach-Object { $_ -split ',' } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim().Replace('\', '/') }
)
$expectedPolicies = @{}
foreach ($rule in @(
    $ExpectedDeploymentPolicy |
        ForEach-Object { $_ -split ',' } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
    $separator = $rule.LastIndexOf('=')
    if ($separator -le 0 -or $separator -eq $rule.Length - 1) {
        throw "Expected deployment policy must use '<path>=<policy>': '$rule'."
    }
    $path = $rule.Substring(0, $separator).Trim().Replace('\', '/')
    $policy = $rule.Substring($separator + 1).Trim().ToLowerInvariant()
    if ($policy -notin @("seed", "upgrade", "refresh", "enforce")) {
        throw "Expected deployment policy '$policy' is invalid."
    }
    if ($expectedPolicies.ContainsKey($path)) {
        throw "Expected deployment policy path was supplied more than once: '$path'."
    }
    $expectedPolicies[$path] = $policy
}

function Assert-SafeDeploymentPath {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Description
    )

    $segments = $Path.Split('/')
    $topLevel = if ($segments.Count -eq 0) { "" } else { $segments[0] }
    if ([string]::IsNullOrWhiteSpace($Path) -or
        $Path.StartsWith('/', [StringComparison]::Ordinal) -or
        $Path.Contains('\') -or
        $Path.Contains('|') -or
        $segments -contains '' -or
        $segments -contains '.' -or
        $segments -contains '..' -or
        $topLevel -ceq 'MelonLoader' -or
        $topLevel -ceq '.packaged-deployment' -or
        $topLevel.StartsWith('.lemonloader-', [StringComparison]::Ordinal) -or
        $topLevel.StartsWith('.lemon-staging-', [StringComparison]::Ordinal)) {
        throw "$Description is not a safe deployment-relative path: '$Path'."
    }
}
foreach ($relativePath in $expectedDeploymentFiles) {
    Assert-SafeDeploymentPath -Path $relativePath -Description "Expected deployment path"
}
foreach ($relativePath in $expectedPolicies.Keys) {
    Assert-SafeDeploymentPath -Path $relativePath -Description "Expected policy path"
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
        [Parameter(Mandatory)] [string]$Scope,
        [Parameter(Mandatory)] [int]$LayoutVersion
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
        ((@("layout-version=$LayoutVersion", "scope=$Scope") + $lines) -join "`n"))
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-DeploymentRevision {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]]$Files)

    [string[]]$lines = @(
        $Files | ForEach-Object {
            "$($_.path)|$($_.size)|$($_.sha256)|$($_.policy)"
        }
    )
    [Array]::Sort($lines, [StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(
        ((@("deployment-revision=1") + $lines) -join "`n"))
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

    $forbiddenEntry = $archive.Entries | Where-Object {
        $_.FullName -ceq "assets/lemonloader_asset_hash.txt" -or
        $_.FullName.StartsWith("assets/dotnet/", [StringComparison]::Ordinal) -or
        $_.FullName.StartsWith("assets/MelonLoader/", [StringComparison]::Ordinal) -or
        $_.FullName.StartsWith("assets/LemonLoader/Mods/", [StringComparison]::Ordinal)
    } | Select-Object -First 1
    if ($null -ne $forbiddenEntry) {
        throw "APK contains a forbidden loader entry '$($forbiddenEntry.FullName)'."
    }

    foreach ($required in @(
        "lib/arm64-v8a/libmain.so",
        "assets/LemonLoader/payload.json",
        "assets/LemonLoader/runtime/dotnet/runtime-identity.json",
        "assets/LemonLoader/runtime/interop/interop-manifest.json")) {
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
    if ($payload.formatVersion -ne 8) {
        throw "APK payload.json has unsupported format '$($payload.formatVersion)'."
    }
    if ($payload.managedRuntimeBackend -notin @("monovm-sgen", "coreclr")) {
        throw "APK managed runtime backend '$($payload.managedRuntimeBackend)' is invalid."
    }
    $privateLibraries = @($payload.privateNativeLibraries)
    if (-not [string]::IsNullOrWhiteSpace($ExpectedManagedRuntimeBackend) -and
        $payload.managedRuntimeBackend -cne $ExpectedManagedRuntimeBackend) {
        throw "APK managed runtime backend is '$($payload.managedRuntimeBackend)', expected '$ExpectedManagedRuntimeBackend'."
    }
    $runtimeIdentityEntry = $archive.GetEntry(
        "assets/LemonLoader/runtime/dotnet/runtime-identity.json")
    $actualRuntimeIdentityHash = Get-EntrySha256 -Entry $runtimeIdentityEntry
    if ($payload.managedRuntimeIdentitySha256 -cne $actualRuntimeIdentityHash) {
        throw "APK managed runtime identity hash is '$($payload.managedRuntimeIdentitySha256)', expected '$actualRuntimeIdentityHash'."
    }
    $runtimeIdentityReader = [IO.StreamReader]::new(
        $runtimeIdentityEntry.Open(),
        [Text.Encoding]::UTF8)
    try {
        $runtimeIdentity = $runtimeIdentityReader.ReadToEnd() | ConvertFrom-Json
    }
    finally {
        $runtimeIdentityReader.Dispose()
    }
    $expectedHostingModel = if ($payload.managedRuntimeBackend -ceq "coreclr") {
        "coreclr-host-api"
    }
    else {
        "hostfxr"
    }
    if ($runtimeIdentity.formatVersion -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$runtimeIdentity.runtimeVersion) -or
        $runtimeIdentity.backend -cne $payload.managedRuntimeBackend -or
        $runtimeIdentity.hostingModel -cne $expectedHostingModel -or
        $runtimeIdentity.engineFile -cne "libcoreclr.so" -or
        ([string]$runtimeIdentity.engineSha256) -notmatch '^[0-9a-fA-F]{64}$') {
        throw "APK managed runtime identity is invalid or inconsistent with payload.json."
    }
    $runtimeEngineEntry = $archive.GetEntry(
        "assets/LemonLoader/runtime/dotnet/shared/Microsoft.NETCore.App/" +
        "$($runtimeIdentity.runtimeVersion)/libcoreclr.so")
    if ($null -eq $runtimeEngineEntry) {
        throw "APK managed runtime engine declared by identity is missing."
    }
    $actualRuntimeEngineHash = Get-EntrySha256 -Entry $runtimeEngineEntry
    if ($runtimeIdentity.engineSha256 -cne $actualRuntimeEngineHash) {
        throw "APK managed runtime engine hash is '$($runtimeIdentity.engineSha256)', expected '$actualRuntimeEngineHash'."
    }
    $sharedRuntimeRoot = "assets/LemonLoader/runtime/dotnet/shared/Microsoft.NETCore.App/" +
        "$($runtimeIdentity.runtimeVersion)"
    $androidCryptoEntry = $archive.GetEntry(
        "$sharedRuntimeRoot/libSystem.Security.Cryptography.Native.Android.so")
    $coreClrCryptoDexHash = [string]$payload.coreClrCryptoDexSha256
    if ($payload.managedRuntimeBackend -ceq "coreclr") {
        if ($null -eq $androidCryptoEntry -or
            $coreClrCryptoDexHash -notmatch '^[0-9a-f]{64}$') {
            throw "APK CoreCLR payload is missing its Android crypto library or helper dex hash."
        }
        if ("lemcrypto.so" -cin $privateLibraries -or
            "lemssl.so" -cin $privateLibraries) {
            throw "APK CoreCLR payload still declares private OpenSSL dependencies."
        }
        $promotedDexEntries = @($archive.Entries | Where-Object {
            $_.FullName -match '^classes(?:(?:[2-9][0-9]*|1[0-9]+))?\.dex$' -and
            (Get-EntrySha256 -Entry $_) -ceq $coreClrCryptoDexHash
        })
        if ($promotedDexEntries.Count -ne 1 -or
            $promotedDexEntries[0].FullName -ceq "classes.dex") {
            throw "APK does not contain exactly one promoted Android CoreCLR crypto dex."
        }
    }
    else {
        if (-not [string]::IsNullOrEmpty($coreClrCryptoDexHash)) {
            throw "APK MonoVM/SGen payload contains CoreCLR crypto helper dex metadata."
        }
        $expectedPrivateLibraries = @("lemcrypto.so", "lemssl.so")
        if (@($expectedPrivateLibraries | Where-Object { $_ -cnotin $privateLibraries }).Count -ne 0) {
            throw "APK MonoVM/SGen payload does not declare its private OpenSSL compatibility pair."
        }
        foreach ($privateLibrary in $expectedPrivateLibraries) {
            $privateEntry = "assets/LemonLoader/runtime/dotnet/native/openssl/$privateLibrary"
            if ($null -eq $archive.GetEntry($privateEntry)) {
                throw "APK is missing private native dependency '$privateEntry'."
            }
        }
    }
    $loaderHash = Get-PayloadTreeHash -Archive $archive -Scope "runtime/loader" `
        -LayoutVersion $payload.formatVersion
    $dotnetHash = Get-PayloadTreeHash -Archive $archive -Scope "runtime/dotnet" `
        -LayoutVersion $payload.formatVersion
    $interopHash = Get-PayloadTreeHash -Archive $archive -Scope "runtime/interop" `
        -LayoutVersion $payload.formatVersion
    $deploymentHash = Get-PayloadTreeHash -Archive $archive -Scope "deployment" `
        -LayoutVersion $payload.formatVersion
    if ($payload.deploymentSha256 -cne $deploymentHash) {
        throw "APK deployment hash is '$($payload.deploymentSha256)', expected '$deploymentHash'."
    }
    foreach ($domain in @(
        @{ Name = "loader"; Property = "loaderSha256"; Hash = $loaderHash },
        @{ Name = "dotnet"; Property = "dotnetSha256"; Hash = $dotnetHash },
        @{ Name = "interop"; Property = "interopSha256"; Hash = $interopHash })) {
        if ($payload.($domain.Property) -cne $domain.Hash) {
            throw "APK $($domain.Name) hash is '$($payload.($domain.Property))', expected '$($domain.Hash)'."
        }
    }
    $unsupportedRuntimeEntry = $archive.Entries |
        Where-Object {
            -not [string]::IsNullOrEmpty($_.Name) -and
            $_.FullName.StartsWith("assets/LemonLoader/runtime/", [StringComparison]::Ordinal) -and
            -not ($_.FullName.StartsWith("assets/LemonLoader/runtime/loader/", [StringComparison]::Ordinal) -or
                $_.FullName.StartsWith("assets/LemonLoader/runtime/dotnet/", [StringComparison]::Ordinal) -or
                $_.FullName.StartsWith("assets/LemonLoader/runtime/interop/", [StringComparison]::Ordinal))
        } |
        Select-Object -First 1
    if ($null -ne $unsupportedRuntimeEntry) {
        throw "APK runtime entry '$($unsupportedRuntimeEntry.FullName)' is outside a supported update domain."
    }
    if ($payload.deploymentProfile -notin @("development", "production", "locked")) {
        throw "APK deployment profile '$($payload.deploymentProfile)' is invalid."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedDeploymentProfile) -and
        $payload.deploymentProfile -cne $ExpectedDeploymentProfile) {
        throw "APK deployment profile is '$($payload.deploymentProfile)', expected '$ExpectedDeploymentProfile'."
    }

    if ($null -eq $payload.deploymentFiles) {
        throw "APK payload does not contain a deployment file manifest."
    }
    $manifestFiles = @($payload.deploymentFiles)
    $duplicateManifestPath = $manifestFiles |
        Group-Object path -CaseSensitive |
        Where-Object Count -ne 1 |
        Select-Object -First 1
    if ($null -ne $duplicateManifestPath) {
        throw "APK deployment manifest contains duplicate path '$($duplicateManifestPath.Name)'."
    }
    $actualDeploymentEntries = @($archive.Entries | Where-Object {
        -not [string]::IsNullOrEmpty($_.Name) -and
        $_.FullName.StartsWith("assets/LemonLoader/deployment/", [StringComparison]::Ordinal)
    })
    if ($manifestFiles.Count -ne $actualDeploymentEntries.Count) {
        throw "APK deployment manifest contains $($manifestFiles.Count) files, but the APK contains $($actualDeploymentEntries.Count)."
    }
    foreach ($file in $manifestFiles) {
        $relativePath = [string]$file.path
        Assert-SafeDeploymentPath -Path $relativePath -Description "APK deployment manifest path"
        if ($file.policy -notin @("seed", "upgrade", "refresh", "enforce")) {
            throw "APK deployment manifest policy '$($file.policy)' is invalid for '$relativePath'."
        }
        $entry = $archive.GetEntry("assets/LemonLoader/deployment/$relativePath")
        if ($null -eq $entry) {
            throw "APK deployment manifest references missing file '$relativePath'."
        }
        $actualHash = Get-EntrySha256 -Entry $entry
        if ([long]$file.size -ne $entry.Length -or [string]$file.sha256 -cne $actualHash) {
            throw "APK deployment manifest metadata does not match '$relativePath'."
        }
        if ($expectedPolicies.ContainsKey($relativePath) -and
            $expectedPolicies[$relativePath] -cne [string]$file.policy) {
            throw "APK deployment policy for '$relativePath' is '$($file.policy)', expected '$($expectedPolicies[$relativePath])'."
        }
    }

    $manifestPathSet = @{}
    foreach ($file in $manifestFiles) {
        $manifestPathSet[[string]$file.path] = $true
    }
    foreach ($relativePath in $manifestPathSet.Keys) {
        $separator = $relativePath.IndexOf('/')
        while ($separator -ge 0) {
            $parent = $relativePath.Substring(0, $separator)
            if ($manifestPathSet.ContainsKey($parent)) {
                throw "APK deployment target '$parent' conflicts with child file '$relativePath'."
            }
            $separator = $relativePath.IndexOf('/', $separator + 1)
        }
    }

    $deploymentRevision = Get-DeploymentRevision -Files $manifestFiles
    if ([string]$payload.deploymentRevisionSha256 -cne $deploymentRevision) {
        throw "APK deployment revision is '$($payload.deploymentRevisionSha256)', expected '$deploymentRevision'."
    }
    $manifestPaths = @($manifestFiles | ForEach-Object { [string]$_.path })
    foreach ($expectedPath in $expectedPolicies.Keys) {
        if ($expectedPath -cnotin $manifestPaths) {
            throw "Expected deployment policy path '$expectedPath' is absent from the manifest."
        }
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
            "assets/LemonLoader/runtime/interop/",
            [StringComparison]::Ordinal)
    }).Count
    if ($interopCount -eq 0) {
        throw "APK contains no generated Interop assemblies."
    }

    Write-Host "Verified LemonLoader APK layout v8:"
    Write-Host "  $apk"
    Write-Host "  Interop assemblies: $interopCount"
    Write-Host "  Expected deployment files: $($expectedDeploymentFiles.Count)"
    Write-Host "  Deployment profile: $($payload.deploymentProfile)"
    Write-Host "  Loader SHA-256: $loaderHash"
    Write-Host "  Dotnet SHA-256: $dotnetHash"
    Write-Host "  Interop SHA-256: $interopHash"
    Write-Host "  Deployment SHA-256: $deploymentHash"
    Write-Host "  Deployment revision SHA-256: $deploymentRevision"
}
finally {
    $archive.Dispose()
}
