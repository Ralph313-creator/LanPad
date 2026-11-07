<#
  LanPad one-line installer - Created by Raptech Solution
  https://github.com/Ralph313-creator/LanPad

  Installs (or updates to) the latest LanPad release. Run in PowerShell:

    irm https://raw.githubusercontent.com/Ralph313-creator/LanPad/main/install.ps1 | iex

  With the setup wizard instead of the quiet install:

    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/Ralph313-creator/LanPad/main/install.ps1))) -Wizard

  It downloads LanPad-Setup-<version>.exe from the latest GitHub release, checks its SHA-256 against the
  digest GitHub publishes, and runs it as admin. Settings (lanpad.ini, games.user.ini) are kept on update.
  CCBoot: run it on the super-client (image write mode), then save the image.
#>
param(
    [switch]$Wizard
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow with the progress bar in PowerShell 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = 'Ralph313-creator/LanPad'
Write-Host ''
Write-Host '  LanPad installer' -ForegroundColor Cyan
Write-Host '  Created by Raptech Solution' -ForegroundColor DarkCyan
Write-Host ''

$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'LanPad-Installer' }
$asset = $release.assets | Where-Object { $_.name -like 'LanPad-Setup-*.exe' } | Select-Object -First 1
if (-not $asset) { throw "The latest release ($($release.tag_name)) has no LanPad-Setup exe." }

$installed = $null
foreach ($key in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{D0C8B206-9B06-4C19-8A5A-5739687366C6}_is1',
                 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{D0C8B206-9B06-4C19-8A5A-5739687366C6}_is1') {
    $item = Get-ItemProperty $key -ErrorAction SilentlyContinue
    if ($item) { $installed = $item.DisplayVersion; break }
}
if ($installed) { Write-Host "  Installed: v$installed   Latest: $($release.tag_name)" }
else { Write-Host "  Latest: $($release.tag_name)" }

$file = Join-Path $env:TEMP $asset.name
Write-Host ("  Downloading {0} ({1:N0} MB)..." -f $asset.name, ($asset.size / 1MB))
Invoke-WebRequest $asset.browser_download_url -OutFile $file -UseBasicParsing -Headers @{ 'User-Agent' = 'LanPad-Installer' }

if ($asset.digest -like 'sha256:*') {
    $want = $asset.digest.Substring(7).ToLowerInvariant()
    $got = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($got -ne $want) { Remove-Item $file -Force; throw "Download is damaged (SHA-256 mismatch). Run the installer again." }
    Write-Host '  SHA-256 ok' -ForegroundColor Green
} elseif ((Get-Item $file).Length -ne $asset.size) {
    Remove-Item $file -Force; throw 'Download is incomplete. Run the installer again.'
}

$setupArgs = if ($Wizard) { @() } else { @('/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS') }
Write-Host '  Installing (Windows asks for admin)...'
$p = Start-Process -FilePath $file -ArgumentList $setupArgs -Verb RunAs -Wait -PassThru
Remove-Item $file -Force -ErrorAction SilentlyContinue
if ($p.ExitCode -ne 0) { throw "Setup failed (exit code $($p.ExitCode))." }

Write-Host ''
Write-Host "  LanPad $($release.tag_name) installed to C:\LanPad - open it from the desktop shortcut." -ForegroundColor Green
Write-Host ''
