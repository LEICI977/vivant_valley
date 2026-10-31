using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class TimelineTests
{
    public static void Run()
    {
        SegmentsAndReload();
        LegacyMigration();
        BudgetAndExcerpts();
        EventsAndRetention();
        Console.WriteLine("Timeline complete turns, segments, old saves, excerpts and event retention tests passed.");
    }

    private static void SegmentsAndReload()
    {
        var memory = new NpcConversationMemory { PlayerId = "p1", NpcName = "Haley", Summary = "Keep my summary." };
        string[] previous = Array.Empty<string>();
        for (int turn = 1; turn <= 8; turn++)
        {
            AddTurn(memory, turn);
            string[] current = ConversationTimelinePolicy.BuildTimeline(memory).ToArray();
            Check(current.Length == turn && current.Take(previous.Length).SequenceEqual(previous), "Appending a turn rewrote its prefix.");
            Check(current[^1].Contains($"user-{turn}") && current[^1].Contains($"reply-{turn}") && current[^1].Contains($"result-{turn}"),
                "A committed action was separated from its dialogue.");
            previous = current;
        }
        AddTurn(memory, 9);
        var compacted = ConversationTimelinePolicy.BuildTimeline(memory).ToArray();
        Check(compacted.Length == 4 && compacted[0].Contains("user-6") && compacted[^1].Contains("user-9"), "Segment did not trim as a batch to four turns.");
        Check(memory.Messages.Count == 27 && memory.Summary == "Keep my summary.", "Prompt trimming deleted raw data or rewrote the summary.");
        var loaded = JsonSerializer.Deserialize<NpcConversationMemory>(JsonSerializer.Serialize(memory))!;
        Check(ConversationTimelinePolicy.BuildTimeline(loaded).SequenceEqual(compacted), "Reload resurrected older turns or changed numbering.");
        Check(ConversationTimelinePolicy.BuildTimeline(loaded.Clone()).SequenceEqual(compacted), "Clone lost the timeline cursor.");
        for (int turn = 10; turn <= 13; turn++)
        {
            AddTurn(loaded, turn);
            loaded.Messages = ConversationTimelinePolicy.RetainRecentMessages(loaded);
            Check(ConversationTimelinePolicy.BuildTimeline(loaded).Take(4).SequenceEqual(compacted), "Raw retention shifted the active segment.");
        }
        AddTurn(loaded, 14);
        Check(ConversationTimelinePolicy.BuildTimeline(loaded).Count == 4, "Second segment never compacted.");
        var other = new NpcConversationMemory { PlayerId = "p2", NpcName = "Haley" };
        AddTurn(other, 1);
        Check(!ConversationTimelinePolicy.BuildTimeline(other)[0].Contains("user-14"), "Timeline crossed a player boundary.");
    }

    private static void LegacyMigration()
    {
        const string oldJson = """
        {"PlayerId":"p","NpcName":"Alex","Summary":"旧长期记忆保持原样","TotalTurns":8,"Messages":[
          {"Role":"assistant","Content":"之前裁剪遗留的回复","GameDate":"Y1 900"},
          {"Role":"user","Content":"邀请","GameDate":"Y1 910"},
          {"Role":"assistant","Content":"我不同意","GameDate":"Y1 910"},
          {"Role":"system","Content":"独立事件：取消同行","GameDate":"Y1 920","Source":"mod_action"}]}
        """;
        var memory = JsonSerializer.Deserialize<NpcConversationMemory>(oldJson)!;
        string[] raw = memory.Messages.Select(message => message.Content).ToArray();
        var timeline = ConversationTimelinePolicy.BuildTimeline(memory).ToArray();
        Check(timeline.Length == 3 && timeline[1].StartsWith("turn ") && timeline[1].Contains("邀请")
            && timeline[1].Contains("我不同意"), "Legacy adjacent pair did not remain complete.");
        Check(timeline[0].StartsWith("fragment ") && timeline[2].StartsWith("event "), "Legacy orphan or ambiguous action was made into a fictitious full turn.");
        Check(memory.Messages.Select(message => message.Content).SequenceEqual(raw)
            && memory.Summary == "旧长期记忆保持原样", "Old save originals were changed during migration.");
        Check(memory.Messages.All(message => message.TimelineSequence > 0)
            && memory.Messages.Select(message => message.TimelineSequence).Distinct().Count() == 4, "Stable sequences missing.");
        Check(ConversationTimelinePolicy.BuildTimeline(memory).SequenceEqual(timeline), "Rebuilding migrated history changed IDs.");
        var cloned = memory.Clone();
        Check(cloned.Messages[1].ConversationTurnId == memory.Messages[1].ConversationTurnId, "Clone lost turn association.");
    }

    private static void BudgetAndExcerpts()
    {
        var memory = new NpcConversationMemory();
        AddTurn(memory, 1, new string('甲', 180) + "但我拒绝送礼。", new string('乙', 220) + "我们取消约定。", "");
        string complete = ConversationTimelinePolicy.BuildTimeline(memory).Single();
        Check(complete.Contains(new string('甲', 180) + "但我拒绝送礼。") && complete.Contains("我们取消约定。")
            && !complete.Contains("omitted"), "Normal dialogue was still clipped at 100 characters.");
        AddTurn(memory, 2, string.Concat(Enumerable.Repeat("😀", 4000)) + "最后明确拒绝。", "好的，取消同行。", "取消已经生效");
        string[] bounded = ConversationTimelinePolicy.BuildTimeline(memory).ToArray();
        Check(bounded.Sum(entry => entry.Length) <= ConversationTimelinePolicy.MaximumCharacters
            && (bounded.Length == 1 || bounded[0] == complete),
            "Oversized latest turn bypassed the budget or left half an old turn.");
        Check(bounded[^1].Contains("excerpt") && bounded[^1].Contains("最后明确拒绝。") && bounded[^1].Contains("取消同行")
            && bounded[^1].Contains("取消已经生效"), "Excerpt hid the omission or lost the reply/result.");
        Check(memory.Messages.Any(message => message.Content.Length > 8000), "Excerpt replaced stored original text.");
        for (int index = 0; index < bounded[^1].Length; index++)
        {
            if (char.IsHighSurrogate(bounded[^1][index]))
                Check(++index < bounded[^1].Length && char.IsLowSurrogate(bounded[^1][index]), "Excerpt split a Unicode pair.");
            else Check(!char.IsLowSurrogate(bounded[^1][index]), "Excerpt starts inside a Unicode pair.");
        }
        Check(ConversationTimelinePolicy.BoundEntries(bounded).SequenceEqual(bounded), "Transport changed the selected window.");
    }

    private static void EventsAndRetention()
    {
        var memory = new NpcConversationMemory();
        for (int index = 0; index < 9; index++)
            memory.Messages.Add(new ConversationMemoryMessage { Role = "system", Source = ConversationMemorySources.ModAction,
                Content = $"独立事件{index}：玩家取消了活动。", GameDate = "Y1 900" });
        Check(ConversationTimelinePolicy.BuildTimeline(memory).Count == 4, "Event-only history grows without batch limits.");
        var heavy = new NpcConversationMemory();
        for (int turn = 1; turn <= 8; turn++)
        {
            int first = heavy.Messages.Count;
            AddTurn(heavy, turn, "问", "答", "事实");
            for (int j = 0; j < 3; j++) heavy.Messages.Add(new ConversationMemoryMessage { Role = "system", Content = "结果", Source = ConversationMemorySources.ModAction });
            ConversationTimelinePolicy.RecordCompletedTurn(heavy, first);
        }
        var before = ConversationTimelinePolicy.BuildTimeline(heavy).ToArray();
        heavy.Messages = ConversationTimelinePolicy.RetainRecentMessages(heavy);
        Check(ConversationTimelinePolicy.BuildTimeline(heavy).SequenceEqual(before), "Record cap truncated a still-active segment.");
        var trimmed = ConversationMemoryPolicy.KeepRecentConversationTurns(heavy.Messages, maximumTurns: 1);
        Check(trimmed.Count == 6 && trimmed[0].Role == "user" && trimmed[1].Role == "assistant", "Raw retention split a heavy turn.");
    }

    private static void AddTurn(NpcConversationMemory memory, int number, string? user = null, string? reply = null, string? result = null)
    {
        int first = memory.Messages.Count;
        string date = "Y1 " + number;
        memory.Messages.Add(new ConversationMemoryMessage { Role = "user", Content = user ?? $"user-{number}", GameDate = date, Source = ConversationMemorySources.AiChat });
        memory.Messages.Add(new ConversationMemoryMessage { Role = "assistant", Content = reply ?? $"reply-{number}", GameDate = date, Source = ConversationMemorySources.AiChat });
        if (result != "") memory.Messages.Add(new ConversationMemoryMessage { Role = "system", Content = result ?? $"result-{number}", GameDate = date, Source = ConversationMemorySources.ModAction });
        ConversationTimelinePolicy.RecordCompletedTurn(memory, first);
        memory.TotalTurns++;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
