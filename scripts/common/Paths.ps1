function Assert-ChildPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (!$fullPath.StartsWith($fullParent, $comparison)) {
        throw "Refusing to modify '$fullPath' because it is outside '$fullParent'."
    }
    # Parent is the caller's trusted root; aliases at or above it are allowed.
    $trustedRoot = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    for ($current = $fullPath; $current -and !$current.TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($trustedRoot, $comparison); $current = [IO.Path]::GetDirectoryName($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing linked output path '$current'."
            }
        }
    }
}
