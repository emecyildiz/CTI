[CmdletBinding()]
param(
    [switch]$UseExistingN8n,
    [switch]$SkipWorkflowImport,
    [ValidatePattern('^https://')]
    [string]$TelegramWebhookUrl,
    [ValidateRange(0, 16)]
    [int]$N8nProxyHops = 1,
    [ValidateRange(1, 65535)][int]$DashboardPort,
    [ValidateRange(1, 65535)][int]$N8nPort
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
. (Join-Path $PSScriptRoot 'scripts/windows-lifecycle.ps1')
[void](Assert-CtiDirectory $PSScriptRoot)
foreach ($name in @('.env', '.cti-owner.json', 'compose.yml')) {
    $path = Join-Path $PSScriptRoot $name
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Unsafe linked configuration file: $name" }
}

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

function Set-EnvironmentValue([string]$Key, [string]$Value) {
    $path = Join-Path $PSScriptRoot '.env'
    $lines = [Collections.Generic.List[string]](Get-Content -LiteralPath $path)
    $updated = $false
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index].StartsWith("$Key=", [StringComparison]::Ordinal)) {
            $lines[$index] = "$Key=$Value"
            $updated = $true
            for ($duplicate = $lines.Count - 1; $duplicate -gt $index; $duplicate--) {
                if ($lines[$duplicate].StartsWith("$Key=", [StringComparison]::Ordinal)) {
                    $lines.RemoveAt($duplicate)
                }
            }
            break
        }
    }
    if (-not $updated) { $lines.Add("$Key=$Value") }
    [IO.File]::WriteAllLines($path, $lines, [Text.UTF8Encoding]::new($false))
}

function ConvertTo-TelegramWebhookUrl([string]$Value) {
    # Validate the original input: System.Uri silently escapes raw newlines and
    # spaces, which must never be copied into a dotenv assignment.
    $pattern = '\Ahttps://([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(:[0-9]{1,5})?(/[A-Za-z0-9._~!()*+,;=:%/-]*)?\z'
    if ($Value -cnotmatch $pattern -or $Value -match '%([^0-9A-Fa-f]|[0-9A-Fa-f]([^0-9A-Fa-f]|$)|$)') {
        Stop-Setup 'TelegramWebhookUrl must be HTTPS with a public DNS hostname and a URL-safe path; credentials, whitespace, backslashes, quotes, dollar signs, queries, and fragments are not allowed.'
    }
    $webhookUri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$webhookUri) -or
        $webhookUri.Scheme -ne 'https' -or $webhookUri.UserInfo -or $webhookUri.Query -or
        $webhookUri.Fragment -or $webhookUri.IsLoopback -or $webhookUri.Port -lt 1 -or
        $webhookUri.Host.Length -gt 253 -or $webhookUri.HostNameType -ne [UriHostNameType]::Dns -or
        $webhookUri.Host -match '^[0-9.]+$' -or $webhookUri.Host -match '\.(localhost|local|internal)$') {
        Stop-Setup 'TelegramWebhookUrl must use a public DNS hostname and a port between 1 and 65535.'
    }
    return $Value.TrimEnd('/') + '/'
}

function Assert-TelegramManagedN8n([bool]$UseExisting, [string]$EnvironmentPath) {
    if ($UseExisting) {
        Stop-Setup 'Configure WEBHOOK_URL on the existing n8n service itself; -TelegramWebhookUrl is for managed n8n only.'
    }
    if (Test-Path -LiteralPath $EnvironmentPath) {
        $managedLine = Select-String -LiteralPath $EnvironmentPath -Pattern '^CTI_MANAGED_N8N=(.*)$' | Select-Object -First 1
        if (-not $managedLine -or $managedLine.Matches[0].Groups[1].Value -ne 'true') {
            Stop-Setup 'The existing .env does not enable managed n8n. Configure WEBHOOK_URL on the existing n8n service itself.'
        }
    }
}

