@echo off
setlocal
rem 编译 C# 版统计程序（实验性，需 .NET 8 SDK：https://dotnet.microsoft.com/download）
cd /d "%~dp0..\csharp"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [失败] 未找到 dotnet，请先安装 .NET 8 SDK
    pause
    exit /b 1
)

echo 正在编译并发布单文件（首次需联网恢复 NuGet 包）...
dotnet publish -c Release -r win-x64 --self-contained false ^
    /p:PublishSingleFile=true -o publish
if errorlevel 1 (echo [失败] 编译失败，把上面报错发给 Claude & pause & exit /b 1)

copy /Y publish\IMEStatsSharp.exe ..\stats-app\IMEStatsSharp.exe >nul

echo.
echo [成功] 生成 stats-app\IMEStatsSharp.exe
echo 试用方法：先在托盘退出 Python 版（互斥锁共享，不退则 C# 版直接闭嘴退出），
echo 再双击 IMEStatsSharp.exe。数据库与日志完全共用，随时可换回 Python 版。
pause
