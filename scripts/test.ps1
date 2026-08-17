[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$testRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "Output\ManagedCompatTests"))
$expectedPrefix = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "Output")).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $testRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create the patcher test directory outside Output: '$testRoot'."
}

$nugetRoot = $env:NUGET_PACKAGES
if ([string]::IsNullOrWhiteSpace($nugetRoot)) {
    $nugetRoot = Join-Path $HOME ".nuget\packages"
}

$fixtures = [ordered]@{
    "MonoMod.Utils.dll" = Join-Path $nugetRoot `
        "monomod.utils\22.7.31.1\lib\net5.0\MonoMod.Utils.dll"
    "0Harmony.dll" = Join-Path $nugetRoot `
        "harmonyx\2.10.2\lib\netstandard2.0\0Harmony.dll"
}

foreach ($fixture in $fixtures.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $fixture.Value -PathType Leaf)) {
        throw "Raw patcher fixture '$($fixture.Key)' was not found at '$($fixture.Value)'. Restore the repository dependencies first."
    }
}

$patcherProject = Join-Path $repositoryRoot "src\LemonLoader.ManagedCompat\LemonLoader.ManagedCompat.csproj"

$net8TestRoot = Join-Path $testRoot "Net8"
New-Item -ItemType Directory -Force -Path $net8TestRoot | Out-Null
foreach ($fixtureName in @("MonoMod.Utils.dll", "0Harmony.dll")) {
    $testAssembly = Join-Path $net8TestRoot $fixtureName
    Copy-Item -LiteralPath $fixtures[$fixtureName] -Destination $testAssembly
    $rawHash = (Get-FileHash -LiteralPath $testAssembly -Algorithm SHA256).Hash

    dotnet run --project $patcherProject --configuration $Configuration -- `
        --runtime-major 8 $testAssembly
    if ($LASTEXITCODE -ne 0) {
        throw "The .NET 8 patcher pass failed for '$fixtureName' with exit code $LASTEXITCODE."
    }

    $patchedHash = (Get-FileHash -LiteralPath $testAssembly -Algorithm SHA256).Hash
    if ($fixtureName -eq "MonoMod.Utils.dll" -and $patchedHash -ne $rawHash) {
        throw "The .NET 8 patcher unexpectedly changed MonoMod.Utils."
    }
    if ($fixtureName -eq "0Harmony.dll" -and $patchedHash -eq $rawHash) {
        throw "The .NET 8 patcher did not add the Harmony resolver guards."
    }
}

if (Test-Path -LiteralPath $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

foreach ($fixture in $fixtures.GetEnumerator()) {
    $testAssembly = Join-Path $testRoot $fixture.Key
    Copy-Item -LiteralPath $fixture.Value -Destination $testAssembly
    $rawHash = (Get-FileHash -LiteralPath $testAssembly -Algorithm SHA256).Hash

    dotnet run --project $patcherProject --configuration $Configuration -- $testAssembly
    if ($LASTEXITCODE -ne 0) {
        throw "The first patcher pass failed for '$($fixture.Key)' with exit code $LASTEXITCODE."
    }

    $patchedHash = (Get-FileHash -LiteralPath $testAssembly -Algorithm SHA256).Hash
    if ($patchedHash -eq $rawHash) {
        throw "The first patcher pass did not change '$($fixture.Key)'."
    }

    dotnet run --project $patcherProject --configuration $Configuration -- $testAssembly
    if ($LASTEXITCODE -ne 0) {
        throw "The idempotence pass failed for '$($fixture.Key)' with exit code $LASTEXITCODE."
    }

    $idempotentHash = (Get-FileHash -LiteralPath $testAssembly -Algorithm SHA256).Hash
    if ($idempotentHash -ne $patchedHash) {
        throw "The idempotence pass changed '$($fixture.Key)' a second time."
    }
}

$cecilPath = Join-Path $nugetRoot "mono.cecil\0.11.6\lib\netstandard2.0\Mono.Cecil.dll"
if (-not (Test-Path -LiteralPath $cecilPath -PathType Leaf)) {
    throw "Mono.Cecil was not found at '$cecilPath'. Restore the patcher project first."
}
Add-Type -Path $cecilPath

$interopFixtureDirectory = Join-Path $testRoot "InteropFixture"
New-Item -ItemType Directory -Force -Path $interopFixtureDirectory | Out-Null
$interopFixturePath = Join-Path $interopFixtureDirectory "InteropFixture.dll"
$assemblyName = [Mono.Cecil.AssemblyNameDefinition]::new(
    "InteropFixture",
    [version]"1.0.0.0")
$fixtureAssembly = [Mono.Cecil.AssemblyDefinition]::CreateAssembly(
    $assemblyName,
    "InteropFixture",
    [Mono.Cecil.ModuleKind]::Dll)
try {
    $fixtureType = [Mono.Cecil.TypeDefinition]::new(
        "Il2CppFixture",
        "NetManager",
        [Mono.Cecil.TypeAttributes]::Public -bor [Mono.Cecil.TypeAttributes]::Class,
        $fixtureAssembly.MainModule.TypeSystem.Object)
    $fixtureMethod = [Mono.Cecil.MethodDefinition]::new(
        "Load",
        [Mono.Cecil.MethodAttributes]::Public -bor [Mono.Cecil.MethodAttributes]::Static,
        $fixtureAssembly.MainModule.TypeSystem.Void)
    $brokenParameter = [Mono.Cecil.ParameterDefinition]::new(
        "onError",
        [Mono.Cecil.ParameterAttributes]::Optional -bor [Mono.Cecil.ParameterAttributes]::HasDefault,
        $fixtureAssembly.MainModule.TypeSystem.String)
    $fixtureMethod.Parameters.Add($brokenParameter)
    $fixtureType.Methods.Add($fixtureMethod)
    $fixtureAssembly.MainModule.Types.Add($fixtureType)
    $fixtureAssembly.Write($interopFixturePath)
}
finally {
    $fixtureAssembly.Dispose()
}

dotnet run --project $patcherProject --configuration $Configuration -- `
    --interop-directory $interopFixtureDirectory
if ($LASTEXITCODE -ne 0) {
    throw "The interop metadata normalization pass failed with exit code $LASTEXITCODE."
}
$normalizedHash = (Get-FileHash -LiteralPath $interopFixturePath -Algorithm SHA256).Hash
$normalizedAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($interopFixturePath)
try {
    $normalizedParameter = $normalizedAssembly.MainModule.Types |
        Where-Object FullName -eq "Il2CppFixture.NetManager" |
        ForEach-Object { $_.Methods[0].Parameters[0] }
    if (-not $normalizedParameter.IsOptional -or
        $normalizedParameter.HasDefault -or
        $normalizedParameter.HasConstant) {
        throw "The interop metadata normalizer did not preserve Optional while removing the invalid HasDefault flag."
    }
}
finally {
    $normalizedAssembly.Dispose()
}

dotnet run --project $patcherProject --configuration $Configuration -- `
    --interop-directory $interopFixtureDirectory
if ($LASTEXITCODE -ne 0) {
    throw "The interop metadata idempotence pass failed with exit code $LASTEXITCODE."
}
$normalizedAgainHash = (Get-FileHash -LiteralPath $interopFixturePath -Algorithm SHA256).Hash
if ($normalizedAgainHash -ne $normalizedHash) {
    throw "The interop metadata idempotence pass changed the fixture a second time."
}

Write-Host "LemonLoader.ManagedCompat tests passed for .NET 8 and .NET 10 dependency transforms and one malformed interop fixture."

$patcherTests = Join-Path $repositoryRoot "tests\LemonLoader.Patcher.Tests\LemonLoader.Patcher.Tests.csproj"
dotnet run --project $patcherTests --configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "The LemonLoader.Patcher regression tests failed with exit code $LASTEXITCODE."
}
