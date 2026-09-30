# Called only for a newly created installer-owned environment, before normal import.
function Invoke-CtiFreshManagedBootstrap([string]$Root, [hashtable]$Settings) {
    $owner = Read-CtiOwner $Root
    if (-not $owner.managed_n8n -or $Settings['CTI_MANAGED_N8N'] -cne 'true' -or
        $Settings['CTI_COMPOSE_PROJECT_NAME'] -cne $owner.project -or
        $Settings['N8N_CONTAINER'] -cne "$($owner.project)-n8n" -or
        $Settings['CTI_N8N_API_URL'] -cne 'http://cti-n8n:5678/api/v1') {
        throw 'Managed setup ownership does not match. No credentials were sent.'
    }
    $plan = Get-CtiPlan $owner
    $container = "$($owner.project)-n8n"
    $target = (ConvertFrom-Json (Invoke-CtiDocker @('inspect', $container)))[0]
    if ($target.Id -cnotin $plan.Containers -or $target.Config.Labels.'com.docker.compose.service' -cne 'cti-n8n') {
        throw 'Managed n8n target is not owned by this installation. No credentials were sent.'
    }
    $data = @{ databasePassword = $Settings['CTI_APP_PASSWORD']; databaseName = $Settings['POSTGRES_DB'] }
    if ($data.databasePassword -cnotmatch '^[A-Za-z0-9_-]{43}$' -or $data.databaseName -cnotmatch '^[A-Za-z_][A-Za-z0-9_]{0,62}$') {
        throw 'Managed database settings are not supported. No credentials were sent.'
    }
    foreach ($file in @('managed-bootstrap.mjs','managed-bootstrap-plan.mjs')) {
        $source = Join-Path $Root "scripts/$file"
        if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked bootstrap script refused.' }
        [void](Invoke-CtiDocker @('cp', $source, "${container}:/tmp/$file"))
    }
    [void](Invoke-CtiDocker @('cp', (Join-Path $Root 'workflows'), "${container}:/tmp/cti-self-hosted-workflows"))
    try {
        # Secret travels on stdin, never in process arguments or terminal output.
        $payload = $data | ConvertTo-Json -Compress
        $output = $payload | & docker exec -i $container node /tmp/managed-bootstrap.mjs 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'Automatic connection could not finish. No tasks were started. Keep this installation for diagnosis; do not overwrite a partial import.' }
        Write-Host 'CTI database connection prepared automatically. No n8n API key was required. Tasks remain disabled.'
    } finally {
        $payload = $null; $output = $null; $data = $null
        # Only fixed files copied by this function; no database or user documents.
        & docker exec --user 0 $container rm -f /tmp/managed-bootstrap.mjs /tmp/managed-bootstrap-plan.mjs *> $null
    }
}
