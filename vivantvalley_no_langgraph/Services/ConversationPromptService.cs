using System.Text;
using System.Text.Json;

namespace VivantValley.Services;

/// <summary>Loads editable manual-chat text once per turn; defaults share the same asset files.</summary>
public sealed class ConversationPromptService
{
    private readonly string? directory;
    private readonly LocalizedPromptAssets assets;
    private readonly Action<string>? log;

    public ConversationPromptService(string? modDirectory = null, Action<string>? log = null)
    {
        directory = modDirectory is null ? null : Path.Combine(modDirectory, "assets", "prompts");
        this.log = log;
        assets = new LocalizedPromptAssets(modDirectory, log);
    }

    public ConversationPromptSettings Load()
    {
        using var language = L.BeginRequest();
        return new(assets.Read("conversation.txt"), assets.Read("conversation-final.txt"),
            Read("conversation-context.json", ParseFields));
    }

    public static string BuildSystemPrompt(string rules, string identity, NpcPersonalitySettings? personality = null)
    {
        string adjustments = personality?.BuildPrompt() ?? string.Empty;
        bool hasIdentity = rules.Contains("{npc_identity}", StringComparison.Ordinal);
        bool hasPersonality = rules.Contains("{player_personality}", StringComparison.Ordinal);
        // A single pass leaves placeholder-looking player text untouched.
        string prompt = System.Text.RegularExpressions.Regex.Replace(rules,
            @"\{npc_identity\}|\{player_personality\}",
            match => match.Value == "{npc_identity}" ? identity : adjustments);
        if (!hasIdentity) prompt = identity.Trim() + "\n\n" + prompt.Trim();
        if (!hasPersonality && adjustments.Length > 0)
        {
            // Preserve existing customized templates, placing preferences after their opening.
            int openingEnd = prompt.IndexOf('\n');
            if (openingEnd < 0) prompt += "\n\n" + adjustments;
            else prompt = prompt.Insert(openingEnd, "\n\n" + adjustments + "\n");
        }
        return prompt.Trim() + "\n\n" + L.T("conversation.player-language");
    }

    private T Read<T>(string name, Func<string, T> parse)
    {
        if (directory is not null)
        {
            try
            {
                return parse(File.ReadAllText(Path.Combine(directory, name), Encoding.UTF8));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                log?.Invoke($"普通对话配置 {name} 无法读取，使用内置默认内容：{ex.Message}");
            }
        }

        using Stream stream = typeof(ConversationPromptService).Assembly.GetManifestResourceStream(
            "VivantValley.Prompts." + name)
            ?? throw new InvalidOperationException($"Missing embedded conversation asset: {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return parse(reader.ReadToEnd());
    }

    private static string[] ParseFields(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("WorldFactFields", out JsonElement fields)
            || fields.ValueKind != JsonValueKind.Array)
            throw new FormatException("WorldFactFields 必须是字符串数组；空数组表示不发送额外游戏事实。");

        var names = new List<string>();
        foreach (JsonElement field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.String
                || !ConversationContextBuilder.AvailableWorldFactFields.Contains(field.GetString(), StringComparer.Ordinal))
                throw new FormatException("WorldFactFields 包含未知字段，请参阅 assets/prompts/README.md。");
            names.Add(field.GetString()!);
        }
        return names.Distinct(StringComparer.Ordinal).ToArray();
    }
}

public sealed record ConversationPromptSettings(string SystemRules, string FinalRules, string[] WorldFactFields);
