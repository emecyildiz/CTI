# Executes the real setup entry point in an owned temporary package with fake Docker/HTTP.
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('cti-setup-flow-' + [Guid]::NewGuid().ToString('N'))
$beforeLocation = Get-Location
$beforeEnvironment = @{}
Get-ChildItem Env: | ForEach-Object { $beforeEnvironment[$_.Name] = $_.Value }
$global:CtiSetupFlowFixture = @{ Commands = [Collections.Generic.List[string]]::new(); HealthUrls = [Collections.Generic.List[string]]::new() }
function docker {
    $a = @($args)
    $global:CtiSetupFlowFixture.Commands.Add(($a -join ' '))
    $global:LASTEXITCODE = 0
    if ($a[0] -eq 'info' -and $a -contains '--format') { return 'fixture-engine' }
    if ($a[0] -eq 'compose' -and $a -contains 'exec') { return '999' }
    if ($a[0] -eq 'exec' -and $a -contains '--version') { return '2.30.5' }
}
function Invoke-WebRequest {
    param([switch]$UseBasicParsing, [string]$Uri, [int]$TimeoutSec)
    $global:CtiSetupFlowFixture.HealthUrls.Add($Uri)
    return [pscustomobject]@{ StatusCode = 200 }
}
function Check($Condition, [string]$Message) { if (-not $Condition) { throw "FAIL: $Message" } }
function Free-Port {
    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $l.Start(); return $l.LocalEndpoint.Port } finally { $l.Stop() }
}
try {
    [void][IO.Directory]::CreateDirectory((Join-Path $testRoot 'scripts'))
    [void][IO.Directory]::CreateDirectory((Join-Path $testRoot 'app/cti/migrations'))
    Copy-Item -LiteralPath (Join-Path $repository 'setup.ps1') -Destination $testRoot
    Copy-Item -LiteralPath (Join-Path $repository 'scripts/windows-lifecycle.ps1') -Destination (Join-Path $testRoot 'scripts')
    Copy-Item -LiteralPath (Join-Path $repository 'compose.yml') -Destination $testRoot
    $d = Free-Port
    do { $n = Free-Port } while ($n -eq $d)
    $env:COMPOSE_FILE = 'unrelated-compose.yml'
    $env:COMPOSE_PROJECT_NAME = 'unrelated-project'
    $env:CTI_NETWORK_NAME = 'unrelated-network'
    & (Join-Path $testRoot 'setup.ps1') -DashboardPort $d -N8nPort $n -SkipWorkflowImport
    $config = [IO.File]::ReadAllText((Join-Path $testRoot '.env'))
    $owner = Get-Content -LiteralPath (Join-Path $testRoot '.cti-owner.json') -Raw | ConvertFrom-Json
    Check ($owner.project -cmatch '^cti-[a-f0-9]{32}$') 'unique project'
    Check ($config.Contains("CTI_DASHBOARD_PORT=$d")) 'dashboard selected port'
    Check ($config.Contains("N8N_PORT=$n")) 'n8n selected port'
    Check ($config.Contains("N8N_WEBHOOK_URL=http://localhost:${n}/")) 'local webhook selected port'
    Check ($env:N8N_WEBHOOK_URL -eq "http://localhost:${n}/") 'child process uses selected webhook'
    Check ($env:COMPOSE_FILE -eq (Join-Path $testRoot 'compose.yml')) 'inherited compose override blocked'
    Check ($env:COMPOSE_PROJECT_NAME -eq $owner.project) 'child project identity pinned'
    Check ($env:CTI_NETWORK_NAME -eq $owner.project) 'inherited network override blocked'
    Check ($global:CtiSetupFlowFixture.HealthUrls -contains "http://127.0.0.1:${d}/health/ready") 'readiness uses selected port'
    & (Join-Path $testRoot 'setup.ps1') -SkipWorkflowImport
    Check ([IO.File]::ReadAllText((Join-Path $testRoot '.env')) -ceq $config) 'reinstall preserves configuration and secrets'
    Check ((Get-Content -LiteralPath (Join-Path $testRoot '.cti-owner.json') -Raw | ConvertFrom-Json).project -ceq $owner.project) 'reinstall preserves ownership'
    $global:CtiSetupFlowFixture.Commands.Clear()
    $failed = $false
    try { & (Join-Path $testRoot 'setup.ps1') -DashboardPort $d -N8nPort $d -SkipWorkflowImport } catch { $failed = $true }
    Check $failed 'duplicate ports rejected'
    Check (-not ($global:CtiSetupFlowFixture.Commands | Where-Object { $_ -match '\bup\b' })) 'invalid ports do not start services'
    Check ([IO.File]::ReadAllText((Join-Path $testRoot '.env')) -ceq $config) 'invalid update leaves saved config unchanged'
    Write-Host 'PASS: complete Windows setup flow with custom ports, unique identity, inherited-input isolation, reinstall preservation and invalid-update rejection (mock Docker/HTTP).'
} finally {
    Remove-Variable -Name CtiSetupFlowFixture -Scope Global -ErrorAction SilentlyContinue
    Set-Location -LiteralPath $beforeLocation.Path
    foreach ($item in @(Get-ChildItem Env:)) {
        if (-not $beforeEnvironment.ContainsKey($item.Name)) { [Environment]::SetEnvironmentVariable($item.Name, $null, 'Process') }
    }
    foreach ($key in $beforeEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $beforeEnvironment[$key], 'Process') }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('cti-setup-flow-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
