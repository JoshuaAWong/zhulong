@echo off
chcp 65001 >nul
set PY=C:\Users\Administrator\AppData\Local\Programs\Python\Python313\python.exe
set PYW=C:\Users\Administrator\AppData\Local\Programs\Python\Python313\pythonw.exe
set PROJ=%~dp0..
echo [1/4] 安装依赖...
"%PY%" -X utf8 -m pip install -r "%PROJ%\requirements.txt" || goto :fail
echo [2/4] 生成计划任务定义（替换解释器路径）...
powershell -NoProfile -Command "(Get-Content '%~dp0任务自启.xml' -Raw) -replace '__PYTHONW__', '%PYW%' -replace '__PROJECT__', '%PROJ%' | Set-Content '%TEMP%\zhulong_autostart.xml' -Encoding Unicode"
powershell -NoProfile -Command "(Get-Content '%~dp0任务看门狗.xml' -Raw) -replace '__PYTHONW__', '%PYW%' -replace '__PROJECT__', '%PROJ%' | Set-Content '%TEMP%\zhulong_watchdog.xml' -Encoding Unicode"
echo [3/4] 注册计划任务（若安全软件弹窗请点允许）...
schtasks /create /f /tn "Zhulong" /xml "%TEMP%\zhulong_autostart.xml" || goto :fail
schtasks /create /f /tn "ZhulongWatchdog" /xml "%TEMP%\zhulong_watchdog.xml" || goto :fail
echo [4/4] 立即启动一次...
schtasks /run /tn "Zhulong"
echo.
echo 部署完成。请检查：① 托盘出现烛龙图标 ② 浏览器打开 http://127.0.0.1:8890 ③ 系统设置-通知已开启且未开勿扰 ④ 建议将 %PROJ% 加入安全软件信任区
goto :eof
:fail
echo 部署失败，请把上方输出发给 AI 分析。
exit /b 1
