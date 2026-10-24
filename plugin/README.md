# DanmakuSpawn —— 直播间弹幕刷怪插件（TShock 6.1 / Terraria 1.4.5.8）

给 `danmaku-bridge.js` 配一个服务端插件：**直播间观众发一条 `/刷怪`，服务器就随机挑一只怪刷到随机一个在线玩家身边**；
想指定怪物/数量/玩家时也有完整语法。

原版客户端（不装任何模组）就能看到怪，因为刷怪完全在服务端完成，走 Terraria 原生 NPC 生成。

```
B站弹幕  "/刷怪"
   │
   ├─ danmaku-bridge.js  解析指令、限速、鉴权
   │        │  REST  /v3/server/rawcmd?cmd=/dspawn @白嫖怪
   │        ▼
   └─ 本插件 DanmakuSpawn
            ├─ 从随机池里抽一只安全怪（撞黑名单就重抽）
            ├─ 抽一个在线玩家
            └─ 入队
            │  GameUpdate（主线程）出队
            ▼
        NPC.NewNPC(在目标玩家附近找一个能落脚的空地)
            │
            ▼
        全服聊天栏：【弹幕】白嫖怪 召唤了 1 只 Mummy 在 饺子喵喵 附近！
```

## 为什么需要插件

TShock 自带的 REST 接口没有任何"定点刷怪"能力：

| 手段 | 为什么不行 |
|---|---|
| `/v2/world/butcher` | 只能清怪 |
| `/v3/server/rawcmd` + `/spawnmob` | `/spawnmob` 只认"在**你自己**身边刷"，REST/控制台调用直接回 `You must use this command in-game.` |
| `/v2/world/meteor`、`/invade` | 只能触发全服事件，不能指定玩家 |

所以只能在服务端加一个插件，直接调 `Terraria.NPC.NewNPC`。

## 安装

```bash
# 1. 编译（宿主机已装 .NET 9 SDK 到 ~/.dotnet-sdk）
cd ~/terraria/danmaku-spawn
./build.sh            # 或者： dotnet build -c Release

# 2. 部署（compose 里 ./plugins 挂到容器的 /plugins，即 -additionalplugins 目录）
cp bin/Release/DanmakuSpawn.dll ~/terraria/plugins/

# 3. 重启服务器让插件加载（插件不支持热加载）
cd ~/terraria
docker compose restart tshock        # 或按你的习惯用 watchdog 重启

# 4. 看日志确认
docker logs --tail 40 tshock | grep DanmakuSpawn
```

启动成功的日志长这样：

```
[DanmakuSpawn] 已加载 v1.0.0，配置 /tshock/danmaku-spawn.json（怪名索引延迟到世界就绪后建立）
[DanmakuSpawn] 怪物名索引 1240 条，显示名 762 条，黑名单 67 个
```

> `refs/` 目录里是从运行中的容器里抠出来的编译引用（`TShockAPI.dll`、`TerrariaServer.dll`、`OTAPI.dll` …）。
> 服务器升级 TShock 后要重新抠一遍再编译，否则插件会加载失败：
> ```bash
> docker cp tshock:/server/bin/. ~/terraria/danmaku-spawn/refs/
> docker cp tshock:/server/ServerPlugins/TShockAPI.dll ~/terraria/danmaku-spawn/refs/
> ```

## 装好之后怎么让它生效

1. 复制 DLL 到 `~/terraria/plugins/`（已放好）
2. 重启 TShock：`cd ~/terraria && docker compose restart tshock`
3. 确认加载：`docker logs --tail 30 tshock | grep DanmakuSpawn`
   → 应看到 `[DanmakuSpawn] 已加载 v1.0.0 ...` 和 `Plugin DanmakuSpawn v1.0.0 (by qqq1942) initiated.`
4. 桥接会自动探测到（最多 45 秒），之后弹幕指令生效，无需重启桥接

## 命令

```
/dspawn                                   ← 主用法：随机怪 + 随机在线玩家
/dspawn <怪物> [数量] [玩家] [@观众名]      ← 指定刷怪
```

> 游戏内玩家直接敲 `/刷怪` 也行（插件给命令挂了中文别名 `刷怪`），效果和 `/dspawn` 一样。
> 直播间观众走 `danmaku-bridge.js`，弹幕里发 `/刷怪`。

