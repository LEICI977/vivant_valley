using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class StoryInvitationTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        var proximity = new EncounterProximityState();
        Check(EncounterProximityPolicy.Observe(proximity, 4, 5, 10, 30), "First approach did not create an opportunity.");
        for (int i = 0; i < 120; i++)
            Check(!EncounterProximityPolicy.Observe(proximity, 4, 5, 10, 30), "Standing near an NPC rerolled.");
        for (int i = 0; i < 29; i++) EncounterProximityPolicy.Observe(proximity, 11, 5, 10, 30);
        Check(!EncounterProximityPolicy.Observe(proximity, 4, 5, 10, 30), "Brief separation rearmed an encounter.");
        // Save and reload while still nearby: a failed roll remains consumed.
        proximity = JsonSerializer.Deserialize<EncounterProximityState>(JsonSerializer.Serialize(proximity))!;
        Check(!EncounterProximityPolicy.Observe(proximity, 4, 5, 10, 30), "Reload rerolled a consumed encounter.");
        for (int i = 0; i < 30; i++) EncounterProximityPolicy.Observe(proximity, float.PositiveInfinity, 5, 10, 30);
        Check(EncounterProximityPolicy.Observe(proximity, 4, 5, 10, 30), "Returning after separation did not rearm.");
        var other = new EncounterProximityState();
        Check(EncounterProximityPolicy.Observe(other, 4, 5, 10, 30), "NPC proximity state leaked between NPCs.");

        foreach (string kind in new[] { "mail", "direct", "visit", "phone" })
        {
            var story = new WeeklyStoryPlan { Status = WeeklyStoryStatus.MailQueued, InvitationKind = kind,
                ScheduledDay = 100, ExpiryDay = 105, InvitationWindowDays = 3, LocationId = "Beach", StartTime = 1700, EndTime = 2150 };
            Check(WeeklyStoryInvitationPolicy.MarkDelivered(story, 101, 2200), "Delivery missing.");
            Check(story.ScheduledDay == 102 && story.ExpiryDay == 104, "Three full future days were not preserved after late delivery.");
            Check(!WeeklyStoryInvitationPolicy.MarkDelivered(story, 102, 1800) && story.ExpiryDay == 104, "Reopening extended the invitation.");
            Check(!WeeklyStoryInvitationPolicy.CanStart(story, 102, 1800, "Beach"), "Unread invitation started a story.");
            story.MailRead = true; story.Status = WeeklyStoryStatus.Ready;
            Check(!WeeklyStoryInvitationPolicy.CanStart(story, 102, 1800, "Town"), "Wrong location started a story.");
            Check(WeeklyStoryInvitationPolicy.ObserveArrival(story, 102, 2150, "Beach"), "On-time arrival was lost.");
            Check(WeeklyStoryInvitationPolicy.CanStart(story, 102, 2200, "Beach"), "A temporarily blocked on-time arrival expired.");
            Check(!WeeklyStoryInvitationPolicy.CanStart(story, 103, 2200, "Beach"), "Yesterday's arrival bypassed today's window.");
            Check(!WeeklyStoryInvitationPolicy.CanStart(story, 105, 1800, "Beach"), "Expired invitation started.");
            story.Status = WeeklyStoryStatus.Declined;
            Check(!WeeklyStoryInvitationPolicy.CanStart(story, 102, 1800, "Beach"), "Declined invitation reopened.");
            Check(!WeeklyStoryInvitationPolicy.IsPending(story), "Declined invitation remained deliverable.");
        }
        var legacy = new WeeklyStoryPlan { Status = WeeklyStoryStatus.Ready, ScheduledDay = 100, ExpiryDay = 100, MailRead = true };
        Check(WeeklyStoryInvitationPolicy.Migrate(legacy, 101, 3) && legacy.ExpiryDay == 104 && legacy.MailRead, "Legacy pending invitation was lost.");
        Check(!WeeklyStoryInvitationPolicy.Migrate(legacy, 102, 3) && legacy.ExpiryDay == 104, "Migration extended twice.");
        legacy.Status = WeeklyStoryStatus.Completed; legacy.InvitationVersion = 0;
        Check(!WeeklyStoryInvitationPolicy.Migrate(legacy, 110, 3), "Migration revived completed content.");
        legacy.Status = WeeklyStoryStatus.Ready; legacy.ScheduledDay = 100; legacy.ExpiryDay = 110; legacy.LocationId = "Beach";
        legacy.StartTime = 1700; legacy.EndTime = 2150; legacy.RetryAfterMinutes = 105 * 1440 + 19 * 60;
        Check(!WeeklyStoryInvitationPolicy.ObserveArrival(legacy, 105, 1800, "Beach"), "Later choice immediately reopened the story.");
        Check(WeeklyStoryInvitationPolicy.CanStart(legacy, 105, 1900, "Beach"), "Deferred story never resumed.");

        foreach (string locale in new[] { "zh", "en" })
        using (L.BeginRequest(locale))
        {
            var personality = new NpcPersonalitySettings { Tone = "TONE_MARKER", Traits = "TRAITS_MARKER", Expression = "EXPRESSION_MARKER" };
            var client = new RecordingClient("{\"dialogue\":\"Hello.\",\"action\":\"talk_only\",\"giftCandidateId\":null,\"motiveTag\":\"greeting\"}");
            await new AiSocialSceneService(client).GenerateAsync("test", new AiSocialSceneRequest
            {
                NpcName = "Abigail", NpcDisplayName = "Abigail", Personality = personality,
                RecentConversation = "长期摘要：MEMORY_MARKER\nNPC：TIMELINE_MARKER",
            });
            string shared = ConversationPromptService.BuildSystemPrompt(new ConversationPromptService().Load().SystemRules,
                RidgesideVillageCompatibility.BuildConversationIdentityPrompt("Abigail", "Abigail"), personality);
            string system = client.Requests.Single().Messages[0].Content;
            Check(system.StartsWith(shared, StringComparison.Ordinal), "Proactive prompt diverged from the manual-chat identity/personality prefix.");
            Check(system.Contains("TONE_MARKER") && system.Contains("TRAITS_MARKER") && system.Contains("EXPRESSION_MARKER"), "Proactive personality was lost.");
            string payload = client.Requests.Single().Messages[1].Content;
            Check(payload.IndexOf("\"memory\"", StringComparison.Ordinal) < payload.IndexOf("\"timeline\"", StringComparison.Ordinal), "Proactive context order changed.");
            string schedule = WeeklyStoryMailService.DescribeSchedule(legacy);
            Check(schedule.Contains("17:00–21:50") && schedule.Contains("Beach"), "Invitation omitted actual trigger hours/location.");
            foreach (string key in new[] { "weekly.invitation.phone", "weekly.invitation.accept", "weekly.invitation.decline" })
                Check(L.T(key) != key, "Missing invitation translation " + locale + ": " + key);
        }
        Console.WriteLine("Encounter reentry, reload, invitation delivery/expiry/arrival/decline, bilingual shared-personality prompt tests passed.");
    }
}
