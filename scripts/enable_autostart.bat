@echo off
setlocal
rem 把统计程序加入开机自启 + 每小时看门狗保活（写入当前用户配置，无需管理员）

for /f "delims=" %%p in ('where pythonw 2^>nul') do (set "PYW=%%p" & goto :found)
echo [失败] 未找到 pythonw.exe，请确认 Python 已安装并加入 PATH
pause
exit /b 1
:found

set "PY=%~dp0..\stats-app\ime_stats.py"
for %%i in ("%PY%") do set "PY=%%~fi"
if not exist "%PY%" (
    echo [失败] 未找到 %PY%
    pause
    exit /b 1
)

set "VBS=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\ime_stats_autostart.vbs"
> "%VBS%" echo Set ws = CreateObject("WScript.Shell")
>>"%VBS%" echo ws.Run """%PYW%"" ""%PY%""", 0, False

echo [1/3] 已写入开机自启：
echo   %VBS%

schtasks /Create /F /TN "IMEStats_Watchdog" /SC HOURLY ^
    /TR "\"%PYW%\" \"%PY%\"" >nul 2>&1
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
