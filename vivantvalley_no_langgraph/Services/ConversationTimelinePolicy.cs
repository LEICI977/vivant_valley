namespace VivantValley.Services;

/// <summary>A persisted window over raw messages. Trimming the prompt never rewrites the originals.</summary>
public static class ConversationTimelinePolicy
{
    // Installed by ModEntry for the active save; null only for pure-policy callers/tests.
    public static Func<NpcConversationMemory, bool>? ArchiveBeforePrune { get; set; }

    public const int MaximumTurns = 8;
    public const int MaximumEvents = 8;
    public const int RetainedTurns = 4;
    public const int RetainedEvents = 4;
    public const int MaximumCharacters = 2400;
    public const int RetainedCharacters = 1200;
    private const string Omission = " …[中间已省略 / omitted]… ";
    private const string ExcerptNotice = "[历史摘录 / excerpt: 原文有省略，不可据此推断同意、承诺或行动成功 / omissions do not establish consent, promises or success]\n";

    /// <summary>Attach the reply and confirmed results to the user message that began this committed turn.</summary>
    public static void RecordCompletedTurn(NpcConversationMemory memory, int firstMessage)
    {
        EnsureSequences(memory);
        if (firstMessage < 0 || firstMessage + 1 >= memory.Messages.Count
            || !IsChat(memory.Messages[firstMessage], "user")
            || !IsChat(memory.Messages[firstMessage + 1], "assistant")) return;
        long turnId = memory.Messages[firstMessage].TimelineSequence;
        for (int index = firstMessage; index < memory.Messages.Count; index++)
            memory.Messages[index].ConversationTurnId = turnId;
    }

    /// <summary>Run before the existing raw-history retention step, including non-chat event writes.</summary>
    public static List<ConversationMemoryMessage> RetainRecentMessages(NpcConversationMemory memory)
    {
        if (memory.FixedConversationSegments) return memory.Messages.ToList();
        BuildTimeline(memory);
        // A failed or unavailable disk write must not silently discard the only raw copy.
        if (ArchiveBeforePrune is not null && !ArchiveBeforePrune(memory)) return memory.Messages.ToList();
        var recent = ConversationMemoryPolicy.KeepRecentConversationTurns(memory.Messages);
        // Preserve the active segment even when an unusually event-heavy turn reaches
        // the old raw-record cap. The segment itself has a separate finite budget.
        int recentStart = recent.Count == 0 ? memory.Messages.Count : memory.Messages.IndexOf(recent[0]);
        int windowStart = memory.Messages.FindIndex(message => message is not null
            && message.TimelineSequence >= memory.TimelineStartSequence);
        return windowStart < 0 ? recent : memory.Messages.Skip(Math.Min(recentStart, windowStart)).ToList();
    }

    public static IReadOnlyList<string> BuildTimeline(NpcConversationMemory memory)
    {
        EnsureSequences(memory);
        if (memory.FixedConversationSegments)
            return SegmentMemoryPolicy.Timeline(memory);
        List<MessageGroup> groups = GroupMessages(memory.Messages)
            .Where(group => group.Messages[^1].TimelineSequence >= memory.TimelineStartSequence).ToList();
        if (groups.Count == 0) return Array.Empty<string>();
        var entries = groups.Select(Render).ToList();
        int turns = groups.Count(group => group.IsTurn);
        int events = groups.Count - turns;
        int characters = entries.Sum(entry => entry.Length);
        if (turns > MaximumTurns || events > MaximumEvents || characters > MaximumCharacters)
        {
            int remove = 0;
            while (remove < groups.Count - 1
                && (turns > RetainedTurns || events > RetainedEvents || characters > RetainedCharacters))
            {
                if (groups[remove].IsTurn) turns--; else events--;
                characters -= entries[remove].Length;
                remove++;
            }
            groups.RemoveRange(0, remove);
            entries.RemoveRange(0, remove);
        }
        memory.TimelineStartSequence = groups[0].Messages[0].TimelineSequence;
        return entries;
    }

    /// <summary>Defense for externally constructed snapshots; entries are whole turns/events, never 100-char lines.</summary>
    public static string[] BoundEntries(IEnumerable<string>? timeline)
    {
        var entries = (timeline ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Length <= MaximumCharacters ? value : ExcerptNotice + Excerpt(value, MaximumCharacters - ExcerptNotice.Length))
            .ToList();
        int characters = entries.Sum(value => value.Length);
        int remove = 0;
        while (remove < entries.Count - 1
            && (entries.Count - remove > MaximumTurns + MaximumEvents || characters > MaximumCharacters))
            characters -= entries[remove++].Length;
        return entries.Skip(remove).ToArray();
    }

