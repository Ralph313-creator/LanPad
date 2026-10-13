# LanPad GUI smoke test on ONE PC (does not take over keyboard or screen):
# - copies the built app to a test folder with Pad mode OFF, windowed viewer, fixed PIN 4321
# - starts a Host window and a Join window (joins 127.0.0.1)
# - checks virtual controllers, the stream viewer, and takes a screenshot
# - closes everything and checks it cleaned up
param(
    [string]$Release = (Join-Path $PSScriptRoot "..\src\LanPad\bin\Release"),
    [int]$WaitSeconds = 10
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W32 {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [StructLayout(LayoutKind.Sequential)] public struct XG { public ushort b; public byte lt, rt; public short lx, ly, rx, ry; }
  [StructLayout(LayoutKind.Sequential)] public struct XS { public uint n; public XG g; }
  [DllImport("xinput1_4.dll")] public static extern int XInputGetState(int i, out XS s);
  public static int Pads() { int c = 0; XS s; for (int i = 0; i < 4; i++) if (XInputGetState(i, out s) == 0) c++; return c; }
}
"@

$Release = (Resolve-Path $Release).Path
$test = Join-Path $env:TEMP "LanPadGuiTest"
New-Item -ItemType Directory -Force $test | Out-Null
Copy-Item (Join-Path $Release "*.dll"), (Join-Path $Release "LanPad.exe"), (Join-Path $Release "LanPad.exe.config") $test -Force
if (-not (Test-Path (Join-Path $test "tools"))) {
    New-Item -ItemType Junction -Path (Join-Path $test "tools") -Target (Join-Path $Release "tools") | Out-Null
}
$ini = Get-Content (Join-Path $Release "lanpad.ini")
$ini = $ini -replace '^RoomPin=.*', 'RoomPin=4321' -replace '^AutoPadMode=.*', 'AutoPadMode=false' -replace '^Fullscreen=.*', 'Fullscreen=false'
Set-Content -Path (Join-Path $test "lanpad.ini") -Value $ini

$result = [ordered]@{}
$padsBefore = [W32]::Pads()
$exe = Join-Path $test "LanPad.exe"
$hostProc = Start-Process -FilePath $exe -ArgumentList '--host' -PassThru
Start-Sleep -Seconds 3
$result['pads plugged by host (expect +4)'] = "$padsBefore -> $([W32]::Pads())"
$joinProc = Start-Process -FilePath $exe -ArgumentList '--join', '127.0.0.1', '--pin', '4321' -PassThru
Start-Sleep -Seconds $WaitSeconds

$hostProc.Refresh(); $joinProc.Refresh()
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
[W32]::MoveWindow($hostProc.MainWindowHandle, 0, 0, [int]($screen.Width / 2), [int]($screen.Height / 2), $true) | Out-Null
[W32]::MoveWindow($joinProc.MainWindowHandle, 0, [int]($screen.Height / 2), [int]($screen.Width / 2), [int]($screen.Height / 2), $true) | Out-Null
Start-Sleep -Seconds 2
$result['mpv players running (expect 2)'] = @(Get-Process mpv -ErrorAction SilentlyContinue).Count
$result['ffmpeg running (expect 1)'] = @(Get-Process ffmpeg -ErrorAction SilentlyContinue).Count

$shot = Join-Path $env:TEMP "LanPad-guitest.png"
$full = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
$g = [System.Drawing.Graphics]::FromImage($full); $g.CopyFromScreen($screen.Location, [System.Drawing.Point]::Empty, $screen.Size); $g.Dispose()
$half = New-Object System.Drawing.Bitmap ([int]($screen.Width / 2)), ([int]($screen.Height / 2))
$g = [System.Drawing.Graphics]::FromImage($half); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($full, 0, 0, $half.Width, $half.Height); $g.Dispose()
$half.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png); $full.Dispose(); $half.Dispose()
$result['screenshot'] = $shot

# Close the Join window first (should send Leave and stop the viewer), then the Host.
$joinProc.CloseMainWindow() | Out-Null
if (-not $joinProc.WaitForExit(8000)) { $joinProc.Kill(); $result['join closed'] = 'KILLED (did not close)' } else { $result['join closed'] = 'ok' }
Start-Sleep -Seconds 2
$result['mpv after join closed (expect 0)'] = @(Get-Process mpv -ErrorAction SilentlyContinue).Count
$result['ffmpeg after join left (expect 0)'] = @(Get-Process ffmpeg -ErrorAction SilentlyContinue).Count
$hostProc.CloseMainWindow() | Out-Null
if (-not $hostProc.WaitForExit(8000)) { $hostProc.Kill(); $result['host closed'] = 'KILLED (did not close)' } else { $result['host closed'] = 'ok' }
Start-Sleep -Seconds 2
$result['pads after host closed (expect back to before)'] = "$padsBefore -> $([W32]::Pads())"
$result.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value }
"--- log tail:"; Get-Content (Join-Path $env:TEMP "LanPad.log") -Tail 25
