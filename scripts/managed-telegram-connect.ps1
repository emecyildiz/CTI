[CmdletBinding()]
param([string]$InstallationPath)
. (Join-Path $PSScriptRoot 'managed-connection-host.ps1')
function Invoke-CtiManagedTelegramConnection([string]$Root, [string]$BotToken, [string]$ChatId) {
    Invoke-CtiManagedConnection $Root 'Telegram' @{botToken=$BotToken;chatId=$ChatId}
}
if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    try {
        $request = Read-CtiConnectionRequest
        Invoke-CtiManagedTelegramConnection $InstallationPath $request.botToken $request.chatId
    } catch {
        [Console]::Error.WriteLine('Telegram connection could not be prepared. No tasks were activated or messages sent. A partial update may require review before retrying; keep the installation folder.')
        exit 1
    } finally { $request = $null }
}
