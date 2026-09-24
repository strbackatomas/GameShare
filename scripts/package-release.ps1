<#
.SYNOPSIS
  Packs what scripts\publish.ps1 built into the zips a GitHub release hands out.

.DESCRIPTION
  Run after publish.ps1. Writes into artifacts\release:

  GameShare-LanParty.zip      GameShare-LanParty.exe with trust-public.key next to it. For the players' own PCs.
  GameShare-Agent.zip         The service and the client for the organiser's PCs, with install-agent.ps1. Self-contained.
  GameShare-Agent-net10.zip   The same, built for a PC that already has the ASP.NET Core Runtime 10.0 installed.
  GameShare-Admin.zip         The administrator's tools (command line and window) and import-lan-installer.ps1.

  The names carry no version on purpose: https://github.com/<owner>/<repo>/releases/latest/download/<name> always
  points at the newest release, so the links on the web page never need changing. The version is in the tag.

  The agent zips keep the repository's layout (scripts\ next to artifacts\agent and artifacts\client), so
  install-agent.ps1 finds everything by its defaults: unzip, then run scripts\install-agent.ps1 from an elevated PowerShell.
  The net10 zip puts its smaller builds under the same folder names for the same reason.
#>
[CmdletBinding()]
param(
    [string]$Artifacts = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\release')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem  # Windows PowerShell 5.1 does not load them by itself

$Artifacts = (Resolve-Path $Artifacts).Path
$Output = [System.IO.Path]::GetFullPath($Output)
$version = ([xml](Get-Content (Join-Path $PSScriptRoot '..\src\Directory.Build.props'))).Project.PropertyGroup.Version
$stage = Join-Path $Output 'stage'
if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Copies a published folder into the staging area. Debug symbols stay out, the players have no use for them.
function Add-Folder($from, $to) {
    if (-not (Test-Path (Join-Path $Artifacts $from))) { throw "artifacts\$from is missing. Run scripts\publish.ps1 first." }
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    Copy-Item -Path (Join-Path $Artifacts "$from\*") -Destination $to -Recurse -Force -Exclude '*.pdb'
}

function Add-File($from, $toFolder) {
    New-Item -ItemType Directory -Force -Path $toFolder | Out-Null
    Copy-Item $from $toFolder -Force
}

function Save-Zip($name) {
    $zip = Join-Path $Output "$name.zip"
    $root = (Resolve-Path (Join-Path $stage $name)).Path.TrimEnd('\') + '\'
    # Entry by entry rather than ZipFile.CreateFromDirectory: under Windows PowerShell 5.1 that writes the folders with
    # backslashes, which some unzip tools turn into file names like "artifacts\agent\GameShare.Agent.exe".
    $archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        foreach ($file in Get-ChildItem $root -Recurse -File) {
            $entry = $file.FullName.Substring($root.Length).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, 'Optimal') | Out-Null
        }
    }
    finally { $archive.Dispose() }
    Write-Host ("{0,-28} {1,6:N0} MB" -f "$name.zip", ((Get-Item $zip).Length / 1MB))
}

$key = Join-Path $PSScriptRoot 'trust-public.key'
if (-not (Test-Path $key -PathType Leaf)) { Write-Warning "No scripts\trust-public.key: the zips go out with verified games off." }

Write-Host "Packing GameShare v$version into $Output`n"

# The players: one exe, and the public key that turns verified games on.
$dir = Join-Path $stage 'GameShare-LanParty'
Add-Folder 'standalone' $dir
Save-Zip 'GameShare-LanParty'

# The organiser's PCs, in both forms. publish.ps1 already put the key into the agent folders.
foreach ($form in @(@{ Name = 'GameShare-Agent'; Suffix = '' }, @{ Name = 'GameShare-Agent-net10'; Suffix = '-net10' })) {
    $dir = Join-Path $stage $form.Name
    Add-Folder "agent$($form.Suffix)" (Join-Path $dir 'artifacts\agent')
    Add-Folder "client$($form.Suffix)" (Join-Path $dir 'artifacts\client')
    foreach ($script in 'install-agent.ps1', 'uninstall-agent.ps1') { Add-File (Join-Path $PSScriptRoot $script) (Join-Path $dir 'scripts') }
    if (Test-Path $key) { Add-File $key (Join-Path $dir 'scripts') }
    $runtime = if ($form.Suffix) { "`r`nThis build needs the ASP.NET Core Runtime 10.0 (x64): https://dotnet.microsoft.com/download/dotnet/10.0`r`n" } else { '' }
    Set-Content -Path (Join-Path $dir 'README.txt') -Encoding UTF8 -Value @"
GameShare v$version - the service for the organiser's PCs
$runtime
From an elevated PowerShell in this folder:

  powershell -ExecutionPolicy Bypass -File scripts\install-agent.ps1 -GameRoots D:\Games

-GameRoots is where the games are. Remove it again with scripts\uninstall-agent.ps1.
Players do not need this: they run GameShare-LanParty.exe from GameShare-LanParty.zip.
"@
    Save-Zip $form.Name
}

# The administrator: both tools, and the one-time converter of the old installer.
$dir = Join-Path $stage 'GameShare-Admin'
Add-Folder 'admin' (Join-Path $dir 'admin')
Add-Folder 'admin-gui' (Join-Path $dir 'admin-gui')
Add-File (Join-Path $PSScriptRoot 'import-lan-installer.ps1') $dir
Save-Zip 'GameShare-Admin'

Remove-Item -Recurse -Force $stage
