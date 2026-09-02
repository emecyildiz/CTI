[CmdletBinding()]
param(
    [switch]$UseExistingN8n,
    [switch]$SkipWorkflowImport
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Stop-Setup([string]$Message) {
    throw $Message
}

function New-Secret([int]$Bytes = 32) {
    $buffer = New-Object byte[] $Bytes
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($buffer) } finally { $generator.Dispose() }
    return ([Convert]::ToBase64String($buffer)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Invoke-Docker([string[]]$Arguments) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        Stop-Setup "Docker command failed: docker $($Arguments -join ' ')"
    }
}

Write-Step 'Checking prerequisites'
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host 'Docker Desktop is required before CTI Self-Hosted can be installed.' -ForegroundColor Yellow
    Write-Host 'Download: https://docs.docker.com/desktop/setup/install/windows-install/'
    Stop-Setup 'Docker was not found. Install Docker Desktop, start it, then run setup.cmd again.'
}

& docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) {
    Stop-Setup 'Docker Compose v2 is unavailable. Update Docker Desktop and retry.'
}
& docker info | Out-Null
if ($LASTEXITCODE -ne 0) {
    Stop-Setup 'Docker Desktop is installed but its engine is not running.'
}

$createdEnvironment = -not (Test-Path -LiteralPath '.env')
if ($createdEnvironment) {
    Write-Step 'Creating a private local configuration'
    $managed = if ($UseExistingN8n) { 'false' } else { 'true' }
    $container = if ($UseExistingN8n) { '' } else { 'cti-n8n' }
    $environment = @"
POSTGRES_DB=cti
POSTGRES_USER=cti_owner
POSTGRES_PASSWORD=$(New-Secret)
CTI_APP_PASSWORD=$(New-Secret)
CTI_DASHBOARD_PASSWORD=$(New-Secret)
CTI_DASHBOARD_BIND=127.0.0.1
CTI_DASHBOARD_PORT=8080
CTI_NETWORK_NAME=cti-self-hosted
CTI_COMPOSE_PROJECT_NAME=cti-self-hosted
CTI_N8N_API_URL=http://cti-n8n:5678/api/v1
N8N_CONTAINER=$container
CTI_MANAGED_N8N=$managed
N8N_PORT=5678
N8N_TIMEZONE=Europe/Istanbul
N8N_ENCRYPTION_KEY=$(New-Secret)
"@
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot '.env'), $environment, [Text.UTF8Encoding]::new($false))
} elseif (Select-String -LiteralPath '.env' -Pattern 'REPLACE_WITH_' -Quiet) {
    Stop-Setup '.env contains placeholder passwords. Replace them or remove .env and rerun setup.'
}

$managedN8n = -not $UseExistingN8n
if (-not $createdEnvironment) {
    $managedLine = Select-String -LiteralPath '.env' -Pattern '^CTI_MANAGED_N8N=(.*)$' | Select-Object -First 1
    $managedN8n = $managedLine -and $managedLine.Matches[0].Groups[1].Value -eq 'true'
}

$environmentValues = @{}
Get-Content -LiteralPath '.env' | ForEach-Object {
    if ($_ -match '^([^#=]+)=(.*)$') { $environmentValues[$matches[1]] = $matches[2] }
}
if ($managedN8n -and -not $environmentValues['N8N_ENCRYPTION_KEY']) {
    Stop-Setup 'N8N_ENCRYPTION_KEY is required when CTI_MANAGED_N8N=true.'
}

Write-Step 'Validating and starting CTI services'
$composeArguments = @('compose')
if ($managedN8n) { $composeArguments += @('--profile', 'managed-n8n') }
Invoke-Docker ($composeArguments + @('config', '--quiet'))
Invoke-Docker ($composeArguments + @('up', '-d', '--build'))

