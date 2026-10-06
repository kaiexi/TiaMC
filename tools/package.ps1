# TiaMC 打包脚本：生成干净的发布包（完整版 + 轻量版）
#
# 用法：
#   pwsh -File tools\package.ps1 -Version 1.0.9
#
# 为什么要用它：以前手工 dotnet publish + Compress-Archive，发布目录里如果跑过启动器，
# 会混进 minecraft\（下载的版本/资源，几百 MB）与 config\logs\，曾经打出一个 915 MB 的包。

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutDir = "C:\Users\3214\Desktop"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$full = Join-Path $env:TEMP "TiaMC-pkg-full-$Version"
$lite = Join-Path $env:TEMP "TiaMC-pkg-lite-$Version"

function New-Package {
    param([string]$Stage, [bool]$SelfContained, [string]$ZipName)

    if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Stage | Out-Null

    $args = @(
        "publish", (Join-Path $repo "src\TiaMc.App\TiaMc.App.csproj"),
        "-c", "Release", "-r", "win-x64",
        "--self-contained", $(if ($SelfContained) { "true" } else { "false" }),
        "-p:PublishSingleFile=false",
        "-p:DebugType=None", "-p:DebugSymbols=false",
        "-o", $Stage,
        "-v", "quiet", "--nologo"
    )
    & dotnet @args | Out-Null

    # 关键：清掉任何运行残留，只保留发布产物 + 文档
    foreach ($junk in @("minecraft", "config", "logs", "runtime", "crash-reports")) {
        $path = Join-Path $Stage $junk
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    Get-ChildItem $Stage -Recurse -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

    # 语言资源只留中文，其余删掉（自包含包里能省几 MB）
    Get-ChildItem $Stage -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' -and $_.Name -notin @('zh-Hans', 'zh-Hant') } |
        Remove-Item -Recurse -Force

    foreach ($doc in @("README.md", "LICENSE", "AI-DISCLOSURE.md", "THIRD-PARTY-NOTICES.md")) {
        $source = Join-Path $repo $doc
        if (Test-Path $source) { Copy-Item $source (Join-Path $Stage $doc) -Force }
    }

    $zip = Join-Path $OutDir $ZipName
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $Stage "*") -DestinationPath $zip -CompressionLevel Optimal

    $size = [math]::Round((Get-Item $zip).Length / 1MB, 2)
    $sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    $files = (Get-ChildItem $Stage -Recurse -File | Measure-Object).Count
    Write-Output ("  " + $ZipName + "  " + $size + " MB  (" + $files + " 个文件)  SHA256 " + $sha.Substring(0, 16) + "…")
    return @{ Zip = $zip; Size = $size; Sha = $sha }
}

Write-Output "打包 TiaMC v$Version"
$r1 = New-Package -Stage $full -SelfContained $true -ZipName "TiaMC-v$Version-win-x64.zip"
$r2 = New-Package -Stage $lite -SelfContained $false -ZipName "TiaMC-v$Version-win-x64-lite.zip"

Remove-Item $full, $lite -Recurse -Force -ErrorAction SilentlyContinue

Write-Output ""
Write-Output "结果："
Write-Output ("  完整版 " + $r1.Size + " MB  " + $r1.Zip)
Write-Output ("  轻量版 " + $r2.Size + " MB  " + $r2.Zip)
Write-Output ("  完整版 SHA256 " + $r1.Sha)
Write-Output ("  轻量版 SHA256 " + $r2.Sha)
