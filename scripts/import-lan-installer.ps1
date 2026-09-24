<#
.SYNOPSIS
  Turns the old LAN party installer (LAN_PARTY_INSTALACE_V2.bat, hry_install_v2\*.7z) into a GameShare game root.

.DESCRIPTION
  Reads the source folder and never changes it. For every <Game>-install.7z:
    - extracts it into <Target>\<Game> (through <Target>\.<Game>.partial, which the scanner ignores, so a
      half-extracted game is never shared),
    - reads the shortcuts the old installer put on the desktop and <Game>-meta.json, and writes gameshare.json:
      the programs to start ("launch"), and what the old installer did after copying ("setup"): the .reg import
      with its cleanup key, the compatibility mode, the profile copied to Documents, and the shared redistributables.
  The shared _redist folder becomes <Target>\_Redist, a package of its own that GameShare shares like a game.

  A game whose folder already exists is not extracted again. Its gameshare.json is written only when it has none,
  or with -OverwriteDefinitions, so hand-tuned definitions survive a second run.

.EXAMPLE
  scripts\import-lan-installer.ps1 -Source 'D:\Instalace V2\hry_install_v2' -Target C:\Hry -Only 'BaboViolent 2','Battlefield 2'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Target,
    [string[]]$Only = @(),
    [switch]$OverwriteDefinitions,
    [int]$ReserveGB = 3
)

$ErrorActionPreference = 'Stop'

# Where the old installer put every game. Paths in shortcuts and .reg files start with it.
$oldRoot = 'C:\Games'
$version = 'lan-v2'
# Everything the old installer offered to every game from the shared _redist folder. Each one is skipped on a PC that has it.
$sharedRequires = @('directx9', 'vcredist2005_x86', 'dotnet40')

$source = (Resolve-Path $Source).Path
$sevenZipSource = Join-Path $source '7za.exe'
if (-not (Test-Path $sevenZipSource -PathType Leaf)) { throw "7za.exe not found in '$source'." }
if (-not (Test-Path $Target)) { New-Item -ItemType Directory -Path $Target | Out-Null }
$target = (Resolve-Path $Target).Path

# Run 7za from a local copy: a share or USB stick can block starting programs.
$sevenZip = Join-Path $env:TEMP 'gameshare-7za.exe'
Copy-Item $sevenZipSource $sevenZip -Force

$utf8 = New-Object System.Text.UTF8Encoding($false)
# Czech labels without non-ASCII characters in this file, so Windows PowerShell 5.1 reads it the same without a BOM.
$playLabel = "Hr$([char]0xE1)t"
$redistName = "Sd$([char]0xED)len$([char]0xE9) knihovny (DirectX, Visual C++, .NET)"
$shell = New-Object -ComObject WScript.Shell

function Slug([string]$name) {
    $slug = ($name.ToLowerInvariant() -replace '[^a-z0-9._-]+', '-').Trim('-', '.', '_')
    if ($slug.Length -eq 0) { return 'game' }
    if ($slug.Length -gt 64) { return $slug.Substring(0, 64) }
    return $slug
}

function Write-Json([string]$path, $value) {
    [System.IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 10), $utf8)
}

