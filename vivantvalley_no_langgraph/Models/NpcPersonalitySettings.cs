using System.Text.Json;
using VivantValley.Services;

namespace VivantValley;

/// <summary>Player-owned preferences, stored separately from generated memories.</summary>
public sealed class NpcPersonalitySettings
{
    public const int FieldLimit = 240;
    public string Tone { get; set; } = string.Empty;
    public string Traits { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Tone) && string.IsNullOrWhiteSpace(Traits)
        && string.IsNullOrWhiteSpace(Expression);

    public static string StorageKey(string npcName)
        => "firstmod.StardewAIMemories/npc-personality/" + Uri.EscapeDataString(npcName);

    public NpcPersonalitySettings Normalize() => new()
    {
        Tone = Clean(Tone), Traits = Clean(Traits), Expression = Clean(Expression)
    };

    private static string Clean(string? value)
        => new SingleLineTextBuffer(FieldLimit) { Text = value ?? string.Empty }.Text.Trim();

    public static NpcPersonalitySettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return (JsonSerializer.Deserialize<NpcPersonalitySettings>(json) ?? new()).Normalize(); }
        catch (JsonException) { return new(); }
    }

    public string Serialize() => JsonSerializer.Serialize(Normalize());

    public string BuildPrompt()
    {
        NpcPersonalitySettings value = Normalize();
        if (value.IsEmpty) return string.Empty;
        string separator = L.IsChinese ? "：" : ": ";
        var lines = new List<string> { L.T("personality.prompt.heading"), L.T("personality.prompt.intro") };
        if (value.Tone.Length > 0) lines.Add(L.T("personality.tone") + separator + value.Tone);
        if (value.Traits.Length > 0) lines.Add(L.T("personality.traits") + separator + value.Traits);
        if (value.Expression.Length > 0) lines.Add(L.T("personality.expression") + separator + value.Expression);
        return string.Join("\n", lines);
    }
}
