[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$UnstrippedDirectory,

    [string]$StrippedDirectory,

    [string]$AssemblyName = "UnityEngine.IMGUIModule.dll",

    [string[]]$RequiredMethod = @(
        "UnityEngine.GUIStyleState::set_background",
        "UnityEngine.GUIStyle::set_fontStyle"
    )
)

$ErrorActionPreference = "Stop"
$unstrippedRoot = [System.IO.Path]::GetFullPath($UnstrippedDirectory)
$unstrippedAssembly = Join-Path $unstrippedRoot $AssemblyName
if (-not (Test-Path -LiteralPath $unstrippedAssembly -PathType Leaf)) {
    throw "Unstripped assembly was not found at '$unstrippedAssembly'."
}

$nugetRoot = $env:NUGET_PACKAGES
if ([string]::IsNullOrWhiteSpace($nugetRoot)) {
    $nugetRoot = Join-Path $HOME ".nuget\packages"
}
$cecilPath = Join-Path $nugetRoot "mono.cecil\0.11.6\lib\netstandard2.0\Mono.Cecil.dll"
if (-not (Test-Path -LiteralPath $cecilPath -PathType Leaf)) {
    throw "Mono.Cecil was not found at '$cecilPath'. Restore the Patcher projects first."
}
Add-Type -Path $cecilPath

function Get-AssemblyMethods {
    param([Parameter(Mandatory)][string]$Path)

    $methods = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $signatures = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Path)
    try {
        $pending = [System.Collections.Generic.Stack[Mono.Cecil.TypeDefinition]]::new()
        foreach ($type in $assembly.MainModule.Types) {
            $pending.Push($type)
        }
        while ($pending.Count -gt 0) {
            $type = $pending.Pop()
            foreach ($nested in $type.NestedTypes) {
                $pending.Push($nested)
            }
            foreach ($method in $type.Methods) {
                [void]$methods.Add("$($type.FullName)::$($method.Name)")
                [void]$signatures.Add($method.FullName)
            }
        }
    }
    finally {
        $assembly.Dispose()
    }
    return [pscustomobject]@{ Methods = $methods; Signatures = $signatures }
}

$unstripped = Get-AssemblyMethods -Path $unstrippedAssembly
$missing = @($RequiredMethod | Where-Object { -not $unstripped.Methods.Contains($_) })
if ($missing.Count -gt 0) {
    throw "Required Unity methods were not restored: $($missing -join ', ')."
}

$addedCount = $null
if (-not [string]::IsNullOrWhiteSpace($StrippedDirectory)) {
    $strippedAssembly = Join-Path ([System.IO.Path]::GetFullPath($StrippedDirectory)) $AssemblyName
    if (-not (Test-Path -LiteralPath $strippedAssembly -PathType Leaf)) {
        throw "Stripped comparison assembly was not found at '$strippedAssembly'."
    }
    $stripped = Get-AssemblyMethods -Path $strippedAssembly
    $addedCount = @($unstripped.Signatures | Where-Object { -not $stripped.Signatures.Contains($_) }).Count
    if ($addedCount -eq 0) {
        throw "The Unity dependency pass did not add any methods to '$AssemblyName'."
    }
}

Write-Host "Unity unstripping verification passed."
Write-Host "  Assembly: $AssemblyName"
Write-Host "  Required methods: $($RequiredMethod.Count)"
if ($null -ne $addedCount) {
    Write-Host "  Methods added over stripped output: $addedCount"
}
