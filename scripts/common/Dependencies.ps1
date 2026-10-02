function Get-InteropDependency {
    param([string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')))
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw
    $revision = [string]$manifest.Project.PropertyGroup.BundledIl2CppInteropRevision
    $url = [string]$manifest.Project.PropertyGroup.BundledIl2CppInteropRepositoryUrl
    $uri = $null
    if ($revision -notmatch '^[0-9a-f]{40}$' -or
        ![Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -cne 'https') {
        throw 'Patcher must define its own valid Il2CppInterop revision and HTTPS repository URL.'
    }
    return @{ Revision = $revision; Url = $url }
}

function Get-InteropSourceRoot {
    param([string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')),
          [string]$SourceRoot)
    if ($SourceRoot) { return [IO.Path]::GetFullPath($SourceRoot) }
    $dependency = Get-InteropDependency -RepositoryRoot $RepositoryRoot
    $sibling = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '../Il2CppInterop'))
    if (Test-Path -LiteralPath (Join-Path $sibling '.git')) {
        $head = @(& git -C $sibling rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -eq 0 -and $head.Count -eq 1 -and $head[0].Trim() -ceq $dependency.Revision) {
            return $sibling
        }
    }
    return [IO.Path]::GetFullPath((Join-Path $RepositoryRoot ".dependencies/Il2CppInterop/$($dependency.Revision)"))
}

function Assert-InteropSourceCheckout {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Revision)
    if (!(Test-Path -LiteralPath (Join-Path $Path '.git'))) { throw "Source is not a Git checkout: '$Path'." }
    $head = @(& git -C $Path rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $head[0].Trim() -cne $Revision) {
        throw "Source '$Path' does not match Patcher's pin '$Revision'; use a separate checkout."
    }
    $changes = @(& git -C $Path status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $changes.Count) { throw "Source '$Path' has local changes; setup will not modify it." }
}

function Initialize-InteropSourceCheckout {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Url,
          [Parameter(Mandatory)][string]$Revision)
    if (Test-Path -LiteralPath $Path) {
        Assert-InteropSourceCheckout -Path $Path -Revision $Revision
        return
    }
    $Path = [IO.Path]::GetFullPath($Path)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    $staging = "$Path.staging-$([Guid]::NewGuid().ToString('N'))"
    try {
        & git -c core.longpaths=true clone --config core.longpaths=true --filter=blob:none --no-checkout $Url $staging
        if ($LASTEXITCODE -ne 0) { throw "Could not clone '$Url'." }
        & git -C $staging cat-file -e "$Revision^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) {
            & git -C $staging fetch --no-tags origin $Revision
            if ($LASTEXITCODE -ne 0) { throw "Patcher's pinned revision '$Revision' is unavailable from '$Url'." }
        }
        & git -C $staging checkout --detach $Revision
        if ($LASTEXITCODE -ne 0) { throw 'Checking out the pinned generator failed.' }
        Assert-InteropSourceCheckout -Path $staging -Revision $Revision
        [IO.Directory]::Move($staging, $Path)
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            if (![IO.Path]::GetFullPath($staging).StartsWith("$Path.staging-", [StringComparison]::Ordinal)) {
                throw 'Refusing to clean unexpected dependency staging.'
            }
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
}
