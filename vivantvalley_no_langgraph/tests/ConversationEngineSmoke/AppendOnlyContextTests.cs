using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class AppendOnlyContextTests
{
    public static void Run()
    {
        var session = new AppendOnlyContextSession<DeepSeekChatMessage>(m => JsonSerializer.Serialize(m));
        var rules = new[] { new DeepSeekChatMessage("system", "You are Abigail. Latest facts override old snapshots.") };
        int bootstraps = 0;
        IReadOnlyList<DeepSeekChatMessage> Bootstrap()
        { bootstraps++; return new[] { new DeepSeekChatMessage("user", "[history] An old promise was cancelled.") }; }
        var first = session.Begin(rules, Bootstrap, new[] { new DeepSeekChatMessage("user", "09:00; hello") }, "empty");
        var full = first.Messages.Concat(new[] { new DeepSeekChatMessage("assistant", "{\"text\":\"Morning!\"}") }).ToList();
        Check(session.Complete(first, full, "observed-one"), "First candidate was not accepted.");
        // Caller mutations must not rewrite the serialized transcript already held by the session.
        full[0].Content = "mutated outside session";
        var second = session.Begin(rules, Bootstrap, new[] { new DeepSeekChatMessage("user", "09:10; where are we?") }, "observed-one");
        Check(!second.IsColdStart && second.Epoch == first.Epoch && bootstraps == 1,
            "A normal continuation rebuilt its epoch.");
        Check(second.Messages[0].Content == rules[0].Content
            && second.Messages[^2].Content == "{\"text\":\"Morning!\"}"
            && second.Messages.Any(m => m.Content == "09:00; hello"), "Exact prior messages or JSON changed.");
        var cancelled = session.Begin(rules, Bootstrap, new[] { new DeepSeekChatMessage("user", "retry") }, "never-observed");
        Check(cancelled.IsColdStart && !cancelled.Messages.Any(m => m.Content.Contains("Morning!")),
            "An unobserved response entered game history.");
        var longTurn = session.Begin(rules, Bootstrap, new[] { new DeepSeekChatMessage("user", new string('x', 500)) }, "observed-one", 220);
        Check(longTurn.IsColdStart && longTurn.Messages[^1].Content.Length == 500,
            "Budget rollover truncated current input or reused an oversized history.");
        var mandatory = session.Begin(rules, Bootstrap, new[] { new DeepSeekChatMessage("user", new string('x', 1200)) }, "observed-one", 100);
        Check(mandatory.BudgetExceeded && mandatory.Messages[^1].Content.Length == 1200,
            "Mandatory input was truncated or the exceeded target was hidden.");
        Check(AiUsageDiagnostics.ReadUsage("{\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":10,\"prompt_cache_hit_tokens\":80}}")?.CachedInputTokens == 80,
            "Reported cached input usage was lost.");
        Check(AiUsageDiagnostics.ReadUsage("{\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":10}}")?.CachedInputTokens is null,
            "Absent cache usage was misreported as zero.");
        var edited = session.Begin(new[] { new DeepSeekChatMessage("system", "You are Abigail; new personality.") },
            Bootstrap, new[] { new DeepSeekChatMessage("user", "hello") }, "observed-one");
        Check(edited.IsColdStart && !edited.Messages.Any(m => m.Content.Contains("Morning!")), "Changed rules reused old epoch.");
        var left = session.Begin(rules, Bootstrap, new[] { new DeepSeekChatMessage("user", "left") }, "observed-one");
        var right = session.Begin(rules, Bootstrap, new[] { new DeepSeekChatMessage("user", "right") }, "observed-one");
        Check(session.Complete(left, left.Messages.Concat(new[] { new DeepSeekChatMessage("assistant", "left reply") }).ToList(), "left-observed"),
            "Valid next branch failed.");
        Check(!session.Complete(right, right.Messages.Concat(new[] { new DeepSeekChatMessage("assistant", "right reply") }).ToList(), "right-observed"),
            "A stale concurrent result overwrote the committed branch.");
        bool rejected = false;
        try { session.Complete(left, new[] { new DeepSeekChatMessage("assistant", "rewrite everything") }, "bad"); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Rewritten prefix was accepted.");
        session.Clear();
        Check(session.Begin(rules, Bootstrap, new[] { new DeepSeekChatMessage("user", "new save") }, "left-observed").IsColdStart,
            "Clear retained another save's continuation.");
        var decision = new DecisionValidator().Validate(new LangGraphResponse
        {
            Decision = new LangGraphDecision { Reply = "Okay.", MemoryUpdate = new LangGraphMemoryUpdate
            {
                SummaryPatch = new string('a', 330) + "; the player refused.",
                OpenLoops = new() { "约好明天去海边" + new string('海', 100) + "但后来取消了" },
            } },
        }, new NpcContextSnapshot(), 1200);
        Check(decision.MemoryUpdate.SummaryPatch.Length == 0 && decision.MemoryUpdate.OpenLoops.Count == 0,
            "Response validation truncated negation before the memory policy could reject it.");
        Console.WriteLine("Append-only context: exact prefixes, observation gates, budgets, edits and concurrent branches passed.");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
