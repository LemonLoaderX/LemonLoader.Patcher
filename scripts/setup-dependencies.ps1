[CmdletBinding()]
param([string]$Il2CppInteropSourceRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common/Dependencies.ps1')
$dependency = Get-InteropDependency
$source = Get-InteropSourceRoot -SourceRoot $Il2CppInteropSourceRoot
Initialize-InteropSourceCheckout -Path $source -Url $dependency.Url -Revision $dependency.Revision
Write-Host "Il2CppInterop @ $($dependency.Revision): $source"
