using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DanmakuSpawn
{
	/// <summary>
	/// 插件配置文件：/tshock/danmaku-spawn.json
	/// 首次启动自动生成，改完执行 /dspawn reload 生效（不用重启服务器）。
	/// </summary>
	internal sealed class SpawnConfig
	{
		/// <summary>总开关。关掉后 /dspawn 只回一句"已关闭"。</summary>
		public bool Enabled { get; set; } = true;

		/// <summary>单条指令最多刷几只（弹幕传 999 也只按这个数封顶）。</summary>
		public int MaxAmount { get; set; } = 10;

		/// <summary>全服每分钟最多刷几次（每次指令算一次，不看数量）。</summary>
		public int MaxPerMinute { get; set; } = 20;

		/// <summary>同一个玩家每分钟最多被刷几次（防止盯着一个人整）。</summary>
		public int MaxPerTargetPerMinute { get; set; } = 6;

		/// <summary>刷怪点相对目标玩家的水平随机范围（格）。</summary>
		public int RangeX { get; set; } = 14;

		/// <summary>刷怪点相对目标玩家的垂直随机范围（格）。</summary>
		public int RangeY { get; set; } = 6;

		/// <summary>刷出来以后要不要全服喊一句。</summary>
		public bool BroadcastSpawn { get; set; } = true;

		/// <summary>随机模式一次刷几只（默认 1）。</summary>
		public int RandomAmount { get; set; } = 1;

		/// <summary>没指定玩家时刷谁附近：random（随机在线玩家）/ 玩家名 / 空=不刷。</summary>
		public string DefaultTarget { get; set; } = "random";

		/// <summary>禁止刷城镇 NPC（防止把商人、护士刷走打死）。</summary>
		public bool BlockTownNpcs { get; set; } = true;

		/// <summary>
		/// 黑名单按前缀扩展开：填 "Eater of Worlds" 会把 Body/Tail 这些分段一起挡掉，
		/// 填 "Moon Lord" 会把 Moon Lord 的头部/核心/手全部挡掉。建议保持 true。
		/// </summary>
		public bool BlockByPrefix { get; set; } = true;

		/// <summary>禁止刷"友好"NPC（小动物、宠物那类）。</summary>
		public bool BlockFriendlyNpcs { get; set; } = false;

		/// <summary>
		/// 黑名单：分段/特殊生成/机制怪，直接 NewNPC 会出怪状（身体不连、无敌、崩服），
		/// 默认挡掉。填的是英文正式名或数字 ID，都行。
		/// </summary>
		public List<string> BlockedNpcs { get; set; } = new List<string>
		{
			// 会拆方块的怪（扔炸弹，能把玩家的房子炸出洞）
			"Clown",
			"Prime Cannon",
			"Dungeon Guardian",
			"Wall of Flesh",
			"Eater of Worlds",
			"Brain of Cthulhu",
			"Skeletron",
			"The Destroyer",
			"Plantera",
			"Golem",
			"Moon Lord",
			"Lunatic Cultist",
		};

		/// <summary>
		/// 随机刷怪的候选池：观众只发 "/刷怪" 时，从这里面随机挑一只。
		/// 黑名单里的会被自动剔掉（所以小丑、BOSS、城镇 NPC 不会出现）。
		/// 填英文正式名或数字 ID；解析不到的项只会在启动日志里告警，不影响其它项。
		/// 留空 = 退化成"全部非黑名单怪"。
		/// </summary>
		public List<string> RandomMobs { get; set; } = new List<string>
		{
			// 肉前 / 地表地下
			"Blue Slime", "Zombie", "Demon Eye", "Skeleton", "Cave Bat", "Piranha",
			"Hornet", "Antlion", "Goblin Scout", "Goblin Archer", "Eater of Souls",
			"Devourer", "Giant Worm", "Digger", "Nymph", "Harpy", "Fire Imp",
			"Meteor Head", "Corruptor", "Slimer", "Cursed Skull",
			// 血月 / 猩红
			"Blood Zombie", "Blood Crawler", "Crimera", "Face Monster",
			// 地狱
			"Hellbat", "Lava Slime", "Lava Bat", "Demon", "Voodoo Demon",
			// 冰雪
			"Ice Slime", "Snow Flinx", "Undead Viking", "Armored Viking", "Icy Merman", "Wolf",
			// 沙漠 / 丛林
			"Sand Slime", "Angry Trapper", "Moss Hornet", "Derpling", "Giant Flying Fox",
			// 地牢
			"Dark Caster", "Skeleton Archer", "Giant Cursed Skull", "Cursed Hammer",
			// 困难模式
			"Wraith", "Pixie", "Unicorn", "Mimic", "Wandering Eye", "Clinger",
			"Corrupt Slime", "Pigron", "Medusa", "Vampire", "Werewolf", "Mummy",
			"Enchanted Sword", "Toxic Sludge", "Rainbow Slime", "Ice Golem",
		};

		/// <summary>
		/// 别名表：弹幕里打左边，游戏里认右边（英文正式名或数字 ID）。
		/// 大小写不敏感。中文服务器名、常见外号都可以加。
		/// </summary>
		public Dictionary<string, string> Aliases { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["史莱姆"] = "Blue Slime",
			["蓝史莱姆"] = "Blue Slime",
			["僵尸"] = "Zombie",
			["丧尸"] = "Zombie",
			["恶魔眼"] = "Demon Eye",
			["眼球"] = "Demon Eye",
			["骷髅"] = "Skeleton",
			["兔子"] = "Bunny",
			["兔"] = "Bunny",
			["蝙蝠"] = "Cave Bat",
			["蝙蝠子"] = "Cave Bat",
			["食人鱼"] = "Piranha",
			["黄蜂"] = "Hornet",
			["蚁狮"] = "Antlion",
			["地狱蝙蝠"] = "Hellbat",
			["火蝙蝠"] = "Hellbat",
			["火焰小鬼"] = "Fire Imp",
			["火焰恶魔"] = "Fire Imp",
			["流星头"] = "Meteor Head",
			["腐化者"] = "Corruptor",
			["吞噬者"] = "Devourer",
			["灵魂吞噬者"] = "Eater of Souls",
			["噬魂怪"] = "Eater of Souls",
			["幽冥"] = "Wraith",
			["怨灵"] = "Wraith",
			["小精灵"] = "Pixie",
			["独角兽"] = "Unicorn",
			["小丑"] = "Clown",
			["哥布林"] = "Goblin Scout",
			["哥布林斥候"] = "Goblin Scout",
			["哥布林弓箭手"] = "Goblin Archer",
			["宝箱怪"] = "Mimic",
			["史莱姆王"] = "King Slime",
			["克苏鲁之眼"] = "Eye of Cthulhu",
			["世界吞噬者"] = "Eater of Worlds",
			["克苏鲁之脑"] = "Brain of Cthulhu",
			["骷髅王"] = "Skeletron",
			["蜂后"] = "Queen Bee",
			["肉山"] = "Wall of Flesh",
			["血肉之墙"] = "Wall of Flesh",
			["毁灭者"] = "The Destroyer",
			["双子魔眼"] = "Retinazer",
			["机械骷髅王"] = "Skeletron Prime",
			["世纪之花"] = "Plantera",
			["石巨人"] = "Golem",
			["猪鲨"] = "Duke Fishron",
			["光之女皇"] = "Empress of Light",
			["邪教徒"] = "Lunatic Cultist",
			["月总"] = "Moon Lord",
			["月亮领主"] = "Moon Lord",
			["地牢守卫"] = "Dungeon Guardian",
			["圣诞史莱姆"] = "Present Mimic",
			["冰史莱姆"] = "Ice Slime",
			["沙史莱姆"] = "Sand Slime",
			["雪人"] = "Snow Flinx",
			["大白鲨"] = "Shark",
			["鲨鱼"] = "Shark",
			["螃蟹"] = "Crab",
			["螃蟹怪"] = "Crab",
			["掘墓者"] = "Digger",
			["巨虫"] = "Giant Worm",
			["宁芙"] = "Nymph",
			["苔藓黄蜂"] = "Moss Hornet",
			["毒泥"] = "Toxic Sludge",
			["岩浆史莱姆"] = "Lava Slime",
			["枯萎之魂"] = "Wandering Eye",
			["漂浮眼球"] = "Wandering Eye",
			["血液僵尸"] = "Blood Zombie",
			["血腥僵尸"] = "Blood Zombie",
			["飞蛇"] = "Flying Snake",
			["沙漠幽灵"] = "Desert Spirit",
			["沙虫"] = "Dune Splicer",
			["神圣史莱姆"] = "Rainbow Slime",
			["彩虹史莱姆"] = "Rainbow Slime",
			["诅咒骷髅"] = "Cursed Skull",
		};

		public static string FilePath => Path.Combine(TShockAPI.TShock.SavePath, "danmaku-spawn.json");

		private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			ReadCommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true,
			WriteIndented = true,
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};

		public static SpawnConfig Load()
		{
			string path = FilePath;
			try
			{
				if (!File.Exists(path))
				{
					var fresh = new SpawnConfig();
					File.WriteAllText(path, JsonSerializer.Serialize(fresh, JsonOpts));
					TShockAPI.TShock.Log.ConsoleInfo($"[DanmakuSpawn] 已生成默认配置 {path}");
					return fresh;
				}
				string text = File.ReadAllText(path);
				var cfg = JsonSerializer.Deserialize<SpawnConfig>(text, JsonOpts);
				if (cfg == null) throw new JsonException("反序列化结果为空");
				cfg.Normalize();
				return cfg;
			}
			catch (Exception ex)
			{
				TShockAPI.TShock.Log.Error($"[DanmakuSpawn] 读取配置失败，改用内置默认值：{ex.Message}");
				var fallback = new SpawnConfig();
				fallback.Normalize();
				return fallback;
			}
		}

		private void Normalize()
		{
			if (MaxAmount < 1) MaxAmount = 1;
			if (MaxAmount > 200) MaxAmount = 200;
			if (MaxPerMinute < 1) MaxPerMinute = 1;
			if (MaxPerTargetPerMinute < 1) MaxPerTargetPerMinute = 1;
			if (RangeX < 2) RangeX = 2;
			if (RangeX > 60) RangeX = 60;
			if (RangeY < 1) RangeY = 1;
			if (RangeY > 40) RangeY = 40;
			if (DefaultTarget == null) DefaultTarget = "random";
			if (BlockedNpcs == null) BlockedNpcs = new List<string>();
			if (Aliases == null) Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}
	}
}
