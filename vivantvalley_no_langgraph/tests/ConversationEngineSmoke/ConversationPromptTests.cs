using System.Text;
using VivantValley.Services;

internal static class ConversationPromptTests
{
    public static void Run()
    {
        ConversationPromptSettings defaults = new ConversationPromptService().Load();
        const string facts = "玩家：Robin\n天气/节日：晴\n配偶/室友：无\n技能：耕10\n进度：社区中心是\n能力：骷髅钥匙是\n姜岛船：是\n任务：罗宾丢失的斧子\n医院：住院\n战斗：装备";
        string minimal = ConversationContextBuilder.BuildWorldFacts(facts, defaults.WorldFactFields);
        Check(minimal == "玩家：Robin\n天气/节日：晴\n配偶/室友：无", "Unexpected default facts.");
        Check(ConversationContextBuilder.BuildWorldFacts(facts, Array.Empty<string>()) == "", "Empty whitelist leaked facts.");
        string optIn = ConversationContextBuilder.BuildWorldFacts(facts, new[] { "任务", "技能" });
        Check(optIn.Contains("罗宾丢失的斧子") && optIn.Contains("耕10") && !optIn.Contains("钥匙"), "Opt-in fields failed.");
        Check(ConversationContextBuilder.BuildWorldFacts("- 当地天气：雨\n- 当前任务：旧任务", defaults.WorldFactFields)
              == "天气/节日：雨", "Legacy snapshots bypassed the whitelist.");

        using var files = new TemporaryConversationPrompts();
        var warnings = new List<string>();
        var service = new ConversationPromptService(files.Root, warnings.Add);
        files.Write("conversation.txt", "测试第一版提示词");
        Check(service.Load().SystemRules == "测试第一版提示词", "Editable UTF-8 prompt was not loaded.");
        files.Write("conversation.txt", "测试第二版提示词");
        Check(service.Load().SystemRules == "测试第二版提示词", "Next turn did not reload prompt text.");
        files.Write("conversation.txt", " ");
        files.Write("conversation-context.json", "{ broken");
        ConversationPromptSettings fallback = service.Load();
        Check(fallback.SystemRules == defaults.SystemRules && fallback.WorldFactFields.SequenceEqual(defaults.WorldFactFields),
            "Malformed files did not fall back to embedded defaults.");
        files.Write("conversation.txt", defaults.SystemRules);
        files.Write("conversation-context.json", "{\"WorldFactFields\":[]}");
        Check(service.Load().WorldFactFields.Length == 0, "Empty whitelist was replaced by defaults.");
        files.Write("conversation-context.json", "{\"WorldFactFields\":[\"拼错的字段\"]}");
        Check(service.Load().WorldFactFields.SequenceEqual(defaults.WorldFactFields), "Unknown field was silently accepted.");
        File.Delete(Path.Combine(files.Root, "assets", "prompts", "conversation-final.txt"));
        Check(service.Load().FinalRules == defaults.FinalRules && warnings.Count >= 4, "Missing asset fallback was not diagnosed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class TemporaryConversationPrompts : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vivant-prompt-test-" + Guid.NewGuid().ToString("N"));

    public TemporaryConversationPrompts()
    {
        Directory.CreateDirectory(Path.Combine(Root, "assets", "prompts"));
        ConversationPromptSettings defaults = new ConversationPromptService().Load();
        Write("conversation.txt", defaults.SystemRules);
        Write("conversation-final.txt", defaults.FinalRules);
        Write("conversation-context.json", "{\"WorldFactFields\":[\"玩家\",\"天气/节日\",\"配偶/室友\"]}");
    }

    public void Write(string name, string content)
        => File.WriteAllText(Path.Combine(Root, "assets", "prompts", name), content, new UTF8Encoding(false));

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
