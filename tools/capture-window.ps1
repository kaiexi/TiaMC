Add-Type -AssemblyName System.Drawing

$src = @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WinCap2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Add-Type -TypeDefinition $src -Language CSharp
[WinCap2]::SetProcessDPIAware() | Out-Null

$titleSubstring = $args[0]
$outPath = $args[1]
$width = if ($args.Count -gt 2) { [int]$args[2] } else { 1680 }
$height = if ($args.Count -gt 3) { [int]$args[3] } else { 1010 }
$x = if ($args.Count -gt 4) { [int]$args[4] } else { 20 }
$y = if ($args.Count -gt 5) { [int]$args[5] } else { 20 }

$proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*$titleSubstring*" } | Select-Object -First 1
if (-not $proc) { Write-Host "no window matching '$titleSubstring'"; exit 1 }

$h = $proc.MainWindowHandle
Write-Host "window: '$($proc.MainWindowTitle)' pid=$($proc.Id)"

[WinCap2]::ShowWindow($h, 9) | Out-Null          # SW_RESTORE
[WinCap2]::SetWindowPos($h, [IntPtr]::Zero, $x, $y, $width, $height, 0x0040) | Out-Null   # SWP_SHOWWINDOW
[WinCap2]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 1500

$r = New-Object WinCap2+RECT
[WinCap2]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
Write-Host "rect: $($r.Left),$($r.Top) ${w}x${ht}"

# Capture only the client area so the frame border is not included.
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
$g.Dispose()
$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $outPath (${w}x${ht})"
