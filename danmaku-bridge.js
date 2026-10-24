#!/usr/bin/env node
/**
 * B站直播弹幕 → 泰拉瑞亚服务器 桥接
 *
 * 两条通路：
 *   1) 弹幕 → 游戏聊天栏   （REST /v2/server/broadcast）
 *   2) 弹幕指令 → 游戏刷怪 （REST /v3/server/rawcmd → TShock 命令 /dspawn）
 *      刷怪由服务器端插件 DanmakuSpawn 执行（在指定玩家附近 NewNPC）
 *
 * 用法：
 *   ROOM_ID=12345 node danmaku-bridge.js
 *
 * 常用环境变量：
 *   ROOM_ID        B站直播间号（短号或长号都行）
 *   REST_TOKEN     TShock REST token（默认读 ../.rest_token）
 *   MAX_PER_MIN    普通弹幕每分钟最多转发多少条（默认 12）
 *   PREFIX         游戏内聊天前缀（默认 "[弹幕]"）
 *   CMD_ENABLE     是否开启弹幕指令刷怪（默认 1，0 关闭）
 *   CMD_PREFIX     指令前缀（默认 "!"，全角"！"也认）
 *   CMD_WORDS      指令词（默认 "刷怪,spawn,召唤,出怪"）
 *   CMD_MAX_PER_MIN  全服每分钟最多几条刷怪指令（默认 6）
 *   CMD_USER_COOLDOWN 同一观众冷却秒数（默认 30）
 *   CMD_MAX_AMOUNT 单条指令最多刷几只（默认 5，插件侧还会二次限制）
 *   CMD_MIN_GUARD  最低舰长等级（0=不限, 3=舰长, 2=提督, 1=总督）
 *   CMD_MIN_MEDAL  最低粉丝牌等级（0=不限）
 *   CMD_ALLOW      白名单观众（逗号分隔，填了就只有这些人能用）
 *   CMD_BLOCK      黑名单观众（逗号分隔）
 *   CMD_DEFAULT_PLAYER 没指定玩家时固定刷谁附近（留空=插件自己随机）
 */
'use strict';

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const crypto = require('crypto');

// ─────────────────────── 配置 ───────────────────────
const BASE = path.dirname(path.resolve(__filename));

const CFG = {
  roomId: process.env.ROOM_ID || '',
  restBase: process.env.REST_BASE || 'http://127.0.0.1:7878',
  restToken: process.env.REST_TOKEN ||
    (() => {
      try { return fs.readFileSync(path.join(BASE, '..', '.rest_token'), 'utf8').trim(); }
      catch { return ''; }
    })(),
  maxPerMin: parseInt(process.env.MAX_PER_MIN || '12', 10),
  prefix: process.env.PREFIX || '[弹幕]',
  minLen: parseInt(process.env.MIN_LEN || '1', 10),
  maxLen: parseInt(process.env.MAX_LEN || '50', 10),
  logFile: path.join(BASE, 'bridge.log'),
  // 屏蔽词（命中的弹幕不转发）
  blockWords: (process.env.BLOCK_WORDS || '').split(',').filter(Boolean),
  // 可选：B站登录 cookie（SESSDATA），一般不需要
  sessdata: process.env.SESSDATA || '',
};

// 刷怪指令相关配置
const CMD = {
  enable: (process.env.CMD_ENABLE || '1') !== '0',
  prefix: process.env.CMD_PREFIX || '!',
  words: (process.env.CMD_WORDS || '刷怪,spawn,召唤,出怪')
    .split(',').map(s => s.trim().toLowerCase()).filter(Boolean),
  maxAmount: Math.max(1, parseInt(process.env.CMD_MAX_AMOUNT || '5', 10)),
  defaultAmount: Math.max(1, parseInt(process.env.CMD_DEFAULT_AMOUNT || '1', 10)),
  maxPerMin: Math.max(1, parseInt(process.env.CMD_MAX_PER_MIN || '6', 10)),
  userCooldown: Math.max(0, parseInt(process.env.CMD_USER_COOLDOWN || '30', 10)),
  minGuard: parseInt(process.env.CMD_MIN_GUARD || '0', 10),
  minMedal: parseInt(process.env.CMD_MIN_MEDAL || '0', 10),
  allow: (process.env.CMD_ALLOW || '').split(',').map(s => s.trim()).filter(Boolean),
  block: (process.env.CMD_BLOCK || '').split(',').map(s => s.trim()).filter(Boolean),
  defaultPlayer: (process.env.CMD_DEFAULT_PLAYER || '').trim(),
};

