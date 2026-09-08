# Shared by setup and management. Loading this file never changes Docker or disk state.
function Invoke-CtiDocker([string[]]$Arguments) {
    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Docker operation failed ($($Arguments[0])). No further cleanup was attempted." }
    return ($output -join "`n")
}

function Assert-CtiDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not [IO.Path]::IsPathRooted($Path) -or $full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\', '/')) {
        throw 'An absolute, non-root installation directory is required.'
    }
    $item = Get-Item -LiteralPath $full -Force
    if (-not $item.PSIsContainer) { throw 'Installation path is not a directory.' }
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installation paths must not traverse a junction or symbolic link.' }
        $item = $item.Parent
    }
    return $full
}

function Read-CtiOwner([string]$Root) {
    $Root = Assert-CtiDirectory $Root
    $path = Join-Path $Root '.cti-owner.json'
    if (-not (Test-Path -LiteralPath $path)) { throw 'No ownership record. Legacy installations need a separate reviewed migration; automatic removal is disabled.' }
    if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe ownership record.' }
    $owner = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($owner.schema -ne 1 -or $owner.product -cne 'CTI Self-Hosted' -or
        $owner.project -cnotmatch '^cti-[a-f0-9]{32}$' -or
        $owner.root -ine $Root -or $owner.managed_n8n -isnot [bool] -or
        [string]::IsNullOrWhiteSpace($owner.engine)) { throw 'Invalid or relocated ownership record. Refusing automatic management.' }
    return $owner
}

function Get-CtiPlan($Owner) {
    $engine = (Invoke-CtiDocker @('info', '--format', '{{.ID}}')).Trim()
    if ($engine -cne $Owner.engine) { throw 'Docker engine differs from the installation record. Refusing to target another engine.' }
    $project = $Owner.project
    $containers = @()
    $ids = (Invoke-CtiDocker @('ps', '-aq', '--filter', "label=com.docker.compose.project=$project")) -split '\s+' | Where-Object { $_ }
    foreach ($id in $ids) {
        $c = (ConvertFrom-Json (Invoke-CtiDocker @('inspect', $id)))[0]
        $labels = $c.Config.Labels
        $services = @('cti-db', 'cti-dashboard')
        if ($Owner.managed_n8n) { $services += 'cti-n8n' }
        if ($labels.'com.docker.compose.project' -cne $project -or
            $labels.'com.docker.compose.service' -cnotin $services -or
            $labels.'com.docker.compose.project.working_dir' -ine $Owner.root) {
            throw 'Unexpected container ownership or service. Nothing will be removed.'
        }
        $containers += $c.Id
    }
    $volumes = @()
    $names = (Invoke-CtiDocker @('volume', 'ls', '-q', '--filter', "label=com.docker.compose.project=$project")) -split '\s+' | Where-Object { $_ }
    foreach ($name in $names) {
        $v = (ConvertFrom-Json (Invoke-CtiDocker @('volume', 'inspect', $name)))[0]
        $keys = @('cti_pgdata')
        if ($Owner.managed_n8n) { $keys += 'cti_n8n_data' }
        $key = $v.Labels.'com.docker.compose.volume'
        if ($v.Labels.'com.docker.compose.project' -cne $project -or $key -cnotin $keys -or $v.Name -cne "${project}_$key") {
            throw 'Unexpected volume ownership. Nothing will be removed.'
        }
        # Do not delete storage also attached to a container outside this installation.
        $users = (Invoke-CtiDocker @('ps', '-aq', '--filter', "volume=$name")) -split '\s+' | Where-Object { $_ }
        foreach ($user in $users) {
            $u = (ConvertFrom-Json (Invoke-CtiDocker @('inspect', $user)))[0]
            if ($u.Id -cnotin $containers) { throw 'A CTI volume is used by another container. Refusing management.' }
        }
        $volumes += $v.Name
    }
    $networks = @()
    $nets = (Invoke-CtiDocker @('network', 'ls', '-q', '--filter', "label=com.docker.compose.project=$project")) -split '\s+' | Where-Object { $_ }
    foreach ($net in $nets) {
        $n = (ConvertFrom-Json (Invoke-CtiDocker @('network', 'inspect', $net)))[0]
        if ($n.Labels.'com.docker.compose.project' -cne $project -or
            $n.Labels.'com.docker.compose.network' -cne 'cti' -or $n.Name -cne $project) {
            throw 'Unexpected network ownership. Nothing will be removed.'
        }
        if ($n.Containers) {
            foreach ($property in $n.Containers.PSObject.Properties) {
                if ($property.Name -cnotin $containers) { throw 'Another container is attached to the CTI network. Refusing management.' }
            }
        }
        $networks += $n.Id
    }
    return [pscustomobject]@{ Containers = $containers; Volumes = $volumes; Networks = $networks }
}

