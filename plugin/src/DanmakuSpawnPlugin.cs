using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace DanmakuSpawn
{
	/// <summary>
	/// 直播间弹幕刷怪插件（TShock 6.1 / Terraria 1.4.5.8）
	///
	/// 提供一条命令，宿主机的弹幕桥接脚本通过 REST /v3/server/rawcmd 调用：
	///     /dspawn &lt;怪物&gt; [数量] [玩家] [@观众名]
	/// 例：
	///     /dspawn 僵尸 3 示例玩家 @白嫖怪
	///
	/// 设计要点：
	///   1) 命令只做校验 + 入队，真正的 NewNPC 在 GamePostUpdate（主线程）里执行，
	///      避免 REST 线程直接改 NPC 数组导致崩服；
	///   2) 玩家名可以是昵称、账号名、或在线序号；@spawn 表示刷在世界出生点；
	///   3) 全局 + 单人双限速，黑名单挡掉分段/机制怪；
	///   4) 支持中文别名，弹幕里打"僵尸""史莱姆""猪鲨"都能认。
	/// </summary>
	[ApiVersion(2, 1)]
	public sealed class DanmakuSpawnPlugin : TerrariaPlugin
	{
		public override string Name => "DanmakuSpawn";
		public override Version Version => new Version(1, 0, 0);
		public override string Author => "qqq1942";
		public override string Description => "把直播间弹幕指令变成指定玩家附近的刷怪";

		private const string ViewerTagReserved = "@spawn";

		private sealed class SpawnRequest
		{
			public int NpcId;
			public string NpcName;
			public int Amount;
			public TSPlayer Target;      // null = 固定坐标（@spawn）
			public int FixedTileX;
			public int FixedTileY;
			public string TargetLabel;
			public string Viewer;
			public DateTime QueuedAt;
		}

		private readonly object _queueLock = new object();
		private readonly object _spawnLock = new object();
		private readonly List<SpawnRequest> _pending = new List<SpawnRequest>();
		private readonly Dictionary<int, string> _displayName = new Dictionary<int, string>();
		private readonly Dictionary<string, int> _nameToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<int> _blocked = new HashSet<int>();
		private readonly List<int> _safePool = new List<int>();
		private readonly List<DateTime> _globalStamps = new List<DateTime>();
		private readonly Dictionary<int, List<DateTime>> _targetStamps = new Dictionary<int, List<DateTime>>();

		private SpawnConfig _cfg = new SpawnConfig();
		private bool _indexReady;
		private int _diagCount;
		private Command _command;
		private System.Threading.Timer _watchdog;
		private DateTime _lastTickAt = DateTime.UtcNow;
		private DateTime _lastFallbackLogAt = DateTime.MinValue;
		private int _spawnedTotal;
		private int _rejectedTotal;

		public DanmakuSpawnPlugin(Main game) : base(game) { }

		public override void Initialize()
		{
			_cfg = SpawnConfig.Load();

			_command = new Command("danmaku.spawn", OnCommand, "dspawn", "dspawnmob", "刷怪")
			{
				AllowServer = true,   // 关键：不加这个，REST/控制台调用会被拒
				DoLog = true,
				HelpText = "/dspawn <怪物> [数量] [玩家] [@观众]  —— 在玩家附近刷怪",
			};
			Commands.ChatCommands.Add(_command);
			ServerApi.Hooks.GamePostUpdate.Register(this, OnGamePostUpdate);
			ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);   // 双保险：哪个在跑用哪个


			// 兜底：万一这个 TShock 版本/配置下 GameUpdate 钩子不触发（比如空服世界停摆），
			// 队列积压 5 秒后由这个定时器直接在主线程之外补刷，保证弹幕指令不会石沉大海。
			_watchdog = new System.Threading.Timer(WatchdogTick, null, 1000, 1000);

			TShock.Log.ConsoleInfo($"[DanmakuSpawn] 已加载 v{Version}，配置 {SpawnConfig.FilePath}（怪名索引延迟到世界就绪后建立）");
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				ServerApi.Hooks.GamePostUpdate.Deregister(this, OnGamePostUpdate);
				ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);

				if (_watchdog != null) { _watchdog.Dispose(); _watchdog = null; }
				if (_command != null) Commands.ChatCommands.Remove(_command);
			}
			base.Dispose(disposing);
		}

		// ───────────────────────── 索引 ─────────────────────────

		/// <summary>
		/// 怪物名表必须在 Lang/NPCID 初始化之后建立。插件加载时机早于世界就绪，
		/// 在 Initialize 里建会拿到半成品的名字表（表现为一堆别名解析失败），
		/// 所以放到 GamePostInitialize 和首次使用命令时补建。
		/// </summary>
		private void EnsureIndexes()
		{
			if (_indexReady) return;
			BuildIndexes();
			BuildSafePool();
			_indexReady = true;
			TShock.Log.ConsoleInfo($"[DanmakuSpawn] 怪物名索引 {_nameToId.Count} 条，显示名 {_displayName.Count} 条，黑名单 {_blocked.Count} 个，随机池 {_safePool.Count} 种");
		}

		private void BuildIndexes()
		{
			_displayName.Clear();
			_nameToId.Clear();
			_blocked.Clear();

			int limit = NPCID.Count;
			// 注意：NegativeIDCount 在本版本里是负数（-66），必须取绝对值，否则 id 1..65 会被整个跳过，
			// 表现为史莱姆/僵尸/克苏鲁之眼这些"最常用的怪"全都查不到名字。
			int negLimit = -Math.Abs((int)NPCID.NegativeIDCount);
			if (negLimit > -1) negLimit = 0;
			for (int id = negLimit; id < limit; id++)
			{
				if (id == 0) continue;

				string localized = null, canonical = null;
				try { localized = Lang.GetNPCNameValue(id); }
				catch (Exception ex) { if (_diagCount++ < 3) TShock.Log.ConsoleWarn($"[DanmakuSpawn] Lang({id}) 异常: {ex.GetType().Name} {ex.Message}"); }
				try { if (NPCID.Search.TryGetName(id, out string nm)) canonical = nm; }
				catch (Exception ex) { if (_diagCount++ < 3) TShock.Log.ConsoleWarn($"[DanmakuSpawn] Search({id}) 异常: {ex.GetType().Name} {ex.Message}"); }

				string display = !string.IsNullOrWhiteSpace(localized) ? localized : canonical;
				if (!string.IsNullOrWhiteSpace(display)) _displayName[id] = display;

				RegisterName(canonical, id);
				RegisterName(localized, id);
			}

			foreach (var kv in _cfg.Aliases)
			{
				if (string.IsNullOrWhiteSpace(kv.Key)) continue;
				if (TryResolveNpc(kv.Value, out int aliasId, out _))
					_nameToId[kv.Key.Trim()] = aliasId;
				else
					TShock.Log.ConsoleWarn($"[DanmakuSpawn] 别名 {kv.Key} -> {kv.Value} 解析失败，已跳过");
			}

			foreach (string entry in _cfg.BlockedNpcs)
			{
				if (string.IsNullOrWhiteSpace(entry)) continue;
				string needle = entry.Trim();
				int before = _blocked.Count;

				if (TryResolveNpc(needle, out int blockedId, out _)) _blocked.Add(blockedId);

				if (_cfg.BlockByPrefix)
				{
					// 名字里去掉空格比较，这样 "Eater of Worlds" 能连带挡住 Body/Tail
					string flat = needle.Replace(" ", "").ToLowerInvariant();
					foreach (var kv in _nameToId)
					{
						string key = kv.Key.Replace(" ", "").ToLowerInvariant();
						if (key.StartsWith(flat)) _blocked.Add(kv.Value);
					}
					foreach (var kv in _displayName)
					{
						string key = kv.Value.Replace(" ", "").ToLowerInvariant();
						if (key.StartsWith(flat)) _blocked.Add(kv.Key);
					}
				}

				if (_blocked.Count == before)
					TShock.Log.ConsoleWarn($"[DanmakuSpawn] 黑名单项 {entry} 没匹配到任何怪，已跳过");
			}

			if (_cfg.BlockTownNpcs || _cfg.BlockFriendlyNpcs)
			{
				var probe = new NPC();
				for (int id = negLimit; id < limit; id++)
				{
					if (id == 0) continue;
					try
					{
						probe.SetDefaults(id, new NPCSpawnParams());
						if (_cfg.BlockTownNpcs && probe.townNPC) _blocked.Add(id);
						if (_cfg.BlockFriendlyNpcs && probe.friendly) _blocked.Add(id);
					}
					catch { }
				}
			}
		}

		/// <summary>
		/// 同一个名字可能同时对应负数变种和正数本体（"Zombie" 既是 -55 变种又是 3 号本体），
		/// 优先登记正数 ID：负数 ID 是引擎内部的配色/体型变种，直接用 NewNPC 生成不可靠。
		/// </summary>
		private void RegisterName(string name, int id)
		{
			if (string.IsNullOrWhiteSpace(name)) return;
			if (_nameToId.TryGetValue(name, out int existing))
			{
				if (existing < 0 && id > 0) _nameToId[name] = id;
				return;
			}
			_nameToId[name] = id;
		}

		/// <summary>
		/// 随机刷怪用的"安全怪物池"：只从配置的 RandomMobs 里挑，再把黑名单（BOSS/分段怪/
		/// 城镇NPC/会拆方块的怪）过滤掉。配空了就退化成"全部非黑名单怪"。
		/// </summary>
		private void BuildSafePool()
		{
			_safePool.Clear();

			foreach (string name in _cfg.RandomMobs)
			{
				if (string.IsNullOrWhiteSpace(name)) continue;
				if (!TryResolveNpc(name.Trim(), out int id, out _))
				{
					TShock.Log.ConsoleWarn($"[DanmakuSpawn] 随机池里的「{name}」解析失败，已跳过");
					continue;
				}
				if (_blocked.Contains(id)) continue;
				if (!_safePool.Contains(id)) _safePool.Add(id);
			}

			if (_safePool.Count == 0)
			{
				foreach (var kv in _displayName)
				{
					if (kv.Key <= 0) continue;
					if (_blocked.Contains(kv.Key)) continue;
					_safePool.Add(kv.Key);
				}
				TShock.Log.ConsoleWarn("[DanmakuSpawn] RandomMobs 为空或全部无效，随机池退化为全部非黑名单怪");
			}
		}

		private static bool IsSpawnMarker(string token)
		{
			return string.Equals(token, "@spawn", StringComparison.OrdinalIgnoreCase) || token == "@出生点";
		}

		private int PickRandomMob(out string display)
		{
			display = null;
			if (_safePool.Count == 0) return 0;
			int id = _safePool[Main.rand.Next(_safePool.Count)];
			display = _displayName.TryGetValue(id, out string name) ? name : $"NPC#{id}";
			return id;
		}

		private bool TryResolveNpc(string token, out int id, out string display)
		{
			id = 0;
			display = null;
			if (string.IsNullOrWhiteSpace(token)) return false;

			token = token.Trim();

			if (int.TryParse(token, out int numeric))
			{
				bool positive = numeric > 0 && numeric < NPCID.Count;
				bool negative = numeric < 0 && numeric >= -Math.Abs((int)NPCID.NegativeIDCount);
				if (positive || negative)
				{
					id = numeric;
					display = _displayName.TryGetValue(id, out string dn) ? dn : $"NPC#{id}";
					return true;
				}
				return false;
			}

			if (_cfg.Aliases.TryGetValue(token, out string aliasTarget) && !string.Equals(aliasTarget, token, StringComparison.OrdinalIgnoreCase))
			{
				if (TryResolveNpc(aliasTarget, out id, out display)) return true;
			}

			if (_nameToId.TryGetValue(token, out id))
			{
				display = _displayName.TryGetValue(id, out string dn) ? dn : token;
				return true;
			}

			// 正式名里带空格/大小写差异时再兜一层：NPCID.Search 自带模糊
			try
			{
				int bySearch = NPCID.Search.GetId(token);
				if (bySearch > 0 && bySearch < NPCID.Count)
				{
					id = bySearch;
					display = _displayName.TryGetValue(id, out string dn2) ? dn2 : token;
					return true;
				}
			}
			catch { }

			id = 0;
			return false;
		}

		// ───────────────────────── 命令 ─────────────────────────

		private void OnCommand(CommandArgs args)
		{
			try
			{
				HandleCommand(args);
			}
			catch (Exception ex)
			{
				args.Player.SendErrorMessage("[刷怪] 内部错误，详情见服务器日志。");
				TShock.Log.Error($"[DanmakuSpawn] 命令异常：{ex}");
			}
		}

		private void HandleCommand(CommandArgs args)
		{
			EnsureIndexes();

			if (!_cfg.Enabled)
			{
				args.Player.SendErrorMessage("[刷怪] 功能已关闭（配置 Enabled=false）。");
				return;
			}

			// 1) 先把 @观众名 摘出来（@spawn / @出生点 是目标写法，要留着）
			var tokens = new List<string>();
			string viewer = null;
			foreach (string raw in args.Parameters)
			{
				string t = raw.Trim();
				if (t.Length == 0) continue;

				if (t.StartsWith("@"))
				{
					if (IsSpawnMarker(t)) { tokens.Add(t); continue; }
					if (viewer == null) viewer = t.Substring(1);
					continue;
				}
				tokens.Add(t);
			}

			// 2) 不带任何参数 = 随机模式：随机一只安全怪 + 随机一个在线玩家
			if (tokens.Count == 0)
			{
				DoRandomSpawn(args.Player, null, viewer);
				return;
			}
			if (tokens.Count == 1 && IsSpawnMarker(tokens[0]))
			{
				DoRandomSpawn(args.Player, tokens[0], viewer);
				return;
			}

			switch (tokens[0].ToLowerInvariant())
			{
				case "help":
				case "帮助":
					SendHelp(args.Player);
					return;
				case "reload":
				case "重载":
					_cfg = SpawnConfig.Load();
					_indexReady = false;
					EnsureIndexes();
					args.Player.SendSuccessMessage($"[刷怪] 配置已重载：限速 {_cfg.MaxPerMinute}/分钟，单人 {_cfg.MaxPerTargetPerMinute}/分钟，单次上限 {_cfg.MaxAmount}，随机池 {_safePool.Count} 种。");
					TShock.Log.ConsoleInfo("[DanmakuSpawn] 配置已重载");
					return;
				case "status":
				case "状态":
					args.Player.SendInfoMessage($"[刷怪] 开关={_cfg.Enabled} 队列={PendingCount} 已刷={_spawnedTotal} 被拒={_rejectedTotal} 怪物索引={_nameToId.Count} 黑名单={_blocked.Count} 随机池={_safePool.Count}");
					args.Player.SendInfoMessage($"[刷怪] 限速 全局{_cfg.MaxPerMinute}/分  单人{_cfg.MaxPerTargetPerMinute}/分  单次≤{_cfg.MaxAmount}只  范围±{_cfg.RangeX}格");
					args.Player.SendInfoMessage($"[刷怪] 诊断 tick={_tickCount} worldRunning={ServerApi.IsWorldRunning} gameMenu={Main.gameMenu}");
					return;
				case "pool":
				case "随机池":
					ListPool(args.Player, tokens.Count > 1 && int.TryParse(tokens[1], out int poolPage) ? poolPage : 1);
					return;
				case "dbg":
				case "查":
					DebugLookup(args.Player, tokens.Skip(1).ToList());
					return;
				case "test":
				case "自检":
				case "测试":
					DoSelfTest(args.Player);
					return;
				case "list":
				case "别名":
					ListAliases(args.Player, tokens.Count > 1 && int.TryParse(tokens[1], out int page) ? page : 1);
					return;
			}

			// 3a) 纯数字 = 数量（观众的本能：「!刷怪 3」就是要 3 只，不是怪物 #3）
			//     数量必须在 MaxAmount 以内，超出才退化成 NPC 数字 ID 解析。
			if (int.TryParse(tokens[0], out int qty) && qty >= 1 && qty <= _cfg.MaxAmount)
			{
				int randomId = PickRandomMob(out string randomName);
				if (randomId == 0)
				{
					args.Player.SendErrorMessage("[刷怪] 随机池是空的，检查配置 RandomMobs。");
					_rejectedTotal++;
					return;
				}

				string qTarget = tokens.Count > 1 ? string.Join(" ", tokens.Skip(1)).Trim() : null;
				var qReq = new SpawnRequest
				{
					NpcId = randomId,
					NpcName = randomName,
					Amount = qty,
					Viewer = viewer,
					QueuedAt = DateTime.UtcNow,
				};

				if (!ApplyTarget(qReq, qTarget, args.Player, out string qErr))
				{
					args.Player.SendErrorMessage($"[刷怪] {qErr}");
					_rejectedTotal++;
					return;
				}
				if (!EnqueueSpawn(qReq, args.Player, out string qRate))
				{
					args.Player.SendErrorMessage($"[刷怪] {qRate}");
					_rejectedTotal++;
				}
				return;
			}

			// 3) 指定了怪物名：最多吃 3 个 token（"Blue Slime"、"骷髅王" 这种带空格的）
			int npcId = 0;
			string npcDisplay = null;
			int consumed = 0;
			bool found = false;
			for (int take = Math.Min(3, tokens.Count); take >= 1; take--)
			{
				string candidate = string.Join(" ", tokens.Take(take));
				if (TryResolveNpc(candidate, out npcId, out npcDisplay))
				{
					consumed = take;
					found = true;
					break;
				}
			}
			if (!found)
			{
				// 3b) 「/dspawn <玩家名>」——怪名解析失败时，退一步当成在线玩家：
				//     随机刷在 TA 身边。这样观众直接打「!刷怪 示例玩家」就有效。
				var pTokens = new List<string>(tokens);
				int pAmount = Math.Clamp(_cfg.RandomAmount, 1, _cfg.MaxAmount);
				if (pTokens.Count > 1 && int.TryParse(pTokens[pTokens.Count - 1], out int pCount))
				{
					pAmount = Math.Clamp(pCount, 1, _cfg.MaxAmount);
					pTokens.RemoveAt(pTokens.Count - 1);
				}

				string playerArg = string.Join(" ", pTokens).Trim();
				TSPlayer near = string.IsNullOrEmpty(playerArg)
					? null
					: ResolveTarget(playerArg, args.Player, out _);

				if (near != null)
				{
					DoRandomSpawnAt(args.Player, near, pAmount, viewer);
					return;
				}

				args.Player.SendErrorMessage($"[刷怪] 找不到怪物「{tokens[0]}」，也没有这个在线玩家。");
				args.Player.SendErrorMessage($"[刷怪] 可用：/dspawn <怪物> [数量] [玩家]  ｜  /dspawn <玩家> = 在他身边刷随机怪  ｜  /dspawn = 全随机");
				_rejectedTotal++;
				return;
			}

			if (_blocked.Contains(npcId))
			{
				args.Player.SendErrorMessage($"[刷怪] 「{npcDisplay}」在黑名单里（BOSS/分段怪/会拆方块的怪）。");
				_rejectedTotal++;
				return;
			}

			var rest = tokens.Skip(consumed).ToList();

			// 4) 数量
			int amount = 1;
			if (rest.Count > 0 && int.TryParse(rest[0], out int parsed))
			{
				amount = Math.Clamp(parsed, 1, _cfg.MaxAmount);
				rest.RemoveAt(0);
			}

			string targetArg = rest.Count > 0 ? string.Join(" ", rest).Trim() : null;

			var req = new SpawnRequest
			{
				NpcId = npcId,
				NpcName = npcDisplay,
				Amount = amount,
				Viewer = viewer,
				QueuedAt = DateTime.UtcNow,
			};

			if (!ApplyTarget(req, targetArg, args.Player, out string targetError))
			{
				args.Player.SendErrorMessage($"[刷怪] {targetError}");
				_rejectedTotal++;
				return;
			}

			if (!EnqueueSpawn(req, args.Player, out string rateError))
			{
				args.Player.SendErrorMessage($"[刷怪] {rateError}");
				_rejectedTotal++;
			}
		}

		/// <summary>随机模式：随机一只安全怪，刷在随机在线玩家（或指定目标）附近。</summary>
		private void DoRandomSpawn(TSPlayer caller, string targetArg, string viewer)
		{
			int npcId = PickRandomMob(out string npcDisplay);
			if (npcId == 0)
			{
				caller.SendErrorMessage("[刷怪] 随机池是空的，检查配置里的 RandomMobs。");
				_rejectedTotal++;
				return;
			}

			var req = new SpawnRequest
			{
				NpcId = npcId,
				NpcName = npcDisplay,
				Amount = Math.Clamp(_cfg.RandomAmount, 1, _cfg.MaxAmount),
				Viewer = viewer,
				QueuedAt = DateTime.UtcNow,
			};

			if (!ApplyTarget(req, targetArg, caller, out string targetError))
			{
				caller.SendErrorMessage($"[刷怪] {targetError}");
				_rejectedTotal++;
				return;
			}

			if (!EnqueueSpawn(req, caller, out string rateError))
			{
				caller.SendErrorMessage($"[刷怪] {rateError}");
				_rejectedTotal++;
			}
		}

		/// <summary>/dspawn 自检：立刻在调用者脚下刷一只怪，并把同步结果回报给他。</summary>
		private void DoSelfTest(TSPlayer ply)
		{
			if (ply == null) return;
			int id = PickRandomMob(out string name);
			if (id == 0)
			{
				ply.SendErrorMessage("[刷怪] 随机池为空，检查配置 RandomMobs / BlockedNpcs。");
				return;
			}

			int before = Main.npc.Count(n => n != null && n.active);
			var req = new SpawnRequest
			{
				NpcId = id,
				NpcName = name,
				Amount = 1,
				Viewer = "自检",
				Target = ply,
				TargetLabel = ply.Name,
				QueuedAt = DateTime.UtcNow,
			};
			ExecuteSpawn(req);
			int after = Main.npc.Count(n => n != null && n.active);

			ply.SendInfoMessage($"[刷怪] 自检：{name}（id={id}）｜全服 NPC 数量 {before} → {after}｜你的位置格 {ply.TileX},{ply.TileY}");
			ply.SendInfoMessage($"[刷怪] 如果脚下出现了怪，说明同步正常；如果日志刷了但游戏里没有，说明客户端没收到同步包。");
		}

		/// <summary>「/dspawn 玩家名」：随机一只安全怪，刷在指定玩家身边。</summary>
		private void DoRandomSpawnAt(TSPlayer caller, TSPlayer target, int amount, string viewer)
		{
			int npcId = PickRandomMob(out string npcDisplay);
			if (npcId == 0)
			{
				caller.SendErrorMessage("[刷怪] 随机池是空的，检查配置里的 RandomMobs。");
				_rejectedTotal++;
				return;
			}

			var req = new SpawnRequest
			{
				NpcId = npcId,
				NpcName = npcDisplay,
				Amount = Math.Clamp(amount, 1, _cfg.MaxAmount),
				Viewer = viewer,
				QueuedAt = DateTime.UtcNow,
				Target = target,
				TargetLabel = target.Name,
			};

			if (!EnqueueSpawn(req, caller, out string rateError))
			{
				caller.SendErrorMessage($"[刷怪] {rateError}");
				_rejectedTotal++;
			}
		}

		private bool ApplyTarget(SpawnRequest req, string targetArg, TSPlayer caller, out string error)
		{
			error = null;

			if (IsSpawnMarker(targetArg))
			{
				req.FixedTileX = Main.spawnTileX;
				req.FixedTileY = Main.spawnTileY;
				req.TargetLabel = "世界出生点";
				return true;
			}

			TSPlayer target = ResolveTarget(targetArg, caller, out error);
			if (target == null) return false;

			req.Target = target;
			req.TargetLabel = target.Name;
			return true;
		}

		private bool EnqueueSpawn(SpawnRequest req, TSPlayer caller, out string error)
		{
			int rateKey = req.Target != null ? req.Target.Index : -100;
			if (!PassRateLimit(rateKey, out error)) return false;

			lock (_queueLock) _pending.Add(req);
			caller.SendSuccessMessage($"[刷怪] 已排队：{req.Amount} 只「{req.NpcName}」→ {req.TargetLabel}");
			return true;
		}

		private TSPlayer ResolveTarget(string arg, TSPlayer caller, out string error)
		{
			error = null;

			var online = TShock.Players.Where(t => t != null && t.Active && t.RealPlayer).ToList();

			if (string.IsNullOrWhiteSpace(arg))
			{
				string def = (_cfg.DefaultTarget ?? "").Trim();
				if (def.Length == 0)
				{
					error = "没有指定玩家，且配置里 DefaultTarget 为空。";
					return null;
				}
				if (def.Equals("random", StringComparison.OrdinalIgnoreCase) || def == "随机")
				{
					if (online.Count == 0)
					{
						error = "现在没有在线玩家可以刷怪。";
						return null;
					}
					return online[Main.rand.Next(online.Count)];
				}
				return ResolveTarget(def, caller, out error);
			}

			if (arg.Equals("random", StringComparison.OrdinalIgnoreCase) || arg == "随机")
			{
				if (online.Count == 0)
				{
					error = "现在没有在线玩家可以刷怪。";
					return null;
				}
				return online[Main.rand.Next(online.Count)];
			}

			if (int.TryParse(arg, out int index))
			{
				if (index >= 0 && index < TShock.Players.Length && TShock.Players[index] != null && TShock.Players[index].Active)
					return TShock.Players[index];
				error = $"玩家序号 {index} 不在线。";
				return null;
			}

			var matches = TSPlayer.FindByNameOrID(arg)
				.Where(t => t != null && t.Active && t.RealPlayer)
				.ToList();

			if (matches.Count == 1) return matches[0];
			if (matches.Count > 1)
			{
				error = "匹配到多个玩家：" + string.Join(", ", matches.Select(t => t.Name)) + "，请输入完整名字。";
				return null;
			}

			error = $"找不到在线玩家「{arg}」。当前在线：{string.Join(", ", online.Select(t => t.Name))}";
			return null;
		}

		private bool PassRateLimit(int rateKey, out string error)
		{
			error = null;
			DateTime now = DateTime.UtcNow;
			_prune();

			if (_globalStamps.Count >= _cfg.MaxPerMinute)
			{
				error = $"全服刷怪太快了（{_cfg.MaxPerMinute}/分钟），等一会儿。";
				return false;
			}

			if (!_targetStamps.TryGetValue(rateKey, out var stamps))
			{
				stamps = new List<DateTime>();
				_targetStamps[rateKey] = stamps;
			}
			if (stamps.Count >= _cfg.MaxPerTargetPerMinute)
			{
				error = $"这个目标被刷得太频繁（{_cfg.MaxPerTargetPerMinute}/分钟），换个目标或等一会儿。";
				return false;
			}

			_globalStamps.Add(now);
			stamps.Add(now);
			return true;
		}

		private void _prune()
		{
			DateTime cutoff = DateTime.UtcNow.AddMinutes(-1);
			_globalStamps.RemoveAll(t => t < cutoff);
			foreach (var key in _targetStamps.Keys.ToList())
			{
				_targetStamps[key].RemoveAll(t => t < cutoff);
				if (_targetStamps[key].Count == 0) _targetStamps.Remove(key);
			}
		}

		// ───────────────────────── 主线程执行 ─────────────────────────

		private int _tickCount;

		private int PendingCount
		{
			get { lock (_queueLock) return _pending.Count; }
		}

		private bool TryDequeue(out SpawnRequest req)
		{
			lock (_queueLock)
			{
				if (_pending.Count == 0) { req = null; return false; }
				req = _pending[0];
				_pending.RemoveAt(0);
				return true;
			}
		}

		private void OnGameUpdate(EventArgs args)
		{
			_tickCount++;
			_lastTickAt = DateTime.UtcNow;
			DrainQueue(8);
		}

		private void OnGamePostUpdate(EventArgs args)
		{
			_lastTickAt = DateTime.UtcNow;
			DrainQueue(8);
		}

		private void DrainQueue(int budget)
		{
			while (budget-- > 0 && TryDequeue(out var req))
			{
				try
				{
					ExecuteSpawn(req);
				}
				catch (Exception ex)
				{
					TShock.Log.Error($"[DanmakuSpawn] 刷怪执行异常：{ex}");
				}
			}
		}

		/// <summary>
		/// 兜底通道：如果游戏主循环 5 秒没有推进（空服世界停摆、钩子没挂上等），
		/// 就由定时器线程把积压的请求直接执行掉，避免弹幕指令永远排在队列里。
		/// </summary>
		private void WatchdogTick(object state)
		{
			try
			{
				SpawnRequest head;
				lock (_queueLock)
				{
					if (_pending.Count == 0) return;
					head = _pending[0];
				}

				double waited = (DateTime.UtcNow - head.QueuedAt).TotalSeconds;
				double sinceTick = (DateTime.UtcNow - _lastTickAt).TotalSeconds;
				if (waited < 5 || sinceTick < 5) return;

				if ((DateTime.UtcNow - _lastFallbackLogAt).TotalSeconds > 60)
				{
					_lastFallbackLogAt = DateTime.UtcNow;
					TShock.Log.ConsoleWarn($"[DanmakuSpawn] 主循环 {sinceTick:F0}s 未推进，改用兜底线程执行刷怪队列（队列 {PendingCount} 条）");
				}

				DrainQueue(int.MaxValue);
			}
			catch (Exception ex)
			{
				TShock.Log.Error($"[DanmakuSpawn] 兜底刷怪失败：{ex}");
			}
		}

		private void ExecuteSpawn(SpawnRequest req)
		{
			lock (_spawnLock)
			{
				ExecuteSpawnCore(req);
			}
		}

		private void ExecuteSpawnCore(SpawnRequest req)
		{
			int baseX, baseY;
			string targetName = req.TargetLabel;

			if (req.Target != null)
			{
				if (!req.Target.Active || !req.Target.RealPlayer)
				{
					Reject(req, $"目标 {req.TargetLabel} 已经离线");
					return;
				}
				baseX = req.Target.TileX;
				baseY = req.Target.TileY;
				targetName = req.Target.Name;
			}
			else
			{
				baseX = req.FixedTileX;
				baseY = req.FixedTileY;
			}

			int spawned = 0;
			var coords = new List<string>();
			for (int i = 0; i < req.Amount; i++)
			{
				if (!TryPickSpawnTile(baseX, baseY, out int tx, out int ty)) continue;

				int idx = NPC.NewNPC(new EntitySource_SpawnNPC(), tx * 16 + 8, ty * 16, req.NpcId, 0, 0f, 0f, 0f, 0f, -1);
				if (idx >= 0 && idx < Main.maxNPCs)
				{
					// 关键：服务端 NewNPC 只在服务器内存里创建，不会自动通知客户端。
					// 原版自然刷怪用的是 NetMessage.SendData(23=NPCUpdate)，漏了这一步
					// 客户端就完全看不见这只怪（日志里有、游戏里没有）。
					NPC npc = Main.npc[idx];
					if (npc.netID == 0) npc.netID = req.NpcId;
					npc.netUpdate = true;
					if (Main.netMode == 2)
					{
						NetMessage.SendData(23, -1, -1, null, idx, 0f, 0f, 0f, 0, 0, 0);
					}
					spawned++;
					coords.Add($"{tx},{ty}");
				}
			}

			if (spawned <= 0)
			{
				Reject(req, "附近找不到能落脚的空地（可能在水里/墙里/太挤）");
				return;
			}

			_spawnedTotal += spawned;
			string who = string.IsNullOrWhiteSpace(req.Viewer) ? "弹幕" : req.Viewer;

			if (_cfg.BroadcastSpawn)
			{
				TSPlayer.All.SendMessage($"【弹幕】{who} 召唤了 {spawned} 只 {req.NpcName} 在 {targetName} 附近！", 255, 220, 120);
			}
			TShock.Log.ConsoleInfo($"[DanmakuSpawn] {who} -> {spawned}x {req.NpcName} near {targetName}"
				+ $" | 基准格 {baseX},{baseY} | 实际刷在 [{string.Join(" ", coords)}]");
		}

		private void Reject(SpawnRequest req, string reason)
		{
			_rejectedTotal++;
			string who = string.IsNullOrWhiteSpace(req.Viewer) ? "弹幕" : req.Viewer;
			TShock.Log.ConsoleInfo($"[DanmakuSpawn] 拒绝 {who} 的 {req.Amount}x {req.NpcName}：{reason}");
			if (_cfg.BroadcastSpawn)
				TSPlayer.All.SendMessage($"【弹幕】{who} 想召唤 {req.NpcName}，但{reason}。", 255, 160, 160);
		}

		/// <summary>在基准点附近找一个"脚下是实心块、身位是空的"格子。</summary>
		private bool TryPickSpawnTile(int baseX, int baseY, out int tileX, out int tileY)
		{
			tileX = tileY = -1;
			int maxX = Main.maxTilesX - 10;
			int maxY = Main.maxTilesY - 10;

			for (int attempt = 0; attempt < 40; attempt++)
			{
				int cx = baseX + Main.rand.Next(-_cfg.RangeX, _cfg.RangeX + 1);
				int cy = baseY + Main.rand.Next(-_cfg.RangeY, _cfg.RangeY + 1);

				if (cx < 10 || cx > maxX || cy < 10 || cy > maxY) continue;
				if (TShock.Utils.TileSolid(cx, cy)) continue;
				if (TShock.Utils.TileSolid(cx, cy - 1)) continue;
				if (!TShock.Utils.TileSolid(cx, cy + 1)) continue;   // 要踩得住

				tileX = cx;
				tileY = cy;
				return true;
			}

			// 兜底：TShock 的空地搜索（可能在水里或半空，但至少不卡在地里）
			try
			{
				TShock.Utils.GetRandomClearTileWithInRange(baseX, baseY - 2, _cfg.RangeX, _cfg.RangeY, out int fx, out int fy);
				if (fx > 10 && fx < maxX && fy > 10 && fy < maxY && !TShock.Utils.TileSolid(fx, fy))
				{
					tileX = fx;
					tileY = fy;
					return true;
				}
			}
			catch { }

			return false;
		}

		// ───────────────────────── 帮助 ─────────────────────────

		private void DebugLookup(TSPlayer ply, List<string> tokens)
		{
			string q = string.Join(" ", tokens).Trim();
			if (q.Length == 0)
			{
				ply.SendErrorMessage("[刷怪] 用法：/dspawn dbg <名字>");
				return;
			}

			if (int.TryParse(q, out int asId))
			{
				string langName = "抛异常", searchName = "抛异常";
				try { langName = Lang.GetNPCNameValue(asId) ?? "<null>"; } catch (Exception ex) { langName = "异常:" + ex.Message; }
				try { searchName = NPCID.Search.TryGetName(asId, out string sn) ? sn : "<无>"; } catch (Exception ex) { searchName = "异常:" + ex.Message; }
				ply.SendInfoMessage($"[刷怪] id {asId}：Lang=「{langName}」 Search=「{searchName}」 显示名=「{(_displayName.TryGetValue(asId, out string dd) ? dd : "<无>")}」");
				return;
			}

			bool inMap = _nameToId.TryGetValue(q, out int mapped);
			ply.SendInfoMessage($"[刷怪] 查「{q}」：索引命中={inMap}{(inMap ? $" id={mapped}" : "")}");

			string searchId = "抛异常";
			try { searchId = NPCID.Search.GetId(q).ToString(); } catch (Exception ex) { searchId = "异常:" + ex.Message; }
			ply.SendInfoMessage($"[刷怪] NPCID.Search.GetId = {searchId}");

			var hits = new List<string>();
			foreach (var kv in _nameToId)
			{
				if (kv.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
					hits.Add($"{kv.Key}({kv.Value})");
				if (hits.Count >= 6) break;
			}
			ply.SendInfoMessage($"[刷怪] 模糊匹配 {(hits.Count == 0 ? "无" : string.Join(", ", hits))}");
		}

		private void ListPool(TSPlayer ply, int page)
		{
			if (_safePool.Count == 0)
			{
				ply.SendInfoMessage("[刷怪] 随机池是空的。");
				return;
			}

			var items = _safePool.Select(id => $"{( _displayName.TryGetValue(id, out string n) ? n : "NPC")}({id})").ToList();
			const int perPage = 20;
			int pages = (items.Count + perPage - 1) / perPage;
			page = Math.Clamp(page, 1, pages);

			ply.SendInfoMessage($"[刷怪] 随机池 {page}/{pages}（共 {items.Count} 种）：");
			foreach (string line in items.Skip((page - 1) * perPage).Take(perPage))
				ply.SendInfoMessage("  " + line);
		}

		private void SendHelp(TSPlayer ply)
		{
			ply.SendInfoMessage("[刷怪] /dspawn            —— 随机一只安全怪，刷在随机在线玩家身边");
			ply.SendInfoMessage("[刷怪] /dspawn <怪物> [数量] [玩家] [@观众名]  —— 指定刷怪");
			ply.SendInfoMessage("[刷怪] /dspawn <玩家> [数量]  —— 只报玩家名：在他身边刷随机怪（观众可直接 !刷怪 示例玩家）");
			ply.SendInfoMessage("[刷怪] 例：/dspawn 僵尸 3 示例玩家 @白嫖怪");
			ply.SendInfoMessage("[刷怪] 玩家可写昵称/账号/在线序号，@spawn 表示世界出生点，省略=配置的默认目标");
			ply.SendInfoMessage("[刷怪] /dspawn 3 —— 纯数字就是数量：随机刷 3 只（数字 >10 才当作 NPC ID）");
			ply.SendInfoMessage("[刷怪] /dspawn 3 示例玩家 —— 3 只随机怪，刷在指定玩家身边");
			ply.SendInfoMessage("[刷怪] /dspawn 自检 —— 立刻在自己脚下刷一只怪并回报同步结果");
			ply.SendInfoMessage("[刷怪] /dspawn list [页] 中文别名 | /dspawn pool [页] 随机池 | /dspawn status | /dspawn reload");
		}

		private void ListAliases(TSPlayer ply, int page)
		{
			var items = new List<string>();
			foreach (var kv in _cfg.Aliases.OrderBy(k => k.Key, StringComparer.Ordinal))
			{
				if (string.IsNullOrWhiteSpace(kv.Key)) continue;
				string shown = kv.Value;
				if (TryResolveNpc(kv.Value, out int id, out string display) && id > 0)
					shown = $"{display}({id})";
				items.Add($"{kv.Key} → {shown}");
			}

			if (items.Count == 0)
			{
				ply.SendInfoMessage("[刷怪] 配置里没有别名。");
				return;
			}

			const int perPage = 12;
			int pages = (items.Count + perPage - 1) / perPage;
			page = Math.Clamp(page, 1, pages);

			ply.SendInfoMessage($"[刷怪] 别名 {page}/{pages}：");
			foreach (string line in items.Skip((page - 1) * perPage).Take(perPage))
				ply.SendInfoMessage("  " + line);
		}
	}
}
