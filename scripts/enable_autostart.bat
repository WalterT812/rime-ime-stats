@echo off
setlocal
rem 开机自启 + 每小时看门狗。优先级：C# 版 > Python 打包版 > Python 脚本

set "CS=%~dp0..\stats-app\IMEStatsSharp.exe"
for %%i in ("%CS%") do set "CS=%%~fi"
set "EXE=%~dp0..\stats-app\IMEStats.exe"
for %%i in ("%EXE%") do set "EXE=%%~fi"

if exist "%CS%"  ( set "RUNNER=%CS%"  & set "TARGET=" & echo 使用 C# 版 IMEStatsSharp.exe & goto :reg )
if exist "%EXE%" ( set "RUNNER=%EXE%" & set "TARGET=" & echo 使用 Python 打包版 IMEStats.exe & goto :reg )
for /f "delims=" %%p in ('where pythonw 2^>nul') do (set "RUNNER=%%p" & goto :usepy)
echo [失败] 找不到 IMEStatsSharp.exe / IMEStats.exe / pythonw，无法注册
pause
exit /b 1
:usepy
set "TARGET=%~dp0..\stats-app\ime_stats.py"
for %%i in ("%TARGET%") do set "TARGET=%%~fi"
echo 使用 Python 脚本 pythonw

:reg
set "VBS=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\ime_stats_autostart.vbs"
> "%VBS%" echo Set ws = CreateObject("WScript.Shell")
if defined TARGET (
    >>"%VBS%" echo ws.Run """%RUNNER%"" ""%TARGET%""", 0, False
) else (
    >>"%VBS%" echo ws.Run """%RUNNER%""", 0, False
)
echo [1/3] 已写入开机自启： %VBS%

if defined TARGET (
    schtasks /Create /F /TN "IMEStats_Watchdog" /SC HOURLY /TR "\"%RUNNER%\" \"%TARGET%\"" >nul 2>&1
) else (
    schtasks /Create /F /TN "IMEStats_Watchdog" /SC HOURLY /TR "\"%RUNNER%\"" >nul 2>&1
)
if %errorlevel%==0 (
    echo [2/3] 已注册每小时看门狗：程序意外退出后 1 小时内自动拉起
    echo       取消：schtasks /Delete /TN "IMEStats_Watchdog"
) else (
    echo [2/3] 看门狗注册失败（不影响开机自启），可右键管理员重试
)

echo [3/3] 正在测试启动……任务栏应出现蓝色"字"图标（已在运行则无变化，有单实例保护）
wscript "%VBS%"
echo.
echo 完成。当前注册的运行目标：%RUNNER%
echo 取消开机自启：删除上面的 vbs 文件即可。
pause
