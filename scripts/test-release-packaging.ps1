#requires -Version 7.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Formats.Tar
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $repositoryRoot "Output/Tests/ReleasePackaging/$([Guid]::NewGuid().ToString('N'))"
$product = Join-Path $fixture 'Product'
$scripts = Join-Path $product 'scripts'
[void][IO.Directory]::CreateDirectory((Join-Path $scripts 'common'))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package-release.ps1') -Destination $scripts
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'common/Paths.ps1') -Destination (Join-Path $scripts 'common')
foreach ($rid in @('win-x64','linux-x64')) {
    $suffix = if ($rid -eq 'win-x64') { '.exe' } else { '' }
  foreach ($application in @('GUI','CLI')) {
    foreach ($name in @('LICENSE','NOTICE',"LemonLoader.Patcher.$application$suffix",
        'Tools/Il2CppInterop/Il2CppInterop.CLI.dll',
        'Tools/Il2CppInterop/lemonloader-il2cppinterop.json',
        (('long-path-' * 12) + '/fixture.dat'))) {
        $path = Join-Path $product "Output/Releases/$rid/$application/$name"
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::WriteAllText($path,'synthetic-release-input')
        [IO.File]::SetLastWriteTimeUtc($path,[DateTime]::new(2020,1,1,0,0,0,[DateTimeKind]::Utc))
    }
  }
}
[void][IO.Directory]::CreateDirectory((Join-Path $product 'Output/Releases/win-x64/CLI/Tools/empty/nested'))
$package = Join-Path $scripts 'package-release.ps1'
$output = Join-Path $product 'Output/Packages/v1.2.3'
& pwsh -NoProfile -File $package -Version v1.2.3
if ($LASTEXITCODE -ne 0) { throw 'Initial fixture packaging failed.' }
$first = @{}
foreach ($file in Get-ChildItem $output -File) { $first[$file.Name] = (Get-FileHash $file.FullName).Hash }
Start-Sleep -Milliseconds 2200
& pwsh -NoProfile -File $package -Version v1.2.3
if ($LASTEXITCODE -ne 0) { throw 'Repeated fixture packaging failed.' }
foreach ($file in Get-ChildItem $output -File) {
    if ((Get-FileHash $file.FullName).Hash -cne $first[$file.Name]) {
        throw "Release repack changed '$($file.Name)' for identical inputs; fixture=$fixture"
    }
}
foreach ($application in @('GUI','CLI')) {
$input = [IO.File]::OpenRead((Join-Path $output "LemonLoader.Patcher.$application-linux-x64.tar.gz"))
$gzip = [IO.Compression.GZipStream]::new($input,[IO.Compression.CompressionMode]::Decompress)
$reader = [System.Formats.Tar.TarReader]::new($gzip)
try {
    $entries = @()
    while ($null -ne ($entry = $reader.GetNextEntry())) {
        $entries += $entry.Name
        $executable = $entry.Name -eq "LemonLoader.Patcher.$application"
        $mode = if ($executable) { 493 } else { 420 }
        if ([int]$entry.Mode -ne $mode -or $entry.Uid -ne 0 -or $entry.Gid -ne 0 -or
            $entry.ModificationTime -ne [DateTimeOffset]::new(2020,1,1,0,0,0,[TimeSpan]::Zero)) {
            throw "Unexpected Linux archive metadata: $($entry.Name)"
        }
        if ($entry -isnot [System.Formats.Tar.GnuTarEntry] -or
            $entry.AccessTime -ne $entry.ModificationTime -or $entry.ChangeTime -ne $entry.ModificationTime) {
            throw "Linux entry has unstable access/change times: $($entry.Name)"
        }
    }
    if ($entries.Count -ne 6 -or @($entries | Where-Object Length -gt 100).Count -ne 1) { throw 'Archive lost a fixture or long path.' }
    if (@($entries | Where-Object { $_ -match '^(CLI|GUI)/' }).Count) { throw 'Archive must have its executable at root.' }
} finally { $reader.Dispose(); $gzip.Dispose(); $input.Dispose() }
}
$sidecar = [IO.File]::ReadAllLines((Join-Path $output 'SHA256SUMS.txt'))
if ($sidecar.Count -ne 4) { throw 'All four archive checksums are required.' }
foreach ($line in $sidecar) {
    $parts = $line -split '  ',2
    if ($parts.Count -ne 2 -or $parts[0] -cne $first[$parts[1]].ToLowerInvariant()) { throw 'Archive checksum sidecar mismatch.' }
}
Write-Host 'PASS Windows/Linux cross-process release reproducibility, long paths, modes, timestamps and checksums.'