Write-Step 'Applying database migrations'
$postgresUser = if ($environmentValues['POSTGRES_USER']) { $environmentValues['POSTGRES_USER'] } else { 'cti_owner' }
$postgresDatabase = if ($environmentValues['POSTGRES_DB']) { $environmentValues['POSTGRES_DB'] } else { 'cti' }
$dashboardPort = if ($environmentValues['CTI_DASHBOARD_PORT']) { $environmentValues['CTI_DASHBOARD_PORT'] } else { '8080' }
$n8nPort = if ($environmentValues['N8N_PORT']) { $environmentValues['N8N_PORT'] } else { '5678' }
$currentVersionText = & docker compose exec -T cti-db psql -U $postgresUser -d $postgresDatabase -Atc 'SELECT COALESCE(max(version), 0) FROM cti.schema_versions;'
if ($LASTEXITCODE -ne 0) { Stop-Setup 'Unable to read the current CTI schema version.' }
$currentVersion = [int]($currentVersionText | Select-Object -Last 1)
Get-ChildItem -LiteralPath 'app/cti/migrations' -Filter '*.sql' | Sort-Object Name | ForEach-Object {
    if ($_.Name -match '^(\d{3})-' -and [int]$matches[1] -gt $currentVersion) {
        $migrationVersion = [int]$matches[1]
        Write-Host "Applying migration $($_.Name)..."
        Invoke-Docker @('compose', 'exec', '-T', 'cti-db', 'psql', '-U', $postgresUser, '-d', $postgresDatabase, '--file', "/opt/cti/migrations/$($_.Name)")
        $currentVersion = $migrationVersion
    }
}

Write-Step 'Waiting for the dashboard'
$dashboardReady = $false
for ($attempt = 0; $attempt -lt 45; $attempt++) {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:${dashboardPort}/health/ready" -TimeoutSec 3
        if ($response.StatusCode -eq 200) { $dashboardReady = $true; break }
    } catch { Start-Sleep -Seconds 2 }
}
if (-not $dashboardReady) {
    & docker compose ps
    Stop-Setup 'The dashboard did not become ready in time.'
}

if ($managedN8n) {
    $n8nContainer = if ($environmentValues['N8N_CONTAINER']) { $environmentValues['N8N_CONTAINER'] } else { 'cti-n8n' }
    Write-Step 'Waiting for the managed n8n service'
    $n8nReady = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        & docker exec $n8nContainer n8n --version 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $n8nReady = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $n8nReady) { Stop-Setup 'The managed n8n service did not become ready in time.' }

    if (-not $SkipWorkflowImport) {
        & docker exec $n8nContainer sh -c 'test -f /home/node/.n8n/.cti-workflows-imported-v1' 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Step 'Importing the CTI workflows in disabled state'
            & docker exec $n8nContainer rm -rf /tmp/cti-self-hosted-workflows 2>$null
            Invoke-Docker @('exec', $n8nContainer, 'mkdir', '-p', '/tmp/cti-self-hosted-workflows')
            Invoke-Docker @('cp', "$(Join-Path $PSScriptRoot 'workflows')\.", "${n8nContainer}:/tmp/cti-self-hosted-workflows")
            Invoke-Docker @('exec', $n8nContainer, 'n8n', 'import:workflow', '--separate', '--input=/tmp/cti-self-hosted-workflows')
            Invoke-Docker @('exec', $n8nContainer, 'sh', '-c', 'touch /home/node/.n8n/.cti-workflows-imported-v1 && rm -rf /tmp/cti-self-hosted-workflows')
        } else {
            Write-Host 'CTI workflows were already imported; duplicate import skipped.'
        }
    }
}

Write-Host "`nCTI Self-Hosted is running." -ForegroundColor Green
Write-Host "Dashboard: http://127.0.0.1:${dashboardPort}"
if ($managedN8n) {
    Write-Host "n8n:       http://127.0.0.1:${n8nPort}"
    Write-Host "`nCreate the first local n8n owner account, then use the protected dashboard setup page to map CTI credentials. Imported workflows remain disabled until you activate them."
} else {
    Write-Host "`nThe CTI database/dashboard are ready. Follow N8N-SETUP.md to connect and import into your existing n8n instance."
}
