# Builds ShinyForge: self-contained app, portable zip and installer (dist\).
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$publish = Join-Path $dist "publish"
# Keep the user's portable Backup Tools folder (FFmpeg, Python, SysBot...) across rebuilds.
$keptBackup = Join-Path $root "_backup_tools_keep"
$oldBackup = Join-Path $dist "ShinyForge Portable\Backup Tools"
if (Test-Path $oldBackup) { if (Test-Path $keptBackup) { Remove-Item $keptBackup -Recurse -Force }; Move-Item $oldBackup $keptBackup }
# rd is more reliable than Remove-Item for ~15,000 sprite files (antivirus / indexer can hold a folder briefly).
for ($i = 0; $i -lt 5 -and (Test-Path $dist); $i++) { cmd /c "rd /s /q `"$dist`" 2>nul"; if (Test-Path $dist) { Start-Sleep 3 } }
if (Test-Path $dist) { throw "Couldn't delete $dist - close any program or Explorer window using it and try again." }

# Build number = how many changes (git commits) ShinyForge has gone through. Version = 1.0.<build>.
$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git -and (Test-Path "C:\Program Files\Git\cmd\git.exe")) { $git = "C:\Program Files\Git\cmd\git.exe" }
$build = 0
if ($git) { try { $build = [int](& $git -C $root rev-list --count HEAD 2>$null) } catch { $build = 0 } }
if ($build -eq 0) { Write-Warning "Not a git repository (or git missing) - building as build 0." }
$version = "1.0.$build"
Write-Host "ShinyForge $version (build $build)"

& "C:\Program Files\dotnet\dotnet.exe" publish (Join-Path $root "ShinyForge\ShinyForge.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$version -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
# The Python hunter for FireRed/LeafGreen on the Switch ships with ShinyForge (PythonBot next to ShinyForge.exe).
Copy-Item (Join-Path $root "PythonBot") (Join-Path $publish "PythonBot") -Recurse
Get-ChildItem (Join-Path $publish "PythonBot") -Recurse -Directory -Filter __pycache__ | Remove-Item -Recurse -Force

# Portable version: same files + portable.flag (settings/data are then kept next to the program).
$portable = Join-Path $dist "ShinyForge Portable"
Copy-Item $publish $portable -Recurse
Set-Content (Join-Path $portable "portable.flag") "ShinyForge portable mode: settings and data are stored in the Data folder next to the program." -Encoding utf8
# Backup Tools folder inside the portable folder (ShinyForge looks here for programs it can't find on the PC).
$backup = New-Item -ItemType Directory -Force (Join-Path $portable "Backup Tools")
Set-Content (Join-Path $backup "README.txt") ("Put programs ShinyForge needs here (FFmpeg, PKHeX, SysBot, 3DS Capture, portable Python, Streamlabs)." +
    " ShinyForge first checks the computer, then this folder (sub-folders included). Excel must be installed normally.") -Encoding utf8
Compress-Archive -Path $portable -DestinationPath (Join-Path $dist "ShinyForge-Portable.zip")
# Put the kept Backup Tools contents back (after zipping, so the download stays small).
if (Test-Path $keptBackup) {
    Remove-Item (Join-Path $portable "Backup Tools") -Recurse -Force
    Move-Item $keptBackup (Join-Path $portable "Backup Tools")
}

# Pokédex tracker spreadsheets bundled with both versions (Gen N\(n) Name\Name Pokedex Tracker.xlsm).
# Source: $env:SHINYFORGE_SHEETS or Desktop\Bot Spreadsheets. Staged in dist\Spreadsheets for the installer and copied
# into the portable folder (not into the zip - they are ~1.3 GB).
$sheetsSrc = if ($env:SHINYFORGE_SHEETS) { $env:SHINYFORGE_SHEETS } else { Join-Path ([Environment]::GetFolderPath("Desktop")) "Bot Spreadsheets" }
$sheets = Join-Path $dist "Spreadsheets"
if (Test-Path $sheetsSrc) {
    robocopy $sheetsSrc $sheets *.xlsm *.xlsx /S /XF "~$*" /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying spreadsheets failed ($LASTEXITCODE)" }
    robocopy $sheets (Join-Path $portable "Spreadsheets") /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying spreadsheets to portable failed ($LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
} else {
    Write-Warning "Spreadsheet folder $sheetsSrc not found - building without bundled spreadsheets."
    New-Item -ItemType Directory -Force $sheets | Out-Null
}

# Installer.
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "C:\Program Files (x86)\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) { & $iscc /Q "/DAppVersion=$version" (Join-Path $root "installer\ShinyForge.iss"); if ($LASTEXITCODE -ne 0) { throw "installer failed" } }
else { Write-Warning "Inno Setup not found - installer skipped." }

Get-ChildItem $dist -File | Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}}
