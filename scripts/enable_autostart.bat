@echo off
chcp 65001 >nul
rem 把统计程序加入开机自启（写入当前用户的"启动"文件夹，无需管理员）
set "VBS=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\ime_stats_autostart.vbs"
set "PY=%~dp0..\stats-app\ime_stats.py"
for %%i in ("%PY%") do set "PY=%%~fi"

> "%VBS%" echo Set ws = CreateObject("WScript.Shell")
>>"%VBS%" echo ws.Run "pythonw ""%PY%""", 0, False

if exist "%VBS%" (
    echo [成功] 已加入开机自启：%VBS%
    echo 下次开机将自动静默启动统计程序。
    echo 如需取消：删除上述文件即可。
) else (
    echo [失败] 写入启动文件夹失败，请手动把 stats-app\start_stats.vbs 的快捷方式放入 shell:startup
)
pause
