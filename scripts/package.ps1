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
    $dist = Join-Path $root "dist\LanPad-$version"
    $running = Get-CimInstance Win32_Process -Filter "Name='LanPad.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) { throw "LanPad is running from $dist - close it first, then run this script again." }
    New-Item -ItemType Directory -Force $dist, (Join-Path $dist "driver") | Out-Null
    # /MIR keeps dist an exact copy of the build output (removes stale files); driver\ and games.user.ini are kept.
    robocopy $release $dist /MIR /XD driver /XF *.pdb selftest.log games.user.ini /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copy to $dist failed (robocopy code $LASTEXITCODE)" }
    Copy-Item (Join-Path $root "scripts\install-image.ps1"), (Join-Path $root "README.md"), (Join-Path $root "SUPPORTED-GAMES.txt") $dist -Force
    $vigem = Get-ChildItem (Join-Path $root "third_party\_downloads\vigem") -Filter "ViGEmBus*.exe" | Select-Object -First 1
    if ($vigem) { Copy-Item $vigem.FullName (Join-Path $dist "driver") -Force }
    else { Write-Warning "ViGEmBus installer not found in third_party\_downloads\vigem" }

    $size = (Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("Package ready: {0} ({1:N0} MB)" -f $dist, $size) -ForegroundColor Green
}
finally {
    Pop-Location
}