| 写法 | 效果 |
|---|---|
| `/dspawn` | 随机一只安全怪，刷在随机一个在线玩家身边（不会拆方块、不是 BOSS）|
| `/dspawn 僵尸` | 1 只僵尸，刷在配置的默认目标附近（默认随机在线玩家） |
| `/dspawn 僵尸 3` | 3 只 |
| `/dspawn 僵尸 3 饺子喵喵` | 3 只，刷在「饺子喵喵」附近（±14 格水平、±6 格垂直里找空地） |
| `/dspawn 僵尸 3 @spawn` | 刷在世界出生点 |
| `/dspawn 僵尸 3 饺子喵喵 @白嫖怪` | 末尾 `@观众名` 只用于全服播报，不参与玩家匹配 |
| `/dspawn Blue Slime 2` | 英文正式名也行，带空格的怪名最多吃 3 个分词 |
| `/dspawn 46` | 数字 ID 也行 |

辅助子命令：

| 命令 | 作用 |
|---|---|
| `/dspawn help` | 用法 |
| `/dspawn list [页]` | 列出中文别名（一页 12 条） |
| `/dspawn pool [页]` | 列出随机模式会抽到的怪物池（一页 20 条） |
| `/dspawn status` | 开关 / 队列 / 已刷 / 被拒 / 索引条数 / 限速 / 诊断信息 |
| `/dspawn dbg <名字或ID>` | 排查用：这个怪到底被解析成哪个 ID、为什么解析不到 |
| `/dspawn reload` | 重载 `danmaku-spawn.json`（不用重启服务器） |

**控制台 / REST 也能直接调**（插件给命令设了 `AllowServer = true`）：

```bash
TOKEN=$(cat ~/terraria/.rest_token)
curl --get --data-urlencode "token=$TOKEN" \
     --data-urlencode "cmd=/dspawn 僵尸 3 饺子喵喵 @白嫖怪" \
     http://127.0.0.1:7878/v3/server/rawcmd
```

## 配置文件

`/tshock/danmaku-spawn.json`（首次启动自动生成，改完 `/dspawn reload` 生效）

| 字段 | 默认 | 说明 |
|---|---|---|
| `Enabled` | `true` | 总开关，关掉后 `/dspawn` 只回一句"已关闭" |
| `MaxAmount` | `10` | 单条指令最多刷几只（弹幕传 999 也只按这个封顶） |
| `MaxPerMinute` | `20` | 全服每分钟最多几条指令 |
| `MaxPerTargetPerMinute` | `6` | 同一个玩家每分钟最多被刷几次（防止盯着一个人整） |
| `RangeX` / `RangeY` | `14` / `6` | 刷怪点相对目标玩家的随机范围（格） |
| `BroadcastSpawn` | `true` | 刷出来以后要不要全服喊一句 |
| `DefaultTarget` | `"random"` | 没指定玩家时刷谁附近：`random`（随机在线玩家）/ 具体玩家名 / 空=不刷 |
| `BlockTownNpcs` | `true` | 禁止刷城镇 NPC（防止把商人护士刷走打死） |
| `BlockFriendlyNpcs` | `false` | 禁止刷小动物类友好 NPC |
| `BlockByPrefix` | `true` | 黑名单按名字前缀扩展：填 `Eater of Worlds` 会连带挡掉 Body/Tail 分段 |
| `RandomAmount` | `1` | 随机模式（只发 `/刷怪`）一次刷几只 |
| `RandomMobs` | 60+ 种 | 随机池：只从这些怪里抽，黑名单里的会被自动剔掉 |
| `BlockedNpcs` | 见下 | 黑名单，写英文正式名或数字 ID 都行 |
| `Aliases` | 见下 | 别名表：弹幕里打中文，游戏里认正式名 |

默认黑名单（会拆方块的怪 + 分段/机制怪，直接 `NewNPC` 会出怪状或崩服）：

```
Clown, Prime Cannon,                    ← 会扔炸弹把玩家房子炸出洞的
Dungeon Guardian, Wall of Flesh, Eater of Worlds, Brain of Cthulhu, Skeletron,
The Destroyer, Plantera, Golem, Moon Lord, Lunatic Cultist
```

加上 `BlockByPrefix` 后实际会挡住 60 多个 ID（各段身体、Moon Lord 的手/核心、城镇 NPC 等），
`/dspawn status` 里的"黑名单=67"就是这个数。

默认别名（节选，完整见 `/dspawn list`）：

```
僵尸/丧尸→Zombie  史莱姆→Blue Slime  恶魔眼→Demon Eye  骷髅→Skeleton  兔子→Bunny
蝙蝠→Cave Bat  食人鱼→Piranha  黄蜂→Hornet  蚁狮→Antlion  火焰小鬼→Fire Imp
腐化者→Corruptor  吞噬者→Devourer  灵魂吞噬者→Eater of Souls  幽冥/怨灵→Wraith
小精灵→Pixie  独角兽→Unicorn  小丑→Clown  哥布林→Goblin Scout  宝箱怪→Mimic
史莱姆王→King Slime  克苏鲁之眼→Eye of Cthulhu  蜂后→Queen Bee  猪鲨→Duke Fishron
光之女皇→Empress of Light  双子魔眼→Retinazer  机械骷髅王→Skeletron Prime
```

