@echo off
rem Installs the GameShare agent with a double-click. Asks for administrator rights (UAC) by itself, then for the game folders.
rem Arguments go to install-agent.ps1 unchanged, for example: install-agent.bat -GameRoots D:\Games -TrustMode Require
setlocal

rem A .bat cannot elevate itself, so it asks PowerShell to start it again as administrator, with the same arguments.
net session >nul 2>&1
if not errorlevel 1 goto elevated
set "GS_SELF=%~f0"
set "GS_ARGS=%*"
powershell -NoProfile -Command "try { if ($env:GS_ARGS) { Start-Process -FilePath $env:GS_SELF -ArgumentList $env:GS_ARGS -Verb RunAs } else { Start-Process -FilePath $env:GS_SELF -Verb RunAs } } catch { exit 1 }"
if errorlevel 1 (
    echo Administrator rights were not granted, nothing was installed.
    pause
)
exit /b

:elevated
rem Next to scripts\ in the release zip, next to the .ps1 in the repository.
set "GS_SCRIPT=%~dp0scripts\install-agent.ps1"
if not exist "%GS_SCRIPT%" set "GS_SCRIPT=%~dp0install-agent.ps1"
if not "%~1"=="" goto witharguments

echo Where are the games this PC should offer? Several folders are separated by ;
echo Press Enter to skip, folders can be added later in the GameShare app.
set "GS_ROOTS="
set /p "GS_ROOTS=Game folders: "
powershell -NoProfile -ExecutionPolicy Bypass -Command "& $env:GS_SCRIPT -GameRoots @($env:GS_ROOTS -split ';' | ForEach-Object { $_.Trim().Trim([char]34) } | Where-Object { $_ })"
goto done

:witharguments
powershell -NoProfile -ExecutionPolicy Bypass -File "%GS_SCRIPT%" %*

:done
if errorlevel 1 (echo. & echo Installation failed, see above.) else (echo. & echo GameShare is installed.)
pause
