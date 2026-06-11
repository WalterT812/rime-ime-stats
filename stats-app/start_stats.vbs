' 静默启动统计程序（无黑窗口）。
' 开机自启：Win+R 输入 shell:startup，把本文件的快捷方式放进去。
Set ws = CreateObject("WScript.Shell")
ws.CurrentDirectory = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
ws.Run "pythonw ime_stats.py", 0, False
