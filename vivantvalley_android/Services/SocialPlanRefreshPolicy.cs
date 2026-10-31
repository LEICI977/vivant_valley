namespace VivantValley.Services;

public static class SocialPlanRefreshPolicy
{
    public static bool ShouldDefer(DailySocialPlan? plan, int day, bool hasActiveScene)
        => plan?.Day == day && (hasActiveScene || plan.Candidates.Any(candidate =>
            candidate.Status is DailySocialCandidateStatus.Generating
                or DailySocialCandidateStatus.Ready or DailySocialCandidateStatus.Presenting));

    public static void PreserveProgress(DailySocialPlan? previous, DailySocialPlan next,
        IReadOnlyDictionary<string, NpcSocialState> npcStates)
    {
        if (previous?.Day != next.Day)
            return;

        // Retain terminal opportunities even if this mode no longer selects their NPC.
        // This keeps the daily completion count and sibling cancellation stable.
        foreach (DailySocialCandidate old in previous.Candidates.Where(candidate =>
                     candidate.Status is DailySocialCandidateStatus.Completed
                         or DailySocialCandidateStatus.Cancelled or DailySocialCandidateStatus.Expired))
        {
            next.Candidates.RemoveAll(candidate => candidate.NpcName.Equals(old.NpcName,
                StringComparison.OrdinalIgnoreCase) && candidate.TimeSlot == old.TimeSlot);
            next.Candidates.Add(old);
        }

        foreach (DailySocialCandidate candidate in next.Candidates)
        {
            if (npcStates.TryGetValue(candidate.NpcName, out NpcSocialState? state)
                && state.CompletedActionIds.Contains(candidate.ActionId))
                candidate.Status = DailySocialCandidateStatus.Completed;
        }
        foreach (DailySocialCandidate completed in next.Candidates
                     .Where(candidate => candidate.Status == DailySocialCandidateStatus.Completed).ToArray())
            DailySocialPlanner.CloseSiblingOpportunities(next, completed.NpcName, completed.ActionId);

        next.TriggeredCount = Math.Max(previous.TriggeredCount,
            next.Candidates.Count(candidate => candidate.Status == DailySocialCandidateStatus.Completed));
        next.GiftCount = previous.GiftCount;
        // Normalization caps candidates; retain completed receipts ahead of fresh opportunities.
        next.Candidates = next.Candidates.OrderBy(candidate =>
            candidate.Status == DailySocialCandidateStatus.Completed ? 0 : 1).ToList();
    }
}
