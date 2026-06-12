@echo off
setlocal
rem 把统计程序加入开机自启 + 每小时看门狗保活（写入当前用户配置，无需管理员）
rem 优先使用打包好的 IMEStats.exe（scripts\build_exe.bat 生成）；没有则用 Python 脚本

set "EXE=%~dp0..\stats-app\IMEStats.exe"
for %%i in ("%EXE%") do set "EXE=%%~fi"
if exist "%EXE%" (
    set "RUNNER=%EXE%"
    set "TARGET="
    echo 检测到 IMEStats.exe，将以单文件 exe 方式注册（不依赖 Python）
    goto :reg
)

for /f "delims=" %%p in ('where pythonw 2^>nul') do (set "RUNNER=%%p" & goto :foundpy)
echo [失败] 既没有 IMEStats.exe 也找不到 pythonw.exe
pause
exit /b 1
:foundpy
set "TARGET=%~dp0..\stats-app\ime_stats.py"
for %%i in ("%TARGET%") do set "TARGET=%%~fi"
if not exist "%TARGET%" (
    echo [失败] 未找到 %TARGET%
    pause
    exit /b 1
)

:reg
set "VBS=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\ime_stats_autostart.vbs"
> "%VBS%" echo Set ws = CreateObject("WScript.Shell")
if defined TARGET (
    >>"%VBS%" echo ws.Run """%RUNNER%"" ""%TARGET%""", 0, False
) else (
    >>"%VBS%" echo ws.Run """%RUNNER%""", 0, False
)

echo [1/3] 已写入开机自启：
echo   %VBS%

if defined TARGET (
    schtasks /Create /F /TN "IMEStats_Watchdog" /SC HOURLY /TR "\"%RUNNER%\" \"%TARGET%\"" >nul 2>&1
) else (
    schtasks /Create /F /TN "IMEStats_Watchdog" /SC HOURLY /TR "\"%RUNNER%\"" >nul 2>&1
)
if %errorlevel%==0 (
    echo [2/3] 已注册每小时看门狗：程序意外退出后 1 小时内自动拉起
    echo       取消方法：schtasks /Delete /TN "IMEStats_Watchdog"
) else (
    echo [2/3] 看门狗注册失败（不影响开机自启），可右键管理员重试
)

echo [3/3] 正在测试启动……任务栏应出现蓝色"字"图标（已在运行则无变化，有单实例保护）
wscript "%VBS%"
echo.
echo 完成。取消开机自启：删除上面的 vbs 文件即可。
pause
