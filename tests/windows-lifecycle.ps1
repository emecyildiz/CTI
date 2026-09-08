# Offline tests: Docker is replaced with an in-memory fixture. Only owned temporary files are deleted.
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/windows-lifecycle.ps1')
$script:root = Join-Path ([IO.Path]::GetTempPath()) ('cti-lifecycle-tests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($script:root)
$script:project = 'cti-' + ('a' * 32)
$script:scenario = ''
$script:mutations = @()
$script:managed = $true

function Assert-True($Condition, [string]$Label) { if (-not $Condition) { throw "FAIL: $Label" } }
function Reject([scriptblock]$Action, [string]$Label) {
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $true }
    Assert-True $caught $Label
    Assert-True ($script:mutations.Count -eq 0) "$Label mutated Docker"
}
function Write-Owner {
    $o = @{ schema = 1; product = 'CTI Self-Hosted'; root = $script:root; project = $script:project; engine = 'test-engine'; managed_n8n = $script:managed }
    [IO.File]::WriteAllText((Join-Path $script:root '.cti-owner.json'), ($o | ConvertTo-Json))
}
function Invoke-CtiDocker([string[]]$Arguments) {
    $command = $Arguments -join ' '
    if ($Arguments[0] -eq 'info') { if ($script:scenario -eq 'wrong-engine') { return 'other-engine' }; return 'test-engine' }
    if ($Arguments[0] -in @('stop', 'rm') -or ($Arguments[0] -in @('network', 'volume') -and $Arguments[1] -eq 'rm')) {
        $script:mutations += $command
        if ($script:scenario -eq 'delete-failed' -and $Arguments[0] -eq 'volume') { throw 'simulated Docker failure' }
        return ''
    }
    if ($script:scenario -eq 'empty') { return '' }
    if ($Arguments[0] -eq 'ps') {
        if ($command -match 'volume=') { if ($script:scenario -eq 'foreign-volume-user') { return 'foreign' }; return 'db' }
        return 'db'
    }
    if ($Arguments[0] -eq 'inspect') {
        $service = if ($script:scenario -eq 'unexpected-service') { 'unrelated-service' } elseif ($script:scenario -eq 'external-n8n') { 'cti-n8n' } else { 'cti-db' }
        $root = if ($script:scenario -eq 'wrong-root') { 'C:\other' } else { $script:root }
        $id = if ($Arguments[1] -eq 'foreign') { 'foreign' } else { 'db' }
        return (ConvertTo-Json -InputObject @(@{ Id = $id; Config = @{ Labels = @{ 'com.docker.compose.project' = $script:project; 'com.docker.compose.service' = $service; 'com.docker.compose.project.working_dir' = $root } } }) -Depth 6 -Compress)
    }
    if ($Arguments[0] -eq 'volume') {
        if ($Arguments[1] -eq 'ls') { return ($script:project + '_cti_pgdata') }
        $p = if ($script:scenario -eq 'wrong-volume') { 'other-project' } else { $script:project }
        return (ConvertTo-Json -InputObject @(@{ Name = ($script:project + '_cti_pgdata'); Labels = @{ 'com.docker.compose.project' = $p; 'com.docker.compose.volume' = 'cti_pgdata' } }) -Depth 4 -Compress)
    }
    if ($Arguments[0] -eq 'network') {
        if ($Arguments[1] -eq 'ls') { return 'net' }
        $members = if ($script:scenario -eq 'foreign-network-user') { @{foreign=@{}} } else { @{db=@{}} }
        return (ConvertTo-Json -InputObject @(@{ Id = 'net'; Name = $script:project; Labels = @{ 'com.docker.compose.project' = $script:project; 'com.docker.compose.network' = 'cti' }; Containers = $members }) -Depth 5 -Compress)
    }
    throw "Unexpected mock command: $command"
}
try {
    Reject { Invoke-CtiManagement $script:root 'Remove' $false $script:project } 'legacy ownership missing'
    Write-Owner
    foreach ($case in @('wrong-engine', 'wrong-root', 'unexpected-service', 'wrong-volume', 'foreign-volume-user', 'foreign-network-user')) {
        $script:scenario = $case
        Reject { Invoke-CtiManagement $script:root 'Purge' $false $script:project } $case
    }
    $script:scenario = 'external-n8n'; $script:managed = $false; Write-Owner
    Reject { Invoke-CtiManagement $script:root 'Remove' $false $script:project } 'external n8n protected'
    $script:scenario = ''; $script:managed = $true; Write-Owner
    Reject { Invoke-CtiManagement $script:root 'Purge' $false 'incorrect' } 'explicit confirmation required'
    Invoke-CtiManagement $script:root 'Purge' $true ''
    Assert-True ($script:mutations.Count -eq 0) 'preview must be read-only'
    Invoke-CtiManagement $script:root 'Stop' $false $script:project
    Assert-True (($script:mutations -join ';') -eq 'stop --time 60 db') 'stop scope'
    $script:mutations = @()
    Invoke-CtiManagement $script:root 'Remove' $false $script:project
    Assert-True (($script:mutations -join ';') -eq 'stop --time 60 db;rm db;network rm net') 'remove must preserve volumes'
    Assert-True (Test-Path -LiteralPath (Join-Path $script:root '.cti-owner.json')) 'remove must preserve configuration'
    $script:mutations = @()
    $known = Join-Path $script:root 'README.md'
    $modified = Join-Path $script:root 'changed.md'
    $unknown = Join-Path $script:root 'my-backup.dump'
    [IO.File]::WriteAllText($known, 'known')
    [IO.File]::WriteAllText($modified, 'original')
    [IO.File]::WriteAllText($unknown, 'private backup')
    $entries = @(@{path='README.md';sha256=(Get-FileHash $known).Hash}, @{path='changed.md';sha256=(Get-FileHash $modified).Hash})
    $manifest = Join-Path $script:root '.cti-package-files.json'
    [IO.File]::WriteAllText($manifest, ($entries | ConvertTo-Json))
    [IO.File]::WriteAllText($modified, 'user edits')
    [IO.File]::WriteAllText((Join-Path $script:root '.env'), 'dummy=test')
    [IO.File]::WriteAllText((Join-Path $script:root '.cti-update-state.json'), '{"product":"CTI Self-Hosted","version":"0.1.0-rc.9"}')
    $script:scenario = 'delete-failed'
    $caught = $false
    try { Invoke-CtiManagement $script:root 'Purge' $false $script:project } catch { $caught = $true }
    Assert-True $caught 'volume failure must abort'
    Assert-True (Test-Path $known) 'Docker failure must preserve package/config'
    Assert-True (Test-Path (Join-Path $script:root '.env')) 'Docker failure must preserve keys'
    $script:scenario = ''; $script:mutations = @()
    [IO.File]::WriteAllText($manifest, (@{path='../outside.txt';sha256=('a'*64)} | ConvertTo-Json))
    Reject { Invoke-CtiManagement $script:root 'Purge' $false $script:project } 'path traversal rejected before Docker mutation'
    [IO.File]::WriteAllText($manifest, ($entries | ConvertTo-Json))
    Invoke-CtiManagement $script:root 'Purge' $false $script:project
    Assert-True (($script:mutations -join ';') -eq "stop --time 60 db;rm db;network rm net;volume rm $($script:project)_cti_pgdata") 'purge exact resources only'
    Assert-True (-not (Test-Path $known)) 'unchanged package removed'
    Assert-True (Test-Path $modified) 'modified file retained'
    Assert-True (Test-Path $unknown) 'unknown backup retained'
    Assert-True (-not (Test-Path (Join-Path $script:root '.env'))) 'purge removes config'
    Assert-True (-not (Test-Path (Join-Path $script:root '.cti-update-state.json'))) 'purge removes pending update receipt'
    Write-Owner
    $script:scenario = 'empty'; $script:mutations = @()
    Invoke-CtiManagement $script:root 'Remove' $false $script:project
    Assert-True ($script:mutations.Count -eq 0) 'repeat removal with absent resources'
    Write-Host 'PASS: ownership/engine/service/shared-resource guards, preview, stop/remove/purge, path safety, failure recovery and file preservation (mock Docker).'
} finally {
    $resolved = [IO.Path]::GetFullPath($script:root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('cti-lifecycle-tests-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
