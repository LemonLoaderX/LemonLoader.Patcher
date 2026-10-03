[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ApkPath,

    [string[]]$ExpectedDeployment = @(),

    [ValidateSet("development", "production", "locked")]
    [string]$ExpectedDeploymentProfile,

    [ValidateSet("coreclr")]
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

$archive = [IO.Compression.ZipFile]::OpenRead($apk)
try {
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName
        $path = if ($name.EndsWith('/')) { $name.Substring(0, $name.Length - 1) } else { $name }
        if (!$path -or $path.Contains('\') -or $path.Contains(':') -or
            $path -match '[\x00-\x1f\x7f]' -or
            @($path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
            throw "APK contains unsafe ZIP entry '$name'."
        }
    }
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
        "assets/LemonLoader/runtime/loader/net6/MelonLoader.dll",
        "assets/LemonLoader/runtime/loader/net6/MelonLoader.NativeHost.dll",
        "assets/LemonLoader/runtime/loader/Dependencies/SupportModules/Il2Cpp.dll",
        "assets/LemonLoader/payload.json")) {
        $requiredEntry = $archive.GetEntry($required)
        if ($null -eq $requiredEntry -or $requiredEntry.Length -eq 0) {
            throw "APK is missing or has an empty required LemonLoader entry '$required'."
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
    if ($payload.formatVersion -ne 9) {
        throw "APK payload.json has unsupported format '$($payload.formatVersion)'; expected 9."
    }
    if ($payload.runtimeRid -cnotin @('android-arm64', 'linux-bionic-arm64')) {
        throw 'APK layout 9 has an unsupported runtime RID.'
    }
    if ($ExpectedManagedRuntimeBackend -and $ExpectedManagedRuntimeBackend -cne 'coreclr') {
        throw 'APK layout 9 uses the CoreCLR backend.'
    }
    $engines = @($archive.Entries | Where-Object {
        $_.FullName -cmatch '^assets/LemonLoader/runtime/dotnet/shared/Microsoft\.NETCore\.App/[^/]+/libcoreclr\.so$'
    })
    if ($engines.Count -ne 1) { throw 'APK must contain exactly one CoreCLR version directory.' }
    $sharedRoot = $engines[0].FullName.Substring(0, $engines[0].FullName.LastIndexOf('/'))
    $cryptoNames = if ($payload.runtimeRid -ceq 'android-arm64') {
        @('System.Private.CoreLib.dll', 'libclrjit.so', 'libSystem.Security.Cryptography.Native.Android.so')
    } else { @('System.Private.CoreLib.dll', 'libclrjit.so', 'libSystem.Security.Cryptography.Native.OpenSsl.so', 'libssl.so', 'libcrypto.so') }
    foreach ($name in $cryptoNames) {
        if ($null -eq $archive.GetEntry("$sharedRoot/$name") -or $archive.GetEntry("$sharedRoot/$name").Length -eq 0) { throw "APK runtime is missing '$name'." }
    }
    if ($payload.runtimeRid -ceq 'linux-bionic-arm64' -and
        $null -ne $archive.GetEntry("$sharedRoot/libSystem.Security.Cryptography.Native.Android.so")) {
        throw 'APK Bionic runtime contains the Android JNI crypto library.'
    }
    if ($payload.runtimeRid -ceq 'android-arm64' -and
        $null -ne $archive.GetEntry("$sharedRoot/libSystem.Security.Cryptography.Native.OpenSsl.so")) {
        throw 'APK Android runtime contains the Bionic OpenSSL crypto library.'
    }
    $unsupported = $archive.Entries | Where-Object {
        $_.FullName.StartsWith('assets/LemonLoader/runtime/', [StringComparison]::Ordinal) -and
        -not [string]::IsNullOrEmpty($_.Name) -and
        -not ($_.FullName.StartsWith('assets/LemonLoader/runtime/loader/', [StringComparison]::Ordinal) -or
              $_.FullName.StartsWith('assets/LemonLoader/runtime/dotnet/', [StringComparison]::Ordinal) -or
              $_.FullName.StartsWith('assets/LemonLoader/runtime/interop/', [StringComparison]::Ordinal))
    } | Select-Object -First 1
    if ($unsupported) { throw "APK runtime entry '$($unsupported.FullName)' is outside a supported domain." }
    $policyMap = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($file in @($payload.deploymentFiles)) {
        if ($null -eq $file) { continue }
        $path = [string]$file.path
        Assert-SafeDeploymentPath -Path $path -Description 'APK deployment policy path'
        $policy = if ($null -eq $file.policy) { 'seed' } else { [string]$file.policy }
        if ($policy -cnotin @('seed', 'upgrade', 'refresh', 'enforce') -or $policyMap.ContainsKey($path)) {
            throw "APK contains an invalid or duplicate deployment policy for '$path'."
        }
        $policyMap.Add($path, $policy)
    }
    $actualPaths = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $archive.Entries | Where-Object {
        -not [string]::IsNullOrEmpty($_.Name) -and
        $_.FullName.StartsWith('assets/LemonLoader/deployment/', [StringComparison]::Ordinal)
    }) {
        $path = $entry.FullName.Substring('assets/LemonLoader/deployment/'.Length)
        Assert-SafeDeploymentPath -Path $path -Description 'APK deployment file path'
        $actualPaths.Add($path, '')
        $actualPolicy = if ($policyMap.ContainsKey($path)) { $policyMap[$path] } else { 'seed' }
        $topLevel = $path.Split('/')[0]
        $expectedPolicy = if ($expectedPolicies.ContainsKey($path)) { $expectedPolicies[$path] }
            elseif ($ExpectedDeploymentProfile -and $topLevel -ceq 'UserData' -and $ExpectedDeploymentProfile -cne 'development') { 'upgrade' }
            elseif ($ExpectedDeploymentProfile -ceq 'production' -and $topLevel -cin @('Mods', 'Plugins', 'UserLibs')) { 'refresh' }
            elseif ($ExpectedDeploymentProfile -ceq 'locked' -and $topLevel -cin @('Mods', 'Plugins', 'UserLibs')) { 'enforce' }
            elseif ($ExpectedDeploymentProfile) { 'seed' } else { $null }
        if ($expectedPolicy -and $actualPolicy -cne $expectedPolicy) {
            throw "APK deployment policy for '$path' is '$actualPolicy', expected '$expectedPolicy'."
        }
    }
    foreach ($paths in @($policyMap, $actualPaths)) {
        foreach ($path in $paths.Keys) {
            for ($separator = $path.IndexOf('/'); $separator -ge 0; $separator = $path.IndexOf('/', $separator + 1)) {
                if ($paths.ContainsKey($path.Substring(0, $separator))) { throw "APK deployment path '$path' has a file/directory conflict." }
            }
        }
    }
    foreach ($path in @($expectedDeploymentFiles) + @($expectedPolicies.Keys)) {
        if (!$actualPaths.ContainsKey($path)) { throw "Expected packaged deployment file '$path' was not found." }
    }
    $interopCount = @($archive.Entries | Where-Object {
        $_.FullName.StartsWith('assets/LemonLoader/runtime/interop/', [StringComparison]::Ordinal) -and
        $_.Name.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)
    }).Count
    if ($interopCount -eq 0) { throw 'APK contains no generated Interop assemblies.' }
    Write-Host 'Verified LemonLoader APK layout v9:'
    Write-Host "  $apk"
    Write-Host "  Interop assemblies: $interopCount"
    Write-Host "  Deployment files: $($actualPaths.Count)"

}
finally {
    $archive.Dispose()
}
