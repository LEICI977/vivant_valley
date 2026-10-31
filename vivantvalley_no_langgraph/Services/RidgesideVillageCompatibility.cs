namespace VivantValley.Services;

/// <summary>Small compatibility helpers for Ridgeside Village's custom NPC roster.</summary>
internal static class RidgesideVillageCompatibility
{
    private static readonly HashSet<string> NpcNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Acorn", "Aguar", "Alissa", "Anton", "Ariah", "Althea", "Belinda", "Blair", "Bliss", "Bert",
        "Bryle", "Carmen", "Corine", "Daia", "Ezekiel", "Faye", "Flor", "Freddie",
        "Helen", "Ian", "Irene", "Jeric", "Jio", "Keahi", "Kenneth", "Kiarra",
        "Kimpoi", "Kiwi", "Lenny", "Lola", "Lorraine", "Lorenzo", "Louie", "Maddie",
        "Maive", "Malaya", "Naomi", "Nadaline", "Olga", "Paula", "Philip", "Pika",
        "Pipo", "Raeriyala", "RelicSpirit", "Richard", "Sari", "Sean", "Shanice",
        "Shiro", "Sonny", "Torts", "TreehouseGirl", "Trinnie", "Undreya", "Ysabelle",
        "Yuuma", "Zachary", "Zayne", "June",
    };

    private static readonly string[] GiftTemplateKeys =
    {
        "rsv_ridge_apple_cake",
        "rsv_ridgeside_clementine",
        "rsv_sierra_wintergreen",
        "rsv_mountain_hokkaido",
        "rsv_highland_blueberry_pie",
        "rsv_fried_fish_ridge",
        "rsv_winter_night_feast",
        "rsv_summit_iced_tea",
        "rsv_ridge_azorean_flower",
        "rsv_matcha_latte",
        "rsv_clementine_cake",
        "rsv_mountain_arugula",
    };

    public static bool IsRidgesideNpc(string? npcName)
        => !string.IsNullOrWhiteSpace(npcName) && NpcNames.Contains(npcName);

    public static IReadOnlyCollection<string> SocialNpcNames => NpcNames;

    public static IReadOnlyList<string> GetGiftTemplateKeys() => GiftTemplateKeys;

    public static string BuildIdentityPrompt(string displayName, string npcName)
    {
        string setting = IsRidgesideNpc(npcName)
            ? L.T("text.9c8e82e1c999")
            : L.T("text.98912c30b587");
        return L.F("text.54a7ffb327df", $"你现在就是{setting}中的 {displayName}（内部名 {npcName}）。");
    }

    /// <summary>Opening clause used by the editable manual-chat prompt.</summary>
    public static string BuildConversationIdentityPrompt(string displayName, string npcName)
    {
        string setting = IsRidgesideNpc(npcName)
            ? L.T("conversation.setting.ridgeside")
            : L.T("text.98912c30b587");
        return L.F("conversation.identity", $"你是{setting}的{displayName}，");
    }

    public static bool IsRidgesideLocation(string? locationName)
        => !string.IsNullOrWhiteSpace(locationName)
           && locationName.StartsWith("Custom_Ridgeside_", StringComparison.OrdinalIgnoreCase);
}