const UA = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36';

// ─────────────────────── 日志 ───────────────────────
function log(msg) {
  const line = `[${new Date().toISOString().replace('T', ' ').slice(0, 19)}] ${msg}`;
  console.log(line);
  try { fs.appendFileSync(CFG.logFile, line + '\n'); } catch { }
}

// ─────────────────────── WBI 签名 ───────────────────────
const MIXIN_TAB = [
  46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
  27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
  37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
  22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52,
];

let wbiMixin = null;
let loginUid = 0;
let wbiExpire = 0;

async function apiGet(url, extraHeaders = {}) {
  const headers = {
    'User-Agent': UA,
    Referer: 'https://live.bilibili.com/',
    ...extraHeaders,
  };
  if (CFG.sessdata) headers['Cookie'] = `SESSDATA=${CFG.sessdata}`;
  const r = await fetch(url, { headers });
  return r.json();
}

async function getMixinKey(force = false) {
  if (!force && wbiMixin && Date.now() < wbiExpire) return wbiMixin;

  const nav = await apiGet('https://api.bilibili.com/x/web-interface/nav');
  if (nav.data && nav.data.isLogin && nav.data.mid) loginUid = Number(nav.data.mid);
  const wbi = (nav.data && nav.data.wbi_img) || {};
  const img = (wbi.img_url || '').split('/').pop().split('.')[0];
  const sub = (wbi.sub_url || '').split('/').pop().split('.')[0];
  if (!img || !sub) throw new Error('无法获取 WBI 密钥');

  wbiMixin = [...(img + sub)]
    .filter((_, i) => true)
    .map((_, i) => (img + sub)[MIXIN_TAB[i]])
    .join('')
    .slice(0, 32);
  wbiExpire = Date.now() + 6 * 3600 * 1000;
  return wbiMixin;
}

async function wbiSign(pathname, params) {
  const mixin = await getMixinKey();
  const p = { ...params, wts: Math.floor(Date.now() / 1000) };
  const query = Object.keys(p).sort()
    .map(k => `${encodeURIComponent(k)}=${encodeURIComponent(p[k])}`)
    .join('&');
  const w_rid = crypto.createHash('md5').update(query + mixin).digest('hex');
  return `${pathname}?${query}&w_rid=${w_rid}`;
}

async function getRoomConf(roomInput) {
  // 1. 解析真实房间号
  const init = await apiGet(`https://api.live.bilibili.com/room/v1/Room/room_init?id=${roomInput}`);
  if (init.code !== 0) throw new Error(`room_init 失败: ${init.code} ${init.message}`);
  const realId = init.data.room_id;
  const liveStatus = init.data.live_status;

  // 2. WBI 签名后取弹幕服务器
  const signed = await wbiSign(
    'https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo',
    { id: realId, type: 0 }
  );
  const info = await apiGet(signed);
  if (info.code !== 0) throw new Error(`getDanmuInfo 失败: ${info.code}`);

  const hosts = [...new Set((info.data.host_list || []).map(h => h.host))];
  return {
    realId,
    liveStatus,
    token: info.data.token,
    hosts: hosts.length ? hosts : ['broadcastlv.chat.bilibili.com'],
  };
}

// ─────────────────────── 弹幕协议 ───────────────────────
function encodePacket(op, body) {
  const b = Buffer.from(JSON.stringify(body), 'utf8');
  const h = Buffer.alloc(16);
  h.writeUInt32BE(16 + b.length, 0);
  h.writeUInt16BE(16, 4);
  h.writeUInt16BE(1, 6);
  h.writeUInt32BE(op, 8);
  h.writeUInt32BE(1, 12);
  return Buffer.concat([h, b]);
}

