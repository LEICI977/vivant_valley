using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using VivantValley.Services;

namespace VivantValley;

public sealed partial class ModEntry
{
    private IClickableMenu? weeklyInvitationMenu;
    private InvitationActor? weeklyInvitationNpc;
    private bool weeklyVisitDoorReady;
    private string weeklyBlockedReason = string.Empty;
    private string weeklyPhoneCallId = string.Empty;
    private sealed record InvitationActor(NPC Npc, GameLocation Location, Vector2 Position, int Facing, bool IgnoreSchedule);

    private static bool HasWeeklyInvitationPhone()
        => Game1.locations.Any(location => location.Objects.Values.Any(item => item is Phone));

    private bool CanOfferWeeklyInvitation()
    {
        ConversationScreenState state = screenStates.Value;
        return Context.IsWorldReady && Context.IsMainPlayer && config.EnableWeeklyStories
            && !weeklyStoryDayEnding && !weeklyStoryRunner.IsRunning && weeklyInvitationMenu is null
            && weeklyStoryWarpDelayTicks == 0 && !IsWeeklyStoryFestivalDay() && CanOpenOwnMenu()
            && !state.HasPendingConversation && state.PendingSocialScene is null
            && state.QueuedSocialScene is null && state.ActiveSocialScene is null
            && state.QueuedDialogue is null;
    }

    private bool IsWeeklyInviterAvailable(NPC npc)
        => npc.CanSocialize && !npc.IsInvisible && !npc.isSleeping.Value && !npc.swimming.Value
           && !npc.doingEndOfRouteAnimation.Value && npc.Sprite.CurrentAnimation is null
           && !npcCombatStateService.IsHospitalized(npc.Name)
           && !npcMoveToolService.HasActiveSession(npc.Name) && !npcMineGuardService.HasActiveSession(npc.Name)
           && !npcFishingService.HasActiveSession(npc.Name);

    private bool MigrateWeeklyStoryTiming(WeeklyStoryPlan story)
    {
        bool changed = WeeklyStoryInvitationPolicy.Migrate(story, Game1.Date.TotalDays,
            config.WeeklyStoryInvitationDays, Game1.player.mailReceived.Contains(story.MailId));
        if (changed) InvalidateGiftMailAsset();
        return changed;
    }

