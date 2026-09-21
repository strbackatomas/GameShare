<#
.SYNOPSIS
  Removes the GameShare agent service and its firewall rules. Game files are never touched.
.PARAMETER RemoveData
  Also deletes the database and logs in %ProgramData%\GameShare. Downloads in progress lose their resume data.
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'GameShare'),
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$serviceName = 'GameShare Agent'

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force; $service.WaitForStatus('Stopped', '00:00:30') }
    sc.exe delete $serviceName | Out-Null
    Write-Host "Service removed"
}

foreach ($name in 'GameShare peer API', 'GameShare discovery', 'GameShare transfer (TCP)', 'GameShare transfer (UDP)') {
    Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}
Write-Host "Firewall rules removed"

$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\GameShare.lnk'
if (Test-Path $shortcut) { Remove-Item -Force $shortcut; Write-Host "Removed the Start menu entry" }

if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir; Write-Host "Removed $InstallDir" }
if ($RemoveData) {
    $data = Join-Path $env:ProgramData 'GameShare'
    if (Test-Path $data) { Remove-Item -Recurse -Force $data; Write-Host "Removed $data" }
}
else {
    Write-Host "Kept the database and logs in $env:ProgramData\GameShare. Use -RemoveData to delete them."
}
