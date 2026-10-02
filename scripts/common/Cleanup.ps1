function Get-GeneratedBuildDirectory {
    param([Parameter(Mandatory)][string]$Root)
    $item = Get-Item -LiteralPath $Root -Force -ErrorAction SilentlyContinue
    if (!$item -or !$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return }
    if (Test-Path -LiteralPath (Join-Path $Root '.git')) { return }
    foreach ($directory in Get-ChildItem -LiteralPath $Root -Directory -Force) {
        if ($directory.Name -in @('bin', 'obj', '.vs', '__pycache__')) {
            $directory.FullName
        } elseif ($directory.Name -notin @('.git', '.dependencies', 'Output') -and
                  !($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Get-GeneratedBuildDirectory -Root $directory.FullName
        }
    }
}

function Assert-GeneratedCleanupPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$RepositoryRoot)
    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath($Path)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (!$path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, $comparison)) {
        throw "Refusing cleanup outside the product repository: '$path'."
    }
    for ($current = $path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing linked cleanup path: '$current'."
        }
    }
    Assert-NoCleanupLinks -Path $path
}

function Assert-NoCleanupLinks {
    param([Parameter(Mandatory)][string]$Path)
    if (!(Test-Path -LiteralPath $Path -PathType Container)) { return }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force) {
        if ($item.Name -eq '.git') {
            throw "Refusing cleanup of a nested repository: '$Path'."
        }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing linked cleanup content: '$($item.FullName)'."
        }
        if ($item.PSIsContainer) { Assert-NoCleanupLinks -Path $item.FullName }
    }
}
