namespace VivantValley.Services;

/// <summary>One budget per save/player/week, including cancelled requests.</summary>
public static class WeeklyStoryPlanningPolicy
{
    public const int MaximumAttempts = 2;

    public static bool MigrateGenerationSettings(ModConfig config)
    {
        if (config.WeeklyStoryGenerationSettingsVersion >= 1) return false;
        if (config.WeeklyStoryMaxOutputTokens == 2400) config.WeeklyStoryMaxOutputTokens = 4000;
        config.WeeklyStoryGenerationSettingsVersion = 1;
        return true;
    }

    public static IReadOnlyList<string> AvailableTimeSlots(WeeklyStoryPlanRequest request)
    {
        int time = request.ScheduledDay > request.CurrentDay ? 600 : request.CurrentTimeOfDay;
        return new[] { ("morning", 1050), ("afternoon", 1550), ("evening", 2050), ("night", 2400) }
            .Where(slot => time < slot.Item2).Select(slot => slot.Item1).ToArray();
    }

    public static bool TryReserve(WeeklyStoryPlanningProgress progress, int week)
    {
        if (week < progress.WeekKey) return false;
        if (week > progress.WeekKey)
        {
            progress.WeekKey = week;
            progress.Attempts = 0;
            progress.Status = "idle";
            progress.FailureReason = string.Empty;
            progress.FailureKind = string.Empty;
            progress.CachedPlan = null;
        }
        if (progress.Attempts >= MaximumAttempts || progress.Status == "succeeded") return false;
        progress.Attempts++;
        progress.Status = "requesting";
        return true;
    }

    public static void Fail(WeeklyStoryPlanningProgress progress, string reason, string kind = "validation")
    {
        progress.Status = "failed";
        progress.FailureReason = WeeklyStoryPlan.LimitText(reason, 400);
        progress.FailureKind = WeeklyStoryPlan.LimitToken(kind, 32);
    }
}
