# 部署本项目的 Rime 配置到用户目录 %APPDATA%\Rime
# 用法：右键“使用 PowerShell 运行”，或在终端执行 .\deploy_rime_config.ps1
$ErrorActionPreference = "Stop"

$src = Join-Path (Split-Path $PSScriptRoot -Parent) "rime-config"
$dst = Join-Path $env:APPDATA "Rime"

if (-not (Test-Path $dst)) {
    Write-Host "未找到 $dst —— 请先安装小狼毫（Weasel）：https://rime.im" -ForegroundColor Red
    exit 1
}

# 备份已有的同名文件
$backup = Join-Path $dst ("backup_" + (Get-Date -Format "yyyyMMdd_HHmmss"))
Get-ChildItem $src -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($src.Length + 1)
    $target = Join-Path $dst $rel
    if (Test-Path $target) {
        $bdir = Split-Path (Join-Path $backup $rel) -Parent
        New-Item -ItemType Directory -Force -Path $bdir | Out-Null
        Copy-Item $target (Join-Path $backup $rel)
    }
    $tdir = Split-Path $target -Parent
    New-Item -ItemType Directory -Force -Path $tdir | Out-Null
    Copy-Item $_.FullName $target -Force
    Write-Host "已部署: $rel"
}

Write-Host ""
Write-Host "配置已复制到 $dst" -ForegroundColor Green
if (Test-Path $backup) { Write-Host "原文件已备份到 $backup" }
Write-Host "最后一步：右键任务栏小狼毫图标 →「重新部署」使配置生效。" -ForegroundColor Yellow
