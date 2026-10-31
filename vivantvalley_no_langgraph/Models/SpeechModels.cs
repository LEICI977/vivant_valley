using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VivantValley;

public sealed class NpcVoiceOption
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("npc_name")] public string NpcName { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("is_default")] public bool IsDefault { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("has_preview_en"), JsonConverter(typeof(PreviewAvailabilityConverter))] public bool HasEnglishPreview { get; set; }
    [JsonPropertyName("has_preview"), JsonConverter(typeof(PreviewAvailabilityConverter))] public bool HasPreview { get; set; }
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
}

// Older servers can send null for previews whose validity stamp was cleared.
// Scope this compatibility rule to preview flags; reject other invalid JSON types.
public sealed class PreviewAvailabilityConverter : JsonConverter<bool>
{
    public override bool HandleNull => true;
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Null or JsonTokenType.False => false,
            JsonTokenType.True => true,
            _ => throw new JsonException("Invalid preview availability flag."),
        };
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}

public sealed class SpeechCatalog
{
    [JsonPropertyName("speech_languages")] public List<string> SpeechLanguages { get; set; } = new();
    [JsonIgnore] public bool Loaded { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("text_price_micros_per_1m")] public long TextPriceMicros { get; set; }
    [JsonPropertyName("speech_price_micros_per_1m")] public long SpeechPriceMicros { get; set; }
    [JsonPropertyName("price_version")] public string PriceVersion { get; set; } = "";
    [JsonPropertyName("voices")] public List<NpcVoiceOption> Voices { get; set; } = new();

    public IReadOnlyList<NpcVoiceOption> ForNpc(string? npc)
        => Voices.Where(v => v.Enabled && v.NpcName == npc).OrderBy(v => v.SortOrder).ThenBy(v => v.Name).ToArray();
    public NpcVoiceOption? Resolve(string npc, string? selected)
    {
        var options = ForNpc(npc);
        return options.FirstOrDefault(v => v.Id == selected) ?? options.FirstOrDefault(v => v.IsDefault) ?? options.FirstOrDefault();
    }
    public string PriceText(bool withVoice)
        => ((withVoice ? SpeechPriceMicros : TextPriceMicros) / 1000000m).ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>Frozen when the player submits a line; the server owns the corresponding price snapshot.</summary>
public sealed record SpeechTurn(string Id, string NpcName, string Token, bool Enabled, string? VoiceOptionId, string? Language = null);

public static class NpcVoicePreference
{
    public static string StorageKey(string npcName)
        => "firstmod.StardewAIMemories/npc-voice/" + Uri.EscapeDataString(npcName);
}
