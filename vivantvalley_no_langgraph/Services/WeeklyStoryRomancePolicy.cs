namespace VivantValley.Services;

/// <summary>Observed relationship history for prompt context; no local intimacy tiers.</summary>
public static class WeeklyStoryRomancePolicy
{
    public static bool HasObservedInterest(IEnumerable<WeeklyStoryHistoryEntry> history, string npcName)
    {
        WeeklyStoryChoiceRecord? latest = history.SelectMany(entry => entry.Selections)
            .LastOrDefault(selection => selection.ResponseDelivered
                && selection.ResponseActor.Equals(npcName, StringComparison.OrdinalIgnoreCase)
                && (selection.Boundary || selection.Outcome is "romantic" or "distant"));
        return latest is { Boundary: false, Outcome: "romantic" };
    }
}