    private void UpdateWeeklyInvitations()
    {
        if (!config.EnableWeeklyStories) return;
        WeeklyStoryPlan? story = weeklyStoryStore.GetOrCreate(GetPlayerId()).CurrentStory;
        if (story is null || !WeeklyStoryInvitationPolicy.IsPending(story)) return;
        int day = Game1.Date.TotalDays;
        if (MigrateWeeklyStoryTiming(story)) weeklyStoryDirty = true;
        if (day > story.ExpiryDay)
        {
            story.Status = WeeklyStoryStatus.Expired;
            weeklyStoryDirty = true;
            StopWeeklyInvitationPhone();
            return;
        }
        // Don't interrupt a local opening with an invitation, even when a menu or
        // another conversation means the scene must wait until the player is free.
        if (weeklyStoryRunner.IsRunning || WeeklyStoryInvitationPolicy.CanStartOnSite(story, day, Game1.timeOfDay,
            Game1.currentLocation.NameOrUniqueName))
        { StopWeeklyInvitationPhone(); return; }
        if (story.InvitationKind == "mail")
        {
            if (!story.MailRead && Game1.player.mailReceived.Contains(story.MailId))
            {
                story.MailRead = true;
                story.Status = WeeklyStoryStatus.Ready;
                weeklyStoryDirty = true;
            }
            if (story.MailRead) return;
            if (story.InvitationDeliveredDay < 0 || (!Game1.player.mailbox.Contains(story.MailId)
                && !Game1.player.mailReceived.Contains(story.MailId)))
            {
                if (!TryQueueWeeklyStoryMail(story, out string failure)) LogWeeklyStoryBlocked(story, "mail: " + failure);
            }
            return;
        }
        // A missed call / never meeting the inviter cannot strand a generated story.
        // Once delivered, reopening never extends the window.
        if (story.InvitationDeliveredDay < 0 && day >= story.CreatedDay + 2)
        {
            StopWeeklyInvitationPhone();
            story.InvitationKind = "mail";
            weeklyStoryDirty = true;
            if (!TryQueueWeeklyStoryMail(story, out string failure)) LogWeeklyStoryBlocked(story, "mail: " + failure);
            return;
        }
        if (story.MailRead || story.LastInvitationOfferDay == day || !CanOfferWeeklyInvitation()) return;
        NPC? npc = Game1.getCharacterFromName(story.Inviter, mustBeVillager: false, includeEventActors: false);
        if (npc is null || !IsWeeklyInviterAvailable(npc)) return;
        if (story.InvitationKind == "phone")
        {
            if (Game1.timeOfDay < 800 || Game1.timeOfDay > 2100 || Phone.whichPhoneCall is not null
                || !Game1.currentLocation.Objects.Values.Any(item => item is Phone)) return;
            weeklyPhoneCallId = "VivantValley.Weekly." + story.StoryId;
            Phone.Ring(weeklyPhoneCallId);
            Phone.intervalsToRing = 3;
            story.LastInvitationOfferDay = day;
            weeklyStoryDirty = true;
            return;
        }
        if (story.InvitationKind == "visit")
        {
            bool spouseInside = Game1.currentLocation is StardewValley.Locations.FarmHouse
                && string.Equals(Game1.player.spouse, npc.Name, StringComparison.OrdinalIgnoreCase);
            if (Game1.timeOfDay > 1200 || (!spouseInside && !(weeklyVisitDoorReady && Game1.currentLocation is Farm))) return;
            if (!StageWeeklyVisitor(npc)) return;
            weeklyVisitDoorReady = false;
        }
        else if (!ReferenceEquals(npc.currentLocation, Game1.currentLocation)
            || Vector2.DistanceSquared(Game1.player.Tile, npc.Tile) > 25) return;
        ShowWeeklyInvitation(story, npc, story.InvitationKind);
    }

    private bool StageWeeklyVisitor(NPC npc)
    {
        if (npc.currentLocation is null) return false;
        var pathfinder = new NpcTilePathfinder();
        if (!pathfinder.TryFindSafeFollowTile(Game1.currentLocation, npc, Game1.player.TilePoint, 2, 5, out Point tile)) return false;
        weeklyInvitationNpc = new InvitationActor(npc, npc.currentLocation, npc.Position, npc.FacingDirection, npc.ignoreScheduleToday);
        try
        {
            npc.controller = null;
            npc.temporaryController = null;
            npc.Halt();
            npc.ignoreScheduleToday = true;
            Game1.warpCharacter(npc, Game1.currentLocation, tile.ToVector2());
            npc.facePlayer(Game1.player);
            return true;
        }
        catch (Exception ex)
        {
            Monitor.Log("weekly invitation staging: " + ex.Message, LogLevel.Warn);
            CloseWeeklyInvitationPresentation();
            return false;
        }
    }

    private void ShowWeeklyInvitation(WeeklyStoryPlan story, NPC npc, string channel)
    {
        int delivery = story.InvitationDeliveredDay, scheduled = story.ScheduledDay, expiry = story.ExpiryDay;
        int offered = story.LastInvitationOfferDay;
        try { ShowWeeklyInvitationCore(story, npc, channel); }
        catch (Exception ex)
        {
            story.InvitationDeliveredDay = delivery;
            story.ScheduledDay = scheduled;
            story.ExpiryDay = expiry;
            story.LastInvitationOfferDay = offered;
            CloseWeeklyInvitationPresentation();
            Monitor.Log("weekly invitation display: " + ex, LogLevel.Warn);
        }
    }

