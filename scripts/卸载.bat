@echo off
chcp 65001 >nul
schtasks /delete /f /tn "Zhulong" 2>nul
schtasks /delete /f /tn "ZhulongWatchdog" 2>nul
taskkill /F /IM pythonw.exe /FI "WINDOWTITLE eq *" >nul 2>&1
echo 已卸载计划任务。若托盘仍有残留图标，请结束 pythonw.exe 进程（任务管理器）。
