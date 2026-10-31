namespace VivantValley.Services;

/// <summary>Allocates the existing context budgets before rendering text.</summary>
public static class ConversationContextBudget
{
    public const int WorldCharacters = 300;
    public const int StoryCharacters = 250;

    // Every present category gets space. Long quest prose cannot evict spouse,
    // hospitalization or world unlocks. Compact flags are supplied by the game.
    public static string WorldFacts(IReadOnlyDictionary<string, string> facts)
    {
        var entries = facts.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToArray();
        if (entries.Length == 0) return string.Empty;
        int remaining = WorldCharacters - entries.Sum(pair => pair.Key.Length + 1) - entries.Length + 1;
        var sizes = entries.Select(pair => Math.Min(pair.Value.Length, 12)).ToArray();
        remaining -= sizes.Sum();
        // Preserve short authoritative fields first; spend the remainder on
        // descriptive fields only after the capability flags fit.
        foreach (string key in new[] { "配偶/室友", "医院", "能力", "进度", "姜岛船", "天气/节日", "技能", "战斗", "玩家", "任务" })
        {
            int index = Array.FindIndex(entries, entry => entry.Key == key);
            if (index < 0 || remaining <= 0) continue;
            int addition = Math.Min(remaining, Math.Max(0, Math.Min(entries[index].Value.Length, 80) - sizes[index]));
            sizes[index] += addition;
            remaining -= addition;
        }
        return string.Join('\n', entries.Select((entry, index) => entry.Key + "：" + Clip(entry.Value, sizes[index])));
    }

    // Compatibility for snapshots supplied by older callers. Normal gameplay
    // supplies the structured fields directly from GameContextBuilder.
    public static string WorldFactsFromLegacy(string? context)
    {
        var facts = new Dictionary<string, string>();
        var labels = new Dictionary<string, string>
        {
            ["当地天气"] = "天气/节日", ["当前任务"] = "任务", ["医院状态"] = "医院",
            ["近期战斗经历"] = "战斗", ["玩家"] = "玩家", ["技能"] = "技能",
            ["配偶/室友"] = "配偶/室友", ["社区中心完成"] = "进度",
            ["关键能力/物品"] = "能力", ["姜岛船已修复"] = "姜岛船",
        };
        foreach (string line in Lines(context))
        {
            int colon = line.IndexOf('：');
            if (colon < 0 || !labels.TryGetValue(line[..colon].TrimStart('-', ' '), out string? key)) continue;
            facts[key] = (key == "进度" ? "社区中心" : "") + line[(colon + 1)..];
        }
        return WorldFacts(facts);
    }

    public static string StoryFacts(string? narrative)
    {
        var episodes = new List<List<string>>();
        foreach (string line in Lines(narrative))
        {
            if (line.StartsWith("[剧情：", StringComparison.Ordinal)) episodes.Add(new List<string> { line });
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (episodes.Count == 0) episodes.Add(new List<string>());
                episodes[^1].Add(line);
            }
        }
        var result = new List<string>();
        int remaining = StoryCharacters;
        // Select a whole recent episode's identity and actual player choice
        // before any ambient lines. Never cut off a choice with TakeLast(6).
        foreach (List<string> episode in episodes.AsEnumerable().Reverse())
        {
            string? header = episode.FirstOrDefault(line => line.StartsWith("[剧情：", StringComparison.Ordinal));
            string[] choices = episode.Where(line => line.StartsWith("- 玩家选择", StringComparison.Ordinal)).ToArray();
            var selected = new List<string>();
            if (header is not null) selected.Add(Clip(header, 64));
            selected.AddRange(choices.Select(line => Clip(line, 110)));
            if (selected.Count == 0) selected.AddRange(episode.TakeLast(2).Select(line => Clip(line, 100)));
            foreach (string line in selected)
            {
                int available = remaining - (result.Count > 0 ? 1 : 0);
                if (available < 12) break;
                string bounded = Clip(line, available);
                result.Add(bounded);
                remaining -= bounded.Length + (result.Count > 1 ? 1 : 0);
            }
            foreach (string line in episode.Except(selected).Where(line => line.StartsWith("- ", StringComparison.Ordinal)
                         && !line.StartsWith("- 玩家选择", StringComparison.Ordinal)).TakeLast(2))
            {
                if (line.Length + 1 > remaining) continue;
                result.Add(line);
                remaining -= line.Length + 1;
            }
            if (remaining < 40) break;
        }
        return string.Join('\n', result);
    }

    private static IEnumerable<string> Lines(string? value)
        => (value ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string Clip(string value, int maximum)
        => value.Length <= maximum ? value : maximum <= 1 ? "" : value[..(maximum - 1)] + "…";
}
