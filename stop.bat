@echo off
chcp 65001 >nul
title Tuchuang 停止

echo.
echo 正在停止 Tuchuang 服务...

REM 通过端口杀进程
for /f "tokens=5" %%a in ('netstat -ano -p tcp ^| findstr ":5000 .*LISTENING"') do (
    echo  - 停止后端 PID %%a
    taskkill /F /PID %%a >nul 2>&1
)
for /f "tokens=5" %%a in ('netstat -ano -p tcp ^| findstr ":5173 .*LISTENING"') do (
    echo  - 停止前端 PID %%a
    taskkill /F /PID %%a >nul 2>&1
)

REM 通过窗口标题兜底
for /f "tokens=2" %%a in ('tasklist /v /fo csv ^| findstr "Tuchuang Backend"') do (
    taskkill /F /PID %%a >nul 2>&1
)
for /f "tokens=2" %%a in ('tasklist /v /fo csv ^| findstr "Tuchuang Frontend"') do (
    taskkill /F /PID %%a >nul 2>&1
)

echo.
echo 已停止。
pause
