#!/usr/bin/env bash
#
# 泰拉瑞亚服务器健康自检 + 自动重启
#
# 背景：2026-09-16 服务器遭畸形数据包攻击，游戏监听线程死亡，
#       但进程和 REST API 仍然存活，导致 systemd/docker 的
#       restart 策略完全检测不到，故障静默了 4 天。
#
# 本脚本检测「监听是否真的存在」，而不是只检测「进程是否活着」。
#
# 用法：
#   ./watchdog.sh           正常巡检（供 cron 调用）
#   ./watchdog.sh --status  只报告状态，不重启
#   ./watchdog.sh --dry-run 模拟故障处理流程，不真的重启
#   ./watchdog.sh --test-notify  测试 QQ 通知是否可用
#
set -uo pipefail

# ──────────────── 配置 ────────────────
BASE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONF="$BASE/watchdog.conf"

# 环境变量优先级高于配置文件。
# 否则 `CONTAINER=xxx ./watchdog.sh`、`EXT_PORT=9999 ./watchdog.sh --ext`
# 这类临时覆盖会被 watchdog.conf 里的值吃掉，导致测试测了个寂寞。
OVERRIDABLE="CONTAINER COMPOSE_DIR REST_PORT LOG_MAX_LINES MAX_RESTARTS_PER_HOUR
NOTIFY_ENABLE NAPCAT_CONTAINER NAPCAT_HTTP_PORT NAPCAT_TOKEN QQ_TARGET QQ_GROUP
EXT_CHECK_ENABLE EXT_HOST EXT_PORT EXT_NODES EXT_REALERT_HOURS"

for _v in $OVERRIDABLE; do
  printf -v "_env_$_v" '%s' "${!_v:-}"
done

[ -f "$CONF" ] && . "$CONF"

for _v in $OVERRIDABLE; do
  _n="_env_$_v"
  _val="${!_n}"
  [ -n "$_val" ] && printf -v "$_v" '%s' "$_val"
done

CONTAINER="${CONTAINER:-tshock}"
COMPOSE_DIR="${COMPOSE_DIR:-$BASE}"
REST_PORT="${REST_PORT:-7878}"
LAN_ENDPOINT="${LAN_ENDPOINT:-}"   # 例：192.168.1.100:7777，用于告警文案
EXT_HOST="${EXT_HOST:-}"
EXT_PORT="${EXT_PORT:-}"
EXT_NODES="${EXT_NODES:-3}"
EXT_REALERT_HOURS="${EXT_REALERT_HOURS:-6}"
LOG="$BASE/watchdog.log"
LOG_MAX_LINES="${LOG_MAX_LINES:-3000}"
MAX_RESTARTS_PER_HOUR="${MAX_RESTARTS_PER_HOUR:-3}"

MODE="run"
case "${1:-}" in
  --status)      MODE="status" ;;
  --dry-run)     MODE="dryrun" ;;
  --test-notify) MODE="testnotify" ;;
  --test-failure) MODE="testfailure" ;;
  --ext)          MODE="ext" ;;
  "")            MODE="run" ;;
  *) echo "未知参数: $1"; exit 2 ;;
esac

# ──────────────── 工具函数 ────────────────
log() {
  local line="$(date '+%F %T') $*"
  echo "$line" >> "$LOG"
  [ "$MODE" != "run" ] && echo "$line"
}

# 日志裁剪，防止无限增长
rotate_log() {
  [ -f "$LOG" ] || return 0
  local n; n=$(wc -l < "$LOG" 2>/dev/null || echo 0)
  if [ "$n" -gt "$LOG_MAX_LINES" ]; then
    tail -n "$((LOG_MAX_LINES / 2))" "$LOG" > "$LOG.tmp" && mv "$LOG.tmp" "$LOG"
  fi
}

# ──────────────── 健康检查 ────────────────
# 1) 容器进程是否在运行
container_running() {
  [ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null)" = "true" ]
}

