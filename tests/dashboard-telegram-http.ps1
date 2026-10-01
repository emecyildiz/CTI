#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
function Port {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
$webPort = Port
do { $absentPort = Port } while ($absentPort -eq $webPort)
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.ArgumentList.Add((Join-Path $repo 'app/cti-dashboard/bin/Release/net8.0/CtiDashboard.dll'))
$start.WorkingDirectory = Join-Path $repo 'app/cti-dashboard'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$webPort"
$start.Environment['CTI_AUTH_MODE'] = 'local'
$start.Environment['CTI_DASHBOARD_PASSWORD'] = 'synthetic-http-test-password'
$start.Environment['CTI_N8N_API_URL'] = "http://127.0.0.1:$absentPort/api/v1"
$start.Environment['Logging__LogLevel__Default'] = 'Critical'
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseCookies = $false
$handler.AllowAutoRedirect = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(15)
$base = "http://127.0.0.1:$webPort"
$csrf = 'x' * 43
$key = 'synthetic-n8n-key-for-http-test'
$bot = '123456789:' + ('A' * 35)
function Check($ok, [string]$label) { if (-not $ok) { throw "FAIL: $label" }; Write-Host "PASS: $label" }
function Post([string]$chat, [string]$origin, [string]$formCsrf) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "$base/setup/telegram-credential-handoff")
    [void]$request.Headers.TryAddWithoutValidation('Cookie', "cti_setup_csrf=$csrf")
    [void]$request.Headers.TryAddWithoutValidation('Origin', $origin)
    $request.Content = [Net.Http.StringContent]::new("_csrf=$formCsrf&connect_workflows=true&n8n_api_key=$key&bot_token=$bot&authorized_chat_id=$chat", [Text.Encoding]::UTF8, 'application/x-www-form-urlencoded')
    try { return $client.SendAsync($request).GetAwaiter().GetResult() } finally { $request.Dispose() }
}
try {
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $ready = $false
    for ($i=0; $i -lt 30; $i++) {
        try { $r=$client.GetAsync("$base/setup.css").GetAwaiter().GetResult(); $ready=$r.IsSuccessStatusCode; $r.Dispose() } catch {}
        if ($ready) { break }
        Start-Sleep -Milliseconds 200
    }
    Check $ready 'isolated dashboard starts without database or Docker'
    foreach ($case in @(@('bad-id',$base,$csrf,400), @('123456789','http://example.invalid',$csrf,403), @('123456789',$base,('y'*43),403), @('123456789',$base,$csrf,503))) {
        $r = Post $case[0] $case[1] $case[2]
        try {
            $body = $r.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            Check ([int]$r.StatusCode -eq $case[3]) "combined Telegram HTTP result $($case[3])"
            Check (-not $body.Contains($key) -and -not $body.Contains($bot)) 'submitted secrets are not reflected'
            if ($case[3] -eq 503) { Check ($body.Contains('Return to setup') -and $body.Contains('No tasks were started')) 'unavailable n8n returns actionable partial-safe screen' }
        } finally { $r.Dispose() }
    }
} finally {
    $client.Dispose()
    if ($process.Id -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
}
