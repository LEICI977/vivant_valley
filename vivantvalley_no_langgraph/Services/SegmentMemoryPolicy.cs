using System.Text.Json;

namespace VivantValley.Services;

public sealed record ConversationSegmentBatch(long ThroughSequence, string Fingerprint,
    IReadOnlyList<ConversationMemoryMessage> Records);
public sealed class SegmentSummaryResult
{
    public string Summary { get; set; } = "";
    public List<string> StableFacts { get; set; } = new();
}

/// <summary>Whole twelve-turn segments. No sliding cursor and no deletion before both summary and archive succeed.</summary>
public static class SegmentMemoryPolicy
{
    public const int TurnsPerSegment = 12;
    public const string RecalledContextSource = "recalled_context";

    /// <summary>New background is appended where it became relevant, never rewritten ahead of earlier dialogue.</summary>
    public static IReadOnlyList<ConversationMemoryMessage> NewContextReferences(
        IEnumerable<ConversationMemoryMessage> history, string recalled, IEnumerable<string> recent, string date, string background = "")
    {
        var existing = history.ToArray();
        var seen = existing.Where(m => m.Source == RecalledContextSource).Select(m => m.Content.Trim()).ToHashSet(StringComparer.Ordinal);
        var result = new List<ConversationMemoryMessage>();
        var references = new[] { recalled ?? "" }
            .Concat(recent.Where(text => !text.StartsWith("[recent_evidence;", StringComparison.Ordinal)));
        foreach (string reference in references)
        {
            string text = reference.Trim();
            if (text.Length == 0 || !seen.Add(text) || ConversationRecallPolicy.Covers(background, text)
                || existing.Any(m => ConversationRecallPolicy.Covers(m.Content, text))) continue;
            result.Add(new() { Role = "system", Source = RecalledContextSource, GameDate = date, Content = text,
                DedupeKey = "recall-" + ContextMemoryCompiler.StableId(text) });
        }
        return result;
    }

    public static string RenderContextRecord(ConversationMemoryMessage message)
        => message.Source == RecalledContextSource
            ? (L.IsChinese ? "【历史参考】\n" : "[Past context]\n") + ConversationRecallPolicy.Readable(message.Content)
            : $"[{message.GameDate}; {message.Source}]\n{ConversationRecallPolicy.Readable(message.Content)}";
    public static void Enable(NpcConversationMemory memory)
    {
        if (memory.FixedConversationSegments) return;
        memory.FixedConversationSegments = true;
        memory.SegmentMemory = ContextMemoryCompiler.BuildCapsule(memory.Summary);
        // Existing raw messages are still available; a previous prompt-window cursor is not a deletion receipt.
        memory.SummarizedThroughSequence = 0;
        ConversationTimelinePolicy.BuildTimeline(memory);
    }

