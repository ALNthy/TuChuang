#!/usr/bin/env bash
# =====================================================================
#  图床（Tuchuang）一键启动脚本 —— Linux
#
#  用法：
#     ./run.sh              # 启动前后端
#     bash run.sh           # 如果没有可执行权限
#     ./run.sh stop         # 停止（等价于 ./stop.sh）
#     ./run.sh restart      # 重启
#     ./run.sh status       # 查看运行状态
# =====================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG_DIR="$ROOT/logs"
mkdir -p "$LOG_DIR"

BACKEND_PID="$LOG_DIR/backend.pid"
FRONTEND_PID="$LOG_DIR/frontend.pid"
BACKEND_LOG="$LOG_DIR/backend.log"
FRONTEND_LOG="$LOG_DIR/frontend.log"

# 颜色输出（非交互终端自动关闭颜色）
if [ -t 1 ]; then
    C_GREEN='\033[0;32m'; C_RED='\033[0;31m'; C_YELLOW='\033[0;33m'; C_CYAN='\033[0;36m'; C_RESET='\033[0m'
else
    C_GREEN=''; C_RED=''; C_YELLOW=''; C_CYAN=''; C_RESET=''
fi

info()  { printf "${C_GREEN}[$(date +%H:%M:%S)] $*${C_RESET}\n"; }
warn()  { printf "${C_YELLOW}[$(date +%H:%M:%S)] $*${C_RESET}\n"; }
error() { printf "${C_RED}[$(date +%H:%M:%S)] $*${C_RESET}\n" >&2; }
title() { printf "${C_CYAN}[$(date +%H:%M:%S)] $*${C_RESET}\n"; }

# ---------------- 子命令实现 ----------------

do_stop() {
    info "正在停止 Tuchuang 服务..."
    for name in backend frontend; do
        pidfile="$LOG_DIR/$name.pid"
        if [ -f "$pidfile" ]; then
            pid="$(cat "$pidfile" 2>/dev/null || true)"
            if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
                kill "$pid" 2>/dev/null || true
                # 给 2 秒优雅退出
                for _ in 1 2 3 4 5; do
                    kill -0 "$pid" 2>/dev/null || break
                    sleep 0.4
                done
                kill -9 "$pid" 2>/dev/null || true
                info "  - 已停止 $name (PID $pid)"
            fi
            rm -f "$pidfile"
        fi
    done
    # 兜底：按端口清理残留
    if command -v ss >/dev/null 2>&1; then
        for port in 5000 5173; do
            pids=$(ss -lptn "sport = :$port" 2>/dev/null | awk 'NR>1 {n=$NF; sub(/.*:/,"",n); sub(/,.*/,"",n); print n}' | sort -u)
            for pid in $pids; do
                [ -n "$pid" ] && [ "$pid" != "$$" ] && kill -9 "$pid" 2>/dev/null || true
            done
        done
    fi
}

