# Downloads the third-party tools LanPad ships with into third_party\ (git-ignored), from official sources:
#   third_party\ffmpeg.exe, ffplay.exe            FFmpeg essentials build (gyan.dev, GitHub mirror GyanD/codexffmpeg)
#   third_party\mpv\                              mpv (shinchiro/mpv-winbuild-cmake)
#   third_party\_downloads\vigem\ViGEmBus*.exe    ViGEmBus driver installer (nefarius/ViGEmBus, Authenticode-signed)
# Versions are pinned; every download is checked against its SHA-256 (ViGEmBus: its signature) before use.
# Already-present files are kept; -Force downloads everything again.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow with the progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path $PSScriptRoot -Parent
$tp = Join-Path $root "third_party"
$dl = Join-Path $tp "_downloads"
New-Item -ItemType Directory -Force $tp, $dl | Out-Null

$ffmpeg = @{ Url = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip"
             Sha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba" }
$mpv    = @{ Url = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261019/mpv-x86_64-20261019-git-6a262d11f6.7z"
             Sha256 = "e23cd1622ad299322fdf37a5405e0ff541fe2031b01487b059580e3aa27c2ba9" }
$vigem  = @{ Url = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe"
             Signer = "Nefarius Software Solutions" }

function Get-File($url, $dir, $sha256) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    $file = Join-Path $dir ([IO.Path]::GetFileName($url))
    if ($Force -or -not (Test-Path $file)) {
        Write-Host "   downloading $url"
        Invoke-WebRequest $url -OutFile "$file.part" -UseBasicParsing
        Move-Item "$file.part" $file -Force
    }
    if ($sha256) {
        $got = (Get-FileHash $file -Algorithm SHA256).Hash
        if ($got -ne $sha256) { Remove-Item $file; throw "SHA-256 mismatch for $file (got $got) - deleted, run again" }
    }
    $file
}

function Expand-To($archive, $dest) {
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory -Force $dest | Out-Null
    if ($archive -like "*.7z") {
        # Windows tar can't read LZMA .7z: needs 7-Zip (winget install 7zip.7zip)
        $7z = @((Get-Command 7z -ErrorAction SilentlyContinue).Source, "$env:ProgramFiles\7-Zip\7z.exe") |
            Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        if (-not $7z) { throw "7-Zip is needed to extract $archive - install it: winget install 7zip.7zip" }
        & $7z x $archive "-o$dest" -y -bso0 -bsp0
    } else {
        tar -xf $archive -C $dest
    }
    if ($LASTEXITCODE -ne 0) { throw "could not extract $archive" }
}

Write-Host "== FFmpeg"
if ($Force -or -not (Test-Path (Join-Path $tp "ffmpeg.exe")) -or -not (Test-Path (Join-Path $tp "ffplay.exe"))) {
    $zip = Get-File $ffmpeg.Url (Join-Path $dl "ffmpeg") $ffmpeg.Sha256
    $x = Join-Path $dl "ffmpeg\x"
    Expand-To $zip $x
    foreach ($exe in "ffmpeg.exe", "ffplay.exe") {
        $src = Get-ChildItem $x -Recurse -Filter $exe | Select-Object -First 1
        if (-not $src) { throw "$exe not found in $zip" }
        Copy-Item $src.FullName $tp -Force
    }
    Remove-Item $x -Recurse -Force
}
Write-Host "   $(& (Join-Path $tp 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1)"

Write-Host "== mpv"
if ($Force -or -not (Test-Path (Join-Path $tp "mpv\mpv.exe"))) {
    $7z = Get-File $mpv.Url (Join-Path $dl "mpv") $mpv.Sha256
    Expand-To $7z (Join-Path $tp "mpv")
    if (-not (Test-Path (Join-Path $tp "mpv\mpv.exe"))) { throw "mpv.exe not found after extracting $7z" }
}
Write-Host "   $(& (Join-Path $tp 'mpv\mpv.exe') --version | Select-Object -First 1)"

Write-Host "== ViGEmBus"
$exe = Get-File $vigem.Url (Join-Path $dl "vigem") $null
$sig = Get-AuthenticodeSignature $exe
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike "*$($vigem.Signer)*") {
    Remove-Item $exe
    throw "ViGEmBus installer signature check failed ($($sig.Status), $($sig.SignerCertificate.Subject)) - deleted"
}
Write-Host "   $([IO.Path]::GetFileName($exe)) signed by $($sig.SignerCertificate.Subject)"

Write-Host "Third-party tools ready in $tp" -ForegroundColor Green
