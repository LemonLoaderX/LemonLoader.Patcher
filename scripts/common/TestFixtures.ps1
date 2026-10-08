# Only callers that created the unique disposable root may use this helper.
# Product cleanup continues to reject repositories and links.
function Remove-TestFixture {
    param([Parameter(Mandatory)][string]$Path)
    $tests = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Output/Tests'))
    $root = [IO.Path]::GetFullPath($Path)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if ([IO.Path]::GetFileName($root) -notmatch '^[0-9a-f]{32}$' -or
        ![string]::Equals([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($root)),
            $tests, $comparison)) {
        throw "Not a uniquely owned test fixture: '$root'."
    }
    for ($current = $root; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Linked test fixture root: '$current'."
        }
    }
    if (!(Test-Path -LiteralPath $root -PathType Container)) { return }
    function Remove-FixtureDirectory([string]$Directory) {
        foreach ($item in Get-ChildItem -LiteralPath $Directory -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                # Test-created links are removed themselves, never followed.
                if ($item.PSIsContainer) { [IO.Directory]::Delete($item.FullName) }
                else { [IO.File]::Delete($item.FullName) }
            } elseif ($item.PSIsContainer) {
                Remove-FixtureDirectory $item.FullName
            } else {
                Remove-Item -LiteralPath $item.FullName -Force
            }
        }
        [IO.Directory]::Delete($Directory)
    }
    Remove-FixtureDirectory $root
}
