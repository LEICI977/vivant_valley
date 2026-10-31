using System.Text;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Quests;
using StardewValley.TerrainFeatures;

namespace VivantValley.Services;

/// <summary>
/// Builds the shared full game snapshot for legacy and proactive scenes on the game thread.
/// Edit manual-chat rules in assets/prompts; ConversationContextBuilder filters its world facts.
/// </summary>
public sealed class GameContextBuilder
{
    private readonly ModConfig config;
    private readonly NpcCombatStateService? combatState;

    public GameContextBuilder(
        ModConfig config,
        NpcCombatStateService? combatState = null)
    {
        this.config = config;
        this.combatState = combatState;
    }

    public NpcGameSnapshot Build(NPC npc)
    {
        Farmer player = Game1.player;
        Friendship? friendship = null;
        player.friendshipData.TryGetValue(npc.Name, out friendship);

        int points = friendship?.Points ?? 0;
        int hearts = player.getFriendshipHeartLevelForNPC(npc.Name);

        var builder = new StringBuilder(4096);
        builder.AppendLine("【此刻的游戏事实（优先级高于模型常识）】");
        builder.AppendLine($"- 村民：{npc.displayName}（内部名 {npc.Name}）");
        builder.AppendLine($"- 玩家：{player.Name}；农场：{player.farmName.Value}；农场类型：{Game1.GetFarmTypeKey()}");
        builder.AppendLine($"- 日期：{Game1.Date.Localize()}（第 {Game1.Date.Year} 年，{Game1.Date.SeasonKey} {Game1.Date.DayOfMonth} 日）；时间：{Game1.getTimeOfDayString(Game1.timeOfDay)}");
        builder.AppendLine($"- 玩家地点：{SafeLocationName(Game1.currentLocation)}；村民地点：{SafeLocationName(npc.currentLocation)}");
        bool isFestivalDay = Utility.isFestivalDay() || Utility.IsPassiveFestivalDay();
        builder.AppendLine($"- 当地天气：{DescribeWeather(Game1.currentLocation)}；今天是否节日：{YesNo(isFestivalDay)}；当前是否正在节日活动：{YesNo(Game1.isFestival())}");
        var worldFacts = new Dictionary<string, string>
        {
            ["玩家"] = $"{player.Name}；{player.farmName.Value}农场",
            ["天气/节日"] = $"{DescribeWeather(Game1.currentLocation)}；节日{YesNo(isFestivalDay)}；活动中{YesNo(Game1.isFestival())}",
            ["配偶/室友"] = string.IsNullOrWhiteSpace(player.spouse) ? "无" : player.spouse,
        };
        string sceneSnapshot = BuildSceneSnapshot(npc);
        builder.AppendLine("【NPC 当前场景快照】");
        builder.AppendLine(sceneSnapshot);

        builder.AppendLine("【玩家与该村民的关系】");
        builder.AppendLine($"- 好感点数：{points}；红心：{hearts}；状态：{DescribeRelationship(friendship)}");
        builder.AppendLine($"- 今天是否完成过原版日常交谈：{YesNo(friendship?.TalkedToToday ?? false)}；本周送礼次数：{friendship?.GiftsThisWeek ?? 0}");
        if (friendship?.LastGiftDate is not null && friendship.LastGiftDate.Year > 0)
            builder.AppendLine($"- 最近送礼日期：{friendship.LastGiftDate.Localize()}");

        builder.AppendLine("【村民基础性格资料】");
        builder.AppendLine($"- 年龄段：{DescribeAge(npc.Age)}；礼貌倾向：{DescribeManners(npc.Manners)}；社交倾向：{DescribeSocial(npc.SocialAnxiety)}；心态：{DescribeOptimism(npc.Optimism)}");
        builder.AppendLine($"- 生日：{npc.Birthday_Season} {npc.Birthday_Day} 日；可正常社交：{YesNo(npc.CanSocialize)}");

        AppendCombatState(builder, npc, worldFacts);

        builder.AppendLine("【玩家当前发展】");
        builder.AppendLine($"- 金钱：{player.Money}g；房屋升级：{player.HouseUpgradeLevel}；矿井最深：{player.deepestMineLevel} 层；到达矿底次数：{player.timesReachedMineBottom}");
        builder.AppendLine($"- 技能：耕种 {player.FarmingLevel}，采矿 {player.MiningLevel}，觅食 {player.ForagingLevel}，钓鱼 {player.FishingLevel}，战斗 {player.CombatLevel}");
        builder.AppendLine($"- 配偶/室友：{(string.IsNullOrWhiteSpace(player.spouse) ? "无" : player.spouse)}");
        builder.AppendLine($"- 社区中心完成：{YesNo(player.hasCompletedCommunityCenter())}；Joja 会员路线：{YesNo(player.mailReceived.Contains("JojaMember"))}；电影院建成：{YesNo(player.theaterBuildDate >= 0)}");
        builder.AppendLine($"- 关键能力/物品：下水道钥匙={YesNo(player.hasRustyKey)}，骷髅钥匙={YesNo(player.hasSkullKey)}，赌场会员卡={YesNo(player.hasClubCard)}，矮人语={YesNo(player.canUnderstandDwarves)}，放大镜={YesNo(player.hasMagnifyingGlass)}，黑暗护符={YesNo(player.hasDarkTalisman)}，魔法墨水={YesNo(player.hasMagicInk)}，城镇钥匙={YesNo(player.HasTownKey)}");
        builder.AppendLine($"- 姜岛船已修复：{YesNo(player.mailReceived.Contains("willyBoatFixed"))}");

        AppendQuests(builder, player, worldFacts);
        AppendSeenEvents(builder, player);
        AppendClosestRelationships(builder, player, npc.Name);

        builder.AppendLine("【扮演规则】");
        builder.AppendLine(RidgesideVillageCompatibility.BuildIdentityPrompt(npc.displayName, npc.Name));
        builder.AppendLine(new LocalizedPromptAssets().Read("game-role.txt"));
        return new NpcGameSnapshot(npc.Name, npc.displayName, L.WithOutputLanguage(builder.ToString()), SceneSnapshot: sceneSnapshot,
            WorldFacts: ConversationContextBudget.WorldFacts(worldFacts));
    }

