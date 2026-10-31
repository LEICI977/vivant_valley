using System.Text.Json;
using VivantValley;
using VivantValley.Menus;
using VivantValley.Services;

internal static class SceneTimingTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static WeeklyStoryPlan Scene() => new()
    {
        FormatVersion = 3, TimingVersion = 1, TimingMode = "flexible", CreatedDay = 10,
        ScheduledDay = 12, ExpiryDay = 15, StartTime = 1700, EndTime = 2150,
        LocationId = "Farm", LocationDisplayName = "Farm", Status = WeeklyStoryStatus.MailQueued,
        InvitationVersion = 1, Inviter = "Sam", FreeScene = new() { Premise = "A chat about the farm" },
    };
    public static void Run()
    {
        var s = Scene();
        Check(WeeklyStoryInvitationPolicy.CanStart(s,10,610,"Farm"), "Morning farm chat still waits for a date, hour or invitation.");
        Check(WeeklyStoryInvitationPolicy.CanStart(s,10,2400,"Farm"), "Flexible chat gained an evening cutoff.");
        Check(!s.MailRead && s.Status == WeeklyStoryStatus.MailQueued, "Checking eligibility forged acceptance.");
        Check(!WeeklyStoryInvitationPolicy.CanStart(s,10,900,"Town"), "Wrong map starts scene.");
        Check(!WeeklyStoryInvitationPolicy.CanStart(s,16,900,"Farm"), "Expired story starts.");
        s.RetryAfterMinutes = 10 * 1440 + 600;
        Check(!WeeklyStoryInvitationPolicy.CanStart(s,10,900,"Farm") && WeeklyStoryInvitationPolicy.CanStart(s,10,1000,"Farm"), "Later/retry delay lost.");
        s.RetryAfterMinutes = 0;
        foreach (var status in new[] {WeeklyStoryStatus.Declined, WeeklyStoryStatus.Completed, WeeklyStoryStatus.Expired, WeeklyStoryStatus.Running})
        { s.Status = status; Check(!WeeklyStoryInvitationPolicy.CanStart(s,10,900,"Farm"), "Terminal/running scene restarted."); }
        s = Scene(); s.TimingMode = "environment"; s.ScheduledDay = 10; s.StartTime = 2000; s.EndTime = 2500;
        Check(!WeeklyStoryInvitationPolicy.CanStart(s,10,900,"Farm") && WeeklyStoryInvitationPolicy.CanStart(s,10,2100,"Farm"), "Night dependency ignored or cannot start without letter.");
        s.ArrivalDay = 10;
        Check(!WeeklyStoryInvitationPolicy.CanStart(s,10,2530,"Farm"), "Arrival bypasses environment requirement.");
        s.TimingMode = "appointment"; s.ScheduledDay = 12;
        Check(!WeeklyStoryInvitationPolicy.CanStart(s,10,2100,"Farm"), "Confirmed future appointment pulled forward.");
        s = Scene(); s.TimingVersion = 0; s.InvitationDeliveredDay = 10;
        s.FreeScene!.Opening.Add(new FreeStoryLine {Actor="Sam",Text="Good afternoon."});
        Check(WeeklyStoryInvitationPolicy.Migrate(s,10,3) && s.TimingMode == "flexible", "Unread legacy letter still locks ordinary farm scene.");
        Check(s.FreeScene.Opening.Count == 1 && !s.FreeScene.Opening[0].Text.Contains("afternoon"), "Old greeting contradicts new time.");
        Check(!WeeklyStoryInvitationPolicy.Migrate(s,11,3), "Migration runs more than once.");
        s = Scene(); s.TimingVersion = 0; s.MailRead = true;
        WeeklyStoryInvitationPolicy.Migrate(s,10,3);
        Check(s.TimingMode == "appointment" && s.ScheduledDay == 12 && s.StartTime == 1700, "Read/accepted legacy schedule changed.");
        s = Scene(); s.TimingVersion = 0;
        WeeklyStoryInvitationPolicy.Migrate(s,10,3,invitationRead:true);
        Check(s.TimingMode == "appointment", "Vanilla read-mail state not preserved on load.");
        s = Scene(); s.TimingVersion = 0; s.Title = "Stargazing";
        WeeklyStoryInvitationPolicy.Migrate(s,10,3);
        Check(s.TimingMode == "environment" && s.ScheduledDay == s.CreatedDay, "Old environment dependency became unrestricted or kept an arbitrary future date.");
        s = Scene(); s.TimingVersion = 0; s.FreeScene!.Started = true; s.FreeScene.Events.Add(new FreeStoryEvent{Text="Played line"});
        WeeklyStoryInvitationPolicy.Migrate(s,10,3);
        Check(s.TimingMode == "appointment" && s.FreeScene.Events.Single().Text == "Played line", "In-progress history changed.");
        s = JsonSerializer.Deserialize<WeeklyStoryPlan>(JsonSerializer.Serialize(Scene()))!; s.Normalize();
        Check(WeeklyStoryInvitationPolicy.CanStart(s,10,700,"Farm"), "Save/load restores artificial date restriction.");
        foreach (var locale in new[] { "zh", "en" }) using (L.BeginRequest(locale))
        {
            string schedule = WeeklyStoryMailService.DescribeSchedule(Scene());
            Check(schedule.Contains("Farm") && !schedule.Contains("17:00"), "Flexible invitation still advertises an arbitrary hour.");
        }
        var config = new ModConfig();
        Check(config.ConversationUiScale == 1f, "New config isn't 100%.");
        config.ConversationUiScale = .75f;
        Check(ConversationUiLayout.MigrateDefaults(config) && config.ConversationUiScale == 1f, "Old default not migrated.");
        config.ConversationUiScale = .75f;
        Check(!ConversationUiLayout.MigrateDefaults(config) && config.ConversationUiScale == .75f, "User's later preference overwritten.");
        config = new ModConfig { ConversationUiScale = 1.3f };
        ConversationUiLayout.MigrateDefaults(config);
        Check(config.ConversationUiScale == 1.3f, "Custom scale overwritten.");
        Console.WriteLine("Flexible scene timing, same-map entry, legacy promises, expiry/defer and input default migration passed.");
    }
}
