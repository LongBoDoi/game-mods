# LemonCoop dual-monitor helper. Launched by the mod (F8); can also be run by hand.
# 1) Computes a window rectangle covering both monitors and writes it to dualmonitor.txt for the mod.
# 2) Waits for the game window to switch to that size (the mod runs r.SetRes), then makes the
#    window borderless and moves it over both monitors.
# Usage: DualMonitor.ps1 [-restore]
param([switch]$restore)
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public class LCWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hh, uint f);
}
"@
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$file = Join-Path $dir "dualmonitor.txt"
$GWL_STYLE = -16; $WS_CAPTION = 0x00C00000; $WS_THICKFRAME = 0x00040000; $WS_SYSMENU = 0x00080000
$SWP_FRAMECHANGED = 0x0020; $SWP_SHOWWINDOW = 0x0040; $SWP_NOZORDER = 0x0004

function Get-GameWindow { $p = Get-Process -Name "LemonCake-Win64-Shipping" -ErrorAction SilentlyContinue; if ($p) { return $p.MainWindowHandle } return [IntPtr]::Zero }

if ($restore) {
  # The game re-applies its own style when it returns to windowed-fullscreen; just put the border back.
  $h = Get-GameWindow; if ($h -ne [IntPtr]::Zero) {
    $style = [LCWin]::GetWindowLong($h, $GWL_STYLE) -bor $WS_CAPTION -bor $WS_THICKFRAME -bor $WS_SYSMENU
    [LCWin]::SetWindowLong($h, $GWL_STYLE, $style) | Out-Null
    [LCWin]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, $SWP_FRAMECHANGED -bor $SWP_NOZORDER -bor 0x0001 -bor 0x0002) | Out-Null
  }
  exit 0
}

$screens = [System.Windows.Forms.Screen]::AllScreens
$prim = $screens | Where-Object { $_.Primary } | Select-Object -First 1
$sec = $screens | Where-Object { -not $_.Primary } | Select-Object -First 1
if (-not $sec) { "only one monitor"; exit 1 }
# Window: full combined width; vertical extent = the overlap band of both monitors so HUD corners stay visible.
$x = [Math]::Min($prim.Bounds.Left, $sec.Bounds.Left)
$right = [Math]::Max($prim.Bounds.Right, $sec.Bounds.Right)
$y = [Math]::Max($prim.Bounds.Top, $sec.Bounds.Top)
$bottom = [Math]::Min($prim.Bounds.Bottom, $sec.Bounds.Bottom)
$w = $right - $x; $hgt = $bottom - $y
# Which monitor is on the left decides the split fractions. P1 belongs on the primary.
if ($prim.Bounds.Left -lt $sec.Bounds.Left) { $leftMon = $prim; $rightMon = $sec } else { $leftMon = $sec; $rightMon = $prim }
$leftFrac = $leftMon.Bounds.Width / $w
$rightFrac = 1 - $leftFrac
if ($prim -eq $leftMon) { $p1 = @(0, $leftFrac); $p2 = @($leftFrac, $rightFrac) } else { $p1 = @($leftFrac, $rightFrac); $p2 = @(0, $leftFrac) }
$line = "{0} {1} {2} {3} {4:F5} {5:F5} {6:F5} {7:F5}" -f $w, $hgt, $x, $y, $p1[0], $p1[1], $p2[0], $p2[1]
Set-Content -Path $file -Value $line -Encoding ASCII
"layout: window ${w}x${hgt} at ($x,$y); P1 slot origin/size $($p1 -join '/'); P2 slot $($p2 -join '/')"

# Wait for the mod to resize the game (client size == w x hgt), then go borderless and move.
for ($i = 0; $i -lt 120; $i++) {
  Start-Sleep -Milliseconds 500
  $h = Get-GameWindow; if ($h -eq [IntPtr]::Zero) { continue }
  $r = New-Object LCWin+RECT; [LCWin]::GetClientRect($h, [ref]$r) | Out-Null
  if (($r.R - $r.L) -eq $w -and ($r.B - $r.T) -eq $hgt) {
    $style = [LCWin]::GetWindowLong($h, $GWL_STYLE) -band (-bnot ($WS_CAPTION -bor $WS_THICKFRAME -bor $WS_SYSMENU))
    [LCWin]::SetWindowLong($h, $GWL_STYLE, $style) | Out-Null
    [LCWin]::SetWindowPos($h, [IntPtr]::Zero, $x, $y, $w, $hgt, $SWP_FRAMECHANGED -bor $SWP_SHOWWINDOW -bor $SWP_NOZORDER) | Out-Null
    "applied borderless span"; exit 0
  }
}
"timed out waiting for the game window to resize"; exit 2
