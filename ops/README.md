# ops/ —— 运维脚本

两个脚本负责「服务器别挂」和「重启别丢档」。

| 文件 | 作用 |
|---|---|
| `watchdog.sh` | 健康巡检 + 自动恢复：容器 / 7777 监听 / REST / 僵尸连接 / **存档是否落盘** |
| `restart.sh` | 安全重启：广播 → QQ 通知 → 倒计时 → 存档并校验落盘 → 停 → 启 → 验证 |
| `watchdog.conf.example` | 配置模板，复制成 `watchdog.conf` 后修改 |

## 部署

```bash
cp ops/watchdog.sh ops/restart.sh ~/terraria/
cp ops/watchdog.conf.example ~/terraria/watchdog.conf
$EDITOR ~/terraria/watchdog.conf      # 填 NapCat token、QQ 号、域名
chmod +x ~/terraria/watchdog.sh ~/terraria/restart.sh
```

## 为什么需要它们

Terraria 的世界只在内存里。**不存盘就停服 = 回档**，这是踩过的坑：
某次直接 `docker compose restart` 加载插件，玩家一小时进度全丢。

`restart.sh` 的四道闸：

1. `/worlds` 挂载必须指向正确的宿主目录，否则中止（挂错必回档）
2. 存档后必须看到 `Terraria.wld` 的 mtime 真的前进，30 秒没变就中止
3. 停服前挂 `.maintenance`，防止看门狗把服务器又拉起来
4. 启动后校验监听 + REST + 世界加载，失败则保留维护标记等人工处理

`watchdog.sh` 有一条容易被忽略的检查：**存档落盘**。
自动备份比存档文件新超过 `WORLD_STALE_MIN` 分钟，说明世界根本没写回宿主盘，
这时重启必然回档 —— 所以它只告警、不重启。

## cron

```cron
*/5  * * * *  ~/terraria/watchdog.sh
*/10 * * * *  ~/terraria/watchdog.sh --ext
```

## 注意

`watchdog.sh` **从不主动连接 7777 游戏端口**。实测裸 TCP 连接会在 20~30 次后
让 Terraria 的监听线程死掉，反而制造故障。它改用 `/proc/net/tcp` 读监听状态。
