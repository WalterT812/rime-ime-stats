# 把 C# 版统计程序安装到稳定位置 %LOCALAPPDATA%\IMEStats，与源码仓库解耦
# ——这样以后移动/重命名/删除本仓库，自启与程序都不受影响（数据本就在 %APPDATA%\IMEStats）
# 用法：右键“使用 PowerShell 运行”，或在终端执行 .\install.ps1
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $env:LOCALAPPDATA "IMEStats"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

Write-Host "[1/5] 编译 C# 版（需 .NET 8 SDK）..."
Push-Location (Join-Path $root "csharp")
dotnet publish IMEStatsSharp.csproj -c Release -r win-x64 --self-contained false `
    /p:PublishSingleFile=true -o publish | Out-Null
Pop-Location

Write-Host "[2/5] 停止正在运行的旧实例..."
Get-Process IMEStatsSharp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host "[3/5] 复制程序到 $dest ..."
Copy-Item (Join-Path $root "csharp\publish\IMEStatsSharp.exe") (Join-Path $dest "IMEStatsSharp.exe") -Force
# word_worker 负责分词/喂词（需本机 Python + jieba/pypinyin），放到 exe 旁边即可被找到
Copy-Item (Join-Path $root "stats-app\word_worker.py") (Join-Path $dest "word_worker.py") -Force
if (-not (Test-Path (Join-Path $dest "config.json"))) {
    Copy-Item (Join-Path $root "stats-app\config.json") (Join-Path $dest "config.json") -ErrorAction SilentlyContinue
}

$exe = Join-Path $dest "IMEStatsSharp.exe"

Write-Host "[4/5] 注册开机自启 + 每小时看门狗（均指向安装位置）..."
$vbs = Join-Path ([Environment]::GetFolderPath('Startup')) "ime_stats_autostart.vbs"
$vbsContent = @'
Set ws = CreateObject("WScript.Shell")
ws.Run """__EXE__""", 0, False
'@ -replace '__EXE__', $exe
Set-Content -Path $vbs -Value $vbsContent -Encoding ASCII
schtasks /Create /F /TN "IMEStats_Watchdog" /SC HOURLY /TR "`"$exe`"" | Out-Null

Write-Host "[5/5] 启动..."
Start-Process $exe

Write-Host ""
Write-Host "完成。程序已安装到 $dest 并启动。" -ForegroundColor Green
Write-Host "今后本仓库可随意移动/删除，不影响自启与运行（数据在 %APPDATA%\IMEStats）。"
Write-Host "卸载：删除该文件夹 + 启动文件夹里的 ime_stats_autostart.vbs + 计划任务 IMEStats_Watchdog。"
