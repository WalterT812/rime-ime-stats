@echo off
chcp 65001 >nul
rem 注册每周词库自动更新：每周日 12:00 拉取最新雾凇拼音词库并自动重新部署
set "PS1=%~dp0install_rime_ice.ps1"
for %%i in ("%PS1%") do set "PS1=%%~fi"

schtasks /Create /F /TN "RimeIce词库每周更新" /SC WEEKLY /D SUN /ST 12:00 ^
    /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%PS1%\""

if %errorlevel%==0 (
    echo [成功] 已注册计划任务：每周日 12:00 自动更新词库并重新部署
    echo 查看/删除：任务计划程序 或 schtasks /Delete /TN "RimeIce词库每周更新"
) else (
    echo [失败] 注册计划任务失败，可尝试右键"以管理员身份运行"
)
pause
