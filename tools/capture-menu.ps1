# Opens a TIA-MC top level menu with its Alt mnemonic and captures the menu
# popups. WPF menus are separate HWNDs, so they are captured on their own.
param(
    [string]$TitleLike = "TIA-MC",
    [string]$Mnemonic = "p",
    [string[]]$SubMenuMnemonics = @(),
    [string]$OutPath = "artifacts\menu.png"
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class Win32Menu {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  public static void AltKey(char c) {
    keybd_event(0x12, 0, 0, IntPtr.Zero);                 // Alt down
    System.Threading.Thread.Sleep(60);
    keybd_event((byte)char.ToUpper(c), 0, 0, IntPtr.Zero); // key down
    System.Threading.Thread.Sleep(40);
    keybd_event((byte)char.ToUpper(c), 0, 2, IntPtr.Zero); // key up
    System.Threading.Thread.Sleep(40);
    keybd_event(0x12, 0, 2, IntPtr.Zero);                 // Alt up
  }
}
"@ -Language CSharp

$proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*$TitleLike*" } | Select-Object -First 1
if (-not $proc) { Write-Host "no window"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Host "window not found"; exit 1 }

[Win32Menu]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
[Win32Menu]::AltKey($Mnemonic[0])
Start-Sleep -Milliseconds 1000

function Get-MenuPopups($rootElement, $processId) {
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    $list = @()
    foreach ($w in $rootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $c)) {
        $cls = $w.Current.ClassName
        if ($cls -like "*Popup*") { $list += $w }
    }
    return $list
}

Write-Host "popups after opening menu: $((Get-MenuPopups $root $proc.Id).Count)"

# Walk down into submenus by hovering them (submenu opens on hover for WPF menus).
foreach ($m in $SubMenuMnemonics) {
    $found = $null
    foreach ($w in Get-MenuPopups $root $proc.Id) {
        $c = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::MenuItem)
        foreach ($item in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)) {
            $n = $item.Current.Name
            # WPF shows "打开目录(O)"; match on the leading text instead of the mnemonic.
            if ($n -like "$m*") { $found = $item; break }
        }
        if ($found) { break }
    }

    if (-not $found) { Write-Host "submenu '$m' not found"; break }
    $r = $found.Current.BoundingRectangle
    Write-Host "hovering '$($found.Current.Name)' at $([int]($r.X + $r.Width/2)),$([int]($r.Y + $r.Height/2))"
    [Win32Menu]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 1100
}

$popups = Get-MenuPopups $root $proc.Id
$minX = [double]::MaxValue; $minY = [double]::MaxValue; $maxX = 0.0; $maxY = 0.0
foreach ($p in $popups) {
    $r = $p.Current.BoundingRectangle
    if ($r.Width -le 0) { continue }
    $minX = [Math]::Min($minX, $r.X); $minY = [Math]::Min($minY, $r.Y)
    $maxX = [Math]::Max($maxX, $r.Right); $maxY = [Math]::Max($maxY, $r.Bottom)
}

if ($maxX -le 0) { Write-Host "no visible popup to capture"; exit 2 }

$pad = 12
$x = [int]([Math]::Max(0, $minX - $pad)); $y = [int]([Math]::Max(0, $minY - $pad))
$w = [int]($maxX - $minX + 2 * $pad); $h = [int]($maxY - $minY + 2 * $pad)
Write-Host "capturing ${w}x${h} at $x,$y"

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $OutPath"

[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
