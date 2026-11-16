# Builds ShinyForge for macOS (Apple silicon + Intel) and Linux (x64) into dist-unix\:
#   ShinyForge-macOS-arm64.zip / -x64.zip            ShinyForge.app (drag it to Applications)
#   ShinyForge-macOS-arm64-Portable.zip / -x64-...   ShinyForge.app + portable.flag (settings stay next to it)
#   shinyforge_<version>_amd64.deb                   Ubuntu / Debian package (menu entry, "shinyforge" command)
#   ShinyForge-Linux-x64-Portable.tar.gz             unpack and run ./ShinyForge
# The .NET programs are published here (Windows); packaging runs in WSL (Ubuntu) so Unix permissions are right and the
# macOS app gets its ad-hoc code signature (rcodesign). Needs: .NET 10 SDK, WSL with Ubuntu (zip, dpkg-deb, curl), Python + Pillow.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist-unix"
for ($i = 0; $i -lt 5 -and (Test-Path $dist); $i++) { cmd /c "rd /s /q `"$dist`" 2>nul"; if (Test-Path $dist) { Start-Sleep 3 } }
New-Item -ItemType Directory -Force $dist | Out-Null

# Same version as the Windows build: 1.X.Z from version.txt.
$version = (Get-Content (Join-Path $root "version.txt") -TotalCount 1).Trim()
if ($version -notmatch '^1\.\d+\.\d+$') { throw "version.txt must hold a version like 1.2.3 (found '$version')" }
Write-Host "ShinyForge $version for macOS and Linux"

foreach ($rid in @("osx-arm64", "osx-x64", "linux-x64")) {
    $out = Join-Path $dist "publish-$rid"
    & "C:\Program Files\dotnet\dotnet.exe" publish (Join-Path $root "ShinyForge.Desktop\ShinyForge.Desktop.csproj") -c Release -r $rid --self-contained true `
        -p:Version=$version -p:UseAppHost=true -o $out -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish $rid failed" }
    Copy-Item (Join-Path $root "PythonBot") (Join-Path $out "PythonBot") -Recurse
    Get-ChildItem (Join-Path $out "PythonBot") -Recurse -Directory -Filter __pycache__ | Remove-Item -Recurse -Force
    Get-ChildItem $out -Recurse -Filter *.pdb | Remove-Item -Force
}

# macOS icon (.icns) from the app's PNG logo.
python -c "from PIL import Image; Image.open(r'$root\ShinyForge\Assets\ShinyForge.png').convert('RGBA').resize((512,512)).save(r'$dist\ShinyForge.icns')"
if ($LASTEXITCODE -ne 0) { throw "icon failed" }

# Package in WSL (Ubuntu).
$wslDist = (& wsl.exe -d Ubuntu-24.04 -- wslpath -a "$dist").Trim()
$wslScript = (& wsl.exe -d Ubuntu-24.04 -- wslpath -a (Join-Path $root "installer\package-unix.sh")).Trim()
& wsl.exe -d Ubuntu-24.04 -- bash "$wslScript" "$wslDist" "$version" "$version"
if ($LASTEXITCODE -ne 0) { throw "packaging failed" }
Get-ChildItem $dist -File | Where-Object { $_.Extension -ne ".icns" } | Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}}
