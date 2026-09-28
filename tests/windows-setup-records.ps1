#Requires -Version 7.0
[CmdletBinding()]
param([switch]$Run)
$ErrorActionPreference = 'Stop'
if (-not $Run) { throw 'Use -Run for isolated Docker PostgreSQL and local dashboard acceptance.' }
$repo = Split-Path -Parent $PSScriptRoot
if ((& docker info --format '{{.OperatingSystem}}') -ne 'Docker Desktop') { throw 'Local Docker Desktop required.' }
$name = 'cti-records-test-' + [Guid]::NewGuid().ToString('N')
$password = [Guid]::NewGuid().ToString('N')
$process = $null
$client = $null
$created = $false
function Check($ok, [string]$label) { if (-not $ok) { throw "FAIL: $label" }; Write-Host "PASS: $label" }
function Port {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
function Sql([string]$query) {
    $result = & docker exec $name psql -U cti_owner -d cti -Atc $query
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance SQL failed.' }
    return ($result -join "`n").Trim()
}
try {
    $dbPort = Port
    $webPort = Port
    $absentPort = Port
    Check (@($dbPort,$webPort,$absentPort | Select-Object -Unique).Count -eq 3) 'distinct loopback ports'
    & dotnet build (Join-Path $repo 'app/cti-dashboard/CtiDashboard.csproj') --no-restore --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard build failed.' }
    & docker run -d --name $name --label 'cti.acceptance=setup-records' --tmpfs /var/lib/postgresql/data `
        -p "127.0.0.1:${dbPort}:5432" -e POSTGRES_USER=cti_owner -e POSTGRES_DB=cti -e "POSTGRES_PASSWORD=$password" `
        postgres:16-alpine@sha256:57c72fd2a128e416c7fcc499958864df5301e940bca0a56f58fddf30ffc07777 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Test database start failed.' }
    $created = $true
    $ready = $false
    for ($i=0; $i -lt 40; $i++) {
        & docker exec $name pg_isready -h 127.0.0.1 -U cti_owner -d cti *> $null
        if ($LASTEXITCODE -eq 0) { $ready=$true; break }
        Start-Sleep -Milliseconds 500
    }
    Check $ready 'isolated PostgreSQL ready'
    & docker cp (Join-Path $repo 'app/cti') "${name}:/tmp/cti" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Test schema copy failed.' }
    & docker exec $name psql -U cti_owner -d cti -v ON_ERROR_STOP=1 -v "cti_app_password=$password" -v "cti_dashboard_password=$password" -f /tmp/cti/schema.sql *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Test schema failed.' }
    $schemaVersion = [int](Sql 'SELECT COALESCE(max(version),0) FROM cti.schema_versions;')
    foreach ($file in (Get-ChildItem (Join-Path $repo 'app/cti/migrations') -Filter '*.sql' | Sort-Object Name)) {
        if ([int]$file.Name.Split('-')[0] -le $schemaVersion) { continue }
        & docker exec $name psql -U cti_owner -d cti -v ON_ERROR_STOP=1 -f "/tmp/cti/migrations/$($file.Name)" *> $null
        if ($LASTEXITCODE -ne 0) { throw "Migration failed: $($file.Name)" }
    }
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add((Join-Path $repo 'app/cti-dashboard/bin/Debug/net8.0/CtiDashboard.dll'))
    $start.WorkingDirectory = Join-Path $repo 'app/cti-dashboard'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$webPort"
    $start.Environment['CTI_AUTH_MODE'] = 'local'
    $start.Environment['ConnectionStrings__Cti'] = "Host=127.0.0.1;Port=$dbPort;Database=cti;Username=cti_dashboard;Password=$password"
    $start.Environment['CTI_N8N_API_URL'] = "http://127.0.0.1:$absentPort/api/v1"
    $process = [Diagnostics.Process]::Start($start)
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseProxy = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    $base = "http://127.0.0.1:$webPort"
    $ready=$false
    for ($i=0; $i -lt 40; $i++) {
        try { $r=$client.GetAsync("$base/health/ready").GetAwaiter().GetResult(); $ready=$r.IsSuccessStatusCode; $r.Dispose() } catch { }
        if ($ready) { break }; Start-Sleep -Milliseconds 500
    }
    Check $ready 'real dashboard database readiness'
    $page=$client.GetAsync("$base/setup").GetAwaiter().GetResult()
    $html=$page.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Check ($page.IsSuccessStatusCode -and $html.Contains('Connect n8n first')) 'setup renders incomplete n8n guidance'
    Check (($page.Headers.GetValues('Referrer-Policy') -join '') -eq 'same-origin') 'setup referrer policy'
    $token=[regex]::Match($html,'name="_csrf" value="([^"]+)"').Groups[1].Value
    Check ($token.Length -eq 43) 'CSRF form and session established'
    $page.Dispose()
    function Post([string]$path, [string]$body, [string]$origin=$base) {
        $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,"$base$path")
        [void]$request.Headers.TryAddWithoutValidation('Origin',$origin)
        $request.Content=[Net.Http.StringContent]::new($body,[Text.Encoding]::UTF8,'application/x-www-form-urlencoded')
        try { return $client.SendAsync($request).GetAwaiter().GetResult() } finally { $request.Dispose() }
    }
    $body="_csrf=$token&provider_type=google_gemini&model_identifier=acceptance-test-model"
    $r=Post '/setup/ai-profile' $body
    Check ([int]$r.StatusCode -eq 302) 'AI profile saves without 403'; $r.Dispose()
    Check ((Sql 'SELECT model_identifier FROM cti.dashboard_ai_provider_status;') -eq 'acceptance-test-model') 'AI profile persisted in real PostgreSQL'
    foreach ($origin in @('null','http://example.invalid')) {
        $r=Post '/setup/ai-profile' $body $origin
        Check ([int]$r.StatusCode -eq 403) "invalid Origin rejected: $origin"; $r.Dispose()
    }
    $r=Post '/setup/ai-profile' '_csrf=invalid&provider_type=google_gemini&model_identifier=must-not-save'
    Check ([int]$r.StatusCode -eq 403) 'invalid token rejected'; $r.Dispose()
    Check ((Sql 'SELECT model_identifier FROM cti.dashboard_ai_provider_status;') -eq 'acceptance-test-model') 'rejected requests do not change profile'
    $sources=(Sql 'SELECT name FROM cti.dashboard_source_options WHERE selectable ORDER BY name LIMIT 2;') -split "`n"
    $sourceBody="_csrf=$token" + (($sources | ForEach-Object { '&source='+[Uri]::EscapeDataString($_.Trim()) }) -join '')
    $r=Post '/setup/sources' $sourceBody
    Check ([int]$r.StatusCode -eq 302) 'source selection saves without 403'; $r.Dispose()
    Check ((Sql 'SELECT count(*) FROM cti.dashboard_source_options WHERE enabled;') -eq '2') 'two selected sources persisted'
    $r=Post '/setup/sources' "_csrf=$token&source=unknown-source"
    Check ([int]$r.StatusCode -eq 400) 'unknown source rejected'; $r.Dispose()
    Check ((Sql 'SELECT count(*) FROM cti.dashboard_source_options WHERE enabled;') -eq '2') 'rejected source selection preserves state'
    $r=Post '/setup/readiness' "_csrf=$token&n8n_api_key=synthetic-acceptance-key-only&include_ai=true"
    Check ([int]$r.StatusCode -eq 503) 'unavailable n8n is not reported ready'; $r.Dispose()
    Write-Host 'PASS: real setup records acceptance; no real provider calls or workflow activation.'
} finally {
    if ($client) { $client.Dispose() }
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if ($created) {
        & docker rm -f -v $name | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Cleanup failed for isolated test container $name" }
        Write-Host 'PASS: isolated database container removed; tmpfs data discarded.'
    }
}
