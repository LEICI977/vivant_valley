using VivantValley;
using VivantValley.Services;

internal static class PersonalityTests
{
    public static void Run()
    {
        Check(NpcPersonalitySettings.Parse(null).IsEmpty && NpcPersonalitySettings.Parse("{broken").IsEmpty,
            "Missing/corrupt preferences must preserve default characterization.");
        Check(NpcPersonalitySettings.Parse("{\"Tone\":null}").IsEmpty, "Null fields must be safe.");
        var preferences = new NpcPersonalitySettings { Tone = "  Direct\nplease  ", Traits = "Independent", Expression = "Brief" };
        var restored = NpcPersonalitySettings.Parse(preferences.Serialize());
        Check(restored.Tone == "Direct please" && restored.Traits == "Independent" && restored.Expression == "Brief",
            "Saved preferences did not survive a serialization round trip.");
        Check(NpcPersonalitySettings.StorageKey("Abigail") != NpcPersonalitySettings.StorageKey("Leah")
            && NpcPersonalitySettings.StorageKey("a/b") != NpcPersonalitySettings.StorageKey("a%2Fb"),
            "NPC storage keys collide.");
        string longText = new string('a',239) + "😀";
        Check(new NpcPersonalitySettings {Tone=longText}.Normalize().Tone == new string('a',239),
            "Field limit split a Unicode character.");
        foreach (string locale in new[] { "zh", "en" })
        {
            using var language = L.BeginRequest(locale);
            var rules = new ConversationPromptService().Load();
            string identity = locale == "zh" ? "你是《星露谷物语》的阿比盖尔，" : "You are Abigail from Stardew Valley. ";
            string empty = ConversationPromptService.BuildSystemPrompt(rules.SystemRules,identity);
            Check(!empty.Contains("{player_personality}") && !empty.Contains(L.T("personality.prompt.heading")),
                "Blank preferences leaked placeholders or added a fake persona.");
            string prompt = ConversationPromptService.BuildSystemPrompt(rules.SystemRules,identity,restored);
            int section = prompt.IndexOf(L.T("personality.prompt.heading"),StringComparison.Ordinal);
            int facts = prompt.IndexOf(locale == "zh" ? "实时事实" : "Live game facts",StringComparison.Ordinal);
            Check(section > identity.Length && section < facts && prompt.Contains("Independent") && prompt.Contains("Brief"),
                "Personality must follow the opening and precede factual rules in both languages.");
            Check(prompt == ConversationPromptService.BuildSystemPrompt(rules.SystemRules,identity,restored),
                "Unchanged preferences must keep a byte-stable system prefix.");
            var literal = new NpcPersonalitySettings {Tone="{npc_identity} {player_personality}"};
            Check(ConversationPromptService.BuildSystemPrompt(rules.SystemRules,identity,literal).Contains(literal.Tone),
                "Player text was interpreted as template syntax.");
            string legacy = ConversationPromptService.BuildSystemPrompt("Legacy custom rules",identity,restored);
            Check(legacy.StartsWith(identity.Trim()) && legacy.Contains("Independent") && legacy.Contains("Legacy custom rules"),
                "Legacy templates dropped player settings or rules.");
            string partial = new NpcPersonalitySettings {Tone="Direct"}.BuildPrompt();
            Check(!partial.Contains(L.T("personality.traits")) && !partial.Contains(L.T("personality.hint.tone")),
                "Blank fields or examples were sent to the model.");
        }
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
