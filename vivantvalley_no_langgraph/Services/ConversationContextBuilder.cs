namespace VivantValley.Services;

/// <summary>Filters manual-chat facts without changing game-side action checks or other scenes.</summary>
public static class ConversationContextBuilder
{
    public static IReadOnlyList<string> AvailableWorldFactFields { get; } = Array.AsReadOnly(new[]
    {
        "玩家", "天气/节日", "配偶/室友", "技能", "进度", "能力", "姜岛船", "任务", "医院", "战斗",
    });

    public static string BuildWorldFacts(string? context, IEnumerable<string> enabledFields)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in (context ?? string.Empty).Split(
                     new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = line.IndexOf('：');
            if (colon < 0) continue;
            string label = line[..colon].TrimStart('-', ' ');
            string key = label switch
            {
                "当地天气" => "天气/节日",
                "当前任务" => "任务",
                "医院状态" => "医院",
                "近期战斗经历" => "战斗",
                "社区中心完成" => "进度",
                "关键能力/物品" => "能力",
                "姜岛船已修复" => "姜岛船",
                _ => label,
            };
            facts[key] = (label == "社区中心完成" ? "社区中心" : "") + line[(colon + 1)..].Trim();
        }
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in enabledFields)
        {
            if (facts.TryGetValue(key, out string? value))
                selected[key] = value;
        }
        return ConversationContextBudget.WorldFacts(selected);
    }
}
