# 弹幕连接器 · B站直播弹幕 → 泰拉瑞亚服务器

把 B 站直播间的弹幕搬进泰拉瑞亚服务器：

- **弹幕 → 游戏聊天栏**：观众说什么，服务器里就显示什么
- **弹幕 → 刷怪**：观众打一句 `!刷怪 僵尸 3 饺子喵喵`，服务器就在指定玩家身边生成 3 只僵尸

**玩家端零安装** —— 不装模组、不用改客户端，原版 Terraria 就能看到怪、看到弹幕。
全部在服务端完成：WebSocket 收弹幕 → TShock REST API → 服务端插件生成 NPC。

```
B站直播间 (WebSocket + WBI 签名)
      │
      ├── 普通弹幕 ──────────────► POST /v2/server/broadcast ──► 游戏聊天栏
      │     PREFIX=[弹幕]                 "【弹幕】昵称: 内容"
      │
      └── 刷怪指令 ──────────────► GET /v3/server/rawcmd?cmd=/dspawn …
            "!刷怪 僵尸 3 @观众"           │
                                           ▼
                              服务端插件 DanmakuSpawn
                                    ├─ 解析：怪物 / 数量 / 目标
                                    ├─ 限速、黑名单、权限门槛
                                    ├─ 在目标周围 ±14 × ±6 格找落脚点
                                    └─ NPC.NewNPC + NetMessage.SendData(23)
                                           ▼
                                    游戏内可见的怪物
```

---

## 目录