function Get-CtiPackageCleanup([string]$Root) {
    $Root = Assert-CtiDirectory $Root
    $manifest = Join-Path $Root '.cti-package-files.json'
    $files = @()
    if (Test-Path -LiteralPath $manifest) {
        if ((Get-Item -LiteralPath $manifest -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe package manifest.' }
        $entries = ConvertFrom-Json ([IO.File]::ReadAllText($manifest))
        foreach ($entry in $entries) {
            $relative = [string]$entry.path
            if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
                $relative -match '(^|[\\/])\.\.([\\/]|$)|:' -or $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid package cleanup path or hash.' }
            $target = [IO.Path]::GetFullPath((Join-Path $Root $relative))
            if (-not $target.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package cleanup escaped installation root.' }
            if (Test-Path -LiteralPath $target) {
                [void](Assert-CtiDirectory (Split-Path -Parent $target))
                $item = Get-Item -LiteralPath $target -Force
                if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe package file.' }
                if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $entry.sha256) { $files += $target }
                else { Write-Host "Preserving modified package file: $relative" }
            }
        }
    }
    foreach ($name in @('.env', '.cti-installation.json', '.cti-package-files.json', '.cti-update-state.json', '.cti-owner.json')) {
        $target = Join-Path $Root $name
        if (Test-Path -LiteralPath $target) {
            $item = Get-Item -LiteralPath $target -Force
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe configuration file.' }
            $files += $target
        }
    }
    return @($files | Select-Object -Unique)
}

function Invoke-CtiManagement([string]$Root, [string]$Action, [bool]$Preview, [string]$Confirmation) {
    if ($Action -cnotin @('Stop', 'Remove', 'Purge')) { throw 'Unknown management action.' }
    $owner = Read-CtiOwner $Root
    $plan = Get-CtiPlan $owner
    $files = @()
    if ($Action -ceq 'Purge') { $files = @(Get-CtiPackageCleanup $owner.root) }
    Write-Host "Project: $($owner.project) | Action: $Action"
    Write-Host "Containers: $($plan.Containers.Count); volumes: $($plan.Volumes.Count); networks: $($plan.Networks.Count); package/config files: $($files.Count)"
    Write-Host 'Docker/WSL, external n8n, shared images, backups and unknown/modified files are never removed.'
    if ($Preview) { return }
    if ($Confirmation -cne $owner.project) { throw 'Confirmation must exactly match the installation project ID.' }
    # Re-check the complete ownership boundary immediately before mutations.
    $plan = Get-CtiPlan $owner
    foreach ($id in $plan.Containers) { [void](Invoke-CtiDocker @('stop', '--time', '60', $id)) }
    if ($Action -ceq 'Stop') { Write-Host 'Stopped. Data and installation files are retained.'; return }
    foreach ($id in $plan.Containers) { [void](Invoke-CtiDocker @('rm', $id)) }
    foreach ($id in $plan.Networks) { [void](Invoke-CtiDocker @('network', 'rm', $id)) }
    if ($Action -ceq 'Remove') { Write-Host 'Services removed. Keep this folder and .env to reuse the retained data.'; return }
    foreach ($name in $plan.Volumes) { [void](Invoke-CtiDocker @('volume', 'rm', $name)) }
    # No recursive directory deletion: preserve backups, new user files and modified sources.
    $files = @(Get-CtiPackageCleanup $owner.root)
    foreach ($file in $files) { Remove-Item -LiteralPath $file -Force }
    Write-Host 'CTI data and owned package/config files removed. Review the remaining folder manually; it may contain backups or modified files. The downloaded EXE is retained.'
}
