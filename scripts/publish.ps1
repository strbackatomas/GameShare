<#
.SYNOPSIS
  Builds the agent and the client into artifacts\ as self-contained folders. Nothing needs to be installed on the target PCs.
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

foreach ($app in @(@{ Name = 'agent'; Project = 'GameShare.Agent' }, @{ Name = 'client'; Project = 'GameShare.Client' })) {
    $target = Join-Path $Output $app.Name
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    dotnet publish (Join-Path $PSScriptRoot "..\src\$($app.Project)") -c Release -r $Runtime --self-contained true -o $target
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of $($app.Project) failed with exit code $LASTEXITCODE" }

    $size = (Get-ChildItem $target -Recurse | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("Published {0} to {1} ({2:N0} MB)" -f $app.Name, (Resolve-Path $target), $size)
}