function Assert-SetupEnvironment([hashtable]$Values, [bool]$ManagedN8n) {
    $passwordKeys = @('POSTGRES_PASSWORD', 'CTI_APP_PASSWORD', 'CTI_DASHBOARD_PASSWORD')
    for ($index = 0; $index -lt $passwordKeys.Count; $index++) {
        $key = $passwordKeys[$index]
        if ([string]::IsNullOrEmpty($Values[$key])) { Stop-Setup "$key is missing." }
        for ($other = 0; $other -lt $index; $other++) {
            if ($Values[$key] -ceq $Values[$passwordKeys[$other]]) {
                Stop-Setup 'Owner, n8n, and dashboard passwords must differ.'
            }
        }
    }
    $binding = $Values['CTI_DASHBOARD_BIND']
    if (-not $binding) { $binding = '127.0.0.1' }
    if ($binding -cnotin @('127.0.0.1', 'localhost')) {
        Stop-Setup 'The first public release only permits a loopback dashboard binding.'
    }
    if ($ManagedN8n -and [string]::IsNullOrEmpty($Values['N8N_ENCRYPTION_KEY'])) {
        Stop-Setup 'N8N_ENCRYPTION_KEY is required when CTI_MANAGED_N8N=true.'
    }
    $dashboard = if ($Values['CTI_DASHBOARD_PORT']) { $Values['CTI_DASHBOARD_PORT'] } else { '8080' }
    $n8n = if ($Values['N8N_PORT']) { $Values['N8N_PORT'] } else { '5678' }
    foreach ($port in @($dashboard, $n8n)) {
        if ($port -notmatch '^[0-9]{1,5}$' -or [int]$port -lt 1 -or [int]$port -gt 65535) { Stop-Setup 'Ports must be integers between 1 and 65535.' }
    }
    if ($ManagedN8n -and [int]$dashboard -eq [int]$n8n) { Stop-Setup 'Dashboard and managed n8n ports must differ.' }
}

function Assert-CtiPortAvailable([int]$Port) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    try {
        $listener.Server.ExclusiveAddressUse = $true
        $listener.Start()
    } catch { throw "Local port $Port is occupied or unavailable. Select another port; no process will be stopped automatically." }
    finally { $listener.Stop() }
}

if ($TelegramWebhookUrl) {
    $TelegramWebhookUrl = ConvertTo-TelegramWebhookUrl $TelegramWebhookUrl
    Assert-TelegramManagedN8n ([bool]$UseExistingN8n) (Join-Path $PSScriptRoot '.env')
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
    if (Test-Path -LiteralPath '.cti-owner.json') { Stop-Setup 'Ownership record exists but .env is missing. Restore the configuration or use management; do not create a new installation over retained data.' }
    $project = 'cti-' + [Guid]::NewGuid().ToString('N')
    $engine = (Invoke-CtiDocker @('info', '--format', '{{.ID}}')).Trim()
    if (-not $engine) { Stop-Setup 'Docker engine identity is unavailable.' }
    Write-Step 'Creating a private local configuration'
    $managed = if ($UseExistingN8n) { 'false' } else { 'true' }
    $container = if ($UseExistingN8n) { '' } else { "$project-n8n" }
    $environment = @"
POSTGRES_DB=cti
POSTGRES_USER=cti_owner
POSTGRES_PASSWORD=$(New-Secret)
CTI_APP_PASSWORD=$(New-Secret)
CTI_DASHBOARD_PASSWORD=$(New-Secret)
CTI_DASHBOARD_BIND=127.0.0.1
CTI_DASHBOARD_PORT=8080
CTI_NETWORK_NAME=$project
CTI_COMPOSE_PROJECT_NAME=$project
CTI_N8N_API_URL=http://cti-n8n:5678/api/v1
N8N_CONTAINER=$container
CTI_MANAGED_N8N=$managed
N8N_PORT=5678
N8N_TIMEZONE=Europe/Istanbul
N8N_ENCRYPTION_KEY=$(New-Secret)
CTI_TELEGRAM_QUERY_ENABLED=false
N8N_WEBHOOK_URL=http://localhost:5678/
N8N_PROXY_HOPS=0
"@
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot '.env'), $environment, [Text.UTF8Encoding]::new($false))
    $owner = [ordered]@{ schema = 1; product = 'CTI Self-Hosted'; root = (Assert-CtiDirectory $PSScriptRoot); project = $project; engine = $engine; managed_n8n = (-not [bool]$UseExistingN8n) }
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot '.cti-owner.json'), ($owner | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
} elseif (Select-String -LiteralPath '.env' -Pattern 'REPLACE_WITH_' -Quiet) {
    Stop-Setup '.env contains placeholder passwords. Replace them or remove .env and rerun setup.'
}