    private void ShowWeeklyInvitationCore(WeeklyStoryPlan story, NPC npc, string channel)
    {
        if (!WeeklyStoryInvitationPolicy.IsPending(story)
            || WeeklyStoryInvitationPolicy.CanStartOnSite(story, Game1.Date.TotalDays, Game1.timeOfDay,
                Game1.currentLocation.NameOrUniqueName))
        { CloseWeeklyInvitationPresentation(); return; }
        WeeklyStoryInvitationPolicy.MarkDelivered(story, Game1.Date.TotalDays, Game1.timeOfDay);
        story.LastInvitationOfferDay = Game1.Date.TotalDays;
        string context = L.T("weekly.invitation." + channel);
        string body = GiftMailContentService.Sanitize(story.MailBody, L.T("weekly.invitation.fallback"), 420);
        string schedule = WeeklyStoryMailService.DescribeSchedule(story);
        string question = $"{npc.displayName} — {context}#{body}#{schedule}";
        Game1.currentLocation.createQuestionDialogue(question, new[]
        {
            new Response("vv_accept", L.T("weekly.invitation.accept")),
            new Response("vv_later", L.T("weekly.invitation.later")),
            new Response("vv_decline", L.T("weekly.invitation.decline")),
        }, (_, answer) =>
        {
            if (!ReferenceEquals(weeklyStoryStore.GetOrCreate(GetPlayerId()).CurrentStory, story)
                || !WeeklyStoryInvitationPolicy.IsPending(story)) return;
            if (answer is not ("vv_accept" or "vv_later" or "vv_decline")) return;
            if (answer == "vv_decline")
            {
                story.Status = WeeklyStoryStatus.Declined;
                story.MailRead = false;
            }
            else
            {
                story.Status = WeeklyStoryStatus.Ready;
                story.MailRead = true;
                if (answer == "vv_later")
                {
                    story.ArrivalDay = -1;
                    story.RetryAfterMinutes = Game1.Date.TotalDays * 1440 + WeeklyStoryInvitationPolicy.Minutes(Game1.timeOfDay) + 60;
                }
                ShowHud(schedule, HUDMessage.newQuest_type);
            }
            RecordWeeklyInvitationMemory(story, channel, answer);
            weeklyStoryDirty = true;
            PersistWeeklyStories(force: true);
        });
        weeklyInvitationMenu = Game1.activeClickableMenu;
        weeklyStoryDirty = true;
        PersistWeeklyStories(force: true);
    }

    private void RecordWeeklyInvitationMemory(WeeklyStoryPlan story, string channel, string answer)
    {
        NpcConversationMemory memory = memoryStore.GetOrCreate(GetPlayerId(), story.Inviter);
        string date = $"{Game1.Date} {Game1.timeOfDay}";
        string fact = L.IsChinese
            ? $"{story.Inviter}通过{L.T("weekly.invitation." + channel)}邀请玩家参加“{story.Title}”。玩家{(answer == "vv_decline" ? "拒绝了" : answer == "vv_later" ? "接受，但表示稍后参加" : "接受了")}。{WeeklyStoryMailService.DescribeSchedule(story)}。活动尚未完成。"
            : $"{story.Inviter} invited the player to '{story.Title}' via {channel}. The player {(answer == "vv_decline" ? "declined" : answer == "vv_later" ? "accepted for later" : "accepted")}. {WeeklyStoryMailService.DescribeSchedule(story)}. The activity has not been completed.";
        memory.Messages.Add(new ConversationMemoryMessage { Role = "system", Content = fact, GameDate = date,
            Source = ConversationMemorySources.ModAction, EventId = story.StoryId + ":invite", DedupeKey = story.StoryId + ":invite" });
        memory.Messages = ConversationTimelinePolicy.RetainRecentMessages(memory);
        memoryDirty = true;
        PersistMemory(force: false);
    }

    private void UpdateWeeklyInvitationPresentation()
    {
        if (weeklyInvitationMenu is not null && !ReferenceEquals(Game1.activeClickableMenu, weeklyInvitationMenu))
            CloseWeeklyInvitationPresentation();
    }

