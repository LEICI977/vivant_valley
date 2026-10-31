namespace VivantValley.Services;

/// <summary>Bounds recent dialogue and recalls deduplicated older memories only when contextually useful.</summary>
public static class ConversationMemoryPolicy
{
    public static Func<string?, string?, string?, bool>? ArchiveBeforeSummaryCompaction { get; set; }
    public const int RecentConversationTurns = 10;
    public const int MaximumLongTermCharacters = 2000;
    public const int MaximumMemoryEntryCharacters = 320;
    public const int MaximumRecallCharacters = 350;
    public const int MaximumRecallEntries = 2;

    private static readonly string[] RecallCues =
    {
        "记得", "还记得", "以前", "上次", "之前", "那件事", "约定", "承诺",
        "remember", "last time", "before", "promise",
    };

    private static readonly string[] TopicShiftCues =
    {
        "换个话题", "换一个话题", "先不说", "别再说", "不要再提", "不想聊", "说点别的",
        "别再提", "别提", "不聊这个", "不说这个", "算了",
        "change the subject", "new topic", "move on", "stop talking about",
    };

    private static readonly HashSet<string> RecallStopTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "我们", "你们", "他们", "玩家", "村民", "这个", "那个", "事情", "今天", "已经",
        "以后", "还是", "只是", "什么", "怎么", "可以", "一起", "最近", "现在", "真的",
        "觉得", "知道", "一下", "好的", "谢谢", "你好",
        "the", "and", "that", "this", "with", "player", "today", "thing",
    };

    private static readonly string[] DurableMemoryMarkers =
    {
        "承诺", "约定", "答应", "拒绝", "选择", "完成", "结婚", "订婚", "恋爱",
        "喜欢", "讨厌", "偏好", "生日", "礼物", "送给", "到达", "住院", "击败",
        "promise", "agreed", "refused", "choice", "completed", "married", "engaged",
        "likes", "dislikes", "gift", "arrived", "hospital",
    };

    private static readonly string[] ExplicitOpenLoopMarkers =
    {
        "约好", "约定", "承诺", "答应", "下次", "改天", "明天", "稍后", "之后再",
        "待办", "未完成", "还要", "再去", "再聊", "再问", "计划",
        "promise", "promised", "agreed", "next_", "next ", "later", "tomorrow",
        "pending", "planned", "plan_", "will_", "needs_to",
    };

    /// <summary>True only for a concrete commitment that can genuinely remain pending.</summary>
    public static bool IsExplicitOpenLoop(string? value)
    {
        string normalized = string.Join(" ", (value ?? string.Empty).Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length is > 0 and <= 96
               && ConversationCommitmentPolicy.GetResolution(normalized) is null
               && ExplicitOpenLoopMarkers.Any(marker =>
                   normalized.Contains(marker, StringComparison.OrdinalIgnoreCase))
               && (new[] { "约好", "约定", "承诺", "答应", "计划", "promise", "agreed", "planned", "pending" }
                       .Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase))
                   || new[] { "一起", "再去", "再聊", "再问", "见面", "带来", "帮", "meet", "trip", "bring", "visit", "mine", "fishing" }
                       .Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsTopicRefusal(string? text)
        => TopicShiftCues.Any(cue => (text ?? string.Empty).Contains(cue, StringComparison.OrdinalIgnoreCase));

    public static string GetCurrentTopicText(string? text)
        => string.Join("，", (text ?? string.Empty).Split(new[] { '，', ',', '。', '！', '!', '？', '?', '；', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(clause => !IsTopicRefusal(clause)).Select(clause => clause.Trim()));

    public static string UpdateLongTermMemory(
        string? existingSummary,
        string? summaryPatch,
        string? gameDate,
        string? playerId,
        string? npcName,
        long conversationTurn)
    {
        bool archived = ArchiveBeforeSummaryCompaction?.Invoke(existingSummary, playerId, npcName) ?? true;
        // Preserve the exact old summary if its archival failed, including legacy entries.
        // New facts still work; a later successful archive permits normal compaction again.
        if (!archived)
        {
            string pending = WholeSummaryPatch(summaryPatch);
            return (existingSummary ?? string.Empty) + (pending.Length == 0 ? string.Empty
                : "\n[" + LimitSingleLine(gameDate,80) + "] " + pending);
        }
        List<string> entries = CoalesceSimilarEntries(ParseEntries(existingSummary));
        string patch = WholeSummaryPatch(summaryPatch);
        if (patch.Length > 0)
        {
            string date = LimitSingleLine(gameDate, 80);
            string datedPatch = $"[{(date.Length > 0 ? date : "日期不详")}] {patch}";
            string? stateKey = GetPreferenceKey(patch);
            if (stateKey is not null)
                entries.RemoveAll(entry => GetPreferenceKey(RemoveDatePrefix(entry)) == stateKey);
            int duplicateIndex = FindSimilarEntry(entries, datedPatch);
            if (duplicateIndex >= 0) entries.RemoveAt(duplicateIndex);
            entries.Add(datedPatch);
        }

        while (JoinedLength(entries) > MaximumLongTermCharacters && entries.Count > 1)
        {
            // Keep the newest fact and prefer durable choices, promises, relationship
            // changes and game-confirmed outcomes over incidental small talk.
            entries.RemoveAt(FindLeastValuableEntry(entries));
        }

        if (entries.Count == 0)
            return string.Empty;

        string result = string.Join('\n', entries);
        // A single oversized legacy entry remains in the archive; omitting it is safer
        // than manufacturing a shorter assertion by removing its final qualification.
        return result.Length <= MaximumLongTermCharacters ? result : string.Empty;
    }

    public static string BuildRandomRecall(
        string? summary,
        string? playerId,
        string? npcName,
        string? gameDate,
        long conversationTurn)
    {
        List<string> entries = ParseEntries(summary);
        if (entries.Count == 0)
            return string.Empty;

        var indices = Enumerable.Range(0, entries.Count).ToList();
        Random random = CreateStableRandom(playerId, npcName, gameDate, conversationTurn.ToString(), "recall");
        for (int index = indices.Count - 1; index > 0; index--)
        {
            int swap = random.Next(index + 1);
            (indices[index], indices[swap]) = (indices[swap], indices[index]);
        }

        var selected = new List<int>();
        int selectedCharacters = 0;
        foreach (int index in indices)
        {
            int additional = entries[index].Length + (selected.Count > 0 ? 1 : 0);
            if (selected.Count >= MaximumRecallEntries || selectedCharacters + additional > MaximumRecallCharacters)
                continue;

            selected.Add(index);
            selectedCharacters += additional;
        }

        if (selected.Count == 0)
        {
            string only = entries[indices[0]];
            return only.Length <= MaximumRecallCharacters ? only : string.Empty;
        }

        selected.Sort();
        return string.Join('\n', selected.Select(index => entries[index]));
    }

    /// <summary>
    /// Recalls only memories that overlap the player's current subject. Generic
    /// greetings and explicit topic changes therefore don't resurrect a random
    /// old concern. Explicit recall cues such as "上次" still retrieve the newest
    /// memories even when the player doesn't repeat the original keywords.
    /// </summary>
    public static string BuildRelevantRecall(
        string? summary,
        string? playerId,
        string? npcName,
        string? gameDate,
        string? currentPlayerText,
        long conversationTurn)
    {
        var result = ContextMemoryCompiler.Compile(currentPlayerText ?? string.Empty,
            ContextMemoryCompiler.FromSummary(summary), tokenBudget: 220, maximumEntries: MaximumRecallEntries);
        // Keep the legacy public character contract without cutting any selected fact in half.
        var complete = new List<string>();
        foreach (string line in result.Text.Split('\n',StringSplitOptions.RemoveEmptyEntries))
            if (complete.Sum(value => value.Length + 1) + line.Length <= MaximumRecallCharacters) complete.Add(line);
        return string.Join('\n',complete);
    }

    public static List<ConversationMemoryMessage> KeepRecentConversationTurns(
        IEnumerable<ConversationMemoryMessage>? messages,
        int maximumTurns = RecentConversationTurns)
    {
        List<ConversationMemoryMessage> source = (messages ?? Array.Empty<ConversationMemoryMessage>())
            .Where(message => message is not null)
            .ToList();
        if (maximumTurns <= 0 || source.Count == 0)
            return new List<ConversationMemoryMessage>();

        int turnCount = 0;
        int start = 0;
        bool foundLimit = false;
        for (int index = source.Count - 1; index >= 0; index--)
        {
            ConversationMemoryMessage message = source[index];
            bool isAiChat = string.IsNullOrWhiteSpace(message.Source)
                            || string.Equals(message.Source, ConversationMemorySources.AiChat, StringComparison.Ordinal);
            if (!isAiChat || !string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            turnCount++;
            if (turnCount == maximumTurns)
            {
                start = index;
                foundLimit = true;
                break;
            }
        }

        // Non-chat event records are bounded too, so a save cannot grow forever before 10 AI turns exist.
        int hardStart = Math.Max(0, source.Count - (maximumTurns * 4));
        start = foundLimit ? Math.Max(start, hardStart) : hardStart;
        // Round the cut back to a complete dialogue/event group, even at the record cap.
        int boundary = 0;
        foreach (var group in ConversationTimelinePolicy.GroupMessages(source))
        {
            if (boundary + group.Messages.Count > start)
            {
                start = boundary;
                break;
            }
            boundary += group.Messages.Count;
        }
        return source.Skip(start).ToList();
    }

    private static List<string> ParseEntries(string? summary)
    {
        var result = new List<string>();
        foreach (string raw in (summary ?? string.Empty).Split(
                     new[] { "\r\n", "\n", "\r" },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = string.Join(" ", raw.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
            if (entry.Length == 0)
                continue;
            if (!entry.StartsWith("[", StringComparison.Ordinal) || !entry.Contains(']', StringComparison.Ordinal))
                entry = "[日期不详] " + entry;
            result.Add(entry);
        }
        return result;
    }

    private static List<string> CoalesceSimilarEntries(List<string> entries)
    {
        var result = new List<string>(entries.Count);
        foreach (string entry in entries)
        {
            string? preference = GetPreferenceKey(RemoveDatePrefix(entry));
            if (preference is not null)
                result.RemoveAll(old => GetPreferenceKey(RemoveDatePrefix(old)) == preference);
            int duplicateIndex = FindSimilarEntry(result, entry);
            if (duplicateIndex >= 0)
                result.RemoveAt(duplicateIndex);
            result.Add(entry);
        }
        return result;
    }

    private static int FindSimilarEntry(IReadOnlyList<string> entries, string candidate)
    {
        string normalizedCandidate = NormalizeForComparison(RemoveDatePrefix(candidate));
        if (normalizedCandidate.Length == 0)
            return -1;

        for (int index = entries.Count - 1; index >= 0; index--)
        {
            string normalizedExisting = NormalizeForComparison(RemoveDatePrefix(entries[index]));
            // Exact recurring states may refresh their date. Distinct events,
            // especially choices and game results, retain date + full text.
            bool sameDate = DatePrefix(entries[index]).Equals(DatePrefix(candidate), StringComparison.Ordinal);
            bool stateFact = GetPreferenceKey(RemoveDatePrefix(candidate)) is not null
                             || IsExplicitOpenLoop(RemoveDatePrefix(candidate));
            if (normalizedExisting.Equals(normalizedCandidate, StringComparison.Ordinal) && (sameDate || stateFact))
            {
                return index;
            }
        }
        return -1;
    }

    private static string DatePrefix(string value)
        => value.StartsWith("[", StringComparison.Ordinal) && value.IndexOf(']') is int end && end > 0
            ? value[..(end + 1)] : string.Empty;

    // Deliberately narrow grammar: only explicit player preference statements
    // replace a state. Narrative dialogue, quotations and multi-fact patches do
    // not get destructively merged based on textual resemblance.
    private static string? GetPreferenceKey(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text.Trim(),
            @"^(?:玩家|player)\s*(?:现在|如今|目前|now\s+)?\s*(?:不再喜欢|不喜欢|喜欢|讨厌|偏爱|不再讨厌|doesn't like|does not like|likes|dislikes)\s*(?<object>[^。；;！!?？\r\n]{1,48})[。.!！]?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? NormalizeForComparison(match.Groups["object"].Value.TrimEnd('.', '。')) : null;
    }

    private static int FindLeastValuableEntry(IReadOnlyList<string> entries)
    {
        int candidate = 0;
        int candidateScore = int.MaxValue;
        // The last item is always the newest patch and cannot be removed here.
        for (int index = 0; index < entries.Count - 1; index++)
        {
            string content = RemoveDatePrefix(entries[index]);
            int durableMarkers = DurableMemoryMarkers.Count(marker =>
                content.Contains(marker, StringComparison.OrdinalIgnoreCase));
            int score = (durableMarkers * 10_000) + index;
            if (score < candidateScore)
            {
                candidate = index;
                candidateScore = score;
            }
        }
        return candidate;
    }

    private static int GetRelevanceScore(string query, string memory)
    {
        HashSet<string> queryTerms = BuildRecallTerms(query);
        HashSet<string> memoryTerms = BuildRecallTerms(memory);
        int overlap = queryTerms.Count(memoryTerms.Contains);
        if (overlap == 0)
            return 0;

        // Exact words and CJK bigrams are deliberately enough to recall a fact;
        // isolated common characters are never emitted as terms.
        return overlap;
    }

    private static HashSet<string> BuildRecallTerms(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var word = new System.Text.StringBuilder();
        var cjkRun = new System.Text.StringBuilder();

        void FlushWord()
        {
            if (word.Length >= 3)
                result.Add(word.ToString());
            word.Clear();
        }

        void FlushCjk()
        {
            for (int index = 0; index + 1 < cjkRun.Length; index++)
                result.Add(cjkRun.ToString(index, 2));
            cjkRun.Clear();
        }

        foreach (char character in value.Normalize())
        {
            if (IsCjk(character))
            {
                FlushWord();
                cjkRun.Append(character);
            }
            else if (char.IsLetterOrDigit(character))
            {
                FlushCjk();
                word.Append(char.ToLowerInvariant(character));
            }
            else
            {
                FlushWord();
                FlushCjk();
            }
        }
        FlushWord();
        FlushCjk();
        result.RemoveWhere(RecallStopTerms.Contains);
        return result;
    }

    private static bool IsCjk(char value)
        => value is >= '\u3400' and <= '\u9fff';

    private static string RemoveDatePrefix(string value)
    {
        string trimmed = value.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal))
            return trimmed;
        int close = trimmed.IndexOf(']');
        return close >= 0 && close + 1 < trimmed.Length
            ? trimmed[(close + 1)..].TrimStart()
            : trimmed;
    }

    private static string NormalizeForComparison(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char character in value.Normalize())
        {
            if (char.IsLetterOrDigit(character) || IsCjk(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static int JoinedLength(IReadOnlyList<string> entries)
        => entries.Sum(entry => entry.Length) + Math.Max(0, entries.Count - 1);

    private static string WholeSummaryPatch(string? value)
    {
        string normalized = string.Join(" ", (value ?? string.Empty).Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaximumMemoryEntryCharacters ? normalized : string.Empty;
    }

    private static string LimitSingleLine(string? value, int maximumLength)
    {
        string normalized = string.Join(" ", (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength].TrimEnd();
    }

    private static Random CreateStableRandom(params string?[] values)
    {
        uint hash = 2166136261;
        foreach (string? value in values)
        {
            foreach (char character in value ?? string.Empty)
            {
                hash ^= character;
                hash *= 16777619;
            }
            hash ^= 0xff;
            hash *= 16777619;
        }
        return new Random(unchecked((int)hash));
    }
}
