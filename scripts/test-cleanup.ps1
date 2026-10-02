#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $repositoryRoot "Output/Tests/Cleanup/$([Guid]::NewGuid().ToString('N'))"
$product = Join-Path $fixture 'Product'
$scripts = Join-Path $product 'scripts'
[void][IO.Directory]::CreateDirectory((Join-Path $scripts 'common'))
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts/clean.ps1') -Destination $scripts
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts/common/Cleanup.ps1') -Destination (Join-Path $scripts 'common')
$clean = Join-Path $scripts 'clean.ps1'
. (Join-Path $scripts 'common/Cleanup.ps1')
function Write-Marker([string]$RelativePath) {
    $path = Join-Path $product $RelativePath
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, 'keep-or-remove-fixture')
}
function Assert-Present([string[]]$Paths, [bool]$Present) {
    foreach ($path in $Paths) {
        if ((Test-Path -LiteralPath (Join-Path $product $path)) -ne $Present) {
            throw "Unexpected cleanup state: '$path'; expected present=$Present."
        }
    }
}
function Reject([scriptblock]$Action) {
    try { & $Action } catch { return }
    throw 'Expected unsafe cleanup rejection.'
}
$transient = @('Output/PublishTemp/marker', 'src/Project/bin/marker', 'src/Project/obj/marker', 'src/LemonLoader.ManagedCompat/marker', 'TestResults/marker')
$durable = @('Output/Releases/marker', 'Output/Packages/marker')
$always = @('Output/Tests/diagnostic.txt', 'Output/RuntimeDevelopment/marker',
    'Output/PrivateInputs/fixture.apk', 'Output/Symbols/marker',
    '.dependencies/Dependency/bin/marker', 'src/Project/code.txt',
    'src/Project/NestedRepository/.git/HEAD', 'src/Project/NestedRepository/bin/marker')
foreach ($path in @($transient + $durable + $always)) { Write-Marker $path }
$shared = Join-Path $fixture 'SharedDependency/bin/marker'
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($shared))
[IO.File]::WriteAllText($shared, 'shared-source-output')
& $clean -WhatIf
Assert-Present ($transient + $durable + $always) $true
& $clean
Assert-Present $transient $false
Assert-Present ($durable + $always) $true
& $clean -AllOutputs -WhatIf
Assert-Present ($durable + $always) $true
& $clean -AllOutputs
Assert-Present $durable $false
Assert-Present $always $true
if ([IO.File]::ReadAllText($shared) -cne 'shared-source-output') { throw 'Shared dependency was cleaned.' }
Reject { Assert-GeneratedCleanupPath -Path $product -RepositoryRoot $product }
Reject { Assert-GeneratedCleanupPath -Path (Join-Path $fixture 'Product-other/output') -RepositoryRoot $product }
$protected = Join-Path $fixture 'Protected'
Write-Marker 'Output/PublishTemp/Nested/.git/HEAD'
Write-Marker 'src/Project/obj/marker'
Reject { & $clean }
Assert-Present @('src/Project/obj/marker') $true
[IO.Directory]::Move((Join-Path $product 'Output/PublishTemp'), (Join-Path $product 'Output/RetainedRepositoryFixture'))
[void][IO.Directory]::CreateDirectory($protected)
[IO.File]::WriteAllText((Join-Path $protected 'keep'), 'protected')
$linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
# A link inside a selected tree must reject the whole cleanup before any deletion.
$selected = Join-Path $product 'Output/PublishTemp'
[void][IO.Directory]::CreateDirectory($selected)
$link = Join-Path $selected 'nested-link'
New-Item -ItemType $linkType -Path $link -Target $protected | Out-Null
Write-Marker 'src/Project/obj/marker'
Reject { & $clean }
Assert-Present @('src/Project/obj/marker') $true
if ([IO.File]::ReadAllText((Join-Path $protected 'keep')) -cne 'protected') { throw 'Linked target was modified.' }
# Rename, do not recursively delete, the fixture tree containing the link.
[IO.Directory]::Move($selected, (Join-Path $product 'Output/RetainedLinkedFixture'))
$link = Join-Path $product 'Output/PublishTemp'
New-Item -ItemType $linkType -Path $link -Target $protected | Out-Null
Reject { & $clean -WhatIf }
Assert-Present @('src/Project/obj/marker') $true
[IO.Directory]::Move($protected, (Join-Path $fixture 'RetainedProtected'))
Reject { & $clean }
Assert-Present @('src/Project/obj/marker') $true
$otherRoot = Join-Path $fixture 'LinkedProduct'
New-Item -ItemType $linkType -Path $otherRoot -Target $product | Out-Null
Reject { Assert-GeneratedCleanupPath -Path (Join-Path $otherRoot 'Output/nonexistent') -RepositoryRoot $otherRoot }
Write-Host 'PASS LemonLoader.Patcher cleanup: standalone scope, preview, retained inputs/evidence, explicit archives, nested repositories and linked-tree rejection'
