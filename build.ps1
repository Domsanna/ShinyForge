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

# Version 1.X.Z from version.txt: X = big updates (Z back to 0), Z = patches / small fixes. Change version.txt before a release.
$version = (Get-Content (Join-Path $root "version.txt") -TotalCount 1).Trim()
if ($version -notmatch '^1\.\d+\.\d+$') { throw "version.txt must hold a version like 1.2.3 (found '$version')" }
Write-Host "ShinyForge $version"

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

# Pokédex tracker spreadsheets (Gen N\(n) Name\Name Pokedex Tracker.xlsm). They are NOT inside the installer (together
# they're over GitHub's 2 GB file limit): each one is published as its own file, tracker-<n>.xlsm, in the
# "spreadsheets-1" release, listed in spreadsheets.json. The installer downloads the ones the user doesn't have yet,
# and ShinyForge downloads a single tracker on demand. dist\sheets holds hard links to the originals (no copies).
# Source: $env:SHINYFORGE_SHEETS, D:\ShinyForge Files\Bot Spreadsheets or Desktop\Bot Spreadsheets.
$sheetsSrc = if ($env:SHINYFORGE_SHEETS) { $env:SHINYFORGE_SHEETS } elseif (Test-Path "D:\ShinyForge Files\Bot Spreadsheets") { "D:\ShinyForge Files\Bot Spreadsheets" } else { Join-Path ([Environment]::GetFolderPath("Desktop")) "Bot Spreadsheets" }
$sheetsOut = New-Item -ItemType Directory -Force (Join-Path $dist "sheets")
$manifest = @()
$pascal = @("procedure AddSheets;", "begin")
if (Test-Path $sheetsSrc) {
    foreach ($f in Get-ChildItem $sheetsSrc -Recurse -File -Include *.xlsm, *.xlsx | Where-Object { -not $_.Name.StartsWith('~$') } | Sort-Object FullName) {
        if ($f.Directory.Name -notmatch '^\((\d+)\)') { Write-Warning "Skipping $($f.FullName) (folder isn't named '(n) Game')"; continue }
        $asset = "tracker-{0:00}{1}" -f [int]$Matches[1], $f.Extension.ToLowerInvariant()
        $rel = $f.FullName.Substring($sheetsSrc.TrimEnd('\').Length + 1)
        $link = Join-Path $sheetsOut $asset
        try { New-Item -ItemType HardLink -Path $link -Target $f.FullName | Out-Null } catch { Copy-Item $f.FullName $link }
        $sha = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifest += [pscustomobject]@{ Asset = $asset; Path = $rel; Sha256 = $sha; Size = $f.Length }
        $pascal += "  AddSheet('$asset', '$($rel.Replace("'", "''"))', '$sha');"
    }
} else { Write-Warning "Spreadsheet folder $sheetsSrc not found - no tracker list for the installer." }
$pascal += "end;"
[IO.File]::WriteAllText((Join-Path $sheetsOut "spreadsheets.json"), (ConvertTo-Json @($manifest) -Depth 3), (New-Object Text.UTF8Encoding $false))
Set-Content (Join-Path $dist "sheets_files.iss") $pascal -Encoding utf8
Write-Host ("{0} trackers listed ({1:N0} MB)" -f $manifest.Count, (($manifest | Measure-Object -Property Size -Sum).Sum / 1MB))

# Installer.
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "C:\Program Files (x86)\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    # Antivirus can hold the new setup file for a moment ("EndUpdateResource failed"): try up to 3 times.
    for ($try = 1; $try -le 3; $try++) {
        & $iscc /Q "/DAppVersion=$version" (Join-Path $root "installer\ShinyForge.iss")
        if ($LASTEXITCODE -eq 0) { break }
        if ($try -eq 3) { throw "installer failed" }
        Write-Warning "Installer build failed (try $try) - retrying in 20 s."
        Start-Sleep 20
    }
}
else { Write-Warning "Inno Setup not found - installer skipped." }

Get-ChildItem $dist -File | Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}}
