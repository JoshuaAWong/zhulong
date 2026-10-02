@echo off
chcp 65001 >nul
set "PROJ=%~dp0.."
schtasks /delete /f /tn "Zhulong" 2>nul
schtasks /delete /f /tn "ZhulongWatchdog" 2>nul
set PID=
for /f %%i in ('powershell -NoProfile -Command "try { (Get-Content '%PROJ%\zhulong\state\heartbeat.json' -Raw | ConvertFrom-Json).pid } catch { }" 2^>nul') do set PID=%%i
if defined PID (
    taskkill /F /PID %PID% >nul 2>&1
    echo 已结束烛龙进程 PID %PID%
) else (
    echo 未发现运行中的烛龙（heartbeat 缺失）。如有残留请手动结束对应 pythonw.exe。
)
echo 已卸载计划任务。
