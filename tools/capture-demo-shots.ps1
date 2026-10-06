# 在“演示环境”里批量采集界面截图（脱敏：路径为 C:\Users\Public\TiaMC-Demo，模组为 Example Mod A/B）
# 用法: powershell -File tools/capture-demo-shots.ps1
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "..\artifacts\shots"),
    [int]$WinX = 40, [int]$WinY = 20, [int]$WinW = 1700, [int]$WinH = 1020
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public class Win32Demo {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int t,bool r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@ -Language CSharp

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$proc = Get-Process TiaMC -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "请先启动演示环境的 TiaMC.exe"; exit 1 }

[Win32Demo]::MoveWindow($proc.MainWindowHandle, $WinX, $WinY, $WinW, $WinH, $true) | Out-Null
[Win32Demo]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Seconds 3

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Host "找不到窗口"; exit 1 }

function Get-Tab([string]$name) {
    $and = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::TabItem)))
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}

function Select-Tab([string]$name) {
    $tab = Get-Tab $name
    if ($tab) {
        $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Seconds 2
        return $true
    }
    Write-Host "  找不到页签: $name"
    return $false
}

function Invoke-Btn([System.Windows.Automation.AutomationElement]$scope, [string]$name) {
    $bc = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)) {
        if ($b.Current.Name -eq $name) {
            try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch {}
        }
    }
    return $false
}

function Shoot([string]$file) {
    $path = Join-Path $OutDir $file
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "capture-window.ps1") `
        "TIA-MC" $path $WinW $WinH $WinX $WinY | Out-Null
    if (Test-Path $path) { Write-Host "  已保存 $file" } else { Write-Host "  截图失败 $file" }
}

# 清零日志，让输出窗口只显示演示环境的日志（避免出现真实用户目录）
Select-Tab "日志" | Out-Null
Invoke-Btn $win "清空" | Out-Null
Start-Sleep -Seconds 1
Select-Tab "概览" | Out-Null
Start-Sleep -Seconds 1

$shots = @(
    @{ Tab = "概览"; File = "01-shell.png" },
    @{ Tab = "版本"; File = "02-versions.png" },
    @{ Tab = "账户"; File = "03-accounts-skin.png" },
    @{ Tab = "模组"; File = "04-mods.png" },
    @{ Tab = "整合包"; File = "05-packs.png" },
    @{ Tab = "日志"; File = "06-log-export.png" },
    @{ Tab = "认证服务端"; File = "07-yggdrasil.png" }
)

foreach ($s in $shots) {
    if (Select-Tab $s.Tab) { Shoot $s.File }
}

# 账户页：生成一张随机皮肤后再截一张（展示皮肤面板与实时预览）
if (Select-Tab "账户") {
    Invoke-Btn (Get-Tab "账户") "随机" | Out-Null
    Start-Sleep -Seconds 2
    Shoot "08-skin-preview.png"
}

# 资源下载页：中文搜索（只涉及公开模组）
if (Select-Tab "资源下载") {
    $tab = Get-Tab "资源下载"
    $cc = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ComboBox)
    $combos = $tab.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cc)
    if ($combos.Count -gt 0) {
        try {
            $combos[0].GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            Start-Sleep -Milliseconds 600
            $li = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)
            foreach ($item in $combos[0].FindAll([System.Windows.Automation.TreeScope]::Descendants, $li)) {
                if ($item.Current.Name -eq '模组') { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
            }
        } catch {}
    }
    $editCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    foreach ($e in $tab.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)) {
        if ([int]$e.Current.BoundingRectangle.Width -gt 150) {
            try { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("机械动力") } catch {}
        }
    }
    Invoke-Btn $tab "搜索" | Out-Null
    Start-Sleep -Seconds 10
    Shoot "09-resources.png"
}

Write-Host "完成，输出目录: $OutDir"
