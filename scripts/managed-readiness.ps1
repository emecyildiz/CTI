[CmdletBinding()]
param([string]$InstallationPath)
. (Join-Path $PSScriptRoot 'managed-connection-host.ps1')
function Invoke-CtiManagedReadiness([string]$Root, [bool]$IncludeAi, [bool]$IncludeTelegram) {
    Invoke-CtiManagedConnection $Root 'Audit' @{includeAi=$IncludeAi;includeTelegram=$IncludeTelegram}
}
if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    try {
        $request = Read-CtiConnectionRequest
        if ($request.includeAi -isnot [bool] -or $request.includeTelegram -isnot [bool]) { throw 'Invalid options.' }
        Invoke-CtiManagedReadiness $InstallationPath $request.includeAi $request.includeTelegram
    } catch {
        [Console]::Error.WriteLine('Preparation check could not finish. No tasks, credentials or messages were changed. Keep the installation folder and review interrupted preparation.')
        exit 1
    } finally { $request = $null }
}
