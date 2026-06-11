# 安装/更新雾凇拼音（rime-ice）词库与方案到 %APPDATA%\Rime
# 雾凇拼音是目前公认体验最接近商业输入法的开源 Rime 方案
# 项目主页：https://github.com/iDvel/rime-ice
# 安装完成后自动触发小狼毫重新部署，可直接用于定时更新词库
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
# 注意：不覆盖本项目已部署的 *.custom.yaml 与 rime.lua
Get-ChildItem $srcDir.FullName -Recurse -File |
    Where-Object { $_.Name -notmatch "\.(md|txt)$" -and
                   $_.Name -notlike "*.custom.yaml" -and
                   $_.Name -ne "rime.lua" } |
    ForEach-Object {
        $rel = $_.FullName.Substring($srcDir.FullName.Length + 1)
        $target = Join-Path $dst $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item $_.FullName $target -Force
    }

Remove-Item $zip -Force
Remove-Item $tmp -Recurse -Force
Write-Host "词库文件已更新。" -ForegroundColor Green

# 自动触发小狼毫重新部署（找不到则提示手动）
$deployer = Get-ChildItem "$env:ProgramFiles\Rime\weasel-*\WeaselDeployer.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $deployer) {
    $deployer = Get-ChildItem "${env:ProgramFiles(x86)}\Rime\weasel-*\WeaselDeployer.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
}
if ($deployer) {
    Write-Host "触发重新部署（首次编译词库约 1-2 分钟）..."
    Start-Process $deployer.FullName "/deploy"
    Write-Host "完成。" -ForegroundColor Green
} else {
    Write-Host "未找到 WeaselDeployer.exe，请手动：右键小狼毫托盘图标 →「重新部署」" -ForegroundColor Yellow
}
