# refs/ —— 编译用的程序集（不入库）

Terraria / TShock 的程序集有版权，不随仓库分发。从你正在跑的 TShock 镜像里拷出来即可：

```bash
CID=$(docker ps -qf name=tshock)
docker cp "$CID":/server/TShockAPI.dll        plugin/refs/
docker cp "$CID":/server/TerrariaServer.dll   plugin/refs/
docker cp "$CID":/server/OTAPI.dll            plugin/refs/
docker cp "$CID":/server/OTAPI.Runtime.dll    plugin/refs/
docker cp "$CID":/server/ModFramework.dll     plugin/refs/
```

拷完执行 `./build.sh --deploy` 即可编译并投放到 `plugins/` 目录。
