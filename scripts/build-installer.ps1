# Builds the one-file installer: dist\LanPad-Setup-<version>.exe
#   1. third-party tools (get-third-party.ps1, skipped when already downloaded)
#   2. Release build + dist\LanPad package (package.ps1)
#   3. Inno Setup compile of installer\LanPad.iss
# Needs the .NET 8 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup) and 7-Zip (winget install 7zip.7zip).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

& (Join-Path $PSScriptRoot "get-third-party.ps1")
& (Join-Path $PSScriptRoot "package.ps1")

$iscc = @((Get-Command ISCC -ErrorAction SilentlyContinue).Source,
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found - install it: winget install JRSoftware.InnoSetup" }

$exe = Join-Path $root "dist\LanPad\LanPad.exe"
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
Get-ChildItem (Join-Path $root "dist") -Filter "LanPad-Setup-*.exe" -ErrorAction SilentlyContinue | Remove-Item -Force

& $iscc /Q "/DAppVersion=$version" (Join-Path $root "installer\LanPad.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (code $LASTEXITCODE)" }

$setup = Join-Path $root "dist\LanPad-Setup-$version.exe"
Write-Host ("Installer ready: {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
