#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
#  安全重启泰拉瑞亚服务器
#
#  2026-10-24 回档事故后新增的两条铁律：
#    1. 重启前必须先存档并确认落盘（世界在内存里，不存盘就停服 = 回档）；
#    2. 重启前必须先打招呼（游戏内广播 + QQ 通知 + 倒计时）。
#
#  用法：
#    ./restart.sh            只做预检，什么都不动（默认安全）
#    ./restart.sh --yes      广播 + QQ 通知 + 倒计时 → 存档 → 停 → 启 → 验证
#    ./restart.sh --dry-run  打印完整流程，不执行
#    ./restart.sh --when-empty  有人在线就一直等，等到空场再自动重启（可后台跑）
# ═══════════════════════════════════════════════════════════════
set -uo pipefail

BASE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$BASE" || exit 1

CONTAINER="${CONTAINER:-tshock}"
REST_PORT="${REST_PORT:-7878}"
WORLD="$BASE/worlds/Terraria.wld"
COUNTDOWN="${COUNTDOWN:-30}"

MODE="precheck"
case "${1:-}" in
  --yes|-y)    MODE="run" ;;
  --dry-run)   MODE="dryrun" ;;
  --when-empty) MODE="whenempty" ;;
  "")          MODE="precheck" ;;
  *) echo "用法: $0 [--yes|--dry-run|--when-empty]"; exit 1 ;;
esac

log() { printf '[%s] %s\n' "$(date '+%F %T')" "$*"; }
TOKEN=$(cat "$BASE/.rest_token" 2>/dev/null || true)

api() {  # api <path>
  curl -s -m 25 "http://127.0.0.1:$REST_PORT$1?token=$TOKEN" 2>/dev/null
}
api_post() {  # api_post <path> [--data-urlencode k=v]...
  local p="$1"; shift
  curl -s -m 25 -X POST "http://127.0.0.1:$REST_PORT$p?token=$TOKEN" "$@" 2>/dev/null
}

# ── QQ 通知（复用 watchdog.conf 里的 napcat 配置）──────────────────
[ -f "$BASE/watchdog.conf" ] && . "$BASE/watchdog.conf"
qq() {
  [ "${NOTIFY_ENABLE:-0}" = "1" ] || return 0
  # 洗掉控制字符/ESC，否则 napcat 会回 Invalid JSON
  set -- "$(printf '%s' "$1" | tr -d '\000-\037' | tr -d '\r')"
  [ -n "${QQ_TARGET:-}" ] || return 0
  local ip
  ip=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "${NAPCAT_CONTAINER:-napcat}" 2>/dev/null)
  [ -n "$ip" ] || { log "   QQ 通知跳过：napcat 未找到"; return 0; }
  local url="http://$ip:${NAPCAT_HTTP_PORT:-3000}" payload
  if [ -n "${QQ_GROUP:-}" ]; then
    payload="{\"group_id\":$QQ_GROUP,\"message\":\"$1\"}"; url="$url/send_group_msg"
  else
    payload="{\"user_id\":$QQ_TARGET,\"message\":\"$1\"}"; url="$url/send_private_msg"
  fi
  local resp
  resp=$(curl -s -m 10 -X POST "$url" -H "Content-Type: application/json" \
    -H "Authorization: Bearer ${NAPCAT_TOKEN:-}" -d "$payload" 2>/dev/null)
  echo "$resp" | grep -q '"status":"ok"' && log "   ✅ QQ 已通知" || log "   ⚠️ QQ 通知失败: ${resp:0:120}"
}

listening() {
  docker exec "$CONTAINER" sh -c 'cat /proc/net/tcp' 2>/dev/null | grep -qi ':1E61'
}
online_players() {
  api /v2/players/list | python3 -c "
import sys,json
try:
    d=json.load(sys.stdin); p=[x['nickname'] for x in d.get('players',[])]
    print('、'.join(p) if p else '无')
except Exception: print('未知')" 2>/dev/null
}

