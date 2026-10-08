#requires -Version 7.0
[CmdletBinding()]
param([string]$GitleaksPath)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $repositoryRoot ('Output/Tests/PublicationScan/' + [Guid]::NewGuid().ToString('N'))
. (Join-Path $PSScriptRoot 'common/TestFixtures.ps1')
try {
    $product = Join-Path $fixture 'Product'
    $source = Join-Path $fixture 'Selected Source'
    $nested = Join-Path $fixture 'Nested Origin'
    function Write-Fixture([string]$Path, [string]$Text) {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
        [IO.File]::WriteAllText($Path, $Text)
    }
    function Invoke-FixtureGit([string]$Path, [string[]]$Arguments) {
        & git -c core.longpaths=true -C $Path @Arguments | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Fixture Git failed: $Arguments" }
    }
    function Commit([string]$Path) {
        Invoke-FixtureGit $Path @('add', '.')
        Invoke-FixtureGit $Path @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--quiet', '-m', 'fixture')
        return (& git -C $Path rev-parse HEAD).Trim()
    }
    function Initialize([string]$Path) {
        [void][IO.Directory]::CreateDirectory($Path)
        Invoke-FixtureGit $Path @('init', '--quiet')
        Write-Fixture (Join-Path $Path 'input.txt') 'baseline'
        return Commit $Path
    }
    function Reject([scriptblock]$Action, [string]$Message) {
        try { & $Action } catch {
            if ($Message -and !$_.Exception.Message.Contains($Message)) { throw }
            return
        }
        throw 'Expected publication input rejection.'
    }
    $null = Initialize $product
    [void][IO.Directory]::CreateDirectory((Join-Path $product 'scripts'))
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts/scan-publication.ps1') -Destination (Join-Path $product 'scripts')
    Write-Fixture (Join-Path $product '.gitleaksignore') "# No fixture exceptions\n"
    $null = Commit $product
    $firstNested = Initialize $nested
    Write-Fixture (Join-Path $nested 'input.txt') 'second'
    $secondNested = Commit $nested
    $baseline = Initialize $source
    Invoke-FixtureGit $source @('-c', 'protocol.file.allow=always', 'submodule', 'add', '--quiet', $nested, 'nested')
    $head = Commit $source
    $archiveInput = Join-Path $fixture 'archive-input'
    Write-Fixture (Join-Path $archiveInput 'input.txt') 'benign archive'
    $archive = Join-Path $fixture 'release.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($archiveInput, $archive)
    $log = Join-Path $fixture 'scanner-calls.jsonl'
    $fake = Join-Path $fixture 'fake-scanner.ps1'
    $fakeBody = @'
[IO.File]::AppendAllText($env:LEMON_SCAN_LOG, (ConvertTo-Json -InputObject @($args) -Compress) + "`n")
exit ([int]$env:LEMON_SCAN_EXIT)
'@
    Write-Fixture $fake $fakeBody
    $entry = Join-Path $product 'scripts/scan-publication.ps1'
    $saved = @{}
    foreach ($name in @('LEMON_SCAN_LOG', 'LEMON_SCAN_EXIT', 'GITLEAKS_CONFIG')) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    try {
        $env:LEMON_SCAN_LOG = $log
        $env:LEMON_SCAN_EXIT = '0'
        & $entry -GitleaksPath $fake -SourceRepository @($source, $source) -ArchivePath $archive `
            -RuntimeRepository ($source + [IO.Path]::DirectorySeparatorChar) -RuntimeUpstreamBase $baseline
        $calls = @(Get-Content -LiteralPath $log | ForEach-Object { ,(ConvertFrom-Json -InputObject $_) })
        if ($calls.Count -ne 4) { throw 'Selected/nested sources were skipped or scanned twice.' }
        if ($calls[0][-1] -cne $product -or $calls[0] -notcontains '--log-opts=HEAD' -or
            $calls[1][-1] -cne $source -or $calls[1] -notcontains "--log-opts=$baseline..HEAD" -or
            $calls[2][-1] -cne (Join-Path $source 'nested') -or $calls[2] -notcontains '--log-opts=HEAD' -or
            $calls[3][-1] -cne $archive -or $calls[3] -notcontains '--max-archive-depth=2') { throw 'Scanner range/archive arguments changed.' }
        foreach ($call in $calls) {
            if ($call -notcontains '--redact' -or $call -notcontains (Join-Path $product '.gitleaksignore')) {
                throw 'Redaction or product ignore ownership was lost.'
            }
        }
        Write-Fixture $log ''
        Reject { & $entry -GitleaksPath $fake -RuntimeRepository $source } 'must be provided together'
        Reject { & $entry -GitleaksPath $fake -RuntimeRepository $source -RuntimeUpstreamBase ('0' * 40) } 'missing'
        $unrelated = (& git -C $source -c user.name=Fixture -c user.email=fixture@example.invalid commit-tree "$head^{tree}" -m unrelated).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not create unrelated fixture commit.' }
        Reject { & $entry -GitleaksPath $fake -RuntimeRepository $source -RuntimeUpstreamBase $unrelated } 'not an ancestor'
        Reject { & $entry -GitleaksPath $fake -SourceRepository (Join-Path $source 'nested/nonexistent') } 'Git checkout root'
        Reject { & $entry -GitleaksPath $fake -SourceRepository (Join-Path $product 'scripts') } 'Git checkout root'
        Reject { & $entry -GitleaksPath $fake -ArchivePath (Join-Path $fixture 'missing.zip') } 'missing'
        Write-Fixture (Join-Path $source 'input.txt') 'tracked local work'
        Reject { & $entry -GitleaksPath $fake -SourceRepository $source } 'tracked changes'
        Write-Fixture (Join-Path $source 'input.txt') 'baseline'
        Invoke-FixtureGit (Join-Path $source 'nested') @('checkout', '--quiet', '--detach', $firstNested)
        Reject { & $entry -GitleaksPath $fake -SourceRepository $source } ''
        Invoke-FixtureGit (Join-Path $source 'nested') @('checkout', '--quiet', '--detach', $secondNested)
        $incomplete = Join-Path $fixture 'Incomplete Source'
        Invoke-FixtureGit $fixture @('clone', '--quiet', '--config', 'core.longpaths=true', '--no-checkout', $source, $incomplete)
        Invoke-FixtureGit $incomplete @('checkout', '--quiet', '--detach', $head)
        Reject { & $entry -GitleaksPath $fake -SourceRepository $incomplete } 'missing or mismatched'
        if ((Get-Item -LiteralPath $log).Length) { throw 'Rejected preflight inputs invoked the scanner.' }
        Write-Fixture (Join-Path $source 'untracked-local.txt') 'not publication input'
        & $entry -GitleaksPath $fake -SourceRepository $source
        $env:LEMON_SCAN_EXIT = '9'
        Reject { & $entry -GitleaksPath $fake } 'exit 9'
        if ($GitleaksPath) {
            $config = Join-Path $fixture 'gitleaks-fixture.toml'
            Write-Fixture $config "title = 'Publication scanner fixture'`n[[rules]]`nid = 'fixture-secret'`nregex = 'LEMON_FIXTURE_SECRET_[A-Z0-9]{16}'`n"
            $env:GITLEAKS_CONFIG = $config
            $realSource = Join-Path $fixture 'Real Scanner Source'
            $null = Initialize $realSource
            $marker = 'LEMON_' + 'FIXTURE_SECRET_' + 'A1B2C3D4E5F6G7H8'
            Write-Fixture (Join-Path $realSource 'fixture-secret.txt') $marker
            $upstream = Commit $realSource
            Write-Fixture (Join-Path $realSource 'patch.txt') 'reviewed benign patch'
            $null = Commit $realSource
            Reject { & $entry -GitleaksPath $GitleaksPath -SourceRepository $realSource } 'Secret scanning failed'
            & $entry -GitleaksPath $GitleaksPath -RuntimeRepository $realSource -RuntimeUpstreamBase $upstream
            $private = Join-Path $fixture 'Private Backup Source'
            $cleanHead = Initialize $private
            Invoke-FixtureGit $private @('checkout', '--quiet', '-b', 'private-backup')
            Write-Fixture (Join-Path $private 'secret.txt') $marker
            $null = Commit $private
            Invoke-FixtureGit $private @('checkout', '--quiet', '--detach', $cleanHead)
            & $entry -GitleaksPath $GitleaksPath -SourceRepository $private
            $secretInput = Join-Path $fixture 'secret-archive-input'
            Write-Fixture (Join-Path $secretInput 'secret.txt') $marker
            $secretArchive = Join-Path $fixture 'secret-release.zip'
            [IO.Compression.ZipFile]::CreateFromDirectory($secretInput, $secretArchive)
            Reject { & $entry -GitleaksPath $GitleaksPath -ArchivePath $secretArchive } 'Secret scanning failed'
        }
        $global:LASTEXITCODE = 0
        Write-Host 'PASS standalone publication scan: explicit inputs, nested HEADs, range/ancestor checks, redaction, preflight and scanner failures'
    } finally {
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
} finally {
    Remove-TestFixture -Path $fixture
}
