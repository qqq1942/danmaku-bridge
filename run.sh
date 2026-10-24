#!/usr/bin/env bash
# 弹幕桥接 启停脚本
set -uo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PIDFILE="$DIR/.bridge.pid"
LOG="$DIR/bridge.out.log"
PATTERN='node (.*/)?danmaku-bridge\.js'

[ -f "$DIR/config.env" ] && set -a && . "$DIR/config.env" && set +a

# 真正在跑的那个 node 进程号（优先按命令行找，pidfile 里的可能是 setsid 外壳）
real_pid() {
  pgrep -f "$PATTERN" | head -1
}

is_running() {
  # 1) pidfile 里的进程还活着
  if [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE" 2>/dev/null)" 2>/dev/null; then
    return 0
  fi
  # 2) 兜底：按命令行匹配（pidfile 可能丢失或过期）
  real_pid >/dev/null 2>&1
}

start() {
  if is_running; then echo "已在运行 (PID $(real_pid))"; return 0; fi
  echo "[$(date '+%F %T')] 启动弹幕桥接 ROOM_ID=${ROOM_ID:-未配置}" >> "$LOG"
  cd "$DIR" && setsid nohup node danmaku-bridge.js >> "$LOG" 2>&1 < /dev/null &
  sleep 2
  local pid; pid=$(real_pid)
  if [ -n "$pid" ]; then
    echo "$pid" > "$PIDFILE"
    echo "已启动 (PID $pid)"
  else
    echo "启动失败，查看 $LOG"; return 1
  fi
}

stop() {
  local pids; pids=$(pgrep -f "$PATTERN")
  if [ -z "$pids" ]; then echo "未在运行"; rm -f "$PIDFILE"; return 0; fi
  local shown; shown=$(echo $pids | tr '\n' ' ')
  echo "$pids" | xargs -r kill 2>/dev/null
  sleep 1
  pids=$(pgrep -f "$PATTERN")
  [ -n "$pids" ] && echo "$pids" | xargs -r kill -9 2>/dev/null
  rm -f "$PIDFILE"
  echo "已停止 (PID $shown)"
}

status() {
  local pid; pid=$(real_pid)
  if [ -n "$pid" ]; then
    echo "运行中 (PID $pid)"
    echo "最近日志："; tail -5 "$DIR/bridge.log" 2>/dev/null | sed 's/^/  /'
  else
    echo "未运行"
  fi
}

case "${1:-status}" in
  start) start ;;
  stop) stop ;;
  restart) stop; sleep 1; start ;;
  status) status ;;
  *) echo "用法: $0 {start|stop|restart|status}"; exit 2 ;;
esac