function Read-Meta([string]$game) {
    $path = Join-Path $source "$game-meta.json"
    if (-not (Test-Path $path -PathType Leaf)) { return $null }
    return Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

# The game-relative path of a program the shortcut points at, or $null.
# A shortcut made on another PC points somewhere else (C:\GOG Games\..., a desktop folder), so when the path does not
# start with C:\Games\<Game>, the longest tail of it that exists in the game folder is used.
function Resolve-InGame([string]$gameDir, [string]$game, [string]$path) {
    if (-not $path) { return $null }
    $prefix = "$oldRoot\$game\"
    if ($path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        $rel = $path.Substring($prefix.Length)
        if (Test-Path (Join-Path $gameDir $rel)) { return $rel }
    }
    $parts = $path -split '\\'
    for ($i = 1; $i -lt $parts.Count; $i++) {
        $rel = ($parts[$i..($parts.Count - 1)] -join '\')
        if ($rel -and (Test-Path (Join-Path $gameDir $rel))) { return $rel }
    }
    return $null
}

function Read-Shortcuts([string]$gameDir, [string]$game) {
    $entries = New-Object System.Collections.ArrayList
    $seen = @{}
    # The shortcut named like the game is the one the old installer put on the desktop, so it goes first.
    $files = @(Get-ChildItem $gameDir -Filter *.lnk -File) + @(Get-ChildItem $gameDir -Filter *.lnk -File -Recurse -Depth 1 | Where-Object { $_.DirectoryName -ne $gameDir })
    $files = @($files | Sort-Object @{ Expression = { if ($_.BaseName -eq $game -and $_.DirectoryName -eq $gameDir) { 0 } else { 1 } } }, FullName)
    foreach ($file in $files) {
        $lnk = $shell.CreateShortcut($file.FullName)
        $exe = Resolve-InGame $gameDir $game $lnk.TargetPath
        if (-not $exe -or [IO.Path]::GetExtension($exe) -ne '.exe') {
            Write-Host "    shortcut '$($file.Name)' -> '$($lnk.TargetPath)' is not a program in the game folder, skipped" -ForegroundColor DarkYellow
            continue
        }
        $key = ($exe + '|' + $lnk.Arguments).ToLowerInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true

        $workDir = Resolve-InGame $gameDir $game $lnk.WorkingDirectory
        if (-not $workDir) { $workDir = Split-Path $exe -Parent }
        if (-not $workDir) { $workDir = '.' }
        $entry = [ordered]@{
            name             = if ($entries.Count -eq 0) { $playLabel } else { $file.BaseName }
            executable       = $exe.Replace('\', '/')
            workingDirectory = $workDir.Replace('\', '/')
        }
        if ($lnk.Arguments) { $entry.arguments = $lnk.Arguments.Trim() }
        [void]$entries.Add($entry)
    }
    return , $entries
}

function Registry-Path([string]$psPath) {
    # meta.json has PowerShell paths: Registry::HKEY_LOCAL_MACHINE\SOFTWARE\...
    $p = $psPath -replace '^Registry::', ''
    $p = $p -replace '^HKEY_LOCAL_MACHINE\\', 'HKLM\' -replace '^HKEY_CURRENT_USER\\', 'HKCU\'
    return $p
}

function Build-Definition([string]$gameDir, [string]$game) {
    $meta = Read-Meta $game
    $launch = Read-Shortcuts $gameDir $game

    $setup = [ordered]@{ requires = $sharedRequires }
    $registry = New-Object System.Collections.ArrayList
    foreach ($reg in @('registry-import.reg', 'register.reg')) {
        if (Test-Path (Join-Path $gameDir $reg) -PathType Leaf) {
            $step = [ordered]@{ file = $reg; originalPath = "$oldRoot\$game" }
            if ($meta -and $meta.registry_cleanup -and $reg -eq 'registry-import.reg') { $step.cleanup = Registry-Path $meta.registry_cleanup }
            [void]$registry.Add($step)
        }
    }
    if ($registry.Count) { $setup.registry = $registry }

    if ($meta -and $meta.kompatibilita -and $meta.exe_soubor) {
        $exe = Resolve-InGame $gameDir $game "$oldRoot\$game\$($meta.exe_soubor)"
        if ($exe) {
            $setup.compatibility = @([ordered]@{ executable = $exe.Replace('\', '/'); layers = 'WINXPSP3' })
            # The old installer added RUNASADMIN to the same layer. Here it is a property of the program that starts.
            foreach ($e in $launch) { if ($e.executable -ieq $exe.Replace('\', '/')) { $e.runAsAdmin = $true } }
        }
    }

    $profileDir = "$game-profile"
    if (Test-Path (Join-Path $gameDir $profileDir) -PathType Container) {
        $setup.profile = @([ordered]@{ from = $profileDir; to = "{Documents}\$game" })
    }

    $redist = Join-Path $gameDir '_redist'
    if (Test-Path $redist -PathType Container) {
        $steps = New-Object System.Collections.ArrayList
        foreach ($f in Get-ChildItem $redist -File | Where-Object { $_.Extension -in '.exe', '.msi' } | Sort-Object Name) {
            [void]$steps.Add([ordered]@{ file = "_redist/$($f.Name)" })
        }
        if ($steps.Count) { $setup.redist = $steps }
    }

    $def = [ordered]@{ gameId = Slug $game; name = $game; version = $version }
    if ($launch.Count) { $def.launch = $launch }
    $def.setup = $setup
    return $def
}

function Write-Definition([string]$gameDir, $definition) {
    $path = Join-Path $gameDir 'gameshare.json'
    if ((Test-Path $path) -and -not $OverwriteDefinitions) {
        Write-Host "    gameshare.json exists, kept (use -OverwriteDefinitions to replace it)" -ForegroundColor DarkGray
        return
    }
    Write-Json $path $definition
    Write-Host "    wrote gameshare.json" -ForegroundColor Green
}

# ---------------------------------------------------------------------------------------------------------------------

$report = New-Object System.Collections.ArrayList
$archives = @(Get-ChildItem $source -Filter '*-install.7z' -File | Sort-Object Length)
foreach ($archive in $archives) {
    $game = $archive.BaseName -replace '-install$', ''
    if ($Only.Count -and $Only -notcontains $game) { continue }
    $gameDir = Join-Path $target $game
    Write-Host "== $game" -ForegroundColor Cyan

    if (-not (Test-Path $gameDir)) {
        $meta = Read-Meta $game
        $neededMB = if ($meta -and $meta.velikost_mb) { [int]$meta.velikost_mb } else { [int]($archive.Length / 1MB * 2) }
        $freeMB = [int]((Get-PSDrive ($target.Substring(0, 1))).Free / 1MB)
        if ($freeMB -lt $neededMB + $ReserveGB * 1024) {
            Write-Host "    not enough space: needs $neededMB MB + $ReserveGB GB reserve, $freeMB MB free. Stopping." -ForegroundColor Red
            break
        }

        $partial = Join-Path $target ".$game.partial"
        if (Test-Path $partial) { Remove-Item $partial -Recurse -Force }
        Write-Host "    extracting $([math]::Round($archive.Length / 1GB, 1)) GB..."
        & $sevenZip x $archive.FullName "-o$partial" -y "-mmt=$([Environment]::ProcessorCount)" -bso0 -bsp1
        if ($LASTEXITCODE -ne 0) { throw "7za failed on '$($archive.Name)' with exit code $LASTEXITCODE." }

        # Build the definition while the old shortcuts are still there, then drop the top-level ones: they point at C:\Games.
        $definition = Build-Definition $partial $game
        Get-ChildItem $partial -Filter *.lnk -File | Remove-Item -Force
        Rename-Item $partial $game
        Write-Definition $gameDir $definition
    } else {
        Write-Host "    folder exists, not extracted" -ForegroundColor DarkGray
        $definition = Build-Definition $gameDir $game
        Write-Definition $gameDir $definition
    }

    $d = Get-Content (Join-Path $gameDir 'gameshare.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $steps = @()
    if ($d.setup.registry) { $steps += 'registry' }
    if ($d.setup.compatibility) { $steps += 'compatibility' }
    if ($d.setup.profile) { $steps += 'profile' }
    if ($d.setup.redist) { $steps += 'redist' }
    [void]$report.Add([pscustomobject]@{
        Game   = $game
        Start  = if ($d.launch) { ($d.launch | ForEach-Object { $_.executable + $(if ($_.runAsAdmin) { ' (admin)' } else { '' }) }) -join ', ' } else { '(none)' }
        Setup  = $steps -join ', '
    })
}

# The shared redistributables, as a package of their own.
$redistSource = Join-Path $source '_redist'
$redistDir = Join-Path $target '_Redist'
if ((Test-Path $redistSource) -and -not $Only.Count) {
    Write-Host "== _Redist" -ForegroundColor Cyan
    if (-not (Test-Path $redistDir)) {
        Copy-Item $redistSource $redistDir -Recurse
        Write-Host "    copied"
    }
    $custom = @{}
    $redistJson = Join-Path $redistSource 'redist.json'
    if (Test-Path $redistJson) { (Get-Content $redistJson -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $custom[$_.Name] = $_.Value } }

    $provides = [ordered]@{
        directx9 = [ordered]@{
            name = 'DirectX 9.0c (June 2010)'; file = 'directx/DXSETUP.exe'; args = '/silent'
            installedIf = [ordered]@{ file = '{SysWOW64}\d3dx9_43.dll' }
        }
        vcredist2005_x86 = [ordered]@{
            name = 'Visual C++ 2005 (x86)'; file = 'vcredist_x86.exe'
            args = if ($custom['vcredist_x86.exe']) { $custom['vcredist_x86.exe'] } else { '/q' }
            installedIf = [ordered]@{ uninstall = 'Microsoft Visual C++ 2005 Redistributable*' }
        }
        dotnet40 = [ordered]@{
            name = '.NET Framework 4'; file = 'dotNetFx40_Full_setup.exe'; args = '/q /norestart'
            installedIf = [ordered]@{ registryKey = 'HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'; registryValue = 'Install' }
        }
    }
    Write-Definition $redistDir ([ordered]@{
        gameId = 'redist'; name = $redistName; version = $version; kind = 'redist'; provides = $provides
    })
}

Remove-Item $sevenZip -Force -ErrorAction SilentlyContinue
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null

Write-Host ''
$report | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Host
