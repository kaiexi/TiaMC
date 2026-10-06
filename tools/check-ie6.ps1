# IE6 兼容性静态检查
# 扫描 Web 版的 IE6 专用页面与样式，找出 IE6（MSHTML 6.0）不支持的写法。
# 用法: powershell -File tools/check-ie6.ps1

param([ValidateSet('ie6', 'ie4')][string]$Profile = 'ie6')

$ErrorActionPreference = 'Continue'
$root = Join-Path $PSScriptRoot '..\src\TiaMc.Web\wwwroot'

# ie6：IE6 档页面；ie4：Trident 4.0（IE4）档页面（限制更多）
$targets = if ($Profile -eq 'ie4') {
    @('ie4.html', 'ie4.css') | ForEach-Object { Join-Path $root $_ }
} else {
    @('legacy.html', 'ie6.css', 'legacy.js') | ForEach-Object { Join-Path $root $_ }
}

# 每条规则: 名称 / 正则 / 适用的文件扩展名
$rules = @(
    @{ Name = 'CSS flex 布局';            Pattern = 'display\s*:\s*(flex|inline-flex)'; Ext = '.css,.html' },
    @{ Name = 'CSS grid 布局';            Pattern = 'display\s*:\s*(grid|inline-grid)|grid-template'; Ext = '.css,.html' },
    @{ Name = 'min-height（IE6 不支持）';  Pattern = 'min-height'; Ext = '.css,.html' },
    @{ Name = 'max-height（IE6 不支持）';  Pattern = 'max-height'; Ext = '.css,.html' },
    @{ Name = 'calc()';                    Pattern = 'calc\('; Ext = '.css,.html' },
    @{ Name = 'border-radius';             Pattern = 'border-radius'; Ext = '.css,.html' },
    @{ Name = 'box-shadow';                Pattern = 'box-shadow'; Ext = '.css,.html' },
    @{ Name = 'rgba()';                    Pattern = 'rgba\('; Ext = '.css,.html' },
    @{ Name = 'opacity';                   Pattern = '(?<!-)\bopacity\s*:'; Ext = '.css,.html' },
    @{ Name = 'transform';                 Pattern = 'transform\s*:'; Ext = '.css,.html' },
    @{ Name = 'transition';                Pattern = 'transition\s*:'; Ext = '.css,.html' },
    @{ Name = 'position:fixed';            Pattern = 'position\s*:\s*fixed'; Ext = '.css,.html' },
    @{ Name = '非 a 元素的 :hover';         Pattern = '(?m)^[^\r\n{}]*?(?<!a):hover'; Ext = '.css' },
    @{ Name = 'CSS 变量 var(--x)';          Pattern = 'var\(--'; Ext = '.css,.html' },
    @{ Name = 'ES6 const';                 Pattern = '(?m)(^|[^\w.])const\s+[A-Za-z_$]'; Ext = '.js' },
    @{ Name = 'ES6 let';                   Pattern = '(?m)(^|[^\w.])let\s+[A-Za-z_$]'; Ext = '.js' },
    @{ Name = 'ES6 箭头函数';               Pattern = '=>'; Ext = '.js' },
    @{ Name = 'fetch()';                   Pattern = '(?<![\w.])fetch\s*\('; Ext = '.js' },
    @{ Name = 'addEventListener 无 IE6 回退'; Pattern = 'addEventListener'; Ext = '.js'; Absent = 'attachEvent' },
    @{ Name = '模板字符串';                  Pattern = '`'; Ext = '.js' },
    @{ Name = 'JSON.parse 直接依赖';         Pattern = 'JSON\.parse'; Ext = '.js' },
    # ---- 以下是 Trident 4.0（IE4）档才检查的项：这些在 IE6 里没问题，但 IE4 没有 ----
    @{ Name = 'document.getElementById（IE5+）'; Pattern = 'getElementById'; Ext = '.js,.html'; OnlyIe4 = $true },
    @{ Name = 'attachEvent（IE5+）';             Pattern = 'attachEvent'; Ext = '.js,.html'; OnlyIe4 = $true },
    @{ Name = 'XMLHttpRequest（IE5+）';          Pattern = 'XMLHttpRequest'; Ext = '.js,.html'; OnlyIe4 = $true },
    @{ Name = 'createElement（IE4 不可靠）';      Pattern = 'createElement'; Ext = '.js,.html'; OnlyIe4 = $true },
    @{ Name = 'CSS filter（IE5.5+）';            Pattern = 'filter\s*:'; Ext = '.css,.html'; OnlyIe4 = $true },
    @{ Name = 'CSS zoom（IE5.5+）';              Pattern = 'zoom\s*:'; Ext = '.css,.html'; OnlyIe4 = $true },
    @{ Name = 'border-collapse（IE5+）';         Pattern = 'border-collapse'; Ext = '.css,.html'; OnlyIe4 = $true },
    @{ Name = 'overflow:auto/scroll（IE5+）';    Pattern = 'overflow\s*:\s*(auto|scroll)'; Ext = '.css,.html'; OnlyIe4 = $true },
    @{ Name = '属性选择器 input[type=]（IE7+）'; Pattern = '\[\s*type\s*='; Ext = '.css'; OnlyIe4 = $true },
    @{ Name = '@media / @import';               Pattern = '@(media|import)'; Ext = '.css,.html'; OnlyIe4 = $true }
)

