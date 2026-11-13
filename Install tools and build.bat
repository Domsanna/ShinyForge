@echo off
title ShinyForge - install tools and build
cd /d "%~dp0"
echo === Installing .NET 10 SDK (Microsoft, via winget) ===
if exist "C:\Program Files\dotnet\sdk\10.*" (echo .NET 10 SDK already installed.) else (
  winget install --id Microsoft.DotNet.SDK.10 -e --accept-package-agreements --accept-source-agreements
)
echo.
echo === Installing Inno Setup 6 (jrsoftware.org, via winget) ===
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" (echo Inno Setup already installed.) else if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" (echo Inno Setup already installed.) else (
  winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements
)
echo.
echo === Building ShinyForge (close ShinyForge first!) ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" > "%~dp0build.log" 2>&1
type "%~dp0build.log"
echo.
echo === Done. Output is in the dist folder. build.log has the full output. ===
pause
