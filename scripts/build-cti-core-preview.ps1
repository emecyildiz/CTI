[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()][string]$DotnetExe = 'dotnet',
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Build and verification of this preview requires x64 Windows.'
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'app\cti-core\CtiCore.csproj'
$assets = Join-Path $root 'app\cti-core\packaging\windows'
$id = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "dist\cti-core-preview\$id"
}
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$archiveName = 'CTI-Core-Windows-x64-preview.zip'
$checksumName = "$archiveName.sha256"
foreach ($name in @($archiveName, $checksumName)) {
    if (Test-Path -LiteralPath (Join-Path $destination $name)) {
        throw "Refusing to replace an existing artifact: $name"
    }
}

$DotnetExe = (Get-Command $DotnetExe -CommandType Application).Source
$sdk = & $DotnetExe --version
if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^\d+\.\d+\.\d+' -or [int](($sdk -split '\.')[0]) -lt 10) {
    throw 'Select a .NET 10 SDK or newer using -DotnetExe.'
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$stage = Join-Path $tempRoot "emecworks-cti-preview-$id"
New-Item -ItemType Directory -Path $stage | Out-Null
$process = $null
try {
    $publish = Join-Path $stage 'publish'
    & $DotnetExe publish $project -c Release -r win-x64 --self-contained true --nologo --output $publish `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:TargetLatestRuntimePatch=true `
        -p:DebugType=None -p:DebugSymbols=false -p:NuGetAuditMode=all
    if ($LASTEXITCODE -ne 0) { throw 'Public CTI publish failed.' }

    $pack = Join-Path $stage 'package'
    New-Item -ItemType Directory -Path $pack | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish 'CtiCore.exe') -Destination (Join-Path $pack 'CTI-Core.exe')
    Copy-Item -LiteralPath (Join-Path $assets 'START-CTI.cmd') -Destination $pack
    Copy-Item -LiteralPath (Join-Path $assets 'ONCE-OKU.txt') -Destination $pack
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $pack 'LICENSE.txt')
    $dotnetRoot = Split-Path -Parent $DotnetExe
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Destination (Join-Path $pack 'DOTNET-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $pack 'DOTNET-NOTICES.txt')
    $zip = Join-Path $stage $archiveName
    Compress-Archive -LiteralPath @((Get-ChildItem -LiteralPath $pack -File).FullName) -DestinationPath $zip -CompressionLevel Optimal

    $unpack = Join-Path $stage 'unpacked'
    Expand-Archive -LiteralPath $zip -DestinationPath $unpack
    $names = @((Get-ChildItem -LiteralPath $unpack -Force).Name | Sort-Object)
    if (($names -join '|') -ne 'CTI-Core.exe|DOTNET-LICENSE.txt|DOTNET-NOTICES.txt|LICENSE.txt|ONCE-OKU.txt|START-CTI.cmd') {
        throw 'The preview archive has unexpected contents.'
    }
    $exe = Join-Path $unpack 'CTI-Core.exe'
    if ((Get-FileHash -LiteralPath $exe).Hash -ne (Get-FileHash -LiteralPath (Join-Path $pack 'CTI-Core.exe')).Hash) {
        throw 'The executable changed inside the ZIP.'
    }

    $versionInfo = [Diagnostics.ProcessStartInfo]::new($exe, '--version')
    $versionInfo.UseShellExecute = $false
    $versionInfo.CreateNoWindow = $true
    $versionInfo.RedirectStandardOutput = $true
    $versionInfo.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $stage 'runtime'
    $versionInfo.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $stage 'no-installed-runtime'
    $versionInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $versionInfo.EnvironmentVariables['DOTNET_ROOT']
    $versionInfo.EnvironmentVariables.Remove('CTI_GEMINI_API_KEY')
    $versionInfo.EnvironmentVariables.Remove('CTI_GEMINI_MODEL')
    $probe = [Diagnostics.Process]::Start($versionInfo)
    try {
        if (-not $probe.WaitForExit(15000)) { $probe.Kill(); throw 'Version check timed out.' }
        $version = $probe.StandardOutput.ReadToEnd().Trim()
        if ($probe.ExitCode -ne 0 -or $version -notmatch '^CTI Core preview / \.NET 10\.0\.\d+ / X64$') {
            throw "Unexpected embedded runtime: $version"
        }
    }
    finally { $probe.Dispose() }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); $port = $listener.LocalEndpoint.Port }
    finally { $listener.Stop() }
    $data = Join-Path $stage 'test-data'
    $versionInfo.Arguments = "--port $port --data-dir `"$data`""
    $versionInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($versionInfo)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $page = $null
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        try {
            $page = Invoke-WebRequest -Uri "http://127.0.0.1:$port/" -UseBasicParsing -TimeoutSec 2
            break
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if ($null -eq $page -or $process.HasExited -or $page.StatusCode -ne 200 -or
        $page.Content -notmatch 'id="source-heading"' -or $page.Content -notmatch 'id="label-securityweek"' -or
        $page.Content -match 'action="/summary"' -or -not (Test-Path -LiteralPath (Join-Path $data 'articles.sqlite3'))) {
        if ($process.HasExited) { Write-Host ($stdout.GetAwaiter().GetResult()); Write-Host ($stderr.GetAwaiter().GetResult()) }
        throw 'The packaged executable did not start an isolated panel with SQLite and AI off.'
    }

    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $artifact = Join-Path $destination $archiveName
    [IO.File]::Copy($zip, $artifact, $false)
    $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $destination $checksumName), "$hash  $archiveName`n", [Text.Encoding]::ASCII)
    Write-Host "PASS: $version; archive executable started the panel and initialized native SQLite with AI off."
    Write-Output $artifact
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $process.Kill()
            $null = $process.WaitForExit(5000)
        }
        $process.Dispose()
    }
    $resolved = Get-Item -LiteralPath $stage
    if ($resolved.Parent.FullName -eq $tempRoot -and $resolved.Name -eq "emecworks-cti-preview-$id" -and
        -not ($resolved.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $resolved.FullName -Recurse -Force
    }
}