$pass = 0
$fail = 0

Write-Host ("兼容性检查档位: {0}    目录: {1}" -f $Profile, $root) -ForegroundColor Cyan
Write-Host ("=" * 78)

foreach ($file in $targets) {
    if (-not (Test-Path $file)) { Write-Host "  跳过（不存在）: $file" -ForegroundColor DarkGray; continue }
    $name = Split-Path $file -Leaf
    $ext = [System.IO.Path]::GetExtension($file)
    $text = [System.IO.File]::ReadAllText($file)
    # 注释里的词不算违规：先剥掉 /* */ 与 // 与 <!-- --> 注释
    $stripped = [regex]::Replace($text, '/\*[\s\S]*?\*/', '')
    $stripped = [regex]::Replace($stripped, '<!--[\s\S]*?-->', '')
    $stripped = [regex]::Replace($stripped, '(?m)//.*$', '')
    $lines = ($stripped -split "`r?`n")
    Write-Host ""
    Write-Host "文件: $name  ($($lines.Count) 行, $([math]::Round((Get-Item $file).Length/1KB,1)) KB)" -ForegroundColor Yellow

    foreach ($rule in $rules) {
        if ($rule.Ext -notlike "*$ext*") { continue }
        if ($rule.ContainsKey('OnlyIe4') -and $Profile -ne 'ie4') { continue }
        # 某些规则在"存在替代写法"时不算违规（例如 addEventListener 旁边有 attachEvent 回退）
        if ($rule.ContainsKey('Absent') -and $stripped -match [regex]::Escape($rule.Absent)) {
            Write-Host ("  [PASS] {0}（存在 {1} 回退）" -f $rule.Name, $rule.Absent) -ForegroundColor Green
            $pass++
            continue
        }
        $hits = @()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $rule.Pattern) { $hits += ($i + 1) }
        }
        if ($hits.Count -eq 0) {
            Write-Host ("  [PASS] {0}" -f $rule.Name) -ForegroundColor Green
            $pass++
        } else {
            Write-Host ("  [FAIL] {0}  行: {1}" -f $rule.Name, ($hits -join ', ')) -ForegroundColor Red
            $fail++
        }
    }
}

Write-Host ""
Write-Host ("=" * 78)
Write-Host ("结果: PASS {0} 项 / FAIL {1} 项" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
if ($fail -eq 0) {
    Write-Host ("{0} 档未发现不兼容写法。" -f $Profile) -ForegroundColor Green
} else {
    Write-Host "请修正上面标记的写法；IE6 里的表现无法保证。" -ForegroundColor Red
    exit 1
}
