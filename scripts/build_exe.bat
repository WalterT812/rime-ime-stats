@echo off
setlocal
rem 把统计程序打包成单文件 IMEStats.exe（之后开机自启/看门狗不再依赖 Python 环境）
cd /d "%~dp0..\stats-app"

where pyinstaller >nul 2>&1
if errorlevel 1 (
    echo 正在安装 pyinstaller ...
    pip install pyinstaller || (echo [失败] pip 安装 pyinstaller 失败 & pause & exit /b 1)
)

echo 正在打包（首次约 1-3 分钟，jieba 词典会被打进去，exe 约 50MB）...
pyinstaller --noconfirm --onefile --noconsole --name IMEStats ^
    --hidden-import word_worker ^
    --collect-data jieba --collect-data pypinyin ^
    ime_stats.py
if errorlevel 1 (echo [失败] 打包失败，把上面报错发给 Claude & pause & exit /b 1)

copy /Y dist\IMEStats.exe IMEStats.exe >nul
rmdir /S /Q build dist >nul 2>&1
del /Q IMEStats.spec >nul 2>&1

echo.
echo [成功] 生成 stats-app\IMEStats.exe
echo 下一步：双击 scripts\enable_autostart.bat 会自动改用 exe 注册自启和看门狗
echo 现在可手动测试：先在托盘退出旧程序，再双击 IMEStats.exe
pause
