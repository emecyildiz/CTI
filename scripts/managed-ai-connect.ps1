[CmdletBinding()]
param([string]$InstallationPath)
. (Join-Path $PSScriptRoot 'managed-connection-host.ps1')
function Invoke-CtiManagedAiConnection([string]$Root, [string]$AiKey) {
    Invoke-CtiManagedConnection $Root 'Ai' @{aiKey=$AiKey}
}
if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    try {
        $request = Read-CtiConnectionRequest
        Invoke-CtiManagedAiConnection $InstallationPath $request.aiKey
    } catch {
        [Console]::Error.WriteLine('AI connection could not be prepared. No tasks were activated. A partial update may require review before retrying; keep the installation folder.')
        exit 1
    } finally { $request = $null }
}
