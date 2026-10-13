# LanPad one-time install. Run as Administrator.
# On diskless CCBoot PCs run it ONCE on the super-client (image write mode), then save the image.
# It is safe to run again (it updates files and replaces its own firewall rules).
#
#   powershell -ExecutionPolicy Bypass -File install-image.ps1            (installs to C:\LanPad)
#   powershell -ExecutionPolicy Bypass -File install-image.ps1 -Target D:\LanPad
param(
    [string]$Target = "C:\LanPad",
    [switch]$NoShortcut
)
$ErrorActionPreference = 'Stop'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host "Please run this script as Administrator." -ForegroundColor Red; exit 1 }

# The package folder is the folder that holds LanPad.exe (this script sits next to it).
$package = $PSScriptRoot
if (-not (Test-Path (Join-Path $package "LanPad.exe"))) { $package = Split-Path $PSScriptRoot -Parent }
if (-not (Test-Path (Join-Path $package "LanPad.exe"))) {
    Write-Host "LanPad.exe not found next to this script. Run it from the LanPad package folder." -ForegroundColor Red
    exit 1
}

Write-Host "== 1/4 Copy LanPad to $Target"
if ((Resolve-Path $package).Path.TrimEnd('\') -ne $Target.TrimEnd('\')) {
    # /E copies subfolders. Kept as they are in the target: lanpad.ini (settings) and games.user.ini (your exe paths).
    # games.ini (the shipped game list) is always updated.
    robocopy $package $Target /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP /XF lanpad.ini games.user.ini selftest.log *.pdb | Out-Null
    if ($LASTEXITCODE -ge 8) { Write-Host "   copy failed - is LanPad running from $Target? Close it and try again." -ForegroundColor Red; exit 1 }
    if (-not (Test-Path (Join-Path $Target "lanpad.ini"))) { Copy-Item (Join-Path $package "lanpad.ini") $Target }
}
Write-Host "   done"

Write-Host "== 2/4 Virtual controller driver (ViGEmBus)"
if (Get-Service ViGEmBus -ErrorAction SilentlyContinue) {
    Write-Host "   already installed"
} else {
    $installer = Get-ChildItem (Join-Path $package "driver") -Filter "ViGEmBus*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $installer) { Write-Host "   ViGEmBus installer not found in the driver folder!" -ForegroundColor Red; exit 1 }
    $sig = Get-AuthenticodeSignature $installer.FullName
    if ($sig.Status -ne 'Valid') { Write-Host "   Installer signature is not valid ($($sig.Status)); not installing." -ForegroundColor Red; exit 1 }
    $p = Start-Process -FilePath $installer.FullName -ArgumentList "/quiet /norestart" -Wait -PassThru
    if (Get-Service ViGEmBus -ErrorAction SilentlyContinue) { Write-Host "   installed" }
    else { Write-Host "   install failed (exit code $($p.ExitCode))" -ForegroundColor Red; exit 1 }
}

Write-Host "== 3/4 Firewall rules"
Get-NetFirewallRule -DisplayName "LanPad*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
$rules = @(
    @{ Name = "LanPad (host and join)"; Program = Join-Path $Target "LanPad.exe" },
    @{ Name = "LanPad stream viewer (mpv)"; Program = Join-Path $Target "tools\mpv\mpv.exe" },
    @{ Name = "LanPad stream viewer (ffplay)"; Program = Join-Path $Target "tools\ffplay.exe" }
)
foreach ($r in $rules) {
    New-NetFirewallRule -DisplayName $r.Name -Direction Inbound -Program $r.Program -Protocol UDP -Action Allow -Profile Any | Out-Null
    Write-Host "   allowed UDP in: $($r.Program)"
}

Write-Host "== 4/4 Shortcut"
if (-not $NoShortcut) {
    $lnk = "C:\Users\Public\Desktop\LanPad.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = Join-Path $Target "LanPad.exe"
    $s.WorkingDirectory = $Target
    $s.Description = "LanPad - play local multiplayer games over LAN"
    $s.Save()
    Write-Host "   $lnk"
}

Write-Host ""
Write-Host "Self-test:"
$t = Start-Process -FilePath (Join-Path $Target "LanPad.exe") -ArgumentList "--selftest" -Wait -PassThru
Get-Content (Join-Path $Target "selftest.log") -ErrorAction SilentlyContinue | Select-String "FAIL|SKIP|CHECKS" | ForEach-Object { "   " + $_.Line }
if ($t.ExitCode -eq 0) { Write-Host "LanPad is ready." -ForegroundColor Green } else { Write-Host "Self-test failed - see selftest.log" -ForegroundColor Red }
