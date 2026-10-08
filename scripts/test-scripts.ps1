#requires -Version 7.0
[CmdletBinding()]
param([switch]$SkipBash = $IsWindows)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'common/Paths.ps1')
$failures = [Collections.Generic.List[string]]::new()
$count = 0
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File) {
    if ($script.Extension -eq '.ps1') {
        $tokens = $null
        $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
        foreach ($error in $errors) { $failures.Add("$($script.FullName): $($error.Message)") }
        $count++
    } elseif ($script.Extension -eq '.sh' -and !$SkipBash) {
        & bash -n $script.FullName
        if ($LASTEXITCODE -ne 0) { $failures.Add("Bash parsing failed: $($script.FullName)") }
        $count++
    }
}
if ($failures.Count) { throw ($failures -join "`n") }
$fixture = Join-Path $repositoryRoot ('Output/Tests/ScriptHelpers/' + [Guid]::NewGuid().ToString('N'))
. (Join-Path $PSScriptRoot 'common/TestFixtures.ps1')
try {
    [void][IO.Directory]::CreateDirectory($fixture)
    Assert-ChildPath -Path (Join-Path $fixture 'child') -Parent $fixture
    foreach ($path in @($fixture, "$fixture-other/child")) {
        $rejected = $false
        try { Assert-ChildPath -Path $path -Parent $fixture } catch { $rejected = $true }
        if (!$rejected) { throw "Unsafe path was accepted: $path" }
    }
    $protected = Join-Path $fixture 'protected'
    [void][IO.Directory]::CreateDirectory($protected)
    $link = Join-Path $fixture 'linked'
    $type = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $type -Path $link -Target $protected | Out-Null
    $rejected = $false
    try { Assert-ChildPath -Path (Join-Path $link 'child') -Parent $fixture } catch { $rejected = $true }
    if (!$rejected) { throw 'Linked output path was accepted.' }
    Assert-ChildPath -Path (Join-Path $link 'child') -Parent $link
    Assert-ChildPath -Path (Join-Path $link 'nested/child') -Parent (Join-Path $link 'nested')
    & (Join-Path $PSScriptRoot 'test-cleanup.ps1')
    & (Join-Path $PSScriptRoot 'test-publication-scan.ps1')
    & (Join-Path $PSScriptRoot 'test-release-packaging.ps1')
    Write-Host "Patcher script syntax and helpers passed ($count scripts; SkipBash=$SkipBash)."
} finally {
    Remove-TestFixture -Path $fixture
}
