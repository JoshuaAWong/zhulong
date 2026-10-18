@echo off
chcp 65001 >nul
setlocal
set "PROJ=%~dp0.."
set "PY="
set "PYW="
rem 解释器：环境变量覆盖或自动发现，校验版本 >=3.13 且 python/pythonw 同目录
for /f "usebackq tokens=1,2 delims==" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0find_python.ps1"`) do (
  if "%%I"=="PY" set "PY=%%J"
  if "%%I"=="PYW" set "PYW=%%J"
)
if not defined PY goto :fail
if not defined PYW goto :fail

echo [1/4] 安装依赖...
"%PY%" -X utf8 -m pip install -r "%PROJ%\requirements.txt" || goto :fail

echo [2/4] 生成计划任务定义（XML 转义解释器与项目路径）...
set "ZHULONG_PYTHONW=%PYW%"
set "ZHULONG_PROJECT=%PROJ%"
set "ZHULONG_TASK_XML_DIR=%TEMP%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare_tasks.ps1" || goto :fail

echo [3/4] 注册计划任务（若安全软件弹窗请点允许）...
schtasks /create /f /tn "Zhulong" /xml "%TEMP%\zhulong_autostart.xml" || goto :fail
schtasks /create /f /tn "ZhulongWatchdog" /xml "%TEMP%\zhulong_watchdog.xml" || goto :fail
schtasks /create /f /tn "ZhulongAC7" /xml "%TEMP%\zhulong_ac7.xml" || goto :fail

echo [4/4] 立即启动一次...
schtasks /run /tn "Zhulong" || goto :fail
echo.
echo 部署完成。请检查：① 托盘出现烛龙图标 ② 浏览器打开 http://127.0.0.1:8890 ③ 系统设置-通知已开启且未开勿扰 ④ 建议将 %PROJ% 加入安全软件信任区
goto :eof

:fail
echo 部署失败，请把上方输出发给 AI 分析。
exit /b 1
