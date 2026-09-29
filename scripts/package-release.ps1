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
  install-agent.ps1 finds everything by its defaults: unzip, then double-click install-agent.bat, which asks for
  administrator rights itself, or run scripts\install-agent.ps1 from an elevated PowerShell.
  The net10 zip puts its smaller builds under the same folder names for the same reason.

  For the updater built into the programs, per build (agent, agent-net10, lanparty):

  GameShare-Update-<build>.zip   The files in the layout of an installation: for the service the agent at the top and the
                                 client in Client\, for the portable build the exe and its key. A PC unpacks it as it is.
  update-<build>.json            What the package holds (every file with its SHA-256, and its torrent), signed with the release
                                 key. Every PC checks it against the key built into it before it takes the update, and the PCs
                                 on a LAN send the package to each other with that torrent.

  The release key is taken from the environment variable GAMESHARE_RELEASE_KEY. Without it the update files are left out,
  with a warning. The signature is checked against scripts\release-public.key, the key the programs are built with, so a
  secret that does not match is found here rather than by every PC refusing the update.

  release-notes.md is this version's section of CHANGELOG.md, for the release and for the update description.
#>
[CmdletBinding()]
param(
    # Left out, artifacts\ and artifacts\release in the repository.
    [string]$Artifacts = '',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 run with -File leaves $PSScriptRoot empty inside param(), so the defaults are filled in here.
if (-not $Artifacts) { $Artifacts = Join-Path $PSScriptRoot '..\artifacts' }
if (-not $Output) { $Output = Join-Path $PSScriptRoot '..\artifacts\release' }
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
    Add-File (Join-Path $PSScriptRoot 'install-agent.bat') $dir
    $runtime = if ($form.Suffix) { "`r`nThis build needs the ASP.NET Core Runtime 10.0 (x64): https://dotnet.microsoft.com/download/dotnet/10.0`r`n" } else { '' }
    Set-Content -Path (Join-Path $dir 'README.txt') -Encoding UTF8 -Value @"
GameShare v$version - the service for the organiser's PCs
$runtime
Double-click install-agent.bat. It asks for administrator rights and then for the folders with the games.

Or from an elevated PowerShell in this folder, with all the options of the script:

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

# This version's section of CHANGELOG.md, from its heading to the next one: the notes of the release and of the update.
$changelog = Get-Content (Join-Path $PSScriptRoot '..\CHANGELOG.md') -Raw -Encoding utf8
$match = [regex]::Match($changelog, "(?ms)^## \[$([regex]::Escape($version))\][^\n]*\n(.*?)(?=^## \[|\z)")
$notes = Join-Path $Output 'release-notes.md'
Set-Content $notes $(if ($match.Success) { $match.Groups[1].Value.Trim() } else { "GameShare $version" }) -Encoding utf8

# The update packages, one per build, each signed. The admin tool that signs them was built by publish.ps1 with the rest.
$admin = Join-Path $Artifacts 'admin\gameshare-admin.exe'
$releasePublicKey = Join-Path $PSScriptRoot 'release-public.key'
if (-not $env:GAMESHARE_RELEASE_KEY) {
    Write-Warning "No GAMESHARE_RELEASE_KEY: no update packages, the programs will not find v$version by themselves."
}
elseif (-not (Test-Path $releasePublicKey -PathType Leaf)) {
    throw "GAMESHARE_RELEASE_KEY is set but scripts\release-public.key is missing: the programs could not check what it signs."
}
else {
    foreach ($update in @(
        @{ Flavor = 'agent'; Parts = @(@{ From = 'agent'; To = '' }, @{ From = 'client'; To = 'Client' }) },
        @{ Flavor = 'agent-net10'; Parts = @(@{ From = 'agent-net10'; To = '' }, @{ From = 'client-net10'; To = 'Client' }) },
        @{ Flavor = 'lanparty'; Parts = @(@{ From = 'standalone'; To = '' }) }
    )) {
        # The folder name is also the name of the package's torrent on every PC.
        $name = "GameShare-Update-$($update.Flavor)"
        $dir = Join-Path $stage $name
        foreach ($part in $update.Parts) { Add-Folder $part.From $(if ($part.To) { Join-Path $dir $part.To } else { $dir }) }
        Save-Zip $name

        $json = Join-Path $Output "update-$($update.Flavor).json"
        # The key stays in the environment, it is never on a command line where a log could show it.
        & $admin release-sign --folder $dir --version $version --flavor $update.Flavor --zip (Join-Path $Output "$name.zip") --notes-file $notes --out $json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Signing the $($update.Flavor) update failed with exit code $LASTEXITCODE." }
        & $admin release-show --file $json --pub $releasePublicKey | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "update-$($update.Flavor).json does not verify against scripts\release-public.key. GAMESHARE_RELEASE_KEY is not the key the programs are built with." }
        Write-Host ("{0,-28} signed" -f "update-$($update.Flavor).json")
    }
}

Remove-Item -Recurse -Force $stage
