# Opens a test Host window (other ports, no controllers plugged, chosen game) and screenshots it.
# Close any open LanPad first: only one LanPad runs at a time.
param(
    [string]$Release = (Join-Path $PSScriptRoot "..\src\LanPad\bin\Release"),
    [string]$Game = "Tekken 7"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
$Release = (Resolve-Path $Release).Path
$test = Join-Path $env:TEMP "LanPadUiCheck"
New-Item -ItemType Directory -Force $test | Out-Null
Copy-Item (Join-Path $Release "*.dll"), (Join-Path $Release "LanPad.exe"), (Join-Path $Release "LanPad.exe.config"), (Join-Path $Release "games.ini") $test -Force
$ini = Get-Content (Join-Path $Release "lanpad.ini")
$ini = $ini -replace '^Port=.*', 'Port=47810' -replace '^StreamPort=.*', 'StreamPort=5010' -replace '^PrePlugPads=.*', 'PrePlugPads=false' -replace '^Game=.*', "Game=$Game" -replace '^RoomPin=.*', 'RoomPin=4321'
Set-Content -Path (Join-Path $test "lanpad.ini") -Value $ini

$p = Start-Process -FilePath (Join-Path $test "LanPad.exe") -ArgumentList '--host' -PassThru
Start-Sleep -Seconds 6
$p.Refresh()
[W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
$r = New-Object W+R; [W]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Ri - $r.L; $h = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size); $g.Dispose()
$shot = Join-Path $env:TEMP "LanPad-uicheck.png"; $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
$p.CloseMainWindow() | Out-Null
if (-not $p.WaitForExit(8000)) { $p.Kill(); "host window had to be killed" } else { "host window closed ok" }
"screenshot: $shot"
"--- log lines:"; Get-Content (Join-Path $env:TEMP "LanPad.log") -Tail 40 | Select-String "Game set|Game search|Host listening|Host stopped|WARN|ERROR" | ForEach-Object { $_.Line }
