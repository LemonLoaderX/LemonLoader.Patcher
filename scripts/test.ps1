[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$patcherTests = Join-Path $repositoryRoot "tests\LemonLoader.Patcher.Tests\LemonLoader.Patcher.Tests.csproj"
dotnet run --project $patcherTests --configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "The LemonLoader.Patcher regression tests failed with exit code $LASTEXITCODE."
}