player_count() {
  api /v2/players/list 2>/dev/null | python3 -c "
import sys,json
try: print(len(json.load(sys.stdin).get('players',[])))
except Exception: print(-1)" 2>/dev/null || echo -1
}

MAINT="$BASE/.maintenance"
KEEP_MAINT=0
rm_maint() { [ "$KEEP_MAINT" = "1" ] || rm -f "$MAINT"; }
trap rm_maint EXIT

# ══════════════ 预检 ══════════════
log "═══ 预检 ═══"

log "1) /worlds 挂载"
MOUNT=$(docker inspect "$CONTAINER" \
  --format '{{range .Mounts}}{{if eq .Destination "/worlds"}}{{.Source}}{{end}}{{end}}' 2>/dev/null)
if [ "$MOUNT" != "$BASE/worlds" ]; then
  log "   ❌ 挂载异常：期望 $BASE/worlds，实际 '${MOUNT}'"
  log "      此时重启会把世界写到别处 → 回档。已中止。"
  exit 2
fi
log "   ✅ $MOUNT → /worlds"

log "2) 世界文件"
log "   $(stat -c '%n  %s bytes  %y' "$WORLD" 2>/dev/null || echo '⚠️ 不存在')"
log "   最新自动备份：$(ls -t tshock/backups/*.bak 2>/dev/null | head -1 | xargs -r basename)"

log "3) 在线玩家：$(online_players)"

if [ "$MODE" = "precheck" ]; then
  log ""
  log "预检通过，未做任何改动。"
  log "确认要重启请执行： ./restart.sh --yes"
  log "（会先游戏内广播 + QQ 通知 + ${COUNTDOWN} 秒倒计时，再存档停服）"
  exit 0
fi

# ══════════════ 空场等待 ══════════════
if [ "$MODE" = "whenempty" ]; then
  COUNTDOWN=5       # 空场时不用等 30 秒，5 秒够看提示
  log "═══ 空场等待模式 ═══"
  log "有人在线就一直等；连续 2 次探到 0 人才重启。日志: restart-when-empty.log"
  ZERO=0
  while true; do
    N=$(player_count)
    if [ "$N" = "0" ]; then
      ZERO=$(( ZERO + 1 ))
      log "   0 人在线（连续 ${ZERO}/2）"
      [ "$ZERO" -ge 2 ] && { log "   ✅ 已空场，开始重启"; break; }
    elif [ "$N" -lt 0 ] 2>/dev/null; then
      ZERO=0
      log "   ⚠️ REST 无响应/服务已停，继续等（这情况看门狗会处理）"
    else
      ZERO=0
      log "   在线 ${N} 人，继续等（每 30 秒查一次）"
    fi
    sleep 30
  done
  MODE="run"
fi

# ══════════════ 执行 ══════════════
log "═══ 执行 ═══"

if [ "$MODE" = "run" ]; then
  log "4) 打招呼：游戏内广播 + QQ 通知"
  api_post /v2/server/broadcast \
    --data-urlencode "msg=【系统】服务器将在 ${COUNTDOWN} 秒后重启（先存档，不会回档），届时会断线，稍后重进即可。" >/dev/null \
    && log "   ✅ 游戏内已广播"
  qq "🟡 泰拉瑞亚服务器将重启\\n在线玩家：$(online_players)\\n已先存档，${COUNTDOWN} 秒后断线重启，约 15 秒恢复。"

  log "5) 倒计时 ${COUNTDOWN} 秒（给玩家反应时间）"
  for i in $(seq "$COUNTDOWN" -1 1); do
    if [ $(( i % 10 )) -eq 0 ] || [ "$i" -le 5 ]; then
      api_post /v2/server/broadcast --data-urlencode "msg=【系统】${i} 秒后重启…" >/dev/null
      log "   ${i}s"
    fi
    sleep 1
  done
fi