do_start() {
    title "============================================================"
    title "  图床（Tuchuang）一键启动 —— Linux"
    title "  后端 ASP.NET Core 10 :5000    前端 Vite :5173"
    title "============================================================"

    # 1. 环境检查
    command -v dotnet >/dev/null 2>&1 || { error "未检测到 dotnet SDK，请先安装 .NET 10 SDK"; exit 1; }
    command -v node    >/dev/null 2>&1 || { error "未检测到 Node.js，请先安装 Node.js 18+";   exit 1; }
    command -v npm     >/dev/null 2>&1 || { error "未检测到 npm，请随 Node.js 一起安装";       exit 1; }

    # 2. 首次依赖安装
    if [ ! -d "$ROOT/backend/bin" ]; then
        info "[1/4] 还原后端 NuGet 依赖（首次较慢）..."
        (cd "$ROOT/backend" && dotnet restore TuchuangApi.csproj)
    else
        info "[1/4] 后端已构建，跳过 restore"
    fi

    if [ ! -d "$ROOT/frontend/node_modules" ]; then
        info "[2/4] 安装前端 npm 依赖（首次较慢）..."
        (cd "$ROOT/frontend" && npm install)
    else
        info "[2/4] 前端 node_modules 已存在，跳过 install"
    fi

    # 3. 清理残留进程
    info "[3/4] 清理残留进程 / 端口..."
    do_stop >/dev/null 2>&1 || true

    # 4. 启动服务（后台，写 PID 文件）
    info "[4/4] 启动后端 :5000 和前端 :5173..."

    # 后端：nohup + disown 让进程脱离 shell 作业控制，shell 退出也不会被 SIGHUP
    cd "$ROOT/backend"
    nohup dotnet run --no-launch-profile --urls http://localhost:5000 >"$BACKEND_LOG" 2>&1 &
    echo $! > "$BACKEND_PID"
    disown 2>/dev/null || true
    cd "$ROOT"

    sleep 3

    # 前端
    cd "$ROOT/frontend"
    nohup npx vite --host 127.0.0.1 --port 5173 >"$FRONTEND_LOG" 2>&1 &
    echo $! > "$FRONTEND_PID"
    disown 2>/dev/null || true
    cd "$ROOT"

    # 5. 启动后健康检查（进程是否存活）
    sleep 4

    backend_pid="$(cat "$BACKEND_PID" 2>/dev/null || true)"
    frontend_pid="$(cat "$FRONTEND_PID" 2>/dev/null || true)"

    if [ -z "$backend_pid" ] || ! kill -0 "$backend_pid" 2>/dev/null; then
        error "后端启动失败，最近 20 行日志："
        tail -20 "$BACKEND_LOG" >&2 || true
        # 清理已启动的前端
        if [ -n "$frontend_pid" ] && kill -0 "$frontend_pid" 2>/dev/null; then
            kill "$frontend_pid" 2>/dev/null || true
        fi
        rm -f "$FRONTEND_PID" "$BACKEND_PID"
        exit 1
    fi
    if [ -z "$frontend_pid" ] || ! kill -0 "$frontend_pid" 2>/dev/null; then
        error "前端启动失败，最近 20 行日志："
        tail -20 "$FRONTEND_LOG" >&2 || true
        # 清理已启动的后端
        kill "$backend_pid" 2>/dev/null || true
        rm -f "$BACKEND_PID" "$FRONTEND_PID"
        exit 1
    fi

    info ""
    info "============================================================"
    info "启动完成！"
    info ""
    info "  浏览页:    http://localhost:5173"
    info "  管理页:    http://localhost:5173/manage"
    info "  管理员:    admin / admin123"
    info ""
    info "  停止服务:  ./stop.sh   或   bash run.sh stop"
    info "  查看日志:  tail -f logs/backend.log    tail -f logs/frontend.log"
    info "============================================================"
    info ""
}

do_status() {
    for name in backend frontend; do
        pidfile="$LOG_DIR/$name.pid"
        port=$( [ "$name" = "backend" ] && echo 5000 || echo 5173 )
        if [ -f "$pidfile" ]; then
            pid="$(cat "$pidfile" 2>/dev/null || true)"
            if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
                info "$name: 运行中  PID=$pid  端口=$port"
            else
                warn "$name: 未运行  (PID 文件存在但进程不存在)"
            fi
        else
            warn "$name: 未运行  (无 PID 文件)"
        fi
    done
}

do_logs() {
    local target="${1:-all}"
    case "$target" in
        backend|be) tail -F "$BACKEND_LOG" ;;
        frontend|fe) tail -F "$FRONTEND_LOG" ;;
        *) 
            info "tail -F 两个日志（Ctrl+C 退出）"
            tail -F "$BACKEND_LOG" "$FRONTEND_LOG"
            ;;
    esac
}

# ---------------- 子命令分发 ----------------

case "${1:-start}" in
    start)   do_start ;;
    stop)    do_stop ;;
    restart) do_stop; do_start ;;
    status)  do_status ;;
    logs)    shift; do_logs "${1:-all}" ;;
    -h|--help|help)
        cat <<'HELP'
用法:
  ./run.sh              启动前后端（默认）
  ./run.sh stop         停止
  ./run.sh restart      重启
  ./run.sh status       查看运行状态
  ./run.sh logs [backend|frontend]   查看日志（默认两个一起）

如果没有可执行权限：
  bash run.sh ...
  或先赋权：  chmod +x run.sh stop.sh
HELP
        ;;
    *)
        error "未知子命令: $1"
        echo "运行 ./run.sh --help 查看用法" >&2
        exit 1
        ;;
esac
