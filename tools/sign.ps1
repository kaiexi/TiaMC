# 给 TiaMC 的发布包做代码签名（防杀软误杀 / 通过 SmartScreen）
#
# 三种方式，按"最接近微软官方签名"排序：
#
#   1. Azure Trusted Signing（推荐，微软自家签名服务，证书由微软签发）
#      - 需要在 Azure 上开通 "Trusted Signing" 并完成发布者身份验证（个人/公司都可申请）
#      - 签名时用 signtool 的 dlib 模式，配合 metadata.json
#      - 用法： .\tools\sign.ps1 -UseTrustedSigning -Metadata .\signing\metadata.json -Path .\dist
#
#   2. 自有代码签名证书（OV / EV，从 CA 购买，例如 SSL.com / Sectigo / DigiCert）
#      - PFX 文件： .\tools\sign.ps1 -Pfx .\cert.pfx -Password "***" -Path .\dist
#      - 证书在证书存储里： .\tools\sign.ps1 -Thumbprint <指纹> -Path .\dist
#
#   3. 没有证书时（免费）：用 SignPath Foundation（开源项目免费签名）
#      - 见 docs\SIGNING.md 里的申请流程；签名在其 CI 里完成
#
# 说明：签名只影响"发布出去的文件"，源码与构建不变。签名后请把新 SHA256 写进 Release 说明。

param(
    [string]$Path = ".",
    [string]$Pfx,
    [string]$Password,
    [string]$Thumbprint,
    [switch]$UseTrustedSigning,
    [string]$Metadata,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

function Find-SignTool {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe",
        "$env:ProgramFiles\Windows Kits\10\bin\*\x64\signtool.exe"
    )
    foreach ($pattern in $candidates) {
        $found = Get-ChildItem $pattern -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

$signtool = Find-SignTool
if (-not $signtool) {
    Write-Error "找不到 signtool.exe。请安装 Windows SDK（含“签名工具”）后重试。"
    exit 1
}
Write-Output "signtool: $signtool"

$targets = Get-ChildItem -Path $Path -Recurse -Include *.exe, *.dll -File |
    Where-Object { $_.Name -notmatch '^(api-ms-|ucrtbase|vcruntime)' }
Write-Output ("待签名文件: " + $targets.Count + " 个")

foreach ($file in $targets) {
    if ($UseTrustedSigning) {
        if (-not $Metadata) { Write-Error "使用 Trusted Signing 时必须提供 -Metadata（metadata.json）"; exit 1 }
        & $signtool sign /v /fd SHA256 /tr $TimestampUrl /td SHA256 `
            /dlib "$env:ProgramFiles\Microsoft Trusted Signing Client\TrustedSigningClient.dll" `
            /dmdf $Metadata $file.FullName
    }
    elseif ($Pfx) {
        & $signtool sign /v /fd SHA256 /tr $TimestampUrl /td SHA256 /f $Pfx /p $Password $file.FullName
    }
    elseif ($Thumbprint) {
        & $signtool sign /v /fd SHA256 /tr $TimestampUrl /td SHA256 /sha1 $Thumbprint $file.FullName
    }
    else {
        Write-Error "请指定 -UseTrustedSigning / -Pfx / -Thumbprint 之一。"
        exit 1
    }
}

Write-Output ""
Write-Output "=== 校验签名 ==="
foreach ($file in $targets) {
    & $signtool verify /pa /v $file.FullName | Select-String -Pattern "Successfully verified|Error" | ForEach-Object { "  " + $_.Line.Trim() }
}

Write-Output ""
Write-Output "完成。记得把新的 SHA256 写进 Release 说明："
Get-ChildItem (Join-Path $Path "..\*.zip") -ErrorAction SilentlyContinue | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    Write-Output ("  " + $_.Name + "  " + $hash)
}
