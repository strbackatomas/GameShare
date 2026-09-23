<#
.SYNOPSIS
  Builds the agent, the client and the administrator's tool into artifacts\, each in two forms.

.DESCRIPTION
  artifacts\<name>          Self-contained. Nothing needs to be installed on the target PC, just copy the folder. Bigger.
  artifacts\<name>-net10    Needs the .NET 10 runtime already installed on the target PC. Smaller, about a fifth of the size.
                            One installer covers the agent and the client: the ASP.NET Core Runtime 10.0 (x64), it includes
                            the plain .NET runtime too. https://dotnet.microsoft.com/download/dotnet/10.0
  artifacts\standalone      GameShare-LanParty.exe: agent and client bundled into one file, no service install and no
                            admin rights needed. For a LAN-party guest, see "Standalone (LAN party) build" in design-notes.md.

  install-agent.ps1 uses artifacts\agent and artifacts\client by default, the self-contained ones, unless told otherwise.
  The administrator's tools (gameshare-admin, the command line, and gameshare-admin-gui, the graphical one) are for the
  administrator's own PC only, not for the players'.
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

# The one source of truth for the version is src\Directory.Build.props (see "Verzování" in README.md), read here just to show it.
$version = ([xml](Get-Content (Join-Path $PSScriptRoot '..\src\Directory.Build.props'))).Project.PropertyGroup.Version
Write-Host "Building GameShare v$version`n"

function Publish-App($project, $target, [bool]$selfContained, [bool]$singleFile = $false) {
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    $flag = if ($selfContained) { 'true' } else { 'false' }  # PowerShell would otherwise pass "True"/"False", dotnet wants lowercase
    $extra = if ($singleFile) { @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') } else { @() }
    # dotnet's own console output must not leak into the function's return value, or the caller gets an object array instead of a number.
    dotnet publish (Join-Path $PSScriptRoot "..\src\$project") -c Release -r $Runtime --self-contained $flag @extra -o $target | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of $project failed with exit code $LASTEXITCODE" }
    [double]$size = (Get-ChildItem $target -Recurse | Measure-Object Length -Sum).Sum / 1MB
    return $size
}

function Format-Size([double]$mb) { if ($mb -lt 1) { "{0:N0} KB" -f ($mb * 1024) } else { "{0:N0} MB" -f $mb } }

foreach ($app in @(
    @{ Name = 'agent'; Project = 'GameShare.Agent' },
    @{ Name = 'client'; Project = 'GameShare.Client' },
    @{ Name = 'admin'; Project = 'GameShare.Admin' },
    @{ Name = 'admin-gui'; Project = 'GameShare.AdminGui' }
)) {
    $full = Publish-App $app.Project (Join-Path $Output $app.Name) $true
    Write-Host ("Published {0} to artifacts\{0} ({1}, self-contained, nothing to install)" -f $app.Name, (Format-Size $full))

    $slim = Publish-App $app.Project (Join-Path $Output "$($app.Name)-net10") $false
    Write-Host ("Published {0} to artifacts\{0}-net10 ({1}, needs the .NET 10 runtime installed)" -f $app.Name, (Format-Size $slim))
}

Write-Host "`nThe -net10 builds need the ASP.NET Core Runtime 10.0 (x64) on the target PC: https://dotnet.microsoft.com/download/dotnet/10.0"

# One portable file: agent and client bundled together, no install, no admin rights. For a LAN-party guest.
$standalone = Publish-App 'GameShare.Standalone' (Join-Path $Output 'standalone') $true $true
Write-Host ("`nPublished standalone to artifacts\standalone\GameShare-LanParty.exe ({0}, one file, nothing to install)" -f (Format-Size $standalone))

Write-Host "`nAll of it is v$version. Right-click an exe, Properties, Details shows the same number, so a mismatched PC is easy to spot."