    internal static List<MessageGroup> GroupMessages(IEnumerable<ConversationMemoryMessage>? messages)
    {
        var source = (messages ?? Array.Empty<ConversationMemoryMessage>()).Where(message => message is not null).ToList();
        var groups = new List<MessageGroup>();
        for (int index = 0; index < source.Count;)
        {
            var block = new List<ConversationMemoryMessage> { source[index++] };
            long explicitId = block[0].ConversationTurnId;
            if (explicitId > 0)
            {
                while (index < source.Count && source[index].ConversationTurnId == explicitId)
                    block.Add(source[index++]);
            }
            else if (IsChat(block[0], "user") && index < source.Count
                && source[index].ConversationTurnId == 0 && IsChat(source[index], "assistant")
                && source[index].GameDate == block[0].GameDate)
            {
                // Older saves have no turn IDs. Only join an adjacent matching pair;
                // ambiguous events/fragments remain separate instead of inventing an association.
                block.Add(source[index++]);
            }
            bool turn = block.Count >= 2 && IsChat(block[0], "user") && IsChat(block[1], "assistant");
            groups.Add(new MessageGroup(block, turn));
        }
        return groups;
    }

    private static void EnsureSequences(NpcConversationMemory memory)
    {
        memory.Messages ??= new();
        long next = Math.Max(1, memory.NextTimelineSequence);
        foreach (var message in memory.Messages.Where(message => message is not null))
            if (message.TimelineSequence > 0) next = Math.Max(next, checked(message.TimelineSequence + 1));
        var seen = new HashSet<long>();
        foreach (var message in memory.Messages.Where(message => message is not null))
        {
            if (message.TimelineSequence <= 0 || !seen.Add(message.TimelineSequence))
            {
                message.TimelineSequence = next++;
                seen.Add(message.TimelineSequence);
            }
        }
        memory.NextTimelineSequence = next;
        foreach (MessageGroup group in GroupMessages(memory.Messages))
            if (group.IsTurn && group.Messages[0].ConversationTurnId == 0)
                foreach (var message in group.Messages)
                    message.ConversationTurnId = group.Messages[0].TimelineSequence;
    }

    private static bool IsChat(ConversationMemoryMessage message, string role)
        => message is not null && string.Equals(message.Role, role, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(message.Source) || message.Source == ConversationMemorySources.AiChat);

    private static string Render(MessageGroup group)
    {
        var first = group.Messages[0];
        string kind = first.Source == ConversationMemorySources.Dream ? "dream (not waking-world facts)" : group.IsTurn ? "turn" : IsChat(first, "user") || IsChat(first, "assistant") ? "fragment" : "event";
        string date = SingleLine(first.GameDate);
        string header = $"{kind} {first.TimelineSequence} [{Excerpt(date, 160)}]\n";
        var lines = group.Messages.Where(message => !string.IsNullOrWhiteSpace(message.Content)).Select(message =>
        {
            string role = message.Role?.ToLowerInvariant() switch { "user" => "玩家/user", "assistant" => "NPC", _ => "事实/fact" };
            if (message.Source == ConversationMemorySources.Dream) role = "梦境记录 / dream, not a real-world event";
            return role + ": " + SingleLine(message.Content);
        }).ToList();
        string full = header + string.Join('\n', lines);
        if (full.Length <= MaximumCharacters) return full;
        string prefix = ExcerptNotice + header;
        int perLine = (MaximumCharacters - prefix.Length - Math.Max(0, lines.Count - 1)) / Math.Max(1, lines.Count);
        if (perLine < Omission.Length + 20)
            return prefix + "[历史记录过长，未展示原文 / oversized historical record omitted]";
        return prefix + string.Join('\n', lines.Select(line => Excerpt(line, perLine)));
    }

    private static string SingleLine(string? value)
        => string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Excerpt(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        int head = (maximum - Omission.Length) / 2;
        int tail = value.Length - (maximum - Omission.Length - head);
        if (head > 0 && char.IsHighSurrogate(value[head - 1])) head--;
        if (tail < value.Length && char.IsLowSurrogate(value[tail])) tail++;
        return value[..head] + Omission + value[tail..];
    }

    internal sealed record MessageGroup(List<ConversationMemoryMessage> Messages, bool IsTurn);
}
