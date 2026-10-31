using System.Text.Json;
using System.Text.RegularExpressions;

namespace VivantValley.Services;

public static class StoryExperiencePolicy
{
    public static WeeklyStoryHistoryEntry Capture(WeeklyStoryPlan story, int day)
        => new()
        {
            StoryId = story.StoryId, Title = story.Title, LocationId = story.LocationId,
            WeekKey = story.WeekKey, CompletedDay = day,
            Participants = story.Actors.Select(a => a.Name).ToList(),
            PlannedPremise = story.FreeScene!.Premise,
            OpeningTexts = story.FreeScene.Opening.Select(l => l.Text).ToList(),
            ObservedEvents = JsonSerializer.Deserialize<List<FreeStoryEvent>>(JsonSerializer.Serialize(story.FreeScene.Events))!,
            Summary = L.IsChinese ? "本次交流已结束；目标是否达成以实际对话和执行记录为准。"
                : "The encounter ended; goal completion depends on observed dialogue and execution records.",
        };

    public static string PendingDescription(WeeklyStoryHistoryEntry story)
        => L.IsChinese ? $"参加过“{story.Title}”的交流，地点{story.LocationId}。摘要未提供；不能推断玩家已接受、目标已完成或已有后续约定。"
            : $"Attended '{story.Title}' at {story.LocationId}. No summary supplied; do not infer acceptance, goal completion or a follow-up agreement.";

    public static FreeStoryEvent[] Witnessed(WeeklyStoryHistoryEntry story, string npc)
        => story.ObservedEvents.Where(e => e.Witnesses.Contains(npc, StringComparer.Ordinal)).ToArray();

    public static string Fingerprint(WeeklyStoryHistoryEntry story, string npc)
        => MemoryEventArchive.Scope(JsonSerializer.Serialize(Witnessed(story, npc)));

    public static string[] Commit(WeeklyStoryHistoryEntry story, string npc, string fingerprint, string summary)
    {
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 500 || Witnessed(story, npc).Length == 0
            || Fingerprint(story, npc) != fingerprint) return Array.Empty<string>();
        var observers = story.Participants.Where(n => Fingerprint(story, n) == fingerprint).ToArray();
        foreach (string observer in observers) story.WitnessSummaries[observer] = summary;
        return observers;
    }

    public static void Publish(NpcConversationMemory memory, WeeklyStoryHistoryEntry story, string summary)
    {
        string id = "story-summary-" + ContextMemoryCompiler.StableId(story.StoryId);
        string fact = "[story_observed;id=" + id + "] " + summary;
        // A summary may already be archived; never publish it as a new event on retry.
        if (memory.Messages.Any(m => m.DedupeKey == id) || memory.Summary.Contains(fact, StringComparison.Ordinal)) return;
        memory.Messages.Add(new ConversationMemoryMessage { Role = "system", Source = ConversationMemorySources.ModSocial,
            GameDate = "day " + story.CompletedDay, LocationName = story.LocationId,
            EventId = id, DedupeKey = id, Content = "[story_observed] " + summary });
        memory.Summary += "\n" + fact;
    }

    public static async Task<string> SummarizeAsync(IDeepSeekClient client, string key, string model,
        WeeklyStoryHistoryEntry story, string npc, CancellationToken cancellation)
    {
        using var language = L.BeginRequest();
        cancellation.ThrowIfCancellationRequested();
        var records = Witnessed(story, npc).Select(e => new { e.Actor, e.Text, e.Action, e.Target }).ToArray();
        if (records.Length == 0) throw new InvalidOperationException("No witnessed story records.");
        string raw = await client.CompleteChatAsync(key, new DeepSeekChatRequest
        {
            NpcName = npc, TaskKind = "memory_summary", Model = model, MaxTokens = 900, Stream = false,
            Messages = new() { new("system", LocalizedPromptAssets.ForRequest("story-experience-summary.txt")),
                new("user", JsonSerializer.Serialize(new { source = "story_observed", date = story.CompletedDay,
                    location = story.LocationId, observer = npc, records }, ExperienceContextPolicy.ReadableJson)) },
        }, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        using var parsed = JsonDocument.Parse(WeeklyStoryPlannerService.ExtractJsonObject(raw));
        string text = parsed.RootElement.GetProperty("summary").GetString()?.Trim() ?? "";
        if (text.Length is < 1 or > 500) throw new InvalidDataException("Invalid story summary; source retained.");
        return text;
    }

    public static bool IsRepeated(string title, string location, IReadOnlyList<FreeStoryLine> opening,
        IEnumerable<WeeklyStoryHistoryEntry> histories, IEnumerable<string> legacyAnchors, out string failure)
    {
        static string Normalize(string s) => Regex.Replace(s, @"[\s\p{P}\p{S}]", "").ToLowerInvariant();
        string normalTitle = Normalize(title);
        foreach (var history in histories.TakeLast(6))
        {
            if (normalTitle.Length > 0 && normalTitle == Normalize(history.Title))
            { failure = "Repeated story title: " + history.Title + ". Create a new cause and opening; do not replay this encounter."; return true; }
            foreach (var line in opening)
            {
                string text = Normalize(line.Text);
                if (text.Length < 24) continue;
                if (history.OpeningTexts.Any(old => Similar(text, Normalize(old)))
                    || Normalize(history.Summary).Contains(text, StringComparison.Ordinal))
                { failure = "Repeated opening from " + history.Title + ". Choose a new situation, not a paraphrase of these lines."; return true; }
            }
        }
        foreach (string old in legacyAnchors)
            if (opening.Any(l => Normalize(l.Text).Length >= 24 && Normalize(old).Contains(Normalize(l.Text), StringComparison.Ordinal)))
            { failure = "Repeated historical opening. Create a new situation rather than replaying recentStories."; return true; }
        failure = ""; return false;
    }
    private static bool Similar(string a, string b)
    {
        if (a == b) return true;
        if (a.Length < 24 || b.Length < 24) return false;
        static HashSet<string> Grams(string s) => Enumerable.Range(0, s.Length - 2).Select(i => s.Substring(i, 3)).ToHashSet();
        var x = Grams(a); var y = Grams(b);
        return (double)x.Intersect(y).Count() / Math.Max(1, x.Union(y).Count()) >= .8;
    }
}