function parsePackets(buf) {
  const out = [];
  let off = 0;
  while (off + 16 <= buf.length) {
    const total = buf.readUInt32BE(off);
    if (total < 16 || off + total > buf.length) break;
    const hlen = buf.readUInt16BE(off + 4);
    const ver = buf.readUInt16BE(off + 6);
    const op = buf.readUInt32BE(off + 8);
    out.push({ ver, op, body: buf.subarray(off + hlen, off + total) });
    off += total;
  }
  return out;
}

// ─────────────────────── 转发到游戏（聊天栏） ───────────────────────
const queue = [];
let sentThisMinute = 0;
let minuteStart = Date.now();

function sanitize(text) {
  return String(text)
    .replace(/[\r\n\t]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

function shouldForward(text) {
  if (text.length < CFG.minLen) return false;
  for (const w of CFG.blockWords) if (w && text.includes(w)) return false;
  return true;
}

async function sendToGame(text) {
  const msg = `${CFG.prefix} ${text}`.slice(0, CFG.maxLen + CFG.prefix.length + 1);
  const url = `${CFG.restBase}/v2/server/broadcast?token=${encodeURIComponent(CFG.restToken)}&msg=${encodeURIComponent(msg)}`;
  try {
    const r = await fetch(url, { method: 'GET' });
    const j = await r.json();
    if (j.status === '200') { log(`已转发到游戏: ${msg}`); return true; }
    log(`转发失败: ${JSON.stringify(j).slice(0, 150)}`);
    return false;
  } catch (e) {
    log(`转发异常: ${e.message}`);
    return false;
  }
}

function pump() {
  // 每分钟窗口重置
  if (Date.now() - minuteStart >= 60000) {
    minuteStart = Date.now();
    sentThisMinute = 0;
  }
  if (queue.length === 0) return;
  if (sentThisMinute >= CFG.maxPerMin) {
    queue.length = 0; // 超过速率限制，丢弃积压
    return;
  }
  const text = queue.shift();
  sentThisMinute++;
  sendToGame(text);
}

// ─────────────────────── 刷怪指令 ───────────────────────
// 插件是否已加载：靠 /dspawn status 的返回判断，没加载就不往游戏里刷失败信息
let pluginReady = false;
let lastProbeLog = 0;

const cmdStats = {
  windowStart: Date.now(),
  count: 0,
  lastByUser: new Map(),
  ok: 0,
  fail: 0,
};

/** 清理单个参数：去掉能掺进 TShock 命令行/URL 的字符，限制长度 */
function cleanToken(raw, maxLen = 24) {
  return String(raw)
    .replace(/[\\/\u0000-\u001f;&=?#'"`<>|]/g, '')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, maxLen);
}

/** 直接往游戏聊天栏丢一句话（不走普通弹幕队列） */
async function broadcastToGame(text) {
  const msg = String(text).slice(0, 200);
  const url = `${CFG.restBase}/v2/server/broadcast?token=${encodeURIComponent(CFG.restToken)}&msg=${encodeURIComponent(msg)}`;
  try {
    await fetch(url, { method: 'GET' });
  } catch (e) {
    log(`广播失败: ${e.message}`);
  }
}

/** 调 TShock 的 /v3/server/rawcmd，返回命令输出行 */
async function runRawCmd(cmdStr) {
  const url = `${CFG.restBase}/v3/server/rawcmd?token=${encodeURIComponent(CFG.restToken)}&cmd=${encodeURIComponent(cmdStr)}`;
  const r = await fetch(url, { method: 'GET' });
  const j = await r.json();
  return j;
}

async function probePlugin() {
  try {
    const j = await runRawCmd('/dspawn status');
    const text = Array.isArray(j.response) ? j.response.join(' ') : '';
    const ok = text.includes('[刷怪]');
    if (ok && !pluginReady) log('插件已就绪：/dspawn 可用，刷怪指令开启');
    if (!ok && pluginReady) log('插件不再响应：刷怪指令暂停');
    pluginReady = ok;
  } catch (e) {
    pluginReady = false;
  }
  return pluginReady;
}

function cmdAllowed(user, guard, medal) {
  if (CMD.block.length && CMD.block.includes(user)) return { ok: false, why: '黑名单' };
  if (CMD.allow.length && !CMD.allow.includes(user)) return { ok: false, why: '不在白名单' };
  if (CMD.minGuard > 0 && guard < CMD.minGuard) return { ok: false, why: `需要舰长等级≥${CMD.minGuard}` };
  if (CMD.minMedal > 0 && medal < CMD.minMedal) return { ok: false, why: `需要粉丝牌≥${CMD.minMedal}级` };
  return { ok: true };
}

/**
 * 解析一条弹幕是不是刷怪指令。
 * 返回 true 表示"这条弹幕我处理了"，调用方不要再当普通弹幕转发。
 *
 * 语法：/刷怪                       ← 最常用：随机一只安全怪 + 随机一个在线玩家
 *       !刷怪 <怪物> [数量] [玩家]
 *       !刷怪 僵尸          → 1 只僵尸，刷在插件默认目标附近
 *       !刷怪 僵尸 3        → 3 只，刷在默认目标附近
 *       !刷怪 僵尸 3 饺子喵喵 → 3 只，刷在"饺子喵喵"附近
 */
async function tryCommand(text, user, meta) {
  if (!CMD.enable) return false;

  let body = null;
  if (text.startsWith(CMD.prefix)) body = text.slice(CMD.prefix.length);
  else if (text.startsWith('！')) body = text.slice(1);          // 全角感叹号
  else if (text.startsWith('#')) body = text.slice(1);
  else if (text.startsWith('/')) body = text.slice(1);          // 观众习惯打 /刷怪
  if (body === null) return false;

  body = body.trim();
  const parts = body.split(/\s+/).filter(Boolean);
  if (parts.length === 0) return false;

  const word = parts.shift().toLowerCase();
  if (!CMD.words.includes(word)) return false;

  if (!pluginReady) {
    if (Date.now() - lastProbeLog > 300000) {
      lastProbeLog = Date.now();
      log(`收到指令但插件未加载，已跳过（重启 TShock 加载 DanmakuSpawn 后自动生效）: ${user}「${text}」`);
    }
    return true;
  }

  // 权限
  const chk = cmdAllowed(user, meta.guard || 0, meta.medal || 0);
  if (!chk.ok) {
    log(`指令拒绝(${chk.why}): ${user}「${text}」`);
    return true;
  }

  // 全局限速
  const now = Date.now();
  if (now - cmdStats.windowStart >= 60000) {
    cmdStats.windowStart = now;
    cmdStats.count = 0;
  }
  if (cmdStats.count >= CMD.maxPerMin) {
    log(`指令拒绝(全服限速 ${CMD.maxPerMin}/分): ${user}「${text}」`);
    return true;
  }

  // 单观众冷却
  const last = cmdStats.lastByUser.get(user) || 0;
  if (now - last < CMD.userCooldown * 1000) {
    const left = Math.ceil((CMD.userCooldown * 1000 - (now - last)) / 1000);
    log(`指令拒绝(冷却 ${left}s): ${user}「${text}」`);
    return true;
  }

  // 参数：不在桥接这层猜"怪物名有没有空格"，原样交给插件解析
  // 插件认：/dspawn <怪物> [数量] [玩家] [@观众]
  const tokens = parts
    .slice(0, 4)
    .map(t => cleanToken(t, 20))
    .filter(Boolean)
    .map(t => /^\d{1,4}$/.test(t) ? String(Math.min(Math.max(parseInt(t, 10), 1), CMD.maxAmount)) : t);

  // 不发参数 = 让插件随机挑一只安全怪、随机挑一个在线玩家
  const viewer = (cleanToken(user, 16) || '观众').replace(/^@+/, '');
  const cmdStr = tokens.length === 0 ? `/dspawn @${viewer}` : `/dspawn ${tokens.join(' ')} @${viewer}`;

  cmdStats.count++;
  cmdStats.lastByUser.set(user, now);

  // 顺手把指令记到游戏聊天栏，让服务器上的人知道是谁点的
  log(`指令: ${user} → ${cmdStr}`);

  try {
    const j = await runRawCmd(cmdStr);
    const lines = Array.isArray(j.response) ? j.response : [JSON.stringify(j)];
    const okLine = lines.find(l => l && l.includes('已排队'));
    if (okLine) {
      cmdStats.ok++;
      log(`指令受理: ${okLine}`);
    } else {
      cmdStats.fail++;
      log(`指令被拒: ${lines.join(' | ').slice(0, 240)}`);
      // 把失败原因转给游戏里的人（插件自己不会广播排队阶段的失败）
      const reason = lines[0] ? lines[0].replace(/^\[刷怪\]\s*/, '') : '未知错误';
      broadcastToGame(`${CFG.prefix} ${viewer}: ${reason}`);
    }
  } catch (e) {
    cmdStats.fail++;
    log(`指令异常: ${e.message}`);
  }

  return true;
}

/** 从 DANMU_MSG 里抽舰长等级 / 粉丝牌等级 */
function extractMeta(j) {
  let guard = 0;
  let medal = 0;
  try {
    // info[7] = 舰长等级：0 无, 1 总督, 2 提督, 3 舰长
    if (Array.isArray(j.info) && typeof j.info[7] === 'number') guard = j.info[7];
  } catch { }
  try {
    // info[3] = 粉丝牌：[等级, 牌子名, up主名, ...]
    const m = j.info && j.info[3];
    if (Array.isArray(m) && m.length > 0) {
      const lv = parseInt(m[0], 10);
      if (!isNaN(lv)) medal = lv;
    }
  } catch { }
  return { guard, medal };
}

// ─────────────────────── 主循环 ───────────────────────
let ws = null;
let heartbeatTimer = null;
let reconnectDelay = 3000;
let danmakuCount = 0;
let hostIndex = 0;

async function connect() {
  let conf;
  try {
    conf = await getRoomConf(CFG.roomId);
  } catch (e) {
    log(`获取房间配置失败: ${e.message}，${reconnectDelay / 1000}s 后重试`);
    setTimeout(connect, reconnectDelay);
    reconnectDelay = Math.min(reconnectDelay * 2, 60000);
    return;
  }

  if (Number(conf.liveStatus) !== 1) {
    log(`房间 ${conf.realId} 当前未开播（live_status=${conf.liveStatus}），60 秒后再检查`);
    setTimeout(connect, 60000);
    return;
  }

  const host = conf.hosts[hostIndex % conf.hosts.length];
  log(`房间 ${CFG.roomId} → ${conf.realId}，直播=${conf.liveStatus}，节点[${hostIndex % conf.hosts.length}] ${host}`);

  let lastPacketAt = Date.now();
  // 无数据看门狗：20 秒收不到任何包就换节点（某些节点对某些房间不推数据）
  const noDataTimer = setInterval(() => {
    if (Date.now() - lastPacketAt > 60000) {
      log(`节点 ${host} 60 秒无数据，切换到下一个节点`);
      hostIndex++;
      clearInterval(noDataTimer);
      if (ws) { try { ws.close(); } catch { } }
      setTimeout(connect, 1000);
    }
  }, 5000);

  ws = new WebSocket(`wss://${host}:2245/sub`);
  if ('binaryType' in ws) ws.binaryType = 'arraybuffer';

  ws.onopen = () => {
    log('WebSocket 已连接，发送认证包');
    ws.send(encodePacket(7, {
      uid: loginUid,
      roomid: conf.realId,
      protover: 3,
      platform: 'web',
      type: 2,
      key: conf.token,
    }));
    heartbeatTimer = setInterval(() => {
      if (ws && ws.readyState === 1) ws.send(encodePacket(2, {}));
    }, 20000);
  };

  ws.onmessage = (ev) => {
    lastPacketAt = Date.now();
    let buf;
    if (Buffer.isBuffer(ev.data)) buf = ev.data;
    else if (ev.data instanceof ArrayBuffer) buf = Buffer.from(ev.data);
    else return;

    for (const p of parsePackets(buf)) {
      if (p.ver === 3) {
        try {
          for (const q of parsePackets(zlib.brotliDecompressSync(p.body))) handle(q);
        } catch { }
      } else {
        handle(p);
      }
    }
  };

  ws.onerror = () => log('WebSocket 错误');
  ws.onclose = () => {
    log(`连接关闭，${reconnectDelay / 1000}s 后重连`);
    if (heartbeatTimer) clearInterval(heartbeatTimer);
    clearInterval(noDataTimer);
    setTimeout(connect, reconnectDelay);
    reconnectDelay = Math.min(reconnectDelay * 2, 60000);
  };

  function handle(p) {
    if (p.op === 8) {
      log('✅ 认证成功，开始接收弹幕');
      reconnectDelay = 3000;
      return;
    }
    if (p.op !== 5) return;
    let j;
    try { j = JSON.parse(p.body.toString('utf8')); } catch { return; }

    if (!j.cmd || !j.cmd.startsWith('DANMU_MSG')) return;

    const text = sanitize(j.info && j.info[1]);
    const user = sanitize((j.info && j.info[2] && j.info[2][1]) || '');
    if (!text) return;

    danmakuCount++;
    log(`收到弹幕 [${user}]: ${text}`);
    if (!shouldForward(text)) return;

    // 先看是不是刷怪指令；是就吃掉，不再当普通弹幕刷屏
    if (CMD.enable && /^[!#/！]/.test(text)) {
      tryCommand(text, user, extractMeta(j)).catch(e => log(`指令处理异常: ${e.message}`));
      return;
    }

    if (queue.length > 50) return; // 防止积压

    // 格式：[弹幕] 用户名: 内容
    queue.push(`${user}: ${text}`);
    pump();
  }
}

// ─────────────────────── 启动 ───────────────────────
if (!CFG.roomId) {
  console.error('错误：请设置 ROOM_ID，例如  ROOM_ID=12345 node danmaku-bridge.js');
  process.exit(1);
}
if (!CFG.restToken) {
  console.error('错误：找不到 TShock REST token（或设置 REST_TOKEN 环境变量）');
  process.exit(1);
}

log(`启动弹幕桥接 | 房间=${CFG.roomId} | 每分钟上限=${CFG.maxPerMin} | 前缀=${CFG.prefix} | 登录态=${CFG.sessdata ? '已带 SESSDATA' : '匿名'}`);
log(`刷怪指令 | ${CMD.enable ? '已开启' : '已关闭'} | 前缀=${CMD.prefix} | 指令词=${CMD.words.join('/')} | 每条上限=${CMD.maxAmount}只 | 全服${CMD.maxPerMin}/分 | 冷却${CMD.userCooldown}s | 舰长门槛=${CMD.minGuard} 粉丝牌门槛=${CMD.minMedal}`);

// 自检模式：不连 B站，直接拿一条"假弹幕"跑一遍指令链路，用来验证 REST/插件是否通
// 用法：SELFTEST_CMD='!刷怪 僵尸 2 @spawn' node danmaku-bridge.js
if (process.env.SELFTEST_CMD) {
  (async () => {
    const text = process.env.SELFTEST_CMD;
    const user = process.env.SELFTEST_USER || '测试观众';
    const guard = parseInt(process.env.SELFTEST_GUARD || '0', 10);
    const medal = parseInt(process.env.SELFTEST_MEDAL || '0', 10);
    await probePlugin();
    log(`自检开始: user=${user} guard=${guard} medal=${medal} 插件就绪=${pluginReady} text=${text}`);
    const handled = await tryCommand(text, user, { guard, medal });
    log(`自检结束: handled=${handled}`);
    setTimeout(() => process.exit(0), 800);
  })();
} else {
  probePlugin();
  setInterval(() => { if (!pluginReady) probePlugin(); }, 45000);   // 插件后装的：45 秒内自动接管
  setInterval(pump, 5000); // 每 5 秒发一条，天然限速
  setInterval(() => {
    if (danmakuCount > 0) log(`统计：已接收 ${danmakuCount} 条弹幕，队列 ${queue.length}，节点[${hostIndex}]，指令 成功${cmdStats.ok}/失败${cmdStats.fail}`);
  }, 60000);
  connect();
}

process.on('SIGINT', () => {
  log('收到停止信号，退出');
  if (heartbeatTimer) clearInterval(heartbeatTimer);
  if (ws) ws.close();
  process.exit(0);
});
