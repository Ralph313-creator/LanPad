# Builds LanPad (Release) and puts a ready-to-copy package in dist\LanPad:
#   LanPad.exe + dlls + lanpad.ini + tools\ (ffmpeg, mpv) + driver\ (ViGEmBus) + install-image.ps1 + README.md
# Copy dist\LanPad to the CCBoot super-client and run install-image.ps1 as Administrator.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet build src\LanPad\LanPad.csproj -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    $release = Join-Path $root "src\LanPad\bin\Release"
    $version = (Get-Item (Join-Path $release "LanPad.exe")).VersionInfo.ProductVersion -replace '\+.*$', ''
    # always the same folder name, so the CCBoot copy step never changes; the version is in LanPad.exe
    $dist = Join-Path $root "dist\LanPad"
    $running = Get-CimInstance Win32_Process -Filter "Name='LanPad.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith((Join-Path $root "dist"), [StringComparison]::OrdinalIgnoreCase) }
    if ($running) { throw "LanPad is running from $(Split-Path $running[0].ExecutablePath) - close it first, then run this script again." }
    # older packages were named dist\LanPad-<version>: remove them
    Get-ChildItem (Join-Path $root "dist") -Directory -Filter "LanPad-*" -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force
        Write-Host "Removed old package $($_.Name)"
    }
    New-Item -ItemType Directory -Force $dist, (Join-Path $dist "driver") | Out-Null
    # /MIR keeps dist an exact copy of the build output (removes stale files); driver\ and games.user.ini are kept.
    robocopy $release $dist /MIR /XD driver /XF *.pdb selftest.log games.user.ini /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copy to $dist failed (robocopy code $LASTEXITCODE)" }
    Copy-Item (Join-Path $root "scripts\install-image.ps1"), (Join-Path $root "README.md"), (Join-Path $root "SUPPORTED-GAMES.txt") $dist -Force
    # The Settings / Controls pages save into the build folder's lanpad.ini: ship the clean default instead.
    Copy-Item (Join-Path $root "lanpad.ini") $dist -Force
    $vigem = Get-ChildItem (Join-Path $root "third_party\_downloads\vigem") -Filter "ViGEmBus*.exe" | Select-Object -First 1
    if ($vigem) { Copy-Item $vigem.FullName (Join-Path $dist "driver") -Force }
    else { Write-Warning "ViGEmBus installer not found in third_party\_downloads\vigem" }

    $size = (Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("Package ready: {0} (LanPad {1}, {2:N0} MB)" -f $dist, $version, $size) -ForegroundColor Green
}
finally {
    Pop-Location
}
