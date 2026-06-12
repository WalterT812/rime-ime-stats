@echo off
setlocal
rem 把统计程序加入开机自启（写入当前用户"启动"文件夹，无需管理员）

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

echo [成功] 已写入开机自启：
echo   %VBS%
echo   解释器：%PYW%
echo   脚本  ：%PY%
echo.
echo 正在测试启动……任务栏应出现蓝色"字"图标（已在运行则无变化，程序有单实例保护）
wscript "%VBS%"
echo.
echo 如需取消自启：删除上面的 vbs 文件即可。
pause
