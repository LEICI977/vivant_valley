namespace VivantValley.Services;

/// <summary>
/// Extracts the small, stable part of the game snapshot that is useful to
/// proactive social prompts. The full GameContext remains available to game
/// code, but is never copied into these provider messages.
/// </summary>
internal sealed record SocialPromptContext(
    string CurrentFacts,
    string CurrentGameTime,
    string SceneLocation,
    IReadOnlyList<string> NearbyPlants,
    IReadOnlyDictionary<string, object?> Relationship,
    string RelationshipText,
    string RoleRules)
{
    public static SocialPromptContext Parse(string? gameContext)
    {
        string text = gameContext ?? string.Empty;
        string scene = ExtractSection(text, "【NPC 当前场景快照】");
        string currentFacts = Limit(
            ExtractSection(text, "【此刻的游戏事实（优先级高于模型常识）】"),
            1800,
            string.Empty);
        if (currentFacts.Length == 0)
            currentFacts = $"地点：{FindLineValue(scene, "- 地点：")}";
        string currentGameTime = FindLineValue(text, "- 日期：");
        string location = FindLineValue(scene, "- 地点：");
        if (location.Length == 0)
            location = FindLineValue(text, "- 村民地点：");

        string plantsValue = FindLineValue(scene, "- 附近植物：");
        IReadOnlyList<string> plants = plantsValue.Equals("无", StringComparison.Ordinal)
            ? Array.Empty<string>()
            : plantsValue
                .Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray();

        string relationshipText = ExtractSection(text, "【玩家与该村民的关系】");
        IReadOnlyDictionary<string, object?> relationship = ParseRelationship(relationshipText);
        string roleRules = Limit(
            ExtractSection(text, "【扮演规则】"),
            2200,
            string.Empty);

        return new SocialPromptContext(
            currentFacts,
            Limit(currentGameTime, 120, "未提供"),
            Limit(location, 120, "未知"),
            plants,
            relationship,
            Limit(relationshipText, 900, "未提供"),
            roleRules);
    }

    public string ToCompactGameContext()
        => "【此刻的游戏事实（优先级高于模型常识）】\n"
           + CurrentFacts + "\n"
           + "【NPC 当前场景快照】\n"
           + $"- 地点：{SceneLocation}\n"
           + $"- 附近植物：{(NearbyPlants.Count == 0 ? "无" : string.Join("、", NearbyPlants))}\n"
           + "【玩家与该村民的关系】\n"
           + RelationshipText + "\n"
           + "【扮演规则】\n"
           + RoleRules;

    private static IReadOnlyDictionary<string, object?> ParseRelationship(string text)
    {
        int points = ParseIntAfter(text, "好感点数：");
        int hearts = ParseIntAfter(text, "红心：");
        int gifts = ParseIntAfter(text, "本周送礼次数：");
        string status = ExtractDelimitedValue(text, "状态：", '；');
        bool talkedToday = text.Contains("今天是否完成过原版日常交谈：是", StringComparison.Ordinal);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["points"] = points,
            ["hearts"] = hearts,
            ["status"] = status.Length == 0 ? "未提供" : status,
            ["talked_today"] = talkedToday,
            ["gifts_this_week"] = gifts,
        };
    }

    private static int ParseIntAfter(string text, string prefix)
    {
        string value = ExtractDelimitedValue(text, prefix, '；');
        return int.TryParse(value, out int parsed) ? parsed : 0;
    }

    private static string ExtractDelimitedValue(string text, string prefix, char delimiter)
    {
        int index = text.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0)
            return string.Empty;
        string value = text[(index + prefix.Length)..];
        int newline = value.IndexOfAny(new[] { '\r', '\n' });
        if (newline >= 0) value = value[..newline];
        int end = value.IndexOf(delimiter);
        if (end >= 0)
            value = value[..end];
        return value.Trim().TrimEnd('。', '.');
    }

    private static string ExtractSection(string text, string header)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(header))
            return string.Empty;

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None);
        int start = Array.FindIndex(lines, line => line.Trim().Equals(header, StringComparison.Ordinal));
        if (start < 0)
            return string.Empty;

        var selected = new List<string>();
        for (int index = start + 1; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.StartsWith("【", StringComparison.Ordinal)
                && line.EndsWith("】", StringComparison.Ordinal))
            {
                break;
            }

            if (line.Length > 0)
                selected.Add(line);
        }

        return string.Join("\n", selected);
    }

    private static string FindLineValue(string text, string prefix)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        foreach (string rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n')
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..].Trim();
        }

        return string.Empty;
    }

    private static string Limit(string? value, int maximumCharacters, string fallback)
    {
        string clean = (value ?? string.Empty).Replace('\r', ' ').Trim();
        if (clean.Length == 0)
            return fallback;
        return clean.Length <= maximumCharacters
            ? clean
            : clean[..maximumCharacters] + "…";
    }
}