# 2) 容器内 7777 是否真的处于 LISTEN 状态（关键检查，能发现"进程活着但监听死了"）
listener_alive() {
  docker exec "$CONTAINER" sh -c 'cat /proc/net/tcp /proc/net/tcp6 2>/dev/null' 2>/dev/null \
    | awk '$4=="0A" {print $2}' \
    | grep -qiE ':1E61$'
}

# 3) 【已移除】对游戏端口的 TCP 连接测试
#    血泪教训：裸 TCP 连接会累积 CLOSE_WAIT 僵尸 socket，约 20-30 个之后
#    Terraria 的监听线程会静默死亡。检查手段本身成了故障源。
#    现在改用 leaked_sockets() 统计僵尸连接，超阈值就提前主动重启。

# 4) 僵尸连接数（CLOSE_WAIT 且本地端口为 7777）
#    这是"监听线程即将死亡"的前兆指标
leaked_sockets() {
  # 注意：grep -c 在 0 命中时退出码是 1，会把 "0" 和调用方的 || echo 0 拼成两行，
  # 导致后面 [ "$LK" -ge N ] 报"需要整数"。这里统一兜成单个数字。
  local n
  n=$(docker exec "$CONTAINER" sh -c 'cat /proc/net/tcp /proc/net/tcp6 2>/dev/null' 2>/dev/null \
    | awk '$4=="08" {print $2}' | grep -ciE ':1E61$')
  echo "${n:-0}"
}

# 5) REST API 是否响应（判断进程是否还能正常处理请求）
rest_alive() {
  local token; token=$(cat "$BASE/.rest_token" 2>/dev/null)
  [ -n "$token" ] || return 1
  curl -s -m 6 -o /dev/null -w '%{http_code}' \
    "http://127.0.0.1:$REST_PORT/v2/server/status?token=$token" 2>/dev/null | grep -q '^200$'
}

# 综合健康判定（注意：不包含任何对游戏端口的主动连接）
healthy() {
  container_running || return 1
  listener_alive    || return 1
  return 0
}