    public static IReadOnlyList<string> Timeline(NpcConversationMemory memory)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(memory.SegmentSummary))
            lines.Add("[previous_segment_summary; derived history, not live facts]\n" + memory.SegmentSummary);
        lines.AddRange(memory.Messages.Where(m => m.TimelineSequence > memory.SummarizedThroughSequence)
            .Select(m => $"[{m.GameDate};source={m.Source}] {m.Role}: {m.Content}"));
        return lines;
    }

    public static ConversationSegmentBatch? NextBatch(NpcConversationMemory memory)
    {
        Enable(memory);
        ConversationTimelinePolicy.BuildTimeline(memory); // Assign legacy sequences and complete-turn identities.
        var pending = memory.Messages.Where(m => m.TimelineSequence > memory.SummarizedThroughSequence).ToArray();
        var turns = pending.Where(m => m.ConversationTurnId > 0)
            .GroupBy(m => m.ConversationTurnId).Where(g => g.Any(m => m.Role == "user") && g.Any(m => m.Role == "assistant"))
            .OrderBy(g => g.Key).Take(TurnsPerSegment).ToArray();
        if (turns.Length < TurnsPerSegment) return null;
        long through = turns[^1].Max(m => m.TimelineSequence);
        var records = pending.Where(m => m.TimelineSequence <= through).Select(m => m.Clone()).ToArray();
        return new(through, Fingerprint(records), records);
    }

    public static string Fingerprint(IEnumerable<ConversationMemoryMessage> records)
        => MemoryEventArchive.Scope(JsonSerializer.Serialize(records.Select(m => m.Clone())));

    public static bool Valid(SegmentSummaryResult? result)
        => result is not null && !string.IsNullOrWhiteSpace(result.Summary) && result.Summary.Length <= 900
            && result.StableFacts is not null && result.StableFacts.Count <= 6
            && result.StableFacts.All(f => !string.IsNullOrWhiteSpace(f) && f.Length <= 240);

    public static bool TryCommit(NpcConversationMemory memory, ConversationSegmentBatch batch,
        SegmentSummaryResult result, Func<NpcConversationMemory, bool> archive)
    {
        if (!Valid(result) || batch.ThroughSequence <= memory.SummarizedThroughSequence) return false;
        var records = memory.Messages.Where(m => m.TimelineSequence > memory.SummarizedThroughSequence
            && m.TimelineSequence <= batch.ThroughSequence).ToArray();
        if (Fingerprint(records) != batch.Fingerprint) return false; // Changed/cancelled/reloaded source cannot be summarized.
        if (!archive(memory)) return false;
        memory.SegmentSummary = result.Summary.Trim();
        string prefix = "segment-" + batch.Fingerprint;
        var facts = (memory.Summary ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string fact in result.StableFacts)
        {
            string line = $"[derived_dialogue;id={prefix}-{ContextMemoryCompiler.StableId(fact)}] {fact.Trim()}";
            if (!facts.Contains(line, StringComparer.Ordinal)) facts.Add(line);
        }
        while (facts.Count > 0 && string.Join('\n', facts).Length > 2000) facts.RemoveAt(0);
        memory.Summary = string.Join('\n', facts);
        memory.SummarizedThroughSequence = batch.ThroughSequence;
        var finalTurn = ConversationTimelinePolicy.GroupMessages(records).LastOrDefault(g => g.IsTurn);
        memory.SegmentBridge = finalTurn?.Messages.Select(m => m.Clone()).ToList() ?? new();
        memory.AutomaticRecallCount = 0;
        memory.ConversationSegment++;
        memory.Messages.RemoveAll(m => m.TimelineSequence <= batch.ThroughSequence);
        memory.SegmentMemory = ContextMemoryCompiler.BuildCapsule(memory.Summary);
        return true;
    }

    public static async Task<SegmentSummaryResult> SummarizeAsync(IDeepSeekClient client, string key, string model,
        string npc, string source, string previous, object records, CancellationToken cancellation)
    {
        // Recalled material is not a new event. Never let it dominate or re-enter stable facts.
        object compactRecords = records is IEnumerable<ConversationMemoryMessage> messages
            ? messages.Where(m => m.Source != RecalledContextSource)
                .Select(m => new { role = m.Role, text = ConversationRecallPolicy.Readable(m.Content), source = m.Source, date = m.GameDate,
                turn = m.ConversationTurnId }).ToArray() : records;
        string raw = await client.CompleteChatAsync(key, new DeepSeekChatRequest
        {
            NpcName = npc, TaskKind = "memory_summary", Model = model, MaxTokens = 1200, Stream = false,
            Thinking = new DeepSeekThinkingOptions { Type = "disabled" }, ReasoningEffort = "low",
            Messages = new() { new("system", LocalizedPromptAssets.ForRequest("segment-summary.txt")),
                new("user", JsonSerializer.Serialize(new { source, previousSegmentSummary = previous, records = compactRecords },
                    new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) },
        }, cancellation).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<SegmentSummaryResult>(WeeklyStoryPlannerService.ExtractJsonObject(raw),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (!Valid(result)) throw new InvalidDataException("Invalid complete segment summary; original dialogue retained.");
        return result!;
    }
}