- [快速开始](#快速开始)
- [第一步：配置直播间（ROOM_ID）](#第一步配置直播间room_id)
- [第二步：配置 B 站 Cookie（可选但推荐）](#第二步配置-b-站-cookie可选但推荐)
- [第三步：决定要不要开启刷怪](#第三步决定要不要开启刷怪)
- [全部配置项](#全部配置项)
- [观众怎么用](#观众怎么用)
- [插件配置（DanmakuSpawn.json）](#插件配置danmaku-spawnjson)
- [权限与分组](#权限与分组)
- [运维](#运维)
- [常见问题](#常见问题)
- [安全须知](#安全须知)
- [License](#license)

---

## 依赖

| 组件 | 要求 | 说明 |
|---|---|---|
| Node.js | ≥ 20 | 只用内置模块，**零 npm 依赖**，不用 `npm install` |
| TShock | 6.x（Terraria 1.4.4.9 / 1.4.5.8 实测） | 需要开启 REST API |
| .NET SDK | 9.0（仅编译插件时需要） | 用现成 DLL 的话不需要 |
| Docker | 可选 | 官方镜像 `ghcr.io/pryaxis/tshock` |

弹幕 → 聊天栏**只需要 TShock REST**；弹幕 → 刷怪额外需要服务端插件 `DanmakuSpawn`。

> **插件没装也能跑**：桥接启动时用 `/dspawn status` 探测插件，没装就静默跳过刷怪指令（不会往游戏里刷错误提示），普通弹幕照常转发。装好插件、重启服务器后，桥接 45 秒内自动接管，不用改配置。

---

## 快速开始

```bash
git clone <你的仓库地址> danmaku-bridge
cd danmaku-bridge

# 1. 复制配置模板
cp config.env.example config.env

# 2. 改三行：直播间号、REST 地址、REST token
$EDITOR config.env

# 3. 启动
./run.sh start
./run.sh status
tail -f bridge.log      # 看弹幕有没有进来
```

REST token 在 TShock 的 `config.json` → `ApplicationRestTokens` 里，形如：

```json
"ApplicationRestTokens": {
  "你的token": { "Username": "qqq1942", "UserGroupName": "owner" }
}
```

装插件（要刷怪才需要）：

```bash
cd plugin
# 先按 plugin/refs/README.md 从 TShock 镜像里拷出程序集
./build.sh --deploy        # 编译 + 复制到 ../plugins/
cd .. && docker compose restart tshock
```

---

## 第一步：配置直播间（ROOM_ID）

打开直播间，**地址栏 `live.bilibili.com/` 后面那串数字就是房间号**：

```
https://live.bilibili.com/23343237
                         ^^^^^^^ 这就是 ROOM_ID
```

```ini
# config.env
ROOM_ID=23343237
```

**短号 / 长号都支持**。B 站主播在后台看到的是短号（如 `23343237`），而接口里用长号（如 `5440`），桥接两种都能处理——填你地址栏看到的那个就行。

> 房间号和 cookie 所属账号可以不匹配：任何账号的 cookie 都可以去听任何公开直播间的弹幕。

改完 `./run.sh restart` 生效。

---

## 第二步：配置 B 站 Cookie（可选但推荐）

### 为什么需要

不填 cookie 时是**匿名连接**，B 站会给匿名连接做过滤：

- 收不到**你自己账号**发的弹幕（测试时会以为坏了）
- 部分房间的弹幕会被抽掉一部分
- 高人气房间可能直接不下发

填了 `SESSDATA` 之后，连接带登录态，弹幕完整性明显更好。只需要 **`SESSDATA` 一个字段**，不需要 `bili_jct`、`DedeUserID` 之类。

### 怎么拿

1. 浏览器登录 B 站（**建议用小号，别用主号**）
2. `F12` 打开开发者工具
3. `Application`（应用）→ 左侧 `Cookies` → `https://www.bilibili.com`
4. 找到名为 `SESSDATA` 的那一行，复制 **Value** 整段（形如 `7f3a9c21%2C1900000000%2C…`，很长）

或者：`Network` 面板随便点一个请求 → `Request Headers` → `Cookie:` 里截取 `SESSDATA=...` 到分号为止。

### 怎么填

```ini
# config.env
SESSDATA="7f3a9c21%2C1900000000%2Cab12cd34EFgh…"
```

- **必须加引号**：值里有 `%2C` 这类字符，shell 会被拆开
- **原样粘贴**，不要做 URL 解码（`%2C` 就留着）
- 值会过期（一般几个月到半年），过期后日志会看到 WebSocket 立刻断开、收不到弹幕，重新复制一份换上即可

### 安全提醒（重要）

**`SESSDATA` 等同于你的账号登录态，拿到它就能操作你的账号。** 所以：

- 用小号，不要用主号
- **绝对不要提交到 git**（本仓库 `.gitignore` 已排除 `config.env`）
- 一旦泄露（贴到群、发到截图、传到公开仓库），立刻去 B 站「账号安全 → 退出所有设备」并改密码
- 本程序只在内存里用它建 WebSocket 连接，不会写日志、不会外传

---

## 第三步：决定要不要开启刷怪

刷怪是**默认开启**的，但你可以完全关掉，只当弹幕转发用。

### 开关

```ini
CMD_ENABLE=1     # 1 = 允许观众发指令刷怪（默认）
CMD_ENABLE=0     # 0 = 只转发弹幕，任何指令都不执行
```

| | `CMD_ENABLE=1` | `CMD_ENABLE=0` |
|---|---|---|
| 普通弹幕转发 | ✅ | ✅ |
| 观众刷怪 | ✅ | ❌（`!刷怪` 被当作普通弹幕转发） |
| 需要装插件 | 要 | 不要 |
| 适合场景 | 直播互动、节目效果 | 只想把弹幕同步进游戏 |

### 建议按这个顺序开

**1）先只开弹幕转发，观察一天**

```ini
CMD_ENABLE=0
```

确认聊天栏正常、没有刷屏、cookie 没过期。

**2）再开刷怪，但上限制**

```ini
CMD_ENABLE=1
CMD_MAX_PER_MIN=6       # 全服每分钟最多 6 条指令
CMD_USER_COOLDOWN=10    # 同一观众 10 秒一次
CMD_MAX_AMOUNT=5        # 单次最多 5 只
```

**3）观众多了再上门槛**

```ini
CMD_MIN_GUARD=3         # 只给舰长及以上（0 不限 / 3 舰长 / 2 提督 / 1 总督）
CMD_MIN_MEDAL=0         # 或按粉丝牌等级：0 不限 / 20 是 20 级牌子
```

或者用白名单：

```ini
CMD_ALLOW=老观众A,老观众B   # 填了就"只有"这些人能用
CMD_BLOCK=捣乱的昵称          # 黑名单，这些人不能用
```

### 防刷爆的双层限制

桥接和插件各有一层，任何一层超限都会拒绝：

| 层 | 限制项 | 默认 |
|---|---|---|
| 桥接 | 全服每分钟指令数 | 6 |
| 桥接 | 同一观众冷却 | 10 秒 |
| 桥接 | 单次数量上限 | 5 只 |
| 插件 | 全服每分钟刷怪次数 | 20 |
| 插件 | **同一目标**每分钟被刷次数 | 6 |
| 插件 | 单次数量上限 | 10 只 |
| 插件 | BOSS / 高危怪黑名单 | 69 个（含全部 BOSS） |

BOSS 黑名单是插件的硬限制，观众写什么都不可能刷出克苏鲁之眼、机械三王、月总这类怪——它们会把服务器搞卡、把玩家秒掉。想放开某只怪，只能改插件配置，见[插件配置](#插件配置danmaku-spawnjson)。

---

## 全部配置项

`config.env`

### 连接与转发

| 变量 | 默认 | 说明 |
|---|---|---|
| `ROOM_ID` | **必填** | B 站直播间号 |
| `SESSDATA` | 空 | 登录 cookie，见[第二步](#第二步配置-b-站-cookie可选但推荐) |
| `REST_BASE` | `http://127.0.0.1:7878` | TShock REST 地址 |
| `REST_TOKEN` | 读 `../.rest_token` | REST token，也可直接写这里 |
| `PREFIX` | `[弹幕]` | 游戏内显示前缀 |
| `MAX_PER_MIN` | `12` | 每分钟最多转发几条。**`0` = 不限量**，来多少转多少 |
| `SEND_INTERVAL_MS` | `150` | 仅不限量模式生效：清积压时两条之间的最小间隔（毫秒）。150 ≈ 上限 400 条/分 |
| `MIN_LEN` | `1` | 短于此长度不转发 |
| `MAX_LEN` | `50` | 超长截断 |
| `BLOCK_WORDS` | 空 | 屏蔽词，逗号分隔，命中直接丢弃 |

> 限量模式（`MAX_PER_MIN > 0`）下超出部分会丢弃积压；不限量模式永不丢弃，队列上限 500 条。

### 刷怪指令

| 变量 | 默认 | 说明 |
|---|---|---|
| `CMD_ENABLE` | `1` | 是否开启刷怪指令 |
| `CMD_PREFIX` | `!` | 指令前缀。`！`、`#`、`/` 也会被识别 |
| `CMD_WORDS` | `刷怪,spawn,召唤,出怪` | 指令词，逗号分隔，大小写不敏感 |
| `CMD_MAX_AMOUNT` | `5` | 桥接侧单次数量上限 |
| `CMD_DEFAULT_AMOUNT` | `1` | 不写数量时刷几只 |
| `CMD_MAX_PER_MIN` | `6` | 全服每分钟最多受理几条指令 |
| `CMD_USER_COOLDOWN` | `30` | 同一观众两次指令的冷却（秒） |
| `CMD_MIN_GUARD` | `0` | 最低舰长等级：`0` 不限 / `3` 舰长 / `2` 提督 / `1` 总督 |
| `CMD_MIN_MEDAL` | `0` | 最低粉丝牌等级 |
| `CMD_ALLOW` | 空 | 白名单，填了就**只有**这些人能用 |
| `CMD_BLOCK` | 空 | 黑名单 |
| `CMD_DEFAULT_PLAYER` | 空 | 没指定玩家时固定刷谁附近；空 = 随机在线玩家 |

---

## 观众怎么用

前缀 `!`、`！`、`#`、`/` 都认，指令词 `刷怪 / spawn / 召唤 / 出怪`。

| 弹幕 | 效果 |
|---|---|
| `!刷怪` | 1 只随机怪，刷在随机在线玩家身边 |
| `!刷怪 3` | **纯数字 = 数量**：3 只随机怪 |
| `!刷怪 3 饺子喵喵` | 3 只随机怪，刷在指定玩家身边 |
| `!刷怪 僵尸 3` | 3 只僵尸 |
| `!刷怪 僵尸 3 饺子喵喵` | 3 只僵尸刷在指定玩家身边 |
| `!刷怪 饺子喵喵` | 只报玩家名：在她身边刷随机怪 |

游戏内会广播：`【弹幕】某观众 召唤了 3 只 Zombie 在 饺子喵喵 附近！`

**数字解析规则**：`1`–`10` 之间的纯数字当作**数量**，超过 10 才当作 NPC 数字 ID。所以 `!刷怪 3` 是 3 只怪，不是「怪物 #3」。

怪物名支持**中文别名**（`僵尸/史莱姆/骷髅/宁芙/地狱蝙蝠/独角兽…` 80 多条）和**英文内部名**（`Zombie`、`Blue Slime`、`Face Monster`），大小写不敏感。

---

## 插件配置（`DanmakuSpawn.json`）

插件第一次启动会自动生成配置文件。默认位置：TShock 配置目录下的 `DanmakuSpawn.json`
（Docker 部署时通常是宿主机的 `./tshock/DanmakuSpawn.json`，容器内 `/tshock/DanmakuSpawn.json`）。

**改完不用重启服务器**，游戏内执行 `/dspawn reload` 即可。

### 开关与广播

| 字段 | 默认 | 说明 |
|---|---|---|
| `Enabled` | `true` | 总开关 |
| `BroadcastSpawn` | `true` | 刷怪时全服广播 |
| `DefaultTarget` | `"random"` | 没写目标时刷哪：`random` / `spawn` / 具体玩家名 |

### 数量与范围

| 字段 | 默认 | 说明 |
|---|---|---|
| `MaxAmount` | `10` | 单次最多几只 |
| `RandomAmount` | `1` | 纯随机模式默认几只 |
| `RangeX` | `14` | 横向搜索半径（格） |
| `RangeY` | `6` | 纵向搜索半径（格） |

落脚点算法：在 ±RangeX / ±RangeY 内随机试 40 次，要求「脚下是实心块、身位两格是空地」；失败再退回 TShock 的空地搜索。所以怪一定站得住，不会卡墙里。

### 限速

| 字段 | 默认 | 说明 |
|---|---|---|
| `MaxPerMinute` | `20` | 全服每分钟刷怪次数 |
| `MaxPerTargetPerMinute` | `6` | 同一目标每分钟被刷次数 |

### 黑名单

| 字段 | 默认 | 说明 |
|---|---|---|
| `BlockedNpcs` | 12 个 | 逐条黑名单，**默认含全部 BOSS** |
| `BlockByPrefix` | `true` | 按前缀批量拉黑 |
| `BlockTownNpcs` | `true` | 拉黑城镇 NPC（向导/商人/护士…） |
| `BlockFriendlyNpcs` | `false` | 拉黑所有友方 NPC（宠物、小动物） |

默认已拉黑：`Clown`、`Prime Cannon`、`Dungeon Guardian`、`Wall of Flesh`、`Eater of Worlds`、
`Brain of Cthulhu`、`Skeletron`、`The Destroyer`、`Plantera`、`Golem`、`Moon Lord`、`Lunatic Cultist`。

### 随机池与中文别名

```json
"RandomMobs": ["Blue Slime", "Zombie", "Demon Eye", "Skeleton", "..."],
"Aliases":    { "僵尸": "Zombie", "史莱姆": "Blue Slime", "骷髅": "Skeleton" }
```

- `RandomMobs`：`!刷怪` 不带怪名时从这个池子抽，默认 60 多种普通怪（不会拆方块、不会穿墙秒人）
- `Aliases`：观众会打的中文词 → 内部英文名，默认 80 多条

**加一只怪**：往 `RandomMobs` 里加英文名，例如 `"Angry Bones"`，`/dspawn reload` 生效。

---

## 权限与分组

插件注册的权限节点是 **`danmaku.spawn`**。

| 谁 | 需要权限吗 |
|---|---|
| 直播间观众 | ❌ 不需要 —— 桥接走 REST 的 console 身份，绕过权限检查 |
| 游戏内玩家手动 `/dspawn` | ✅ 需要 |

授权给某个组：

```bash
# 游戏内聊天框或控制台
/group addperm default danmaku.spawn     # 让普通玩家也能自己刷
/group delperm default danmaku.spawn     # 收回
```

TShock 默认分组继承链：`guest → default → vip → newadmin → admin → trustedadmin → owner`，
`superadmin` 是虚拟组（拥有 `*`）。

### 游戏内管理员命令

| 命令 | 效果 |
|---|---|
| `/dspawn` | 随机 1 只安全怪 + 随机在线玩家 |
| `/dspawn 3` | 3 只随机怪 |
| `/dspawn 僵尸 3 饺子喵喵` | 3 只僵尸刷在该玩家身边 |
| `/dspawn @spawn 僵尸` | 刷在世界出生点 |
| `/dspawn 自检` | 在自己脚下刷一只并回报 NPC 数量（排障用） |
| `/dspawn status` | 开关 / 队列 / 已刷 / 被拒 / 限速 / 诊断 |
| `/dspawn pool [页]` | 查看随机池 |
| `/dspawn list [页]` | 查看中文别名表 |
| `/dspawn reload` | 重载配置文件 |

---

## 运维

```bash
./run.sh start | stop | restart | status
tail -f bridge.log
```

日志里能直接看到每条指令的去向：

```
[14:35:26] 指令: qqq1942 → /dspawn 3 @qqq1942
[14:35:26] 指令受理: [刷怪] 已排队：3 只「Pixie」→ qqq1942
[14:35:31] 指令拒绝(冷却 12s): 某观众「！刷怪」
[14:35:49] 收到弹幕 [某观众]: 给点给点
[14:35:49] 已转发到游戏: [弹幕] 某观众: 给点给点
```

建议加 cron 保活：

```cron
@reboot       /path/to/danmaku-bridge/run.sh start
*/5  * * * *  /path/to/danmaku-bridge/run.sh start   # 已在跑则静默退出
```

---

## 常见问题

### 收不到任何弹幕

1. `ROOM_ID` 填错（填成 UP 主的 UID 了）—— 应该是 `live.bilibili.com/` 后面那串
2. 直播间没开播 —— 桥接会暂停，开播后自动恢复
3. `SESSDATA` 过期 —— 日志里 WebSocket 立刻断开，换一份 cookie

### 能看到别人的弹幕，看不到自己的

正常现象：匿名连接会被 B 站过滤掉自己账号的弹幕。填 `SESSDATA` 即可。

### 指令发了没反应

```bash
grep -E "指令|拒绝" bridge.log | tail -20
```

| 日志 | 含义 | 处理 |
|---|---|---|
| `指令拒绝(冷却 Ns)` | 同一观众太快 | 等，或调小 `CMD_USER_COOLDOWN` |
| `指令拒绝(全服限速)` | 全服打满 | 调大 `CMD_MAX_PER_MIN` |
| `指令拒绝(粉丝牌/舰长门槛)` | 权限不够 | 调 `CMD_MIN_GUARD` / `CMD_MIN_MEDAL` |
| `指令受理` 但游戏里没怪 | 插件没装/没加载 | `/dspawn status` 看 `队列` 和 `tick` |
| 完全没有「指令」字样 | 前缀或指令词不对 | 检查 `CMD_PREFIX` / `CMD_WORDS` |

### 刷怪成功但玩家看不见

**已修复的经典坑**：服务端 `NPC.NewNPC()` 只在服务器内存里创建 NPC，**必须补一句同步包**客户端才知道怪存在：

```csharp
int idx = NPC.NewNPC(new EntitySource_SpawnNPC(), x, y, type, ...);
NetMessage.SendData(23, -1, -1, null, idx);   // 23 = NPCUpdate，漏了这行游戏里就没有怪
```

本插件已包含这一步。日志会打印实际坐标，便于确认落点：

```
[DanmakuSpawn] 弹幕 -> 3x Pixie near qqq1942 | 基准格 3191,380 | 实际刷在 [3184,382 3182,382 3198,376]
```

### `/dspawn status` 里 `tick=0`

刚启动瞬间是 0，属正常。如果**持续**是 0，说明主循环钩子没挂上，插件会改用兜底线程执行队列（能刷出来，但不在主线程）。
`worldRunning=False` 是 TerrariaApi 自身的返回值问题，插件不依赖它，无害。

### 服务器崩了 / 世界回档

Terraria 的世界只在内存里，**不存盘就停服 = 回档**。停服前一定要：

```bash
# 1. REST 存档
curl -X POST "http://127.0.0.1:7878/v2/world/save?token=$TOKEN"
# 2. 确认存档文件真的变了（mtime 前进）
stat -c '%y %s' worlds/Terraria.wld
# 3. 再停
docker compose stop
```

回档后用 TShock 的自动备份（`<tshock>/backups/*.bak`，每 10 分钟一个）覆盖回去即可，最多丢 10 分钟。

---

## 安全须知

- `SESSDATA` 等于账号登录态：用小号、别提交、泄露了立刻「退出所有设备」+ 改密码
- 本仓库 `.gitignore` 已排除 `config.env`、`.rest_token` 等含密钥文件
- REST token 只在本机使用，**不要把 7878 端口暴露到公网**
- 刷怪功能建议配合限速与门槛，避免被刷爆（见[第三步](#第三步决定要不要开启刷怪)）
- 本程序不含任何收集/上报逻辑，所有数据都在你自己机器上

---

## License

MIT
