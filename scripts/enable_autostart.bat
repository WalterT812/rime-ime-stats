@echo off
chcp 65001 >nul
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
ech