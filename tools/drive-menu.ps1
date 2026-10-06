# Opens a top level menu of the running TiaMC window and screenshots the popup
# itself (a WPF popup is its own HWND, so capturing it directly is reliable).
param(
    [string]$TitleLike = "TIA-MC",
    [string]$MenuHeader = "椤圭洰(P)",
    [string[]]$SubMenuHeaders = @(),
    [string]$OutPath = "artifacts\menu.png"
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

$proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*$TitleLike*" } | Select-Object -First 1
if (-not $proc) { Write-Host "no window"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Host "window not found"; exit 1 }

function Get-Popups($rootElement, $processId) {
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    $list = @()
    foreach ($w in $rootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $c)) {
        $cls = $w.Current.ClassName
        if ($cls -like "*Popup*" -or $cls -like "*Menu*") { $list += $w }
    }
    return $list
}

function Find-InPopups($rootElement, $processId, $name) {
    foreach ($w in Get-Popups $rootElement $processId) {
        $c = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $found = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if ($found) { return @{ Element = $found; Popup = $w } }
    }
    return $null
}

# Open the top level menu by clicking it (Invoke does not open WPF menus).
$menu = $null
$mi = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $MenuHeader)),
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::MenuItem)))
$menu = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $mi)
if (-not $menu) { Write-Host "menu '$MenuHeader' not found"; exit 1 }

$rect = $menu.Current.BoundingRectangle
$clickX = [int]($rect.X + $rect.Width / 2)
$clickY = [int]($rect.Y + $rect.Height / 2)
Write-Host "clicking '$MenuHeader' at $clickX,$clickY"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class Clicker {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int index);

  // UI Automation reports physical pixels while SetCursorPos uses logical ones.
  public static double ScaleFactor() {
    IntPtr dc = GetDC(IntPtr.Zero);
    int dpi = GetDeviceCaps(dc, 88); // LOGPIXELSX
    ReleaseDC(IntPtr.Zero, dc);
    if (dpi <= 0) return 1.0;
    return dpi / 96.0;
  }

  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(120);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(60);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
  }
}
"@ -Language CSharp

$scale = 1.0   # UI Automation already reports physical pixels on this host
Write-Host "display scale: $scale"
$clickX = [int]($rect.X + 4)
$clickY = [int]($rect.Y + $rect.Height / 2)
Start-Sleep -Milliseconds 900

foreach ($name in $SubMenuHeaders) {
    $hit = Find-InPopups $root $proc.Id $name
    if (-not $hit) { Write-Host "submenu '$name' not found"; continue }
    $r = $hit.Element.Current.BoundingRectangle
    Write-Host "hovering submenu '$name' at $([int]($r.X + $r.Width/2)),$([int]($r.Y + $r.Height/2))"
    [Clicker]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 900
}

# Union of all popup rectangles is the menu area to capture.
$popups = Get-Popups $root $proc.Id
if ($popups.Count -eq 0) {
    # Fall back to the top level menu rectangle so the capture still shows something.
    $minX = $rect.X; $minY = $rect.Y; $maxX = $rect.Right; $maxY = $rect.Bottom + 400
} else {
    $minX = [double]::MaxValue; $minY = [double]::MaxValue; $maxX = 0.0; $maxY = 0.0
    foreach ($p in $popups) {
        $r = $p.Current.BoundingRectangle
        if ($r.Width -le 0) { continue }
        $minX = [Math]::Min($minX, $r.X); $minY = [Math]::Min($minY, $r.Y)
        $maxX = [Math]::Max($maxX, $r.Right); $maxY = [Math]::Max($maxY, $r.Bottom)
    }
    if ($maxX -le 0) { $minX = $rect.X; $minY = $rect.Y; $maxX = $rect.Right; $maxY = $rect.Bottom + 400 }
}

$pad = 14
$x = [int]([Math]::Max(0, $minX - $pad))
$y = [int]([Math]::Max(0, $minY - $pad))
$w = [int]($maxX - $minX + 2 * $pad)
$h = [int]($maxY - $minY + 2 * $pad)
Write-Host "capturing popups: ${w}x${h} at $x,$y (popups=$($popups.Count))"

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $OutPath"

# Close the menu.
[Clicker]::Click($clickX, [int]($rect.Y + 200))

