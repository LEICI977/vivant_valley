namespace VivantValley.Services;

/// <summary>Presence starts ordinary scenes; only real timing dependencies constrain the clock.</summary>
public static class WeeklyStoryInvitationPolicy
{
    public static bool IsPending(WeeklyStoryPlan story)
        => story.Status is WeeklyStoryStatus.MailQueued or WeeklyStoryStatus.Ready;
    public static int Minutes(int time) => time / 100 * 60 + time % 100;
    public static bool IsFlexible(WeeklyStoryPlan story)
        => story.FormatVersion == 3 && story.TimingVersion >= 1 && story.TimingMode == "flexible";

    public static bool MarkDelivered(WeeklyStoryPlan story, int day, int time)
    {
        if (story.InvitationDeliveredDay >= 0) return false;
        story.InvitationDeliveredDay = day;
        // Future dates inferred by the planner never delay a flexible scene.
        if (story.FormatVersion != 3 || story.TimingMode == "appointment" || story.TimingVersion < 1)
            story.ScheduledDay = Math.Max(story.ScheduledDay, day + (time > story.EndTime ? 1 : 0));
        story.ExpiryDay = Math.Max(story.ScheduledDay, day + Math.Clamp(story.InvitationWindowDays, 1, 14));
        return true;
    }

    public static bool CanStartOnSite(WeeklyStoryPlan story, int day, int time, string location)
        => story.FormatVersion == 3 && story.TimingVersion >= 1 && IsPending(story)
           && day >= (IsFlexible(story) ? story.CreatedDay : story.ScheduledDay)
           && day <= story.ExpiryDay && location == story.LocationId
           && day * 1440 + Minutes(time) >= story.RetryAfterMinutes
           && (IsFlexible(story) || time >= story.StartTime
               && (time <= story.EndTime || story.TimingMode == "appointment" && story.ArrivalDay == day));

    public static bool ObserveArrival(WeeklyStoryPlan story, int day, int time, string location)
    {
        bool eligible = story.FormatVersion == 3 && story.TimingVersion >= 1
            ? CanStartOnSite(story,day,time,location)
            : IsPending(story) && story.MailRead && day >= story.ScheduledDay && day <= story.ExpiryDay
                && time >= story.StartTime && time <= story.EndTime && location == story.LocationId
                && day * 1440 + Minutes(time) >= story.RetryAfterMinutes;
        if (!eligible || story.ArrivalDay == day) return false;
        story.ArrivalDay = day;
        return true;
    }

    public static bool CanStart(WeeklyStoryPlan story, int day, int time, string location)
        => story.FormatVersion == 3 && story.TimingVersion >= 1 ? CanStartOnSite(story,day,time,location)
           : story.Status == WeeklyStoryStatus.Ready && story.MailRead
               && day >= story.ScheduledDay && day <= story.ExpiryDay && location == story.LocationId
               && day * 1440 + Minutes(time) >= story.RetryAfterMinutes
               && time >= story.StartTime && (time <= story.EndTime || story.ArrivalDay == day);

    public static bool Migrate(WeeklyStoryPlan story, int day, int days, bool invitationRead = false)
    {
        if (!IsPending(story)) return false;
        bool changed = false;
        if (story.FormatVersion == 3 && story.TimingVersion < 1)
        {
            story.TimingVersion = 1;
            // Preserve read/accepted schedules and already played dialogue. Merely
            // placing an unread letter in the mailbox is not a player agreement.
            bool issued = story.MailRead || invitationRead || story.FreeScene?.Started == true;
            string premise = story.Title + " " + story.Summary + " " + story.FreeScene?.Premise;
            bool dependsOnTime = System.Text.RegularExpressions.Regex.IsMatch(premise,
                @"观星|看星|星空|日落|日出|stargaz|sunset|sunrise|watch(?:ing)? (?:the )?stars",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            story.TimingMode = issued ? "appointment" : dependsOnTime ? "environment" : "flexible";
            story.TimingReason = issued ? "Previously read or accepted schedule" : dependsOnTime ? "Legacy time-dependent activity" : "";
            if (!issued) story.ScheduledDay = story.CreatedDay;
            if (!issued && !dependsOnTime)
            {
                story.ScheduledDay = story.CreatedDay;
                story.StartTime = 600; story.EndTime = 2600; story.TimeSlot = "anytime";
                // Old prewritten greetings may assert a wrong time after relaxing it.
                // No history is changed: only an unstarted, unaccepted opening is replaced.
                if (story.FreeScene is { } scene)
                {
                    scene.Opening = new() { new FreeStoryLine { Actor = story.Inviter,
                        Text = L.T("weekly.onsite.opening"), Target = "player" } };
                    scene.PendingLines.Clear();
                }
                story.MailBody = L.T("weekly.flexible.invitation");
            }
            changed = true;
        }
        if (story.InvitationVersion >= 1) return changed;
        story.InvitationVersion = 1;
        story.InvitationKind = "mail";
        story.InvitationWindowDays = Math.Clamp(days, 1, 14);
        story.InvitationDeliveredDay = day;
        story.ExpiryDay = Math.Max(story.ExpiryDay, day + story.InvitationWindowDays);
        return true;
    }
}
