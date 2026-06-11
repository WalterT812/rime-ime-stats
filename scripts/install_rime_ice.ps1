# 安装雾凇拼音（rime-ice）词库与方案到 %APPDATA%\Rime
# 雾凇拼音是目前公认体验最接近商业输入法的开源 Rime 方案
# 项目主页：https://github.com/iDvel/rime-ice
$ErrorActionPreference = "Stop"

$dst = Join-Path $env:APPDATA "Rime"
if (-not (Test-Path $dst)) {
    Write-Host "未找到 $dst —— 请先安装小狼毫（Weasel）：https://rime.im" -ForegroundColor Red
    exit 1
}

$zipUrl = "https://github.com/iDvel/rime-ice/archive/refs/heads/main.zip"
$tmp = Join-Path $env:TEMP "rime-ice"
$zip = "$tmp.zip"

Write-Host "下载雾凇拼音（约几十 MB，含词库）..."
Invoke-WebRequest -Uri $zipUrl -OutFile $zip

Write-Host "解压..."
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
Expand-Archive $zip -DestinationPath $tmp

$srcDir = Get-ChildItem $tmp -Directory | Select-Object -First 1
Write-Host "复制方案文件到 $dst ..."
# 注意：不覆盖本项目已部署的 *.custom.yaml
Get-ChildItem $srcDir.FullName -Recurse -File |
    Where-Object { $_.Name -notmatch "\.(md|txt)$" -and $_.Name -notlike "*.custom.yaml" } |
    ForEach-Object {
        $rel = $_.FullName.Substring($srcDir.FullName.Length + 1)
        $target = Join-Path $dst $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item $_.FullName $target -Force
    }

Remove-Item $zip -Force
Remove-Item $tmp -Recurse -Force

Write-Host ""
Write-Host "雾凇拼音安装完成。" -ForegroundColor Green
Write-Host "最后一步：右键任务栏小狼毫图标 →「重新部署」。" -ForegroundColor Yellow