### 随机池（`RandomMobs`）

默认 60 多种常见敌对怪，覆盖地表/地下/洞穴/冰雪/沙漠/丛林/地牢/困难模式：
史莱姆、僵尸、恶魔眼、骷髅、蝙蝠、食人鱼、黄蜂、蚁狮、哥布林、噬魂怪、蠕虫、
宁芙、鹰身女妖、火焰小鬼、流星头、腐化者、诅咒骷髅、血月怪、地狱怪、冰雪怪、
地牢怪（暗黑法师/骷髅弓箭手/巨型诅咒骷髅/诅咒锤）、困难模式怪（幽魂/小精灵/独角兽/宝箱怪/美杜莎/吸血鬼/木乃伊……）。

想加/减：改 `/tshock/danmaku-spawn.json` 的 `RandomMobs`，然后 `/dspawn reload`。
填英文正式名或数字 ID；解析不到的项只会在启动日志里告警。

**随机池里不会有**：BOSS、分段怪、城镇 NPC、友好小动物、以及会拆方块的怪（小丑等）。

## 实现要点

1. **不在 REST 线程里直接刷怪**：命令只做校验 + 入队，真正 `NPC.NewNPC` 在
   `ServerApi.Hooks.GameUpdate`（游戏主线程）里执行，避免和主循环抢 `Main.npc` 数组。
2. **兜底通道**：如果主循环 5 秒没推进（空服世界停摆、钩子没挂上），一个 `System.Threading.Timer`
   会把积压请求直接执行掉，日志里会写"主循环 Ns 未推进，改用兜底线程执行刷怪队列"。
3. **名字索引延迟建立**：插件 `Initialize()` 早于 `Lang`/`NPCID` 就绪，那时建表会漏掉
   一大半名字（表现为"僵尸"查不到）。所以索引放在第一次用命令时建立。
4. **负数 ID 处理**：`NPCID.NegativeIDCount` 在本版本是**负值**（-66），取范围时必须
   `Math.Abs`，否则 id 1..65（史莱姆、僵尸、克苏鲁之眼这些最常用的怪）会整个被跳过。
   同名怪优先登记正数 ID（`Zombie` 既是 -55 变种又是 3 号本体，取 3）。
5. **走位保护**：随机池抽怪时先过滤黑名单；抽到的怪如果解析失败不会卡死，池子空了会退化成"全部非黑名单怪"。
6. **落点搜索**：在目标玩家周围随机取点，要求"脚下是实心块、身位两格是空的"，
   试 40 次，失败再退回 `TShock.Utils.GetRandomClearTileWithInRange`。

## 排错

| 现象 | 原因 / 处理 |
|---|---|
| 日志里完全没有 `[DanmakuSpawn]` | DLL 没放对目录，或 TShock 版本对不上（`[Server API] Info Plugin DanmakuSpawn ... initiated.` 必须出现） |
| `/dspawn` 回 `You must use this command in-game.` | 说明跑到 TShock 自带的 `/spawnmob` 了，检查 `/dspawn` 是否被别的插件占用 |
| 回"找不到怪物「xxx」" | `/dspawn dbg xxx` 看是名字没登记还是被别名写错；英文名用正式名（`Eater of Souls` 不是 `Soul Eater`） |
| 随机模式抽到的怪不想要 | 从 `RandomMobs` 里删掉，`/dspawn reload` |
| 回"找不到在线玩家" | 玩家名要用游戏内昵称/账号名，或改用在线序号 |
| 回"在主线程队列里一直不动" | `/dspawn status` 看 `tick=`。`tick=0` 且 `worldRunning=False` 说明世界没在推进（空服常见），此时靠兜底线程，5 秒后照样会刷 |
| 刷出来了但玩家看不见 | 玩家离落点太远（墙里/另一侧），把 `RangeX`/`RangeY` 调小 |

## 已知边界

- 只支持**正数 ID 的普通怪**和其它版本里能正常 `NewNPC` 的变种；分段 BOSS、需要特殊生成逻辑的
  （克苏鲁之脑、世界吞噬者、月亮领主等）默认黑名单挡住。
- 不处理"玩家在虚空/液体里"的极端情况：找不到落脚点时指令会失败并播报原因。
- 刷怪不落进领地保护区（插件不查 region），如果需要可以和 TShock 领地保护配合。
