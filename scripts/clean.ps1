#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param([switch]$AllOutputs)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'common/Cleanup.ps1')
$outputNames = @('PublishTemp')
if ($AllOutputs) { $outputNames += @('Releases', 'Packages') }
$paths = @(
    foreach ($name in $outputNames) { Join-Path $repositoryRoot "Output/$name" }
    foreach ($name in @('src', 'tests')) {
        Get-GeneratedBuildDirectory -Root (Join-Path $repositoryRoot $name)
    }
    foreach ($name in @('src/LemonLoader.ManagedCompat', 'TestResults', '.vs')) {
        Join-Path $repositoryRoot $name
    }
)
# Validate the whole selection before deleting any tree, including nested links.
foreach ($path in $paths) { Assert-GeneratedCleanupPath -Path $path -RepositoryRoot $repositoryRoot }
foreach ($path in $paths) {
    if ((Test-Path -LiteralPath $path) -and $PSCmdlet.ShouldProcess($path, 'Remove generated product tree')) {
        Remove-Item -LiteralPath $path -Recurse -Force
        Write-Host "Removed $path"
    }
}
Write-Host "Patcher cleanup completed. AllOutputs=$AllOutputs; WhatIf=$WhatIfPreference"