# 返回具体的故障原因，便于告警
diagnose() {
  local reasons=()
  container_running || reasons+=("容器未运行")
  if container_running; then
    listener_alive || reasons+=("7777 无监听（监听线程已死）")
    rest_alive     || reasons+=("REST API 无响应")
    local lk; lk=$(leaked_sockets)
    [ "${lk:-0}" -ge "$LEAK_THRESHOLD" ] && reasons+=("僵尸连接 ${lk} 个（阈值 ${LEAK_THRESHOLD}）")
  fi
  [ ${#reasons[@]} -eq 0 ] && echo "正常" || (IFS='、'; echo "${reasons[*]}")
}

# ──────────────── 通知 ────────────────
# 通过 NapCat 的 OneBot HTTP 接口发 QQ 消息
# 需要先在 napcat 配置里启用 httpServers，并在 watchdog.conf 填 QQ_TARGET
notify() {
  local msg="$1"
  [ "${NOTIFY_ENABLE:-0}" = "1" ] || return 0
  [ -n "${QQ_TARGET:-}" ] || return 0

  local napcat_ip; napcat_ip=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "${NAPCAT_CONTAINER:-napcat}" 2>/dev/null)
  [ -n "$napcat_ip" ] || { log "通知失败：找不到 napcat 容器 IP"; return 1; }

  local url="http://$napcat_ip:${NAPCAT_HTTP_PORT:-3000}"
  local payload
  if [ -n "${QQ_GROUP:-}" ]; then
    payload="{\"group_id\":$QQ_GROUP,\"message\":\"$msg\"}"
    url="$url/send_group_msg"
  else
    payload="{\"user_id\":$QQ_TARGET,\"message\":\"$msg\"}"
    url="$url/send_private_msg"
  fi

  local resp
  resp=$(curl -s -m 10 -X POST "$url" \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer ${NAPCAT_TOKEN:-}" \
    -d "$payload" 2>/dev/null)

  if echo "$resp" | grep -q '"status":"ok"'; then
    log "QQ 通知已发送"
  else
    log "QQ 通知失败: ${resp:0:200}"
  fi
}

# ──────────────── 重启限流 ────────────────
STAMP="$BASE/.watchdog_restarts"
recent_restarts() {
  [ -f "$STAMP" ] || { echo 0; return; }
  local cutoff; cutoff=$(date -d '1 hour ago' +%s 2>/dev/null || echo 0)
  awk -v c="$cutoff" '$1 > c' "$STAMP" 2>/dev/null | wc -l
}
record_restart() { date +%s >> "$STAMP"; }

# ──────────────── 恢复动作 ────────────────
# ──────────────── 存档落盘检查 ────────────────
# 踩过的坑（2026-10-24）：容器 /worlds 挂载错位时，世界每 10 分钟照常存进
# tshock/backups，但宿主上的 worlds/Terraria.wld 停在旧时间，重启后就"回档"。
# 判据：最新自动备份比 world 文件新超过 WORLD_STALE_MIN 分钟 → 存档没落盘。
world_stale_secs() {
  local w="$BASE/worlds/Terraria.wld"
  [ -f "$w" ] || { echo -1; return; }
  local latest a b
  latest=$(ls -t "$BASE"/tshock/backups/*.bak 2>/dev/null | head -1)
  [ -n "$latest" ] || { echo 0; return; }
  a=$(stat -c %Y "$w" 2>/dev/null || echo 0)
  b=$(stat -c %Y "$latest" 2>/dev/null || echo 0)
  local d=$(( b - a ))
  [ "$d" -lt 0 ] && d=0
  echo "$d"
}

do_restart() {
  # 限流检查放在最前面，这样 --dry-run 也能验证熔断逻辑
  local n; n=$(recent_restarts)
  if [ "$n" -ge "$MAX_RESTARTS_PER_HOUR" ]; then
    log "⛔ 最近 1 小时已重启 $n 次，超过上限 $MAX_RESTARTS_PER_HOUR，停止自动重启（需人工介入）"
    notify "🔴 泰拉瑞亚服务器异常【需人工介入】\\n原因：${DIAG}\\n最近1小时已自动重启${n}次，已达上限，不再重启。"
    return 1
  fi

  if [ "$MODE" = "dryrun" ]; then
    log "[模拟] 将会执行恢复流程（优先优雅关闭以保存世界，失败则 docker restart）"
    return 0
  fi

  notify "🟡 泰拉瑞亚服务器异常，正在自动恢复\\n原因：${DIAG}"

  # 优先优雅关闭：会保存世界存档，且 docker 的 restart 策略会自动拉起
  if rest_alive; then
    local token; token=$(cat "$BASE/.rest_token" 2>/dev/null)
    log "尝试优雅关闭（会保存世界）..."
    curl -s -m 8 -o /dev/null \
      "http://127.0.0.1:$REST_PORT/v2/server/off?token=$token&confirm=true" 2>/dev/null
    for _ in $(seq 1 15); do
      sleep 3
      if healthy; then log "✅ 优雅重启成功"; record_restart; return 0; fi
    done
    log "优雅关闭未生效，改用 docker restart"
  else
    log "REST API 无响应，直接 docker restart"
  fi

  cd "$COMPOSE_DIR" 2>/dev/null && docker compose restart "$CONTAINER" >/dev/null 2>&1 \
    || docker restart "$CONTAINER" >/dev/null 2>&1

  for _ in $(seq 1 20); do
    sleep 3
    if healthy; then
      log "✅ 重启成功，服务已恢复"
      record_restart
      notify "🟢 泰拉瑞亚服务器已自动恢复\\n原因：${DIAG}\\n现在可以连了。"
      return 0
    fi
  done

  log "❌ 重启后仍未恢复，请人工检查"
  notify "🔴 泰拉瑞亚服务器自动恢复失败，请人工检查\\n原因：${DIAG}"
  return 1
}

# ──────────────── 公网入口探测 ────────────────
# 从公网另一侧对 域名:端口 做 TCP 连接测试，双源冗余：
#   源1 portchecker.io   —— 直接 API，响应快，作为主检测
#   源2 check-host.net   —— 多个境外节点，作为交叉验证
# 只有「没有任何来源报告可达」时才判定 fail，最大限度避免误报。
#
# 输出三态：
#   "ok <通数> <不通数>"    至少一个来源确认可达
#   "fail <通数> <不通数>"  所有来源都确认不可达
#   "unknown"               检测服务异常/被限流，无法判定（此时不告警）
ext_probe() {
  local pc_verdict="unknown" ch_verdict="unknown"

  # ── 源1: portchecker.io ──
  local r
  r=$(curl -s -m 25 -X POST "https://portchecker.io/api/v1/query" \
        -H "Content-Type: application/json" \
        -d "{\"host\":\"${EXT_HOST}\",\"ports\":[${EXT_PORT}]}" 2>/dev/null)
  if [ -n "$r" ]; then
    pc_verdict=$(printf '%s' "$r" | python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
except Exception:
    print('unknown'); sys.exit()
if d.get('error'):
    print('unknown'); sys.exit()
ch = d.get('check') or []
if not ch:
    print('unknown'); sys.exit()
ok = sum(1 for c in ch if c.get('status'))
print('ok' if ok else 'fail')
" 2>/dev/null)
  fi
  [ "$pc_verdict" = "ok" ] && { echo "ok 1 0"; return; }

  # ── 源2: check-host.net 多节点 ──
  local resp rid res out i
  resp=$(curl -s -m 20 -H "Accept: application/json" \
    "https://check-host.net/check-tcp?host=${EXT_HOST}:${EXT_PORT}&max_nodes=${EXT_NODES}" 2>/dev/null)
  rid=$(printf '%s' "$resp" | python3 -c "import sys,json;print(json.load(sys.stdin).get('request_id',''))" 2>/dev/null)
  if [ -n "$rid" ]; then
    for i in $(seq 1 8); do
      sleep 3
      res=$(curl -s -m 15 -H "Accept: application/json" \
        "https://check-host.net/check-result/$rid" 2>/dev/null)
      [ -n "$res" ] || continue
      out=$(printf '%s' "$res" | python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
except Exception:
    print('wait'); sys.exit()
ok = fail = pend = 0
for n, r in d.items():
    if not r:
        pend += 1; continue
    r = r[0] if isinstance(r, list) else r
    if isinstance(r, dict) and 'error' in r:
        fail += 1
    elif isinstance(r, dict):
        ok += 1
    else:
        pend += 1
if ok > 0:
    print(f'ok {ok} {fail}')
elif pend == 0 and fail > 0:
    print(f'fail {ok} {fail}')
else:
    print('wait')
" 2>/dev/null)
      case "$out" in
        ok\ *)  echo "$out"; return ;;
        fail\ *) ch_verdict="fail"; break ;;
      esac
    done
  fi

  # ── 汇总判定 ──
  if [ "$ch_verdict" = "fail" ] || [ "$pc_verdict" = "fail" ]; then
    echo "fail 0 ${EXT_NODES}"
    return
  fi
  echo "unknown"
}

# ──────────────── 主流程 ────────────────
rotate_log

DIAG=$(diagnose)

case "$MODE" in
  status)
    log "── 状态巡检 ──"
    log "  容器运行   : $(container_running && echo ✅ || echo ❌)"
    log "  7777 监听  : $(listener_alive && echo ✅ || echo ❌)"
    log "  REST API   : $(rest_alive && echo ✅ || echo ❌)"
    LK=$(leaked_sockets 2>/dev/null || echo 0)
    if [ "${LK:-0}" -ge "$LEAK_THRESHOLD" ]; then
      log "  僵尸连接   : ⚠️ ${LK} 个（阈值 ${LEAK_THRESHOLD}，下次巡检将预防性重启）"
    else
      log "  僵尸连接   : ${LK} 个 / 阈值 ${LEAK_THRESHOLD} ✅"
    fi
    WS=$(world_stale_secs)
    if [ "$WS" = "-1" ]; then
      log "  存档落盘   : ❌ worlds/Terraria.wld 不存在"
    elif [ "$WS" -ge $(( WORLD_STALE_MIN * 60 )) ]; then
      log "  存档落盘   : ⚠️ 已 $(( WS / 60 )) 分钟未更新（备份比存档新），重启会回档！"
    else
      log "  存档落盘   : ✅ $(( WS / 60 )) 分钟前落盘"
    fi
    log "  综合判定   : $DIAG"
    log "  近1小时重启: $(recent_restarts) 次"
    if [ -f "$BASE/.watchdog_heartbeat" ]; then
      hb=$(cat "$BASE/.watchdog_heartbeat" 2>/dev/null || echo 0)
      age=$(( $(date +%s) - hb ))
      log "  上次自动巡检: ${age} 秒前"
      [ "$age" -gt 900 ] && log "  ⚠️ 超过 15 分钟未巡检，检查 cron 是否正常"
    else
      log "  上次自动巡检: 尚无记录（等待 cron 首次执行）"
    fi
    exit 0
    ;;

  testfailure)
    log "── 故障告警测试（不会真的重启服务器）──"
    DIAG="测试：模拟 7777 无监听（监听线程已死）、TCP 连接失败"
    notify "🟡 [测试] 泰拉瑞亚服务器异常，正在自动恢复\\n原因：${DIAG}\\n（这是测试消息，服务器实际正常）"
    log "已发送测试告警，请查看 QQ。未执行任何重启操作。"
    exit 0
    ;;

  testnotify)
    log "测试 QQ 通知..."
    NOTIFY_ENABLE=1 notify "🔔 泰拉瑞亚服务器看门狗测试消息（收到即表示通知链路正常）"
    log "已尝试发送，请查看 QQ。若失败请看上方日志。"
    exit 0
    ;;

  dryrun)
    log "── 模拟巡检 ──"
    log "  当前判定: $DIAG"
    if healthy; then
      log "  服务器健康，无需处理"
    else
      log "  不健康，模拟恢复流程："
      do_restart
    fi
    exit 0
    ;;

  ext)
    [ "${EXT_CHECK_ENABLE:-0}" = "1" ] || exit 0
    if [ -z "$EXT_HOST" ] || [ -z "$EXT_PORT" ]; then
      log "公网监控未配置 EXT_HOST / EXT_PORT，跳过"
      exit 0
    fi

    DOWN_FLAG="$BASE/.ext_down"
    ALERT_STAMP="$BASE/.ext_last_alert"

    log "── 公网入口巡检 ${EXT_HOST}:${EXT_PORT} ──"

    # 1) 域名解析
    dns=$(timeout 6 dig +short A "$EXT_HOST" @223.5.5.5 2>/dev/null | head -1)
    if [ -n "$dns" ]; then log "  域名解析: $dns"; else log "  ⚠️ 域名解析失败"; fi

    # 2) 外部节点探测
    probe=$(ext_probe)
    case "$probe" in
      ok*)
        set -- $probe; n_ok=$2; n_fail=$3
        log "  ✅ 外部可达: 通 $n_ok / 不通 $n_fail"
        if [ -f "$DOWN_FLAG" ]; then
          rm -f "$DOWN_FLAG" "$ALERT_STAMP"
          log "  🟢 公网入口已恢复"
          notify "🟢 泰拉瑞亚【公网入口已恢复】\\n${EXT_HOST}:${EXT_PORT} 现在可以从外网连入了。"
        fi
        ;;

      fail*)
        set -- $probe; n_ok=$2; n_fail=$3
        log "  ⚠️ 外部不可达: 通 $n_ok / 不通 $n_fail —— 二次确认中..."
        probe2=$(ext_probe)
        case "$probe2" in
          ok*)  log "  二次确认可达 → 判定为瞬时抖动，不告警"; exit 0 ;;
          fail*) log "  二次确认仍不可达 → 确认故障" ;;
          *)    log "  二次确认时检测服务异常 → 本次不告警"; exit 0 ;;
        esac

        touch "$DOWN_FLAG"

        # 告警节流：首次立即告警，此后每 EXT_REALERT_HOURS 小时提醒一次
        need_alert=1
        if [ -f "$ALERT_STAMP" ]; then
          last=$(cat "$ALERT_STAMP" 2>/dev/null || echo 0)
          if [ $(( ($(date +%s) - last) / 3600 )) -lt "$EXT_REALERT_HOURS" ]; then
            need_alert=0
          fi
        fi

        if [ "$need_alert" = "1" ]; then
          date +%s > "$ALERT_STAMP"
          msg="🔴 泰拉瑞亚【公网入口不可达】\\n${EXT_HOST}:${EXT_PORT} 从外网连不上（通 ${n_ok} / 不通 ${n_fail}）"
          if [ -n "$dns" ]; then
            msg="$msg\\n域名解析: $dns"
          else
            msg="$msg\\n⚠️ 域名解析失败"
          fi
          msg="$msg\\n局域网${LAN_ENDPOINT:+ $LAN_ENDPOINT}仍然正常。\\n可能原因：宽带换 IP 后域名没跟上 / 路由器转发规则失效 / 云端中转挂了 / 被运营商收进大内网(CGNAT)。"
          notify "$msg"
          log "  已发送告警"
        else
          log "  告警节流中（${EXT_REALERT_HOURS} 小时内已提醒过，不重复发送）"
        fi
        ;;

      *)
        log "  ⚠️ 检测服务异常（$probe），本次跳过判定，不告警"
        ;;
    esac
    exit 0
    ;;

  run)
    # 心跳：每次巡检都刷新，用来确认看门狗本身还活着
    date +%s > "$BASE/.watchdog_heartbeat"

    # 维护模式：手动改世界/停机维护时创建 .maintenance，看门狗不做任何干预。
    # 否则你想停服改文件，5 分钟内就会被它自动拉起来。
    if [ -f "$BASE/.maintenance" ]; then
      log "维护模式（.maintenance 存在），跳过巡检与自愈"
      exit 0
    fi

    # ── 预防性维护：僵尸连接超阈值就提前重启，赶在监听线程死亡之前 ──
    if container_running; then
      LEAK=$(leaked_sockets)
      if [ "${LEAK:-0}" -ge "$LEAK_THRESHOLD" ]; then
        log "⚠️ 预防性重启：僵尸连接 ${LEAK} 个（阈值 ${LEAK_THRESHOLD}）"
        DIAG="预防性维护（僵尸连接 ${LEAK} 个）"
        notify "🟡 泰拉瑞亚服务器【预防性重启】\\n僵尸连接累积 ${LEAK} 个（阈值 ${LEAK_THRESHOLD}）\\n提前清理，避免监听线程死亡。属正常维护，约 10 秒恢复。"
        do_restart
        exit $?
      fi
    fi

    if healthy; then
      # 健康时只在状态变化时记一条，避免刷日志
      if [ -f "$BASE/.watchdog_was_down" ]; then
        rm -f "$BASE/.watchdog_was_down"
        log "✅ 服务恢复正常"
      fi

      # 存档落盘检查：世界没写回宿主盘 = 重启必回档，必须告警（不重启，重启没用）
      WS=$(world_stale_secs)
      STAMP="$BASE/.world_stale_alert"
      if [ "$WS" != "-1" ] && [ "$WS" -ge $(( ${WORLD_STALE_MIN:-25} * 60 )) ]; then
        last=0; [ -f "$STAMP" ] && last=$(cat "$STAMP" 2>/dev/null || echo 0)
        if [ $(( $(date +%s) - last )) -ge 21600 ]; then
          date +%s > "$STAMP"
          notify "🔴 泰拉瑞亚【存档没落盘】\\nworlds/Terraria.wld 已 $(( WS / 60 )) 分钟没更新，但自动备份仍在生成。\\n说明世界存到了别处/挂载异常。现在重启会回档，请先人工检查 docker 挂载。"
          log "⚠️ 存档 $(( WS / 60 )) 分钟未落盘，已告警"
        fi
      fi
      exit 0
    fi

    touch "$BASE/.watchdog_was_down"
    log "⚠️ 检测到异常: $DIAG"
    do_restart
    exit $?
    ;;
esac
