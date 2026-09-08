[CmdletBinding()]
param(
    [string]$InstallationPath = $PSScriptRoot,
    [ValidateSet('Stop', 'Remove', 'Purge')][string]$Action = 'Stop',
    [switch]$Preview,
    [string]$ConfirmProject
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts/windows-lifecycle.ps1')
Invoke-CtiManagement $InstallationPath $Action ([bool]$Preview) $ConfirmProject
