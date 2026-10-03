[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string[]]$ReleaseArchive = @()
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
& (Join-Path $PSScriptRoot 'test-scripts.ps1')
& (Join-Path $PSScriptRoot 'test-dependencies.ps1')
$patcherTests = Join-Path $repositoryRoot "tests\LemonLoader.Patcher.Tests\LemonLoader.Patcher.Tests.csproj"
dotnet run --project $patcherTests --configuration $Configuration -- `
    --verify-apk-script (Join-Path $PSScriptRoot 'verify-apk-layout.ps1')
if ($LASTEXITCODE -ne 0) {
    throw "The LemonLoader.Patcher regression tests failed with exit code $LASTEXITCODE."
}
dotnet run --project (Join-Path $repositoryRoot 'tests/LemonLoader.Patcher.GUI.Tests') --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw 'The Patcher GUI regressions failed.' }
foreach ($archive in $ReleaseArchive) {
    dotnet run --no-build --project $patcherTests --configuration $Configuration -- `
        --validate-release ([System.IO.Path]::GetFullPath($archive))
    if ($LASTEXITCODE -ne 0) {
        throw "Release archive validation failed for '$archive' with exit code $LASTEXITCODE."
    }
}
