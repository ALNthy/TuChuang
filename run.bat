@echo off
chcp 65001 >nul
setlocal enableextensions
title Tuchuang 一键启动

set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"

echo.
echo ============================================================
echo   图床（Tuchuang）一键启动
echo   后端 ASP.NET Core 10 :5000    前端 Vite :5173
echo ============================================================
echo.

REM ---- 环境检查 ----
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [错误] 未检测到 dotnet SDK，请先安装 .NET 10 SDK
    echo        https://dotnet.microsoft.com/download/dotnet/10.0
    pause & exit /b 1
)
where node >nul 2>&1
if errorlevel 1 (
    echo [错误] 未检测到 Node.js，请先安装 Node.js 18+
    echo        https://nodejs.org/
    pause & exit /b 1
)

REM ---- 后端依赖还原（仅首次） ----
if not exist "%ROOT%\backend\bin" (
    echo [1/4] 还原后端 NuGet 依赖（首次较慢）...
    dotnet restore "%ROOT%\backend\TuchuangApi.csproj"
    if errorlevel 1 ( echo [错误] 后端依赖还原失败 & pause & exit /b 1 )
) else (
    echo [1/4] 后端已构建，跳过 restore
)

REM ---- 前端依赖安装（仅首次） ----
if not exist "%ROOT%\frontend\node_modules" (
    echo [2/4] 安装前端 npm 依赖（首次较慢）...
    pushd "%ROOT%\frontend"
    call npm install
    popd
    if errorlevel 1 ( echo [错误] 前端依赖安装失败 & pause & exit /b 1 )
) else (
    echo [2/4] 前端 node_modules 已存在，跳过 install
)

REM ---- 杀掉占用 5000 / 5173 的旧进程 ----
echo [3/4] 清理 5000 / 5173 端口...
for /f "tokens=5" %%a in ('netstat -ano -p tcp ^| findstr ":5000 .*LISTENING"') do (
    taskkill /F /PID %%a >nul 2>&1
)
for /f "tokens=5" %%a in ('netstat -ano -p tcp ^| findstr ":5173 .*LISTENING"') do (
    taskkill /F /PID %%a >nul 2>&1
)

REM ---- 启动后端（新窗口） ----
echo [4/4] 启动后端 :5000 和前端 :5173...
start "Tuchuang Backend :5000" cmd /k "cd /d %ROOT%\backend && dotnet run --no-launch-profile --urls http://localhost:5000"

REM 等后端起一会儿再起前端
timeout /t 2 /nobreak >nul

start "Tuchuang Frontend :5173" cmd /k "cd /d %ROOT%\frontend && npx vite --host 127.0.0.1 --port 5173"

timeout /t 4 /nobreak >nul
echo.
echo ============================================================
echo 启动完成！
echo.
echo   浏览页:    http://localhost:5173
echo   管理页:    http://localhost:5173/manage
echo   管理员:    admin / admin123
echo.
echo   停止服务:  关闭上面两个弹出的命令行窗口
echo              或运行 stop.bat
echo ============================================================
echo.
pause
endlocal
