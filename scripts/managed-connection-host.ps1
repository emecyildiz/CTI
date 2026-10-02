# Run from the trusted embedded package, never from the installed script copy.
. (Join-Path $PSScriptRoot 'windows-lifecycle.ps1')

function Invoke-CtiManagedConnection([string]$Root, [ValidateSet('Ai','Telegram','Audit')][string]$Mode, [hashtable]$Configuration) {
    if ($Mode -ceq 'Ai') {
        if ($Configuration.aiKey -isnot [string] -or $Configuration.aiKey -cnotmatch '\A[\x21-\x7E]{20,512}\z') { throw 'Unsupported AI key format.' }
        $sources = @('scripts/managed-ai-connect.mjs','scripts/managed-ai-plan.mjs')
        $marker = 'CTI_MANAGED_AI_RESULT:v1:prepared'
    } elseif ($Mode -ceq 'Telegram') {
        if ($Configuration.botToken -isnot [string] -or $Configuration.botToken -cnotmatch '\A[0-9]{5,20}:[A-Za-z0-9_-]{20,128}\z' -or
            $Configuration.chatId -isnot [string] -or $Configuration.chatId -cnotmatch '\A[0-9]{5,20}\z') { throw 'Unsupported private Telegram configuration.' }
        $sources = @('scripts/managed-telegram-connect.mjs','scripts/managed-telegram-plan.mjs')
        $marker = 'CTI_MANAGED_TELEGRAM_RESULT:v1:prepared'
    } else {
        if ($Configuration.includeAi -isnot [bool] -or $Configuration.includeTelegram -isnot [bool]) { throw 'Unsupported audit options.' }
        $sources = @('scripts/managed-readiness.mjs','scripts/managed-readiness-plan.mjs','scripts/managed-telegram-plan.mjs')
        $marker = 'CTI_MANAGED_AUDIT_RESULT:v1:'
    }
    $owner = Read-CtiOwner $Root
    $envPath = Join-Path $owner.root '.env'
    $item = Get-Item -LiteralPath $envPath -Force
    if ($item.PSIsContainer -or $item.Length -gt 65536 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe configuration file.' }
    $settings = @{}
    $selected = @('CTI_MANAGED_N8N','CTI_COMPOSE_PROJECT_NAME','N8N_CONTAINER','CTI_N8N_API_URL')
    foreach ($line in [IO.File]::ReadAllLines($envPath)) {
        if ($line -match '^([^=]+)=(.*)$' -and $Matches[1] -cin $selected) {
            if ($settings.ContainsKey($Matches[1])) { throw 'Duplicate managed connection setting.' }
            $settings[$Matches[1]] = $Matches[2]
        }
    }
    if (-not $owner.managed_n8n -or $settings['CTI_MANAGED_N8N'] -cne 'true' -or
        $settings['CTI_COMPOSE_PROJECT_NAME'] -cne $owner.project -or
        $settings['N8N_CONTAINER'] -cne "$($owner.project)-n8n" -or
        $settings['CTI_N8N_API_URL'] -cne 'http://cti-n8n:5678/api/v1') { throw 'Managed connection ownership mismatch.' }
    $plan = Get-CtiPlan $owner
    $container = "$($owner.project)-n8n"
    $target = (ConvertFrom-Json (Invoke-CtiDocker @('inspect', $container)))[0]
    $image = 'docker.n8n.io/n8nio/n8n:2.30.5@sha256:450853cd21a2ce36587c4c860eb26927c1ceba9496bf55f4c213b5d3a6dc8c6f'
    if ($target.Id -cnotin $plan.Containers -or $target.Config.Labels.'com.docker.compose.service' -cne 'cti-n8n' -or
        $target.Config.Image -cne $image -or -not $target.State.Running) { throw 'Unsupported or unowned managed service.' }
    $package = Assert-CtiDirectory (Split-Path -Parent $PSScriptRoot)
    $workflowRoot = Assert-CtiDirectory (Join-Path $package 'workflows')
    $workflows = @(Get-ChildItem -LiteralPath $workflowRoot -Force)
    if ($workflows.Count -ne 8) { throw 'Unexpected reviewed workflow package.' }
    foreach ($file in $workflows) {
        if ($file.PSIsContainer -or $file.Extension -cne '.json' -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe reviewed workflow file.' }
    }
    foreach ($relative in $sources) {
        $path = Join-Path $package $relative
        [void](Assert-CtiDirectory (Split-Path -Parent $path))
        $source = Get-Item -LiteralPath $path -Force
        if ($source.PSIsContainer -or ($source.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe connection helper.' }
    }
    $copied = $false
    try {
        $copied = $true
        foreach ($relative in $sources) {
            $name = Split-Path -Leaf $relative
            [void](Invoke-CtiDocker @('cp', (Join-Path $package $relative), "${container}:/tmp/$name"))
        }
        # Remove only this fixed scratch template directory, not n8n storage.
        [void](Invoke-CtiDocker @('exec','--user','0',$container,'rm','-rf','/tmp/cti-self-hosted-workflows'))
        [void](Invoke-CtiDocker @('cp', $workflowRoot, "${container}:/tmp/cti-self-hosted-workflows"))
        $payload = $Configuration | ConvertTo-Json -Compress
        $entry = '/tmp/' + (Split-Path -Leaf $sources[0])
        $output = $payload | & docker exec -i $container node $entry 2>&1
        $result = ($output -join "`n").Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Managed connection operation did not complete.' }
        if ($Mode -ceq 'Audit') {
            if ($result.Length -gt 1024 -or -not $result.StartsWith($marker,[StringComparison]::Ordinal)) { throw 'Unsupported audit response.' }
            $audit = $result.Substring($marker.Length) | ConvertFrom-Json
            $names = @($audit.PSObject.Properties.Name | Sort-Object)
            if (($names -join ',') -cne 'ai,database,drafts,errorRouting,ready,schema,telegram' -or $audit.schema -isnot [int] -or $audit.schema -ne 1) { throw 'Unsupported audit schema.' }
            foreach ($name in @('database','drafts','errorRouting','ready')) { if ($audit.$name -isnot [bool]) { throw 'Unsupported audit status.' } }
            foreach ($name in @('ai','telegram')) {
                $requested = if ($name -ceq 'ai') { $Configuration.includeAi } else { $Configuration.includeTelegram }
                if (($requested -and $audit.$name -isnot [bool]) -or (-not $requested -and $null -ne $audit.$name)) { throw 'Unsupported optional audit status.' }
            }
            $expected = $audit.database -and $audit.drafts -and $audit.errorRouting -and $audit.ai -ne $false -and $audit.telegram -ne $false
            if ($audit.ready -ne $expected) { throw 'Inconsistent audit status.' }
            Write-Output $result
        } else {
            if ($result -cne $marker) { throw 'Managed connection preparation did not complete.' }
            Write-Output $marker
        }
    } finally {
        $Configuration = $null; $payload = $null; $output = $null; $result = $null; $audit = $null
        if ($copied) {
            # Fixed disposable helper files only. Cleanup failure must not mask the result.
            $cleanup = @('exec','--user','0',$container,'rm','-f') + @($sources | ForEach-Object { '/tmp/' + (Split-Path -Leaf $_) })
            try { [void](Invoke-CtiDocker $cleanup) } catch { }
        }
    }
}

function Read-CtiConnectionRequest {
    $chars = New-Object char[] 8193
    try {
        $count = 0
        while ($count -lt $chars.Length) {
            $read = [Console]::In.Read($chars, $count, $chars.Length - $count)
            if ($read -eq 0) { break }
            $count += $read
        }
        if ($count -gt 8192) { throw 'Oversized input.' }
        return ([string]::new($chars,0,$count) | ConvertFrom-Json)
    } finally { [Array]::Clear($chars,0,$chars.Length) }
}
