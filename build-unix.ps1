# Builds ShinyForge for macOS (Apple silicon + Intel) and Linux (x64) into dist-unix\:
#   ShinyForge-macOS-arm64.zip / -x64.zip            ShinyForge.app (drag it to Applications)
#   ShinyForge-macOS-arm64-Portable.zip / -x64-...   ShinyForge.app + portable.flag (settings stay next to it)
#   shinyforge_<version>_amd64.deb                   Ubuntu / Debian package (menu entry, "shinyforge" command)
#   ShinyForge-Linux-x64-Portable.tar.gz             unpack and run ./ShinyForge
# The .NET programs are published here; packaging runs in Linux so Unix permissions are right and the macOS app gets its
# ad-hoc code signature (rcodesign). On Windows that's WSL (Ubuntu); on Linux (GitHub Actions, .github/workflows/build.yml)
# it runs directly. Needs: .NET 10 SDK, zip, dpkg-deb, curl, Python + Pillow (and WSL with Ubuntu on Windows).
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist-unix"
$onWindows = $env:OS -eq "Windows_NT"
$onCi = [bool]$env:CI
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
if ($onWindows) { for ($i = 0; $i -lt 5 -and (Test-Path $dist); $i++) { cmd /c "rd /s /q `"$dist`" 2>nul"; if (Test-Path $dist) { Start-Sleep 3 } } }
elseif (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

# Same version as the Windows build: 1.X.Z from version.txt.
$version = (Get-Content (Join-Path $root "version.txt") -TotalCount 1).Trim()
if ($version -notmatch '^1\.\d+\.\d+$') { throw "version.txt must hold a version like 1.2.3 (found '$version')" }
Write-Host "ShinyForge $version for macOS and Linux"

foreach ($rid in @("osx-arm64", "osx-x64", "linux-x64")) {
    $out = Join-Path $dist "publish-$rid"
    & $dotnet publish ([IO.Path]::Combine($root, "ShinyForge.Desktop", "ShinyForge.Desktop.csproj")) -c Release -r $rid --self-contained true `
        -p:Version=$version -p:UseAppHost=true -p:ContinuousIntegrationBuild=$onCi -o $out -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish $rid failed" }
    Copy-Item (Join-Path $root "PythonBot") (Join-Path $out "PythonBot") -Recurse
    Get-ChildItem (Join-Path $out "PythonBot") -Recurse -Directory -Filter __pycache__ | Remove-Item -Recurse -Force
    Get-ChildItem $out -Recurse -Filter *.pdb | Remove-Item -Force
}

# macOS icon (.icns) from the app's PNG logo.
$python = if ($onWindows) { "python" } else { "python3" }
$png = [IO.Path]::Combine($root, "ShinyForge", "Assets", "ShinyForge.png")
& $python -c "from PIL import Image; Image.open(r'$png').convert('RGBA').resize((512,512)).save(r'$(Join-Path $dist "ShinyForge.icns")')"
if ($LASTEXITCODE -ne 0) { throw "icon failed" }

$script = [IO.Path]::Combine($root, "installer", "package-unix.sh")
if ($onWindows) {
    # Package in WSL (Ubuntu).
    $wslDist = (& wsl.exe -d Ubuntu-24.04 -- wslpath -a "$dist").Trim()
    $wslScript = (& wsl.exe -d Ubuntu-24.04 -- wslpath -a $script).Trim()
    & wsl.exe -d Ubuntu-24.04 -- bash "$wslScript" "$wslDist" "$version" "$version"
}
else { & bash $script $dist $version $version }
if ($LASTEXITCODE -ne 0) { throw "packaging failed" }

# Checksums of the downloads (see build.ps1).
Get-ChildItem $dist -File | Where-Object { $_.Extension -ne ".icns" -and $_.Name -ne "SHA256SUMS-unix.txt" } | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content (Join-Path $dist "SHA256SUMS-unix.txt") -Encoding ascii
Get-ChildItem $dist -File | Where-Object { $_.Extension -ne ".icns" } | Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}}
