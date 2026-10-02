$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'common/Dependencies.ps1')
$fixture = Join-Path $repositoryRoot "Output/Tests/Dependencies/$([Guid]::NewGuid().ToString('N'))"
[void][IO.Directory]::CreateDirectory($fixture)
function Assert-Equal($Expected, $Actual) {
    if ($Expected -cne $Actual) { throw "Expected '$Expected', got '$Actual'." }
}
function Assert-Rejected([scriptblock]$Action) {
    try { & $Action } catch { return }
    throw 'Expected source setup to reject the checkout.'
}
function Invoke-FixtureGit([string]$Path, [string[]]$Arguments) {
    & git -C $Path @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $Arguments" }
}
$origin = Join-Path $fixture 'origin'
[void][IO.Directory]::CreateDirectory($origin)
Invoke-FixtureGit $origin @('init', '--quiet')
Invoke-FixtureGit $origin @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--quiet', '--allow-empty', '-m', 'first')
$first = (& git -C $origin rev-parse HEAD).Trim()
Invoke-FixtureGit $origin @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--quiet', '--allow-empty', '-m', 'second')
$second = (& git -C $origin rev-parse HEAD).Trim()
$product = Join-Path $fixture 'Patcher'
[void][IO.Directory]::CreateDirectory($product)
[xml]$manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$manifest.Project.PropertyGroup.BundledIl2CppInteropRevision = $first
$manifest.Save((Join-Path $product 'Directory.Build.props'))
$sibling = Join-Path $fixture 'Il2CppInterop'
Initialize-InteropSourceCheckout -Path $sibling -Url $origin -Revision $first
Assert-Equal $sibling (Get-InteropSourceRoot -RepositoryRoot $product)
Initialize-InteropSourceCheckout -Path $sibling -Url 'https://invalid.example/never-fetch' -Revision $first
Invoke-FixtureGit $sibling @('checkout', '--quiet', '--detach', $second)
$cache = [IO.Path]::GetFullPath((Join-Path $product ".dependencies/Il2CppInterop/$first"))
Assert-Equal $cache (Get-InteropSourceRoot -RepositoryRoot $product)
Initialize-InteropSourceCheckout -Path $cache -Url $origin -Revision $first
Assert-Equal $second ((& git -C $sibling rev-parse HEAD).Trim())
Assert-Rejected { Initialize-InteropSourceCheckout -Path $sibling -Url $origin -Revision $first }
[IO.File]::WriteAllText((Join-Path $cache 'local-work.txt'), 'keep')
Assert-Rejected { Initialize-InteropSourceCheckout -Path $cache -Url $origin -Revision $first }
Assert-Equal 'keep' ([IO.File]::ReadAllText((Join-Path $cache 'local-work.txt')))
Assert-Equal $sibling (Get-InteropSourceRoot -RepositoryRoot $product -SourceRoot $sibling)
$manifest.Project.PropertyGroup.BundledIl2CppInteropRevision = $second
$manifest.Save((Join-Path $product 'Directory.Build.props'))
Assert-Equal $sibling (Get-InteropSourceRoot -RepositoryRoot $product)
$invalid = Join-Path $product '.dependencies/invalid'
Assert-Rejected { Initialize-InteropSourceCheckout -Path $invalid -Url $origin -Revision ('0' * 40) }
if ((Test-Path -LiteralPath $invalid) -or @(Get-ChildItem -Path (Join-Path (Split-Path $invalid) '.staging-*') -ErrorAction SilentlyContinue).Count) {
    throw 'Failed setup published or left staging output.'
}
Write-Host 'PASS independent Patcher pins, matching/conflicting siblings, isolated caches, explicit roots and non-mutating setup'
