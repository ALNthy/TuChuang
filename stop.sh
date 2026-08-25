#!/usr/bin/env bash
# 图床停止脚本 —— 等价于 ./run.sh stop
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG_DIR="$ROOT/logs"

# 颜色
if [ -t 1 ]; then
    C_GREEN='\033[0;32m'; C_RED='\033[0;31m'; C_YELLOW='\033[0;33m'; C_RESET='\033[0m'
else
    C_GREEN=''; C_RED=''; C_YELLOW=''; C_RESET=''
fi

info()  { printf "${C_GREEN}[$(date +%H:%M:%S)] $*${C_RESET}\n"; }
warn()  { printf "${C_YELLOW}[$(date +%H:%M:%S)] $*${C_RESET}\n"; }

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
        else
            warn "  - $name 未运行（PID $pid 不存在）"
        fi
        rm -f "$pidfile"
    else
        warn "  - $name 无 PID 文件"
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

info "已停止。"
