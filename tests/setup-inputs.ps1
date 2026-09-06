# Offline regression tests. Only extracted setup functions run; Docker is never called.
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'setup.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$functionNames = @('Stop-Setup', 'ConvertTo-TelegramWebhookUrl', 'Assert-TelegramManagedN8n', 'Set-EnvironmentValue', 'Assert-SetupEnvironment')
foreach ($name in $functionNames) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Setup function is missing: $name" }
    # Extracted scriptblocks have no file-backed PSScriptRoot. Supply an owned
    # temporary root for the writer without running the setup entry point.
    $functionText = $definition.Extent.Text.Replace('$PSScriptRoot', '$script:SetupTestRoot')
    . ([ScriptBlock]::Create($functionText))
}

function Assert-Valid([string]$Value, [string]$Expected) {
    $actual = ConvertTo-TelegramWebhookUrl $Value
    if ($actual -cne $Expected) { throw "Unexpected normalized URL: $actual" }
}
function Assert-Rejected([scriptblock]$Action, [string]$Label) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Unsafe input was accepted: $Label" }
}

Assert-Valid 'https://hooks.example.com' 'https://hooks.example.com/'
Assert-Valid 'https://Hooks.Example.com:8443/n8n-webhooks/v1//' 'https://Hooks.Example.com:8443/n8n-webhooks/v1/'
Assert-Valid 'https://hooks.example.com/a%20b/~v1' 'https://hooks.example.com/a%20b/~v1/'
$invalidValues = @(
    "https://hooks.example.com/`nCTI_DASHBOARD_BIND=0.0.0.0`nCTI_TEST=x",
    'https://hooks.example.com/\nCTI_DASHBOARD_BIND=0.0.0.0\nCTI_TEST=x',
    'https://hooks.example.com/$POSTGRES_PASSWORD',
    'https://hooks.example.com/"quoted"',
    "https://hooks.example.com/'quoted'",
    'https://hooks.example.com/path with space',
    'https://user:password@hooks.example.com/',
    'https://hooks.example.com/?query=1',
    'https://hooks.example.com/#fragment',
    'http://hooks.example.com/',
    'https://localhost/',
    'https://hooks.localhost/',
    'https://127.0.0.1/',
    'https://0.0.0.0/',
    'https://[::1]/',
    'https://-invalid.example.com/',
    'https://hooks.example.com:0/',
    'https://hooks.example.com:65536/',
    'https://hooks.example.com/bad%',
    'https://hooks.example.com/bad%2x'
)
foreach ($value in $invalidValues) {
    Assert-Rejected { ConvertTo-TelegramWebhookUrl $value } $value
}

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('cti-setup-tests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporaryDirectory)
try {
    $script:SetupTestRoot = $temporaryDirectory
    $environmentPath = Join-Path $temporaryDirectory '.env'
    [IO.File]::WriteAllLines($environmentPath, @(
        '# Preserve unrelated settings and secrets.',
        'POSTGRES_PASSWORD=unchanged-owner-secret',
        'CTI_MANAGED_N8N=true',
        'N8N_WEBHOOK_URL=http://old.example.com/',
        'N8N_WEBHOOK_URL=http://duplicate.example.com/'
    ))
    Set-EnvironmentValue 'N8N_WEBHOOK_URL' 'https://hooks.example.com/base/'
    $lines = [IO.File]::ReadAllLines($environmentPath)
    if (@($lines | Where-Object { $_ -like 'N8N_WEBHOOK_URL=*' }).Count -ne 1) { throw 'Duplicate target keys were not removed.' }
    if ($lines -notcontains 'POSTGRES_PASSWORD=unchanged-owner-secret') { throw 'Unrelated secret was changed.' }
    if ($lines -notcontains '# Preserve unrelated settings and secrets.') { throw 'Comment was changed.' }
    Assert-TelegramManagedN8n $false $environmentPath
    Set-EnvironmentValue 'CTI_MANAGED_N8N' 'false'
    $before = [IO.File]::ReadAllText($environmentPath)
    Assert-Rejected { Assert-TelegramManagedN8n $false $environmentPath } 'saved existing-n8n mode'
    if ([IO.File]::ReadAllText($environmentPath) -cne $before) { throw 'Rejected configuration was changed.' }
    Assert-Rejected { Assert-TelegramManagedN8n $true $environmentPath } 'existing-n8n flag'

    $validEnvironment = @{
        POSTGRES_PASSWORD = 'owner-secret'
        CTI_APP_PASSWORD = 'app-secret'
        CTI_DASHBOARD_PASSWORD = 'dashboard-secret'
        CTI_DASHBOARD_BIND = '127.0.0.1'
        N8N_ENCRYPTION_KEY = 'encryption-secret'
    }
    Assert-SetupEnvironment $validEnvironment $true
    foreach ($key in @('POSTGRES_PASSWORD', 'CTI_APP_PASSWORD', 'CTI_DASHBOARD_PASSWORD', 'N8N_ENCRYPTION_KEY')) {
        $invalidEnvironment = $validEnvironment.Clone()
        $invalidEnvironment[$key] = ''
        Assert-Rejected { Assert-SetupEnvironment $invalidEnvironment $true } "missing $key"
    }
    foreach ($pair in @(@('POSTGRES_PASSWORD', 'CTI_APP_PASSWORD'), @('POSTGRES_PASSWORD', 'CTI_DASHBOARD_PASSWORD'), @('CTI_APP_PASSWORD', 'CTI_DASHBOARD_PASSWORD'))) {
        $invalidEnvironment = $validEnvironment.Clone()
        $invalidEnvironment[$pair[0]] = $invalidEnvironment[$pair[1]]
        Assert-Rejected { Assert-SetupEnvironment $invalidEnvironment $true } "duplicate $($pair -join '/')"
    }
    $invalidEnvironment = $validEnvironment.Clone()
    $invalidEnvironment['CTI_DASHBOARD_BIND'] = '0.0.0.0'
    Assert-Rejected { Assert-SetupEnvironment $invalidEnvironment $true } 'public dashboard bind'
    $externalEnvironment = $validEnvironment.Clone()
    $externalEnvironment.Remove('N8N_ENCRYPTION_KEY')
    Assert-SetupEnvironment $externalEnvironment $false
} finally {
    $resolved = [IO.Path]::GetFullPath($temporaryDirectory)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('cti-setup-tests-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Host 'PASS: PowerShell setup URL safety, environment preservation, existing-n8n guard, and preflight policy.'