if ($TelegramWebhookUrl) {
    Set-EnvironmentValue 'CTI_TELEGRAM_QUERY_ENABLED' 'true'
    Set-EnvironmentValue 'N8N_WEBHOOK_URL' $TelegramWebhookUrl
    Set-EnvironmentValue 'N8N_PROXY_HOPS' $N8nProxyHops.ToString([Globalization.CultureInfo]::InvariantCulture)
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
$oldDashboardPort = if ($environmentValues['CTI_DASHBOARD_PORT']) { $environmentValues['CTI_DASHBOARD_PORT'] } else { '8080' }
$oldN8nPort = if ($environmentValues['N8N_PORT']) { $environmentValues['N8N_PORT'] } else { '5678' }
if ($PSBoundParameters.ContainsKey('N8nPort') -and -not $managedN8n) { Stop-Setup 'N8nPort only configures managed n8n; the existing n8n service is not modified.' }
if ($PSBoundParameters.ContainsKey('DashboardPort')) { $environmentValues['CTI_DASHBOARD_PORT'] = [string]$DashboardPort }
if ($PSBoundParameters.ContainsKey('N8nPort')) { $environmentValues['N8N_PORT'] = [string]$N8nPort }
Assert-SetupEnvironment $environmentValues $managedN8n
$selectedDashboard = [int]$environmentValues['CTI_DASHBOARD_PORT']
$selectedN8n = [int]$environmentValues['N8N_PORT']
if (-not $selectedDashboard) { $selectedDashboard = 8080 }
if (-not $selectedN8n) { $selectedN8n = 5678 }
if ($createdEnvironment -or $selectedDashboard -ne [int]$oldDashboardPort) { Assert-CtiPortAvailable $selectedDashboard }
if ($managedN8n -and ($createdEnvironment -or $selectedN8n -ne [int]$oldN8nPort)) { Assert-CtiPortAvailable $selectedN8n }
if (Test-Path -LiteralPath '.cti-owner.json') {
    $owner = Read-CtiOwner $PSScriptRoot
    if ($environmentValues['CTI_COMPOSE_PROJECT_NAME'] -cne $owner.project -or
        $environmentValues['CTI_NETWORK_NAME'] -cne $owner.project -or $managedN8n -ne $owner.managed_n8n -or
        ($managedN8n -and $environmentValues['N8N_CONTAINER'] -cne "$($owner.project)-n8n")) { Stop-Setup 'Configuration differs from recorded installation ownership.' }
    [void](Get-CtiPlan $owner)
}
if ($PSBoundParameters.ContainsKey('DashboardPort')) { Set-EnvironmentValue 'CTI_DASHBOARD_PORT' ([string]$selectedDashboard) }
if ($PSBoundParameters.ContainsKey('N8nPort')) {
    Set-EnvironmentValue 'N8N_PORT' ([string]$selectedN8n)
    if ($environmentValues['CTI_TELEGRAM_QUERY_ENABLED'] -ne 'true' -and $environmentValues['N8N_WEBHOOK_URL'] -match '^http://(localhost|127\.0\.0\.1):[0-9]+/$') {
        Set-EnvironmentValue 'N8N_WEBHOOK_URL' "http://localhost:${selectedN8n}/"
        $environmentValues['N8N_WEBHOOK_URL'] = "http://localhost:${selectedN8n}/"
    }
}

Write-Step 'Validating and starting CTI services'
# Pin Compose inputs; inherited COMPOSE_FILE/profiles must not redirect setup.
$env:COMPOSE_FILE = Join-Path $PSScriptRoot 'compose.yml'
$env:COMPOSE_PROFILES = if ($managedN8n) { 'managed-n8n' } else { '' }
foreach ($key in $environmentValues.Keys) {
    if ($key -cmatch '^(CTI_|N8N_|POSTGRES_)[A-Z0-9_]+$') { [Environment]::SetEnvironmentVariable($key, [string]$environmentValues[$key], 'Process') }
}
$env:COMPOSE_PROJECT_NAME = $environmentValues['CTI_COMPOSE_PROJECT_NAME']
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
$queryEnabled = $environmentValues['CTI_TELEGRAM_QUERY_ENABLED'] -eq 'true' -or [bool]$TelegramWebhookUrl
if ($queryEnabled) {
    $configuredWebhook = if ($TelegramWebhookUrl) { $TelegramWebhookUrl } else { $environmentValues['N8N_WEBHOOK_URL'] }
    Write-Host "Interactive Telegram query: configured for $configuredWebhook" -ForegroundColor Yellow
    Write-Host 'The HTTPS route must reach n8n before you activate CTI Telegram Query.'
} else {
    Write-Host 'Interactive Telegram query: disabled. Outbound reports and alerts can still be configured.'
}
