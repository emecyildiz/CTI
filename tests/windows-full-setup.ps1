[CmdletBinding()]
param([switch]$Run)
$ErrorActionPreference = 'Stop'
if (-not $Run) { throw 'Use -Run for an isolated real CTI setup test on local Docker Desktop.' }
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'scripts/windows-lifecycle.ps1')
if ((& docker context show) -ne 'desktop-linux') { throw 'Local Docker Desktop is required.' }
$engine = (& docker info --format '{{.OperatingSystem}}').Trim()
if ($LASTEXITCODE -ne 0 -or $engine -ne 'Docker Desktop') { throw 'Local Docker Desktop is unavailable.' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('cti-full-setup-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$owner = $null
$passed = $false
function Check($Condition, [string]$Label) { if (-not $Condition) { throw "FAIL: $Label" }; Write-Host "PASS: $Label" }
function Port {
    $l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    try {$l.Start();return $l.LocalEndpoint.Port} finally {$l.Stop()}
}
function Setup([string[]]$Arguments) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'setup.ps1') @Arguments
    if($LASTEXITCODE -ne 0){throw 'Real setup.ps1 failed.'}
}
function Sql([string]$Query) {
    $id=(& docker ps -q --filter "label=com.docker.compose.project=$($owner.project)" --filter 'label=com.docker.compose.service=cti-db').Trim()
    $value=& docker exec $id psql -U cti_owner -d cti -Atc $Query
    if($LASTEXITCODE -ne 0){throw 'Acceptance SQL failed.'}
    return ($value -join "`n")
}
function Workflows {
    $container=$owner.project+'-n8n'
    & docker exec $container n8n export:workflow --all --output=/tmp/cti-test-workflows.json
    if($LASTEXITCODE -ne 0){throw 'Workflow export failed.'}
    $raw=& docker exec $container cat /tmp/cti-test-workflows.json
    if($LASTEXITCODE -ne 0){throw 'Workflow readback failed.'}
    $items=ConvertFrom-Json ($raw -join "`n")
    Check (@($items).Count -eq 8) 'eight workflows imported without duplicates'
    Check (@($items | Where-Object {$_.active}).Count -eq 0) 'all workflows remain disabled'
}
try {
    $files=@(& git -C $repo -c "safe.directory=$($repo.Replace('\','/'))" ls-files)
    $files+=@('manage.ps1','scripts/windows-lifecycle.ps1')
    foreach($file in ($files | Select-Object -Unique)) {
        $source=Join-Path $repo $file
        if(-not(Test-Path -LiteralPath $source -PathType Leaf)){continue}
        $target=Join-Path $root $file
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        Copy-Item -LiteralPath $source -Destination $target
    }
    $d=Port; do {$n=Port} while($n -eq $d)
    Setup @('-DashboardPort',[string]$d,'-N8nPort',[string]$n)
    $owner=Read-CtiOwner $root
    Check ((Sql 'SELECT max(version) FROM cti.schema_versions;').Trim() -eq '29') 'schema migrated to version 29'
    Check ((Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$d/health/ready").StatusCode -eq 200) 'dashboard readiness on custom port'
    Check ((Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$n/healthz").StatusCode -eq 200) 'real n8n health on custom port'
    Workflows
    [void](Sql "CREATE TABLE public.cti_setup_probe(value text); INSERT INTO public.cti_setup_probe VALUES ('preserved');")
    $envHash=(Get-FileHash -LiteralPath (Join-Path $root '.env')).Hash
    Setup @()
    Check ((Get-FileHash -LiteralPath (Join-Path $root '.env')).Hash -eq $envHash) 'reinstall preserves .env and keys'
    Workflows
    Invoke-CtiManagement $root 'Remove' $false $owner.project
    Setup @()
    Check ((Sql 'SELECT value FROM public.cti_setup_probe;').Trim() -eq 'preserved') 'real PostgreSQL data survives remove and setup'
    Check ((Get-FileHash -LiteralPath (Join-Path $root '.env')).Hash -eq $envHash) 'remove and setup preserve credentials and ports'
    Workflows
    $passed=$true
} finally {
    if(Test-Path -LiteralPath (Join-Path $root '.cti-owner.json')) {
        try {
            $owner=Read-CtiOwner $root
            Invoke-CtiManagement $root 'Purge' $false $owner.project
            $remaining=Get-CtiPlan $owner
            Check (-not($remaining.Containers.Count -or $remaining.Volumes.Count -or $remaining.Networks.Count)) 'all real acceptance Docker resources cleaned'
        } catch {$passed=$false;Write-Warning "Cleanup requires review at ${root}: $($_.Exception.Message)"}
    }
    [IO.File]::WriteAllText((Join-Path $root 'acceptance-result.json'),(@{passed=$passed;project=if($owner){$owner.project}else{$null};at_utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json))
    Write-Host "Acceptance report: $root"
}
if(-not $passed){throw 'Full setup acceptance failed.'}
