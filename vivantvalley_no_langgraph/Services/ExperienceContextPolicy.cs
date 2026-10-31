using System.Text.Json;
using System.Text.Encodings.Web;

namespace VivantValley.Services;

/// <summary>A read-only projection of existing personal memory, never a second event ledger.</summary>
public sealed record ExperienceFact(string Source, string Date, string Status, string Text);

public static class ExperienceContextPolicy
{
    public static readonly JsonSerializerOptions ReadableJson = new()
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static IReadOnlyList<ExperienceFact> Build(string npc, int day, NpcConversationMemory? memory,
        IEnumerable<DreamEpisode> dreams, IEnumerable<WeeklyStoryHistoryEntry> stories,
        IEnumerable<SocialMailGift> mail, IEnumerable<RecentKnownEvent> news,
        int maximum = 4, int tokenBudget = 2400)
    {
        var result = new List<ExperienceFact>();
        bool Add(string source, string date, string status, string text)
        {
            text = ConversationRecallPolicy.Readable(text);
            if (text.Length == 0 || result.Count >= maximum || result.Any(e => e.Source == source
                || ConversationRecallPolicy.Covers(e.Text, text))) return false;
            int cost = ContextMemoryCompiler.EstimateTokens(text) + 40;
            if (cost > tokenBudget) return false; // Whole summaries only: never cut away an outcome or refusal.
            tokenBudget -= cost;
            result.Add(new(source, date, status, text)); return true;
        }
        // Prefer finished summaries to scattered quotations. Dates describe the source, not today.
        if (memory is not null)
            Add("dialogue_summary", "previous conversation segment", "historical_not_current_state", memory.SegmentSummary);
        var dream = dreams.Where(e => e.Npc == npc && e.Day <= day && day - e.Day <= 7
            && e.Status != "active" && e.Lines.Any(l => l.Role == "assistant")).OrderByDescending(e => e.Day).FirstOrDefault();
        if (dream is not null)
            Add("dream_only", "night " + dream.Day, "not_waking_actions_or_promises", string.IsNullOrWhiteSpace(dream.Summary)
                ? (L.IsChinese ? "共同经历过一次梦境交谈，摘要尚未完成，不推断内容。" : "Shared a dream conversation; summary is pending, so do not infer its content.")
                : dream.Summary);
        var story = stories.Where(h => h.CompletedDay <= day && day - h.CompletedDay <= 14 && h.Participants.Contains(npc))
            .OrderByDescending(h => h.CompletedDay).FirstOrDefault();
        if (story is not null)
            Add("story_observed", "day " + story.CompletedDay, "encounter_ended_not_goal_completed",
                story.WitnessSummaries.TryGetValue(npc, out string? summary) ? summary : StoryExperiencePolicy.PendingDescription(story));
        var letter = mail.Where(m => m.NpcName == npc && m.IsQueued && m.QueuedDay <= day
            && day - Math.Max(m.QueuedDay, m.RewardDeliveredDay) <= 14).OrderByDescending(m => Math.Max(m.QueuedDay, m.RewardDeliveredDay)).FirstOrDefault();
        if (letter is not null)
            Add("mail", "queued day " + letter.QueuedDay + (letter.RewardDelivered ? "; claimed day " + letter.RewardDeliveredDay : ""),
                letter.RewardDelivered ? "attachment_claimed" : "queued_reading_and_claim_not_confirmed",
                (L.IsChinese ? $"寄出附件为{letter.GiftDisplayName}的信。" : $"Sent a letter with {letter.GiftDisplayName} as its attachment.")
                + (letter.LetterBody.Length <= 400 ? (L.IsChinese ? "信中说：" : "Letter says: ") + letter.LetterBody : ""));
        foreach (var item in news.Where(n => n.Day <= day).OrderByDescending(n => n.Day))
            Add(item.Kind, "day " + item.Day, item.Kind == "heard" ? "hearsay_not_witnessed" : "recorded", item.Fact);
        if (memory is not null)
        {
            foreach (var message in memory.Messages.AsEnumerable().Reverse().Where(m => m.Role == "system"
                && m.Source is ConversationMemorySources.ModGift or ConversationMemorySources.VanillaGift
                    or ConversationMemorySources.ModAction or ConversationMemorySources.VanillaEvent
                && !m.DedupeKey.Contains(":scene:", StringComparison.Ordinal)
                && !m.DedupeKey.StartsWith("story-summary-", StringComparison.Ordinal)))
                Add("observed_event", message.GameDate, "code_recorded", message.Content);
            // Persistent preferences remain useful when there are no recent episodes.
            foreach (var fact in ContextMemoryCompiler.FromSummary(memory.Summary).Reverse()
                .Where(f => f.Source is "derived_dialogue" or "dialogue" && f.Status is null
                    && !f.Text.Contains("[said]", StringComparison.Ordinal) && !f.Text.Contains("[left]", StringComparison.Ordinal)
                    && !f.Text.Contains("DREAM", StringComparison.OrdinalIgnoreCase) && !f.Text.Contains("梦境", StringComparison.Ordinal)))
                Add("past_context", "historical; exact date unavailable", "not_an_active_promise", fact.Text);
        }
        return result;
    }
}
