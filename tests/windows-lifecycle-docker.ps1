# Opt-in local Docker Desktop acceptance. Only synthetic Node/Alpine fixture data is used.
[CmdletBinding()]
param([switch]$Run)
$ErrorActionPreference = 'Stop'
if (-not $Run) { throw 'Pass -Run to create and remove isolated synthetic Docker Desktop fixtures.' }
$repository = Split-Path -Parent $PSScriptRoot
. (Join-Path $repository 'scripts/windows-lifecycle.ps1')

function Invoke-FixtureDocker([string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $result = & docker.exe @Arguments 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "Fixture Docker command failed ($($Arguments -join ' ')): $($result -join ' ')" }
    return ($result -join "`n")
}
function Check($Condition, [string]$Message) { if (-not $Condition) { throw "FAIL: $Message" }; Write-Host "PASS: $Message" }
function Free-Port {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
$context = (Invoke-FixtureDocker @('context', 'show')).Trim()
$info = ConvertFrom-Json (Invoke-FixtureDocker @('info', '--format', '{{json .}}'))
if ($context -cne 'desktop-linux' -or $info.OperatingSystem -cne 'Docker Desktop') { throw 'This test only runs on the local Docker Desktop Linux context.' }
$image = (Invoke-FixtureDocker @('image', 'inspect', 'node:24-alpine', '--format', '{{.Id}}')).Trim()
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('cti-lifecycle-docker-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($runRoot)
$fixtures = [Collections.Generic.List[object]]::new()
$foreignId = $null
$foreignLabel = [Guid]::NewGuid().ToString('N')
$before = @{}
Get-ChildItem Env: | ForEach-Object { $before[$_.Name] = $_.Value }
$success = $false
function New-Fixture([string]$Name) {
    $root = Join-Path $runRoot $Name
    [void][IO.Directory]::CreateDirectory($root)
    $project = 'cti-' + [Guid]::NewGuid().ToString('N')
    $owner = [pscustomobject]@{ schema=1; product='CTI Self-Hosted'; root=(Assert-CtiDirectory $root); project=$project; engine=$info.ID; managed_n8n=$true }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures/windows-lifecycle.compose.yml') -Destination (Join-Path $root 'compose.yml')
    [IO.File]::WriteAllText((Join-Path $root '.cti-owner.json'), ($owner | ConvertTo-Json))
    [IO.File]::WriteAllText((Join-Path $root '.env'), 'TEST_ONLY=no_credentials')
    [IO.File]::WriteAllText((Join-Path $root 'keep-backup.dump'), 'synthetic backup must survive')
    $hash = (Get-FileHash -LiteralPath (Join-Path $root 'compose.yml')).Hash
    [IO.File]::WriteAllText((Join-Path $root '.cti-package-files.json'), (ConvertTo-Json -InputObject @(@{path='compose.yml';sha256=$hash})))
    $d = Free-Port
    do { $n = Free-Port } while ($n -eq $d)
    $fixture = [pscustomobject]@{ Root=$root; Owner=$owner; Dashboard=$d; N8n=$n }
    $fixtures.Add($fixture)
    return $fixture
}
function Compose($Fixture, [string[]]$Command) {
    $env:CTI_TEST_IMAGE = $image
    $env:CTI_TEST_PROJECT = $Fixture.Owner.project
    $env:CTI_TEST_DASHBOARD_PORT = [string]$Fixture.Dashboard
    $env:CTI_TEST_N8N_PORT = [string]$Fixture.N8n
    $env:COMPOSE_FILE = ''
    $env:COMPOSE_PROFILES = ''
    [void](Invoke-FixtureDocker (@('compose','--project-directory',$Fixture.Root,'--env-file',(Join-Path $Fixture.Root '.env'),'-f',(Join-Path $Fixture.Root 'compose.yml'),'-p',$Fixture.Owner.project) + $Command))
}
function Service-Id($Fixture, [string]$Service) {
    return (Invoke-FixtureDocker @('ps','-aq','--filter',"label=com.docker.compose.project=$($Fixture.Owner.project)",'--filter',"label=com.docker.compose.service=$Service")).Trim()
}
function Assert-Data($Fixture) {
    foreach ($service in @('cti-db','cti-n8n')) {
        $value = (Invoke-FixtureDocker @('exec',(Service-Id $Fixture $service),'cat','/data/probe')).Trim()
        Check ($value -ceq 'synthetic-cti-data') "$service data retained"
    }
}
try {
    $main = New-Fixture 'main'
    Compose $main @('up','-d')
    $sentinel = New-Fixture 'sentinel'
    Compose $sentinel @('up','-d')
    $sentinelIds = @((Get-CtiPlan $sentinel.Owner).Containers)
    $plan = Get-CtiPlan $main.Owner
    Check ($plan.Containers.Count -eq 3 -and $plan.Volumes.Count -eq 2 -and $plan.Networks.Count -eq 1) 'real Docker ownership labels and Windows working directory accepted'
    foreach ($service in @('cti-db','cti-n8n')) { [void](Invoke-FixtureDocker @('exec',(Service-Id $main $service),'sh','-c','printf synthetic-cti-data > /data/probe')) }
    foreach ($port in @($main.Dashboard, $main.N8n)) {
        $connected = $false
        for ($attempt = 0; $attempt -lt 15 -and -not $connected; $attempt++) {
            $client = [Net.Sockets.TcpClient]::new()
            try { $client.Connect('127.0.0.1', $port); $connected = $client.Connected } catch { Start-Sleep -Milliseconds 200 }
            finally { $client.Dispose() }
        }
        if (-not $connected) {
            foreach ($service in @('cti-dashboard','cti-n8n')) {
                $id = Service-Id $main $service
                Write-Host (Invoke-FixtureDocker @('inspect','--format','{{json .NetworkSettings.Ports}}',$id))
                Write-Host (Invoke-FixtureDocker @('logs','--tail','10',$id))
            }
        }
        Check $connected "selected loopback port $port reachable"
    }
    Invoke-CtiManagement $main.Root 'Purge' $true ''
    Assert-Data $main
    Invoke-CtiManagement $main.Root 'Stop' $false $main.Owner.project
    foreach ($id in $plan.Containers) { Check ((Invoke-FixtureDocker @('inspect','--format','{{.State.Running}}',$id)).Trim() -eq 'false') 'stop stopped an owned container' }
    Compose $main @('up','-d')
    Assert-Data $main

    $foreignId = (Invoke-FixtureDocker @('run','-d','--network','none','--label',"io.emecworks.cti-test=$foreignLabel",'--mount',"type=volume,src=$($main.Owner.project)_cti_pgdata,dst=/data",$image,'sleep','infinity')).Trim()
    $rejected = $false
    try { Invoke-CtiManagement $main.Root 'Purge' $false $main.Owner.project } catch { $rejected = $_.Exception.Message -like '*another container*' }
    Check $rejected 'shared volume rejected before mutation'
    Assert-Data $main
    [void](Invoke-FixtureDocker @('rm','-f',$foreignId)); $foreignId = $null

    [void](Invoke-FixtureDocker @('network','connect',$main.Owner.project,$sentinelIds[0]))
    $rejected = $false
    try { Invoke-CtiManagement $main.Root 'Remove' $false $main.Owner.project } catch { $rejected = $_.Exception.Message -like '*Another container*' }
    Check $rejected 'shared network rejected before mutation'
    [void](Invoke-FixtureDocker @('network','disconnect',$main.Owner.project,$sentinelIds[0]))

    Invoke-CtiManagement $main.Root 'Remove' $false $main.Owner.project
    $afterRemove = Get-CtiPlan $main.Owner
    Check ($afterRemove.Containers.Count -eq 0 -and $afterRemove.Volumes.Count -eq 2 -and $afterRemove.Networks.Count -eq 0) 'remove retained both data volumes'
    Compose $main @('up','-d')
    Assert-Data $main
    Invoke-CtiManagement $main.Root 'Purge' $false $main.Owner.project
    $afterPurge = Get-CtiPlan $main.Owner
    Check ($afterPurge.Containers.Count -eq 0 -and $afterPurge.Volumes.Count -eq 0 -and $afterPurge.Networks.Count -eq 0) 'purge removed only target resources'
    Check (-not (Test-Path -LiteralPath (Join-Path $main.Root '.env'))) 'purge removed fixture configuration'
    Check (-not (Test-Path -LiteralPath (Join-Path $main.Root 'compose.yml'))) 'purge removed unchanged package file'
    Check (([IO.File]::ReadAllText((Join-Path $main.Root 'keep-backup.dump'))) -ceq 'synthetic backup must survive') 'backup file preserved'
    Check ((Get-CtiPlan $sentinel.Owner).Containers.Count -eq 3) 'separate sentinel project preserved'
    foreach ($id in $sentinelIds) { Check ((Invoke-FixtureDocker @('inspect','--format','{{.State.Running}}',$id)).Trim() -eq 'true') 'sentinel container still running' }
    $success = $true
} finally {
    if ($foreignId) {
        $labels = ConvertFrom-Json (Invoke-FixtureDocker @('inspect','--format','{{json .Config.Labels}}',$foreignId))
        if ($labels.'io.emecworks.cti-test' -ceq $foreignLabel) { [void](Invoke-FixtureDocker @('rm','-f',$foreignId)) }
    }
    foreach ($f in $fixtures) {
        # Only this run's recorded roots/projects are candidates; do not prune unrelated resources.
        try {
            if (Test-Path -LiteralPath (Join-Path $f.Root '.cti-owner.json')) { Invoke-CtiManagement $f.Root 'Purge' $false $f.Owner.project }
            $remaining = Get-CtiPlan $f.Owner
            if ($remaining.Containers.Count -or $remaining.Volumes.Count -or $remaining.Networks.Count) { throw 'Fixture resources remain.' }
        } catch { $success = $false; Write-Warning "Cleanup requires review for $($f.Owner.project) at $($f.Root): $($_.Exception.Message)" }
    }
    foreach ($item in @(Get-ChildItem Env:)) { if (-not $before.ContainsKey($item.Name)) { [Environment]::SetEnvironmentVariable($item.Name, $null, 'Process') } }
    foreach ($key in $before.Keys) { [Environment]::SetEnvironmentVariable($key, $before[$key], 'Process') }
    $report = @{ passed=$success; at_utc=[DateTime]::UtcNow.ToString('o'); engine=$info.ID; context=$context; projects=@($fixtures | ForEach-Object { $_.Owner.project }); scope='Synthetic Node/Alpine Docker lifecycle only; not full CTI database/n8n or GUI acceptance.' }
    [IO.File]::WriteAllText((Join-Path $runRoot 'result.json'), ($report | ConvertTo-Json -Depth 4))
    Write-Host "Result: $runRoot"
}
if (-not $success) { throw 'Real Docker lifecycle acceptance did not pass or cleanup was incomplete.' }
Write-Host 'PASS: isolated real Docker lifecycle and cleanup. No production CTI, database restore or GUI acceptance is claimed.'
