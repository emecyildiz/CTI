# Explicit opt-in: one offline, temporary pinned n8n container; no real installation.
param([switch]$Run)
$ErrorActionPreference = 'Stop'
if (-not $Run) { throw 'Use -Run to authorize the isolated Docker fixture.' }
$repository = Split-Path -Parent $PSScriptRoot
$root = Join-Path ([IO.Path]::GetTempPath()) ('cti-ai-docker-test-' + [Guid]::NewGuid().ToString('N'))
$project = 'cti-' + [Guid]::NewGuid().ToString('N')
$container = "$project-n8n"
$image = 'docker.n8n.io/n8nio/n8n:2.30.5@sha256:450853cd21a2ce36587c4c860eb26927c1ceba9496bf55f4c213b5d3a6dc8c6f'
$created = $false
function Invoke-FixtureDocker([string[]]$Arguments, [string]$InputText = '') {
    if ($InputText) { $output = $InputText | & docker @Arguments 2>&1 } else { $output = & docker @Arguments 2>&1 }
    if ($LASTEXITCODE -ne 0) { throw "Fixture Docker failed at $($Arguments[0]); output withheld." }
    return ($output -join "`n")
}
function Check($Condition, [string]$Label) { if (-not $Condition) { throw "FAIL: $Label" }; Write-Host "PASS: $Label" }
try {
    [void](Invoke-FixtureDocker @('image','inspect',$image)) # Do not download images implicitly.
    $engine = (Invoke-FixtureDocker @('info','--format','{{.ID}}')).Trim()
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'scripts'))
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'workflows'))
    foreach ($file in @('windows-lifecycle.ps1','managed-connection-host.ps1','managed-ai-connect.ps1','managed-ai-connect.mjs','managed-ai-plan.mjs','managed-telegram-connect.ps1','managed-telegram-connect.mjs','managed-telegram-plan.mjs','managed-readiness.ps1','managed-readiness.mjs','managed-readiness-plan.mjs','managed-bootstrap.mjs','managed-bootstrap-plan.mjs')) {
        [IO.File]::WriteAllBytes((Join-Path $root "scripts/$file"),[IO.File]::ReadAllBytes((Join-Path $repository "scripts/$file")))
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repository 'workflows') -Filter '*.json') {
        [IO.File]::WriteAllBytes((Join-Path $root "workflows/$($file.Name)"),[IO.File]::ReadAllBytes($file.FullName))
    }
    [IO.File]::WriteAllText((Join-Path $root '.cti-owner.json'),(@{schema=1;product='CTI Self-Hosted';root=$root;project=$project;engine=$engine;managed_n8n=$true} | ConvertTo-Json))
    [IO.File]::WriteAllText((Join-Path $root '.env'),"CTI_MANAGED_N8N=true`nCTI_COMPOSE_PROJECT_NAME=$project`nN8N_CONTAINER=$container`nCTI_N8N_API_URL=http://cti-n8n:5678/api/v1`n")
    [void](Invoke-FixtureDocker @('run','-d','--name',$container,'--label',"com.docker.compose.project=$project",'--label','com.docker.compose.service=cti-n8n','--label',"com.docker.compose.project.working_dir=$root",'--network','none','--tmpfs','/home/node/.n8n:uid=1000,gid=1000,mode=700','--entrypoint','sleep',$image,'900'))
    $created = $true
    foreach ($file in @('managed-bootstrap.mjs','managed-bootstrap-plan.mjs')) { [void](Invoke-FixtureDocker @('cp',(Join-Path $root "scripts/$file"),"${container}:/tmp/$file")) }
    [void](Invoke-FixtureDocker @('cp',(Join-Path $root 'workflows'),"${container}:/tmp/cti-self-hosted-workflows"))
    [void](Invoke-FixtureDocker @('exec','-i',$container,'node','/tmp/managed-bootstrap.mjs') (@{databasePassword=('a'*43);databaseName='cti'} | ConvertTo-Json -Compress))
    $receipt = (Invoke-FixtureDocker @('exec',$container,'cat','/home/node/.n8n/.cti-managed-bootstrap-v1')) | ConvertFrom-Json
    Check ($receipt.database -and -not $receipt.ai) 'fresh database-only bootstrap, no AI credential'
    $powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $requests = @(
        @{script='managed-ai-connect.ps1';configuration=@{aiKey='synthetic-first-provider-key-only'};secret='synthetic-first-provider-key-only';marker='CTI_MANAGED_AI_RESULT:v1:prepared'},
        @{script='managed-ai-connect.ps1';configuration=@{aiKey='synthetic-replacement-provider-key'};secret='synthetic-replacement-provider-key';marker='CTI_MANAGED_AI_RESULT:v1:prepared'},
        @{script='managed-telegram-connect.ps1';configuration=@{botToken=('123456789:' + ('A'*35));chatId='123456789'};secret=('123456789:' + ('A'*35));marker='CTI_MANAGED_TELEGRAM_RESULT:v1:prepared'},
        @{script='managed-telegram-connect.ps1';configuration=@{botToken=('987654321:' + ('B'*35));chatId='987654321'};secret=('987654321:' + ('B'*35));marker='CTI_MANAGED_TELEGRAM_RESULT:v1:prepared'},
        @{script='managed-readiness.ps1';configuration=@{includeAi=$true;includeTelegram=$true};secret='must-not-be-emitted';marker='CTI_MANAGED_AUDIT_RESULT:v1:'}
    )
    foreach ($request in $requests) {
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($powershell)
        $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
        $process.StartInfo.RedirectStandardInput=$true; $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
        foreach ($arg in @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $root "scripts/$($request.script)"),'-InstallationPath',$root)) { $process.StartInfo.ArgumentList.Add($arg) }
        try {
            [void]$process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
            $process.StandardInput.Write(($request.configuration|ConvertTo-Json -Compress)); $process.StandardInput.Close()
            if (-not $process.WaitForExit(240000)) { $process.Kill($true); throw 'Fixture bridge timeout' }
            $result = $stdout.GetAwaiter().GetResult(); $errorText = $stderr.GetAwaiter().GetResult()
            if ($request.script -eq 'managed-readiness.ps1') {
                Check ($process.ExitCode -eq 0 -and $result.Trim().StartsWith($request.marker)) 'real Windows PowerShell read-only audit returns structured result'
                $audit = $result.Trim().Substring($request.marker.Length) | ConvertFrom-Json
                Check ($audit.ready -eq $true -and $audit.drafts -eq $true -and $audit.database -eq $true -and $audit.errorRouting -eq $true -and $audit.ai -eq $true -and $audit.telegram -eq $true) 'real encrypted-export audit confirms all selected bindings, reviewed graphs and disabled drafts'
                Check (-not $result.Contains($requests[3].secret) -and -not $result.Contains('987654321')) 'audit result contains no token or private ID'
            } else {
                Check ($process.ExitCode -eq 0 -and $result.Trim() -ceq $request.marker -and -not $errorText.Contains($request.secret)) "real Windows PowerShell $($request.script) stdin bridge succeeds without leaking secret"
            }
        } finally { $process.Dispose() }
    }
    [void](Invoke-FixtureDocker @('exec',$container,'sh','-c','test ! -f /tmp/managed-ai-connect.mjs && test ! -f /tmp/managed-ai-plan.mjs && test ! -f /home/node/.n8n/.cti-managed-ai-pending-v1'))
    Check $true 'helper files and pending marker cleaned'
    [void](Invoke-FixtureDocker @('exec',$container,'sh','-c','test ! -f /tmp/managed-telegram-connect.mjs && test ! -f /tmp/managed-telegram-plan.mjs && test ! -f /home/node/.n8n/.cti-managed-telegram-pending-v1 && test ! -f /home/node/.n8n/.cti-managed-connection-lock-v1'))
    Check $true 'Telegram helper files, pending marker and common lock cleaned'
    [void](Invoke-FixtureDocker @('exec',$container,'sh','-c','test ! -f /tmp/managed-readiness.mjs && test ! -f /tmp/managed-readiness-plan.mjs && test ! -f /home/node/.n8n/.cti-managed-connection-lock-v1'))
    Check $true 'audit helper files and temporary lock cleaned'
    $hostFiles = [IO.Directory]::GetFiles($root,'*',[IO.SearchOption]::AllDirectories)
    Check (-not ($hostFiles | Where-Object { [IO.File]::ReadAllText($_).Contains('synthetic-replacement-provider-key') })) 'no provider secret persisted in host fixture files'
    Check (-not ($hostFiles | Where-Object { [IO.File]::ReadAllText($_).Contains($requests[3].secret) })) 'no Telegram token persisted in host fixture files'
    Write-Host 'PASS: offline AI and Telegram create/update through real host bridges. No provider request, message or workflow activation.'
} finally {
    if ($created) {
        $owned = (Invoke-FixtureDocker @('inspect',$container) | ConvertFrom-Json)[0]
        if ($owned.Config.Labels.'com.docker.compose.project' -cne $project -or $owned.Config.Labels.'com.docker.compose.project.working_dir' -ine $root) { throw 'Fixture cleanup ownership mismatch' }
        [void](Invoke-FixtureDocker @('rm','-f',$owned.Id))
    }
    $full = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notlike 'cti-ai-docker-test-*') { throw 'Unsafe fixture cleanup' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    Write-Host 'PASS: isolated container/tmpfs and host fixture cleaned.'
}