if [ "$MODE" = "dryrun" ]; then
  log "4) [模拟] 游戏内广播 + QQ 通知 + ${COUNTDOWN} 秒倒计时"
fi

log "6) 进入维护模式（暂停看门狗，否则它会立刻把服务器拉回来）"
[ "$MODE" = "run" ] && touch "$MAINT" && log "   ✅ $MAINT 已创建" || log "   [模拟] touch $MAINT"

log "7) 存档并确认落盘"
if [ "$MODE" = "run" ]; then
  BEFORE=$(stat -c %Y "$WORLD" 2>/dev/null || echo 0)
  RESP=$(api_post /v2/world/save)
  echo "$RESP" | grep -q '"status": *"200"' || { log "   ❌ 存档失败：$RESP"; KEEP_MAINT=1; exit 3; }
  OK=0
  for _ in $(seq 1 15); do
    sleep 2
    [ "$(stat -c %Y "$WORLD" 2>/dev/null || echo 0)" -gt "$BEFORE" ] && { OK=1; break; }
  done
  if [ "$OK" != "1" ]; then
    log "   ❌ 30 秒内 $WORLD 没更新 —— 存档没落盘，已中止重启（现在停服就是回档）"
    KEEP_MAINT=1; exit 4
  fi
  log "   ✅ 已落盘：$(stat -c '%s bytes  %y' "$WORLD")"
else
  log "   [模拟] POST /v2/world/save 并校验 mtime 变化"
fi

log "8) 停止 → 启动"
if [ "$MODE" = "run" ]; then
  docker compose stop 2>&1 | tail -1 | sed 's/^/   /'
  docker compose start 2>&1 | tail -1 | sed 's/^/   /'
else
  log "   [模拟] docker compose stop && docker compose start"
  log "模拟完成，未做任何改动。"; exit 0
fi

log "9) 等待 7777 监听"
for i in $(seq 1 40); do
  sleep 2
  listening && { log "   ✅ $((i*2)) 秒内已监听"; break; }
  if [ "$i" = "40" ]; then
    log "   ❌ 80 秒仍未监听，保留维护标记，请人工检查"; KEEP_MAINT=1; exit 5
  fi
done

log "10) 验证"
api /v2/server/status | grep -q '"status": *"200"' && log "   ✅ REST 正常" || log "   ⚠️ REST 未就绪"
log "   世界：$(docker logs --since 2m "$CONTAINER" 2>&1 | grep -a 'Loading world data' | tail -1 | sed 's/\x1b\[[0-9;]*m//g' | tr -d '\r')"
log "   文件：$(stat -c '%s bytes  写入 %y' "$WORLD")"

# 11) 插件自检：让插件在出生点刷一只僵尸，从日志里确认"建了 + 同步了 + 坐标合理"
log "11) 插件自检（/dspawn 僵尸 1 @spawn）"
SELFTEST="（插件未响应）"
API_RAW=$(curl -s -m 15 --get "http://127.0.0.1:$REST_PORT/v3/server/rawcmd" \
  --data-urlencode "token=$TOKEN" --data-urlencode "cmd=/dspawn 僵尸 1 @spawn" 2>/dev/null)
echo "$API_RAW" | grep -q '已排队' && log "   ✅ 指令被受理" || log "   ⚠️ 指令未被受理：${API_RAW:0:160}"
sleep 4
SELFTEST=$(docker logs --since 40s "$CONTAINER" 2>&1 | grep -a 'DanmakuSpawn.*实际刷在' | tail -1 | sed 's/\x1b\[[0-9;]*m//g')
[ -n "$SELFTEST" ] && log "   ✅ $SELFTEST" || log "   ⚠️ 日志里没有刷怪记录"

rm -f "$MAINT"
log ""
log "✅ 重启完成，维护标记已撤除"
qq "🟢 泰拉瑞亚服务器已重启完成\\n世界已存档，可以进服了。\\n插件自检：${SELFTEST}"
