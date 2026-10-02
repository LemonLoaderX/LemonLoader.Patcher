#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$GitleaksPath = 'gitleaks',
    [string[]]$SourceRepository = @(),
    [string[]]$ArchivePath = @(),
    [string]$RuntimeRepository,
    [ValidatePattern('^[0-9a-f]{40}$')][string]$RuntimeUpstreamBase
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ignorePath = Join-Path $repositoryRoot '.gitleaksignore'
if (!(Test-Path -LiteralPath $ignorePath -PathType Leaf)) { throw 'The product reviewed Gitleaks ignore file is missing.' }
$scanner = Get-Command $GitleaksPath -ErrorAction Stop
if ($scanner.CommandType -notin @('Application', 'ExternalScript')) { throw 'GitleaksPath must resolve to an executable or script.' }
if ([string]::IsNullOrWhiteSpace($RuntimeRepository) -xor [string]::IsNullOrWhiteSpace($RuntimeUpstreamBase)) {
    throw 'RuntimeRepository and RuntimeUpstreamBase must be provided together.'
}
$runtimeRoot = if ($RuntimeRepository) { [IO.Path]::GetFullPath($RuntimeRepository).TrimEnd([IO.Path]::DirectorySeparatorChar) } else { $null }
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$comparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
$seen = [Collections.Generic.HashSet[string]]::new($comparer)
$repositories = [Collections.Generic.List[object]]::new()
function Add-RepositoryTree([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Source repository paths must not be empty.' }
    $repository = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (!$seen.Add($repository)) { return }
    $top = @(& git -c core.longpaths=true -C $repository rev-parse --show-toplevel)
    if ($LASTEXITCODE -ne 0 -or $top.Count -ne 1 -or
        ![IO.Path]::GetFullPath($top[0].Trim()).Equals($repository, $comparison)) {
        throw "Source must be a Git checkout root: '$repository'."
    }
    $changes = @(& git -c core.longpaths=true -C $repository status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0 -or $changes.Count) { throw "Source '$repository' has tracked changes; scan reviewed HEAD inputs." }
    $logOptions = 'HEAD'
    if ($runtimeRoot -and $repository.Equals($runtimeRoot, $comparison)) {
        & git -c core.longpaths=true -C $repository cat-file -e "$RuntimeUpstreamBase^{commit}"
        if ($LASTEXITCODE -ne 0) { throw 'Runtime upstream commit is missing.' }
        & git -c core.longpaths=true -C $repository merge-base --is-ancestor $RuntimeUpstreamBase HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Runtime upstream commit is not an ancestor of HEAD.' }
        $logOptions = "$RuntimeUpstreamBase..HEAD"
    }
    [void]$repositories.Add([pscustomobject]@{ Path = $repository; LogOptions = $logOptions })
    $modules = Join-Path $repository '.gitmodules'
    if (!(Test-Path -LiteralPath $modules -PathType Leaf)) { return }
    $status = @(& git -c core.longpaths=true -C $repository submodule status --recursive)
    if ($LASTEXITCODE -ne 0 -or @($status | Where-Object { $_ -notmatch '^ ' }).Count) {
        throw "Source '$repository' has missing or mismatched nested dependencies."
    }
    $entries = @(& git config -f $modules --get-regexp '^submodule\..*\.path$')
    if ($LASTEXITCODE -eq 1 -and !$entries.Count) { return }
    if ($LASTEXITCODE -ne 0) { throw 'Could not read selected source submodule paths.' }
    foreach ($entry in $entries) {
        if ($entry -notmatch '^submodule\..*\.path\s+(.+)$') { throw 'Invalid submodule path entry.' }
        $child = [IO.Path]::GetFullPath((Join-Path $repository $Matches[1]))
        if (!$child.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, $comparison)) {
            throw 'Selected source submodule escapes its repository.'
        }
        Add-RepositoryTree $child
    }
}
# Preflight all selected inputs before invoking the scanner.
Add-RepositoryTree $repositoryRoot
foreach ($source in $SourceRepository) { Add-RepositoryTree $source }
if ($runtimeRoot) { Add-RepositoryTree $runtimeRoot }
$archives = @(foreach ($archive in $ArchivePath) {
    if ([string]::IsNullOrWhiteSpace($archive)) { throw 'Archive paths must not be empty.' }
    $resolved = [IO.Path]::GetFullPath($archive)
    if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Release archive is missing: '$resolved'." }
    $resolved
})
foreach ($repository in $repositories) {
    Write-Host "Scanning Git history: $($repository.Path) [$($repository.LogOptions)]"
    & $scanner.Source git --no-banner --redact --gitleaks-ignore-path $ignorePath `
        "--log-opts=$($repository.LogOptions)" $repository.Path
    if ($LASTEXITCODE -ne 0) { throw "Secret scanning failed for '$($repository.Path)' (exit $LASTEXITCODE)." }
}
foreach ($archive in $archives) {
    Write-Host "Scanning release archive: $archive"
    & $scanner.Source dir --no-banner --redact --max-archive-depth=2 --gitleaks-ignore-path $ignorePath $archive
    if ($LASTEXITCODE -ne 0) { throw "Secret scanning failed for '$archive' (exit $LASTEXITCODE)." }
}
Write-Host 'Selected publication inputs passed secret scanning.'
