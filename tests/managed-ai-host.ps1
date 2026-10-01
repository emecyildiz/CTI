# Offline host bridge acceptance; no real Docker or operator credentials.
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$script:root = Join-Path ([IO.Path]::GetTempPath()) ('cti-ai-host-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($script:root)
$package = Join-Path $script:root 'trusted-package'
[void][IO.Directory]::CreateDirectory((Join-Path $package 'scripts'))
[void][IO.Directory]::CreateDirectory((Join-Path $package 'workflows'))
foreach ($file in @('windows-lifecycle.ps1','managed-ai-connect.ps1','managed-ai-connect.mjs','managed-ai-plan.mjs')) {
    [IO.File]::WriteAllBytes((Join-Path $package "scripts/$file"), [IO.File]::ReadAllBytes((Join-Path $repository "scripts/$file")))
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repository 'workflows') -Filter '*.json') {
    [IO.File]::WriteAllBytes((Join-Path $package "workflows/$($file.Name)"),[IO.File]::ReadAllBytes($file.FullName))
}
. (Join-Path $package 'scripts/managed-ai-connect.ps1')
$script:project = 'cti-' + ('a' * 32)
$script:scenario = ''
$script:changes = @()
$script:sent = 0
$script:key = 'synthetic-provider-key-not-real'
$script:passed = 0
function Check($Condition, [string]$Label) { if (-not $Condition) { throw "FAIL: $Label" }; $script:passed++ }
function Owner([bool]$Managed = $true) {
    [IO.File]::WriteAllText((Join-Path $script:root '.cti-owner.json'), (@{schema=1;product='CTI Self-Hosted';root=$script:root;project=$script:project;engine='fixture-engine';managed_n8n=$Managed} | ConvertTo-Json))
}
function Settings([string]$Extra = '') {
    [IO.File]::WriteAllText((Join-Path $script:root '.env'), "CTI_MANAGED_N8N=true`nCTI_COMPOSE_PROJECT_NAME=$script:project`nN8N_CONTAINER=$script:project-n8n`nCTI_N8N_API_URL=http://cti-n8n:5678/api/v1`n$Extra")
}
function Invoke-CtiDocker([string[]]$Arguments) {
    if ($Arguments[0] -eq 'info') { if ($script:scenario -eq 'wrong-engine') { return 'foreign-engine' }; return 'fixture-engine' }
    if ($Arguments[0] -eq 'ps') { return 'owned-id' }
    if ($Arguments[0] -in @('volume','network')) { return '' }
    if ($Arguments[0] -eq 'inspect') {
        $image = 'docker.n8n.io/n8nio/n8n:2.30.5@sha256:450853cd21a2ce36587c4c860eb26927c1ceba9496bf55f4c213b5d3a6dc8c6f'
        if ($script:scenario -eq 'wrong-image') { $image = 'untrusted:latest' }
        $service = if ($script:scenario -eq 'wrong-service') { 'foreign' } else { 'cti-n8n' }
        $root = if ($script:scenario -eq 'wrong-root') { 'C:/foreign' } else { $script:root }
        $id = if ($script:scenario -eq 'wrong-target' -and $Arguments[1] -eq "$script:project-n8n") { 'foreign-id' } else { 'owned-id' }
        return (ConvertTo-Json -InputObject @(@{Id=$id;State=@{Running=($script:scenario -ne 'stopped')};Config=@{Image=$image;Labels=@{'com.docker.compose.project'=$script:project;'com.docker.compose.service'=$service;'com.docker.compose.project.working_dir'=$root}}}) -Depth 7 -Compress)
    }
    $script:changes += ,$Arguments
    if ($script:scenario -eq 'copy-failed' -and $Arguments[0] -eq 'cp') { throw 'Synthetic copy failure' }
    return ''
}
function docker {
    $script:sent++
    Check (($args -join ' ') -notmatch [regex]::Escape($script:key)) 'key absent from command arguments'
    Check (($input -join "`n" | ConvertFrom-Json).aiKey -ceq $script:key) 'key transferred on stdin only'
    $global:LASTEXITCODE = 0
    if ($script:scenario -eq 'native-failed') { $global:LASTEXITCODE = 1; return $script:key }
    if ($script:scenario -eq 'malformed-result') { return 'unexpected native output' }
    return 'CTI_MANAGED_AI_RESULT:v1:prepared'
}
function Reject([scriptblock]$Action, [string]$Label, [bool]$BeforeCopy = $true) {
    $script:changes = @(); $script:sent = 0
    $caught = $false
    try { & $Action | Out-Null } catch {
        $caught = $true
        Check (-not $_.Exception.Message.Contains($script:key)) 'failure hides key'
        if (-not $BeforeCopy -and $script:changes.Count -eq 0) { throw "Fixture unexpectedly failed before copying: $($_.Exception.Message)" }
    }
    Check $caught "$Label refused"
    if ($BeforeCopy) { Check ($script:changes.Count -eq 0 -and $script:sent -eq 0) "$Label sends nothing" }
}
try {
    Reject { Invoke-CtiManagedAiConnection $script:root $script:key } 'missing owner'
    Owner; Settings
    foreach ($case in @('wrong-engine','wrong-image','wrong-service','wrong-root','wrong-target','stopped')) {
        $script:scenario=$case
        Reject { Invoke-CtiManagedAiConnection $script:root $script:key } $case
    }
    $script:scenario=''; Owner $false
    Reject { Invoke-CtiManagedAiConnection $script:root $script:key } 'external service'
    Owner; Settings 'CTI_MANAGED_N8N=true'
    Reject { Invoke-CtiManagedAiConnection $script:root $script:key } 'duplicate setting'
    Settings
    foreach ($bad in @('', 'short', 'spaces are not accepted here')) { Reject { Invoke-CtiManagedAiConnection $script:root $bad } 'invalid key' }
    foreach ($case in @('copy-failed','native-failed','malformed-result')) {
        $script:scenario=$case
        Reject { Invoke-CtiManagedAiConnection $script:root $script:key } $case $false
        Check (($script:changes[-1] -join ' ') -match 'rm -f /tmp/managed-ai-connect.mjs /tmp/managed-ai-plan.mjs$') "$case failure cleans fixed helpers"
    }
    $script:scenario=''; $script:changes=@(); $script:sent=0
    $result = Invoke-CtiManagedAiConnection $script:root $script:key
    Check ($result -ceq 'CTI_MANAGED_AI_RESULT:v1:prepared') 'only structured success emitted'
    Check ($script:sent -eq 1) 'exactly one secret handoff'
    Check (($script:changes | Where-Object { $_[0] -eq 'cp' }).Count -eq 3) 'trusted helpers and templates copied'
    Check (([IO.File]::ReadAllText((Join-Path $script:root '.env'))) -notmatch [regex]::Escape($script:key)) 'key not persisted in host config'
    Write-Host "PASS: $script:passed managed AI host assertions; no real Docker used."
} finally {
    $full = [IO.Path]::GetFullPath($script:root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notlike 'cti-ai-host-test-*') { throw 'Unsafe fixture cleanup' }
    Remove-Item -LiteralPath $full -Recurse -Force
}