    private static string BuildSceneSnapshot(NPC npc)
    {
        GameLocation? location = npc.currentLocation;
        if (location is null)
            return "- 地点：未知\n- 附近植物：无";

        Point center = npc.TilePoint;
        var lines = new List<string>(2)
        {
            $"- 地点：{SafeLocationName(location)}",
        };

        string[] nearbyPlants = location.terrainFeatures.Pairs
            .Where(pair => IsNear(pair.Key, center, 5))
            .Select(pair => DescribePlant(pair.Value))
            .Where(value => value.Length > 0)
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(8)
            .Select(group => group.Count() > 1 ? $"{group.Key} x{group.Count()}" : group.Key)
            .ToArray();
        string[] nearbyLargePlants = location.largeTerrainFeatures
            .Where(feature => IsNear(feature.Tile, center, 6))
            .Select(DescribeLargePlant)
            .Where(value => value.Length > 0)
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(8)
            .Select(group => group.Count() > 1 ? $"{group.Key} x{group.Count()}" : group.Key)
            .ToArray();
        string[] plants = nearbyPlants
            .Concat(nearbyLargePlants)
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(8)
            .Select(group => group.Count() > 1 ? $"{group.Key} x{group.Count()}" : group.Key)
            .ToArray();
        if (plants.Length > 0)
            lines.Add("- 附近植物：" + string.Join("、", plants));
        else
            lines.Add("- 附近植物：无");
        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsNear(Vector2 tile, Point center, int radius)
        => Math.Abs((int)tile.X - center.X) <= radius
           && Math.Abs((int)tile.Y - center.Y) <= radius;

    private static bool IsNear(Point tile, Point center, int radius)
        => Math.Abs(tile.X - center.X) <= radius
           && Math.Abs(tile.Y - center.Y) <= radius;

    private static string DescribeObject(StardewValley.Object? value)
    {
        if (value is null)
            return string.Empty;
        try
        {
            string name = Clean(value.DisplayName);
            return name.Length == 0 ? Clean(value.Name) : name;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DescribePlant(TerrainFeature? feature)
        => feature switch
        {
            Tree tree when tree.stump.Value => "树桩",
            Tree => "树",
            FruitTree => "果树",
            HoeDirt dirt when dirt.crop is not null => DescribeHoeDirt(dirt),
            Grass => "草",
            _ => string.Empty,
        };

    private static string DescribeLargePlant(LargeTerrainFeature? feature)
        => feature?.GetType().Name switch
        {
            "Bush" => "灌木",
            _ => string.Empty,
        };

    private static string DescribeTerrain(TerrainFeature? feature)
        => feature switch
        {
            Tree tree when tree.stump.Value => "树桩",
            Tree => "树",
            FruitTree => "果树",
            HoeDirt dirt => DescribeHoeDirt(dirt),
            Grass => "草地",
            Flooring => "地板",
            _ => feature is null ? string.Empty : DescribeTypeName(feature.GetType().Name),
        };

    private static string DescribeHoeDirt(HoeDirt dirt)
    {
        Crop? crop = dirt.crop;
        if (crop is null)
            return "空耕地（未种植）";

        string cropName = ResolveCropName(crop);
        string growthState = crop.dead.Value
            ? "已枯萎"
            : crop.fullyGrown.Value
                ? "已成熟"
                : "正在生长";
        return $"{cropName}（{growthState}）";
    }

    private static string ResolveCropName(Crop crop)
    {
        try
        {
            string? harvestItemId = crop.GetData()?.HarvestItemId;
            if (string.IsNullOrWhiteSpace(harvestItemId))
                harvestItemId = crop.indexOfHarvest.Value;

            if (!string.IsNullOrWhiteSpace(harvestItemId))
            {
                string qualifiedId = harvestItemId.StartsWith("(", StringComparison.Ordinal)
                    ? harvestItemId
                    : "(O)" + harvestItemId;
                string displayName = Clean(ItemRegistry.GetData(qualifiedId)?.DisplayName);
                if (displayName.Length > 0)
                    return displayName;
            }
        }
        catch
        {
            // A modded or partially loaded crop must not cause the whole snapshot to fail.
        }

        return "未知作物（已种植）";
    }

    private static string DescribeLargeTerrain(LargeTerrainFeature? feature)
        => feature is null ? string.Empty : DescribeTypeName(feature.GetType().Name);

    private static string DescribeTypeName(string typeName)
        => typeName switch
        {
            "Bush" => "灌木",
            "Grass" => "草地",
            "Tree" => "树",
            "FruitTree" => "果树",
            _ => typeName.Replace("LargeTerrainFeature", "", StringComparison.Ordinal),
        };

    private static string DescribeMovement(NPC npc)
    {
        if (npc.temporaryController is not null)
            return "正在执行临时路径或跟随动作";
        if (npc.controller is not null)
            return "正在执行日程路径";
        return "当前没有路径控制，可能停留或自由行动";
    }

    private static string DescribeFacing(int facingDirection)
        => facingDirection switch
        {
            0 => "上",
            1 => "右",
            2 => "下",
            3 => "左",
            _ => "未知",
        };

    private void AppendCombatState(StringBuilder builder, NPC npc, Dictionary<string, string> facts)
    {
        if (combatState is null)
            return;

        NpcCombatState state = combatState.GetOrCreate(npc.Name);
        NpcWeaponSnapshot? weaponSnapshot = combatState.GetWeapon(npc.Name);
        string weapon = weaponSnapshot?.DisplayName ?? "银河剑";
        builder.AppendLine($"- 战斗装备：{weapon}；NPC 生命值：{state.CurrentHealth}/{state.MaxHealth}");
        if (state.IsHospitalized)
        {
            int remaining = Math.Max(0, state.HospitalReleaseDay - Game1.Date.TotalDays);
            facts["医院"] = $"住院至第{state.HospitalReleaseDay}天；移动/护卫禁用";
            builder.AppendLine($"- 医院状态：住院至第 {state.HospitalReleaseDay} 天（还剩 {remaining} 天）；移动和下矿护卫工具当前不可用。");
        }
        else if (state.DefeatCount > 0)
        {
            facts["战斗"] = $"矿井败{state.DefeatCount}次；{state.LastDefeatDate}";
            builder.AppendLine($"- 近期战斗经历：在矿井被击败 {state.DefeatCount} 次；上次事件日期：{state.LastDefeatDate}");
        }
    }

    private void AppendQuests(StringBuilder builder, Farmer player, Dictionary<string, string> facts)
    {
        int limit = Math.Max(0, config.MaxQuestsInContext);
        if (limit == 0)
        {
            facts["任务"] = "未提供";
            builder.AppendLine("- 当前任务：无");
            return;
        }

        List<string> quests = new();
        foreach (Quest quest in player.questLog)
        {
            if (quest.completed.Value || quest.destroy.Value || quest.IsHidden())
                continue;

            string name = Clean(quest.GetName());
            string objective = Clean(string.Join("；", quest.GetObjectiveDescriptions()));
            if (string.IsNullOrWhiteSpace(objective))
                objective = Clean(quest.currentObjective);

            quests.Add(string.IsNullOrWhiteSpace(objective) ? name : $"{name}（{objective}）");
            if (quests.Count >= limit)
                break;
        }

        facts["任务"] = quests.Count == 0 ? "无" : string.Join("；", quests);
        builder.AppendLine($"- 当前任务：{facts["任务"]}");
    }

    private void AppendSeenEvents(StringBuilder builder, Farmer player)
    {
        int limit = Math.Max(0, config.MaxSeenEventIdsInContext);
        if (limit == 0)
            return;

        string[] ids = player.eventsSeen
            .Select(id => id?.ToString() ?? "")
            .Where(id => id.Length > 0)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

        builder.AppendLine($"- 已观看事件 ID（仅用于避免剧情错位）：{(ids.Length == 0 ? "无" : string.Join(", ", ids))}");
    }

    private static void AppendClosestRelationships(StringBuilder builder, Farmer player, string targetName)
    {
        string[] top = player.friendshipData.Pairs
            .Where(pair => !pair.Key.Equals(targetName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Value.Points)
            .Take(8)
            .Select(pair => $"{pair.Key} {pair.Value.Points / 250}心/{DescribeRelationship(pair.Value)}")
            .ToArray();

        builder.AppendLine($"- 玩家其他重要关系：{(top.Length == 0 ? "无" : string.Join("；", top))}");
    }

    public static string DescribeRelationship(Friendship? friendship)
    {
        if (friendship is null)
            return "普通相识";
        if (friendship.IsDivorced())
            return "已离婚";
        if (friendship.IsRoommate())
            return "室友婚姻";
        if (friendship.IsMarried())
            return "已婚";
        if (friendship.IsEngaged())
            return "订婚";
        if (friendship.IsDating())
            return "恋爱中";
        return "朋友/相识";
    }

    private static string DescribeWeather(GameLocation location)
    {
        if (location.IsGreenRainingHere())
            return "绿雨";
        if (location.IsLightningHere())
            return "雷雨";
        if (location.IsSnowingHere())
            return "下雪";
        if (location.IsRainingHere())
            return "下雨";
        if (location.IsDebrisWeatherHere())
            return "大风";
        return "晴朗/无特殊天气";
    }

    private static string SafeLocationName(GameLocation? location)
        => location is null ? "未知" : (string.IsNullOrWhiteSpace(location.DisplayName) ? location.NameOrUniqueName : location.DisplayName);

    private static string YesNo(bool value) => value ? "是" : "否";

    private static string DescribeAge(int value) => value switch
    {
        NPC.child => "儿童",
        NPC.teen => "青少年",
        _ => "成年人"
    };

    private static string DescribeManners(int value) => value switch
    {
        NPC.polite => "礼貌",
        NPC.rude => "较直接/粗鲁",
        _ => "中性"
    };

    private static string DescribeSocial(int value) => value == NPC.shy ? "害羞" : "外向";

    private static string DescribeOptimism(int value) => value == NPC.negative ? "偏消极" : "偏积极";

    private static string Clean(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? ""
            : text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

public sealed record NpcGameSnapshot(
    string NpcName,
    string NpcDisplayName,
    string SystemPrompt,
    string NarrativeContext = "",
    IReadOnlyList<string>? RecentSessionFacts = null,
    string SceneSnapshot = "",
    string WorldFacts = "");