    private void StopWeeklyInvitationPhone()
    {
        if (weeklyPhoneCallId.Length > 0 && Phone.whichPhoneCall == weeklyPhoneCallId) Phone.StopRinging();
        weeklyPhoneCallId = string.Empty;
    }

    private void CloseWeeklyInvitationPresentation()
    {
        if (weeklyInvitationMenu is not null && ReferenceEquals(Game1.activeClickableMenu, weeklyInvitationMenu))
            Game1.exitActiveMenu();
        weeklyInvitationMenu = null;
        StopWeeklyInvitationPhone();
        InvitationActor? actor = weeklyInvitationNpc;
        weeklyInvitationNpc = null;
        if (actor is null) return;
        try
        {
            actor.Npc.Halt();
            actor.Npc.ignoreScheduleToday = actor.IgnoreSchedule;
            Game1.warpCharacter(actor.Npc, actor.Location, actor.Position / Game1.tileSize);
            actor.Npc.Position = actor.Position;
            actor.Npc.faceDirection(actor.Facing);
            if (!actor.IgnoreSchedule) new NpcScheduleRecoveryService(Monitor).Release(actor.Npc, false, "weekly_invitation");
        }
        catch (Exception ex) { Monitor.Log("weekly invitation restore: " + ex, LogLevel.Error); }
    }

    private void ObserveWeeklyStoryArrival()
    {
        WeeklyStoryPlan? story = weeklyStoryStore.GetOrCreate(GetPlayerId()).CurrentStory;
        if (story is null || !config.EnableWeeklyStories) return;
        string location = Game1.currentLocation?.NameOrUniqueName ?? string.Empty;
        if (story.ArrivalDay >= 0 && (story.ArrivalDay != Game1.Date.TotalDays || location != story.LocationId))
        { story.ArrivalDay = -1; weeklyStoryDirty = true; }
        if (WeeklyStoryInvitationPolicy.ObserveArrival(story, Game1.Date.TotalDays, Game1.timeOfDay, location))
            weeklyStoryDirty = true;
    }

    private void LogWeeklyStoryBlocked(WeeklyStoryPlan story, string reason)
    {
        string key = story.StoryId + ":" + reason;
        if (weeklyBlockedReason == key) return;
        weeklyBlockedReason = key;
        Monitor.Log($"weekly_story waiting title={story.Title} reason={reason}", LogLevel.Debug);
    }

    private sealed class WeeklyStoryPhoneHandler : IPhoneHandler
    {
        private readonly ModEntry owner;
        public WeeklyStoryPhoneHandler(ModEntry owner) => this.owner = owner;
        // Explicitly scheduled only near an actual telephone; never displace vanilla calls.
        public string CheckForIncomingCall(Random random) => null!;
        public IEnumerable<KeyValuePair<string, string>> GetOutgoingNumbers() => Array.Empty<KeyValuePair<string, string>>();
        public bool TryHandleOutgoingCall(string callId) => false;
        public bool TryHandleIncomingCall(string callId, out Action showDialogue)
        {
            showDialogue = null!;
            if (!Context.IsWorldReady || !Context.IsMainPlayer) return false;
            WeeklyStoryPlan? story = owner.weeklyStoryStore.GetOrCreate(GetPlayerId()).CurrentStory;
            if (story is null || callId != "VivantValley.Weekly." + story.StoryId
                || !WeeklyStoryInvitationPolicy.IsPending(story) || story.MailRead) return false;
            string playerId = GetPlayerId();
            showDialogue = () =>
            {
                if (!Context.IsWorldReady || GetPlayerId() != playerId || !owner.CanOfferWeeklyInvitation()
                    || !ReferenceEquals(owner.weeklyStoryStore.GetOrCreate(playerId).CurrentStory, story)) return;
                NPC? npc = Game1.getCharacterFromName(story.Inviter, mustBeVillager: false, includeEventActors: false);
                if (npc is not null) owner.ShowWeeklyInvitation(story, npc, "phone");
            };
            return true;
        }
    }
}
