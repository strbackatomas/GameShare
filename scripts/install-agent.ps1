<#
.SYNOPSIS
  Installs the GameShare agent as a Windows service and opens only the ports it needs.

.DESCRIPTION
  Run from an elevated PowerShell. Publish first with scripts\publish.ps1.
  The published folders are self-contained, so the target PC needs no .NET runtime installed.
  If artifacts\client exists the desktop client is installed too, with a Start menu entry for all users.

  Firewall rules are added for the Private and Domain profiles only, never Public, and never for the control API.
  The control API on 127.0.0.1 is not reachable from the network by design and needs no rule.
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$SourceDir = (Join-Path $PSScriptRoot '..\artifacts\agent'),
    [string]$ClientSourceDir = (Join-Path $PSScriptRoot '..\artifacts\client'),
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'GameShare'),
    [string[]]$GameRoots = @(),
    [int]$PeerApiPort = 47702,
    [int]$DiscoveryPort = 47800,
    [int]$TorrentPort = 6881
)

$ErrorActionPreference = 'Stop'
$serviceName = 'GameShare Agent'
$exe = Join-Path $InstallDir 'GameShare.Agent.exe'

if (-not (Test-Path (Join-Path $SourceDir 'GameShare.Agent.exe'))) {
    throw "GameShare.Agent.exe not found in '$SourceDir'. Run scripts\publish.ps1 first."
}

# Replace an existing installation cleanly.
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping and removing the existing service"
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force; $existing.WaitForStatus('Stopped', '00:00:30') }
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Copying files to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force

# Initial game folders and ports go into appsettings.json. The user can change the folders later in the client.
$settingsPath = Join-Path $InstallDir 'appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$settings.Agent.PeerApiPort = $PeerApiPort
$settings.Agent.DiscoveryPort = $DiscoveryPort
$settings.Agent.TorrentPort = $TorrentPort
$settings.Agent.InitialGameRoots = @($GameRoots)
$settings | ConvertTo-Json -Depth 5 | Set-Content -Path $settingsPath -Encoding UTF8

# The desktop client, if it was published. It talks to the agent on this machine, so it needs no settings.
if (Test-Path (Join-Path $ClientSourceDir 'GameShare.exe')) {
    $clientDir = Join-Path $InstallDir 'Client'
    Write-Host "Installing the client to $clientDir"
    New-Item -ItemType Directory -Force -Path $clientDir | Out-Null
    Copy-Item -Path (Join-Path $ClientSourceDir '*') -Destination $clientDir -Recurse -Force

    $shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\GameShare.lnk'
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $link.TargetPath = Join-Path $clientDir 'GameShare.exe'
    $link.WorkingDirectory = $clientDir
    $link.Description = 'GameShare'
    $link.Save()
}
else {
    Write-Host "No published client found in '$ClientSourceDir', installing the agent only."
}

Write-Host "Creating the service"
sc.exe create $serviceName binPath= "`"$exe`"" start= delayed-auto DisplayName= $serviceName | Out-Null
sc.exe description $serviceName "Shares game files with other GameShare PCs on the local network." | Out-Null
# Restart after a crash: after 5 s, after 30 s, then every minute. The counter resets after a day.
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null

Write-Host "Opening firewall ports for the Private and Domain profiles"
$rules = @(
    @{ Name = 'GameShare peer API';  Protocol = 'TCP'; Port = $PeerApiPort },
    @{ Name = 'GameShare discovery'; Protocol = 'UDP'; Port = $DiscoveryPort },
    @{ Name = 'GameShare transfer (TCP)'; Protocol = 'TCP'; Port = $TorrentPort },
    @{ Name = 'GameShare transfer (UDP)'; Protocol = 'UDP'; Port = $TorrentPort }
)
foreach ($r in $rules) {
    Get-NetFirewallRule -DisplayName $r.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $r.Name -Direction Inbound -Action Allow -Protocol $r.Protocol -LocalPort $r.Port `
        -Profile Private, Domain -Program $exe | Out-Null
}

Write-Host "Starting the service"
Start-Service -Name $serviceName
Write-Host "Done. Logs: $env:ProgramData\GameShare\logs"
