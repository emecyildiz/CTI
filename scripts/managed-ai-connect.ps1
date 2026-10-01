[CmdletBinding()]
param([string]$InstallationPath)

# Run from the trusted embedded package, never from the installed script copy.
. (Join-Path $PSScriptRoot 'windows-lifecycle.ps1')

function Invoke-CtiManagedAiConnection([string]$Root, [string]$AiKey) {
    if ($AiKey -cnotmatch '^[\x21-\x7E]{20,512}$') { throw 'Unsupported AI key format.' }
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
    $sources = @('scripts/managed-ai-connect.mjs','scripts/managed-ai-plan.mjs')
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
        $payload = @{ aiKey = $AiKey } | ConvertTo-Json -Compress
        $output = $payload | & docker exec -i $container node /tmp/managed-ai-connect.mjs 2>&1
        if ($LASTEXITCODE -ne 0 -or ($output -join "`n").Trim() -cne 'CTI_MANAGED_AI_RESULT:v1:prepared') { throw 'Managed AI preparation did not complete.' }
        Write-Output 'CTI_MANAGED_AI_RESULT:v1:prepared'
    } finally {
        $AiKey = $null; $payload = $null; $output = $null
        if ($copied) {
            # Fixed disposable helper files only. Cleanup failure must not mask the result.
            try { [void](Invoke-CtiDocker @('exec','--user','0',$container,'rm','-f','/tmp/managed-ai-connect.mjs','/tmp/managed-ai-plan.mjs')) } catch { }
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    try {
        # Bound input before parsing. Secrets never appear in process arguments or errors.
        $chars = New-Object char[] 8193
        $count = 0
        while ($count -lt $chars.Length) {
            $read = [Console]::In.Read($chars, $count, $chars.Length - $count)
            if ($read -eq 0) { break }
            $count += $read
        }
        if ($count -gt 8192) { throw 'Oversized input.' }
        $request = [string]::new($chars,0,$count) | ConvertFrom-Json
        Invoke-CtiManagedAiConnection $InstallationPath $request.aiKey
    } catch {
        [Console]::Error.WriteLine('AI connection could not be prepared. No tasks were activated. A partial update may require review before retrying; keep the installation folder.')
        exit 1
    } finally { $request = $null; if ($chars) { [Array]::Clear($chars,0,$chars.Length) } }
}
