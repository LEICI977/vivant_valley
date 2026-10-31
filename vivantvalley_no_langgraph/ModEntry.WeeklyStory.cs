using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Services;

namespace VivantValley;

public sealed partial class ModEntry
{
    private const string WeeklyStorySaveDataKey = "weekly-story-state-v1";

    private WeeklyStorySaveStore weeklyStoryStore = new();
    private WeeklyStoryLocationService weeklyStoryLocationService = null!;
    private WeeklyStoryPlannerService weeklyStoryPlannerService = null!;
    private WeeklyStoryRunner weeklyStoryRunner = null!;
    private CancellationTokenSource weeklyStoryCancellation = new();
    private Task<WeeklyStoryPlanningResult>? pendingWeeklyStoryTask;
    private WeeklyStoryPlanRequest? pendingWeeklyStoryRequest;
    private bool weeklyStoryLedgerAvailable = true;
    private bool weeklyStoryDayEnding;
    private int weeklyStoryWarpDelayTicks;
    private bool weeklyStoryDirty;

    private void InitializeWeeklyStories()
    {
        weeklyStoryLocationService = new WeeklyStoryLocationService();
        weeklyStoryPlannerService = new WeeklyStoryPlannerService(
            deepSeekClient,
            new WeeklyStoryValidator(),
            aiRequestDebugWriter,
            new WeeklyStoryPromptService(Helper.DirectoryPath, message => Monitor.Log(message, LogLevel.Warn)));
        weeklyStoryRunner = new WeeklyStoryRunner(Monitor, npcPresentationService);
        StardewValley.Objects.Phone.PhoneHandlers.Add(new WeeklyStoryPhoneHandler(this));
    }

    private void LoadWeeklyStories()
    {
        CloseWeeklyInvitationPresentation();
        weeklyStoryRunner.Interrupt("save_loaded");
        CancelWeeklyStoryPlanning();
        weeklyStoryDayEnding = false;
        weeklyStoryDirty = false;
        try
        {
            weeklyStoryStore = Helper.Data.ReadSaveData<WeeklyStorySaveStore>(WeeklyStorySaveDataKey)
                ?? new WeeklyStorySaveStore();
            weeklyStoryStore.Normalize();
            if (RepairWeeklyStoryAfterLoad())
                weeklyStoryDirty = true;
            PlayerWeeklyStoryState loadedState = weeklyStoryStore.GetOrCreate(GetPlayerId());
            // A small local ledger survives returning to title without saving.
            // Restoring a game day must not silently purchase the same request.
            try
            {
                WeeklyStoryPlanningProgress? ledger = Helper.Data.ReadJsonFile<WeeklyStoryPlanningProgress>(WeeklyStoryLedgerPath());
                if (ledger is not null && (ledger.WeekKey > loadedState.Planning.WeekKey
                    || (ledger.WeekKey == loadedState.Planning.WeekKey && ledger.Attempts >= loadedState.Planning.Attempts)))
                {
                    ledger.Normalize();
                    loadedState.Planning = ledger;
                }
                weeklyStoryLedgerAvailable = true;
            }
            catch (Exception ex)
            {
                weeklyStoryLedgerAvailable = false;
                Monitor.Log($"周剧情调用记录读取失败，暂停生成以免读档重复请求：{ex.Message}", LogLevel.Warn);
            }
            if (Game1.currentLocation is not null
                && loadedState.VisitedLocationIds.Add(Game1.currentLocation.NameOrUniqueName))
            {
                weeklyStoryDirty = true;
            }
        }
        catch (Exception ex)
        {
            weeklyStoryStore = new WeeklyStorySaveStore();
            weeklyStoryDirty = true;
            Monitor.Log($"读取每周剧情状态失败，将使用空状态：{ex}", LogLevel.Error);
        }

        InvalidateGiftMailAsset();
        EnsureWeeklyStoryPlanning();
    }

    private void OnWeeklyStoryDayStarted()
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
            return;
        weeklyStoryDayEnding = false;
        weeklyVisitDoorReady = false;
        PlayerWeeklyStoryState state = weeklyStoryStore.GetOrCreate(GetPlayerId());
        WeeklyStoryPlan? story = state.CurrentStory;
        if (story is not null)
        {
            if (MigrateWeeklyStoryTiming(story)) weeklyStoryDirty = true;
            if (story.Status == WeeklyStoryStatus.MailQueued
                && Game1.player.mailReceived.Contains(story.MailId))
            {
                story.MailRead = true;
                story.Status = WeeklyStoryStatus.Ready;
                weeklyStoryDirty = true;
            }
            if (Game1.Date.TotalDays > story.ExpiryDay
                && story.Status is WeeklyStoryStatus.MailQueued or WeeklyStoryStatus.Ready)
            {
                story.Status = WeeklyStoryStatus.Expired;
                weeklyStoryDirty = true;
            }
        }
        EnsureWeeklyStoryPlanning();
        PersistWeeklyStories(force: false);
    }

    private void OnWeeklyStoryDayEnding()
    {
        CloseWeeklyInvitationPresentation();
        weeklyStoryDayEnding = true;
        FinishWeeklyStoryPlanning();
        if (weeklyStoryRunner.IsRunning)
            weeklyStoryRunner.Interrupt("day_ending");
        CancelWeeklyStoryPlanning();
        PersistWeeklyStories(force: true);
    }

    private void OnWeeklyStoryWarped(WarpedEventArgs e)
    {
        if (!e.IsLocalPlayer || !Context.IsMainPlayer)
            return;
        CloseWeeklyInvitationPresentation();
        PlayerWeeklyStoryState state = weeklyStoryStore.GetOrCreate(GetPlayerId());
        if (state.VisitedLocationIds.Add(e.NewLocation.NameOrUniqueName))
            weeklyStoryDirty = true;
        if (weeklyStoryRunner.IsRunning)
            weeklyStoryRunner.Interrupt("player_warped");
        weeklyVisitDoorReady = e.OldLocation is StardewValley.Locations.FarmHouse && e.NewLocation is Farm;
        weeklyStoryWarpDelayTicks = 30;
        ObserveWeeklyStoryArrival();
    }

    private void OnWeeklyStoryReturnedToTitle()
    {
        CloseWeeklyInvitationPresentation();
        weeklyStoryRunner.Interrupt("returned_to_title");
        CancelWeeklyStoryPlanning();
        weeklyStoryStore = new WeeklyStorySaveStore();
        weeklyVisitDoorReady = false;
        weeklyBlockedReason = string.Empty;
        weeklyStoryDirty = false;
        weeklyStoryLedgerAvailable = true;
        weeklyStoryWarpDelayTicks = 0;
    }

    private void UpdateWeeklyStories(UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
            return;
        UpdateWeeklyInvitationPresentation();
        weeklyStoryRunner.Update(Game1.currentGameTime);
        UpdateFreeStory();
        FinishWeeklyStoryPlanning();
        if (weeklyStoryWarpDelayTicks > 0)
            weeklyStoryWarpDelayTicks--;
        if (e.IsMultipleOf(60))
        {
            EnsureWeeklyStoryPlanning();
            ObserveWeeklyStoryArrival();
            TryStartWeeklyStory();
            UpdateWeeklyInvitations();
        }
        if (weeklyStoryDirty)
            PersistWeeklyStories(force: false);
    }

    private void EnsureWeeklyStoryPlanning()
    {
        if (!Context.IsWorldReady
            || !Context.IsMainPlayer
            || !config.EnableWeeklyStories
            || !weeklyStoryLedgerAvailable
            || weeklyStoryDayEnding
            || string.IsNullOrWhiteSpace(runtimeApiKey)
            || pendingWeeklyStoryTask is not null
            || weeklyStoryRunner.IsRunning)
        {
            return;
        }

        int weekKey = Game1.Date.TotalDays / 7;
        PlayerWeeklyStoryState state = weeklyStoryStore.GetOrCreate(GetPlayerId());
        WeeklyStoryPlan? current = state.CurrentStory;
        if (current is not null
            && current.Status is WeeklyStoryStatus.MailQueued
                or WeeklyStoryStatus.Ready
                or WeeklyStoryStatus.Running)
        {
            return;
        }
        if (state.LastPlanningWeekKey >= weekKey)
            return;
        if (state.Planning.WeekKey == weekKey && state.Planning.CachedPlan is { } cached)
        {
            state.CurrentStory = cached;
            state.LastPlanningWeekKey = weekKey;
            weeklyStoryDirty = true;
            if (cached.Status == WeeklyStoryStatus.MailQueued && cached.InvitationKind == "mail" && !TryQueueWeeklyStoryMail(cached, out string cachedFailure))
                Monitor.Log($"复用周剧情邀请信投递失败：{cachedFailure}", LogLevel.Warn);
            return;
        }
        if (state.Planning.WeekKey > weekKey || (state.Planning.WeekKey == weekKey
            && state.Planning.Attempts >= WeeklyStoryPlanningPolicy.MaximumAttempts)) return;

        IReadOnlyList<WeeklyStoryNpcCandidate> npcs = BuildWeeklyStoryNpcCandidates();
        IReadOnlyList<WeeklyStoryLocationCandidate> locations = weeklyStoryLocationService.BuildCandidates()
            .Where(location => !location.RequiresPriorVisit || state.VisitedLocationIds.Contains(location.Id))
            .ToArray();
        if (npcs.Count == 0 || locations.Count == 0)
        {
            // Eligibility can change during the week; this isn't an AI attempt.
            return;
        }

        int currentDay = Game1.Date.TotalDays;
        bool festivalToday = IsWeeklyStoryFestivalDay();
        int scheduledDay = currentDay + (festivalToday || Game1.timeOfDay >= 2300 ? 1 : 0);
        WeeklyStoryDateContext currentDate = new()
        {
            TotalDays = currentDay,
            Year = Game1.Date.Year,
            Season = Game1.Date.SeasonKey,
            DayOfMonth = Game1.Date.DayOfMonth,
        };
        WeeklyStoryDateContext storyDate = WeeklyStoryDateContext.FromTotalDays(scheduledDay);
        string currentWeather = DescribeWeeklyStoryWeather(Game1.currentLocation);
        string storyWeather = scheduledDay > currentDay
            ? DescribeWeeklyStoryTomorrowWeather()
            : currentWeather;

        string seed = CreateWeeklyStorySeed(weekKey);
        var request = new WeeklyStoryPlanRequest
        {
            FreeScene = true,
            MinimumChoices = 2,
            InvitationWindowDays = Math.Clamp(config.WeeklyStoryInvitationDays, 1, 14),
            WeekKey = weekKey,
            CurrentDay = currentDay,
            CurrentTimeOfDay = Game1.timeOfDay,
            ScheduledDay = scheduledDay,
            EarliestDay = scheduledDay,
            LatestDay = scheduledDay,
            CurrentDate = currentDate,
            StoryDate = storyDate,
            CurrentWeather = currentWeather,
            StoryWeather = storyWeather,
            FestivalToday = festivalToday,
            FestivalName = festivalToday ? DescribeWeeklyStoryFestival(currentDate) : string.Empty,
            PlayerName = Game1.player.Name,
            Seed = seed,
            Npcs = npcs,
            Locations = locations,
            RecentHistory = state.History.TakeLast(6).ToArray(),
            Model = currentAiProfile.Model,
            ThinkingType = "disabled",
            ReasoningEffort = "low",
            MaxOutputTokens = config.WeeklyStoryMaxOutputTokens,
            PreviousFailureReason = state.Planning.WeekKey == weekKey ? state.Planning.FailureReason : string.Empty,
            PreviousFailureKind = state.Planning.WeekKey == weekKey ? state.Planning.FailureKind : string.Empty,
        };
        if (!WeeklyStoryPlanningPolicy.TryReserve(state.Planning, weekKey)) return;
        weeklyStoryDirty = true;
        if (!PersistWeeklyStoryLedger(state.Planning)) return;
        PersistWeeklyStories(force: true);
        pendingWeeklyStoryRequest = request;
        pendingWeeklyStoryTask = weeklyStoryPlannerService.PlanAsync(
            runtimeApiKey,
            request,
            weeklyStoryCancellation.Token);
        Monitor.Log($"正在生成第 {weekKey + 1} 周的自由恋爱剧情（第 {state.Planning.Attempts}/2 次尝试；输出上限 {request.MaxOutputTokens} tokens；自由场景开场，不预设玩家选择）。", LogLevel.Info);
    }

    private void FinishWeeklyStoryPlanning()
    {
        if (pendingWeeklyStoryTask is null || !pendingWeeklyStoryTask.IsCompleted)
            return;
        Task<WeeklyStoryPlanningResult> task = pendingWeeklyStoryTask;
        WeeklyStoryPlanRequest? request = pendingWeeklyStoryRequest;
        pendingWeeklyStoryTask = null;
        pendingWeeklyStoryRequest = null;
        if (request is null || request.WeekKey != Game1.Date.TotalDays / 7)
            return;
        if (request.CurrentDay != Game1.Date.TotalDays)
        {
            Monitor.Log("周剧情规划返回时游戏日期已变化，丢弃旧结果并按最新天气重新生成。", LogLevel.Debug);
            WeeklyStoryPlanningPolicy.Fail(weeklyStoryStore.GetOrCreate(GetPlayerId()).Planning, "返回时日期已变化，需使用最新日期天气。");
            EnsureWeeklyStoryPlanning();
            return;
        }

        WeeklyStoryPlanningResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            result = WeeklyStoryPlanningResult.Failed(ex.Message);
        }

        if (!result.Success || result.Plan is null)
        {
            PlayerWeeklyStoryState failedState = weeklyStoryStore.GetOrCreate(GetPlayerId());
            WeeklyStoryPlanningPolicy.Fail(failedState.Planning, result.FailureReason, result.FailureKind);
            weeklyStoryDirty = true;
            PersistWeeklyStoryLedger(failedState.Planning);
            Monitor.Log($"本周剧情规划失败（{result.FailureKind}，已用{failedState.Planning.Attempts}/2次）：{result.FailureReason}", LogLevel.Warn);
            if (failedState.Planning.Attempts < WeeklyStoryPlanningPolicy.MaximumAttempts)
            {
                EnsureWeeklyStoryPlanning();
                return;
            }
            Monitor.Log("本周自动生成预算已用完，失败原因已保留；不会因读档或跨天追加 AI 请求。可用 vivant_story_status 查看。", LogLevel.Info);
            return;
        }

        WeeklyStoryPlan story = result.Plan;
        story.MailId = CreateWeeklyStoryMailId(GetPlayerId(), story.StoryId);
        story.Status = WeeklyStoryStatus.MailQueued;
        string[] invitationKinds = { "mail", "direct", "visit", "phone" };
        story.InvitationKind = invitationKinds[StableByte(story.Seed) % invitationKinds.Length];
        if (story.InvitationKind == "phone" && !HasWeeklyInvitationPhone()) story.InvitationKind = "mail";
        PlayerWeeklyStoryState state = weeklyStoryStore.GetOrCreate(GetPlayerId());
        state.CurrentStory = story;
        state.LastPlanningWeekKey = request.WeekKey;
        state.Planning.Status = "succeeded";
        state.Planning.FailureReason = string.Empty;
        state.Planning.FailureKind = string.Empty;
        state.Planning.CachedPlan = story;
        PersistWeeklyStoryLedger(state.Planning);
        weeklyStoryDirty = true;
        PersistWeeklyStories(force: true);
        if (story.InvitationKind == "mail" && !TryQueueWeeklyStoryMail(story, out string failure))
        {
            story.Status = WeeklyStoryStatus.MailQueued;
            weeklyStoryDirty = true;
            PersistWeeklyStories(force: true);
            Monitor.Log($"每周剧情邀请信投递失败：{failure}", LogLevel.Error);
            return;
        }
        Monitor.Log(
            $"本周剧情已安排：{story.Title}；{story.LocationDisplayName}；第 {story.ScheduledDay} 天 {story.TimeSlot}。",
            LogLevel.Info);
        if (story.RepairNotes.Count > 0)
            Monitor.Log("周剧情已在本地修正格式：" + string.Join("；", story.RepairNotes), LogLevel.Debug);
    }

    private static bool IsWeeklyStoryFestivalDay()
        => Utility.isFestivalDay() || Utility.IsPassiveFestivalDay() || Game1.isFestival();

    private static string DescribeWeeklyStoryWeather(GameLocation? location)
    {
        if (location is not null)
        {
            if (location.IsGreenRainingHere())
                return "green_rain";
            if (location.IsLightningHere())
                return "storm";
            if (location.IsSnowingHere())
                return "snow";
            if (location.IsRainingHere())
                return "rain";
            if (location.IsDebrisWeatherHere())
                return "wind";
        }

        if (Game1.isLightning)
            return "storm";
        if (Game1.isSnowing)
            return "snow";
        if (Game1.isRaining)
            return "rain";
        return "clear";
    }

    private static string DescribeWeeklyStoryTomorrowWeather()
    {
        string forecast = (Game1.weatherForTomorrow?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
        return forecast switch
        {
            "rain" => "rain",
            "storm" => "storm",
            "snow" => "snow",
            "wind" or "debris" => "wind",
            "greenrain" or "green_rain" => "green_rain",
            "festival" or "wedding" => "clear",
            "sun" or "sunny" or "clear" => "clear",
            _ => string.IsNullOrWhiteSpace(forecast) ? "unknown" : forecast,
        };
    }

    private static string DescribeWeeklyStoryFestival(WeeklyStoryDateContext date)
    {
        if (date.Season.Equals("spring", StringComparison.OrdinalIgnoreCase))
        {
            return date.DayOfMonth switch
            {
                13 => "Egg Festival",
                24 => "Flower Dance",
                15 or 16 or 17 => "Desert Festival",
                _ => "festival",
            };
        }

        if (date.Season.Equals("summer", StringComparison.OrdinalIgnoreCase))
        {
            return date.DayOfMonth switch
            {
                11 => "Luau",
                20 or 21 => "Trout Derby",
                28 => "Dance of the Moonlight Jellies",
                _ => "festival",
            };
        }

        if (date.Season.Equals("fall", StringComparison.OrdinalIgnoreCase))
        {
            return date.DayOfMonth switch
            {
                16 => "Stardew Valley Fair",
                27 => "Spirit's Eve",
                _ => "festival",
            };
        }

        return date.DayOfMonth switch
        {
            8 => "Festival of Ice",
            12 or 13 => "SquidFest",
            15 or 16 or 17 => "Night Market",
            25 => "Feast of the Winter Star",
            _ => "festival",
        };
    }

    private IReadOnlyList<WeeklyStoryNpcCandidate> BuildWeeklyStoryNpcCandidates()
    {
        string playerId = GetPlayerId();
        string spouse = Game1.player.spouse ?? string.Empty;
        var candidates = new List<(WeeklyStoryNpcCandidate Candidate, int Score)>();
        foreach (NPC npc in Game1.locations
                     .SelectMany(location => location.characters.OfType<NPC>())
                     .Where(npc => npc.IsVillager
                                   && !npc.IsMonster
                                   && npc.CanSocialize
                                   && !npc.IsInvisible
                                   && !npcCombatStateService.IsHospitalized(npc.Name))
                     .GroupBy(npc => npc.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            if (!Game1.player.friendshipData.TryGetValue(npc.Name, out Friendship? friendship))
                continue;
            int hearts = Game1.player.getFriendshipHeartLevelForNPC(npc.Name);
            bool isSpouse = npc.Name.Equals(spouse, StringComparison.OrdinalIgnoreCase)
                            || friendship.IsMarried()
                            || friendship.IsEngaged();
            bool isDatable = npc.datable.Value || friendship.IsDating() || isSpouse;
            if (!isDatable || (!isSpouse && hearts < config.WeeklyStoryMinimumHearts))
                continue;

            int lastConversationDay = socialStore.GetOrCreatePlayer(playerId).TryGetNpc(npc.Name, out NpcSocialState? socialNpc)
                && socialNpc is not null
                ? socialNpc.LastConversationDay
                : -1;
            int recency = lastConversationDay < 0 ? 0 : Math.Max(0, 14 - (Game1.Date.TotalDays - lastConversationDay));
            int score = (isSpouse ? 10_000 : friendship.IsDating() ? 5000 : 0) + hearts * 100 + recency * 10;
            score -= weeklyStoryStore.GetOrCreate(playerId).History.TakeLast(2)
                .Count(h => h.Participants.Contains(npc.Name)) * 250;
            score += StableByte($"{CreateWeeklyStorySeed(Game1.Date.TotalDays / 7)}\u001f{npc.Name}");
            candidates.Add((
                new WeeklyStoryNpcCandidate(
                    npc.Name,
                    npc.displayName,
                    hearts,
                    GameContextBuilder.DescribeRelationship(friendship),
                    isDatable,
                    isSpouse,
                    RidgesideVillageCompatibility.BuildIdentityPrompt(npc.displayName, npc.Name)
                    + "严格按照该角色的原版性格、说话方式、价值观、生活背景和已知经历行动与说话；不要套用其他 NPC 的性格。"
                    + "\n" + ReadNpcPersonality(Game1.player, npc.Name).BuildPrompt(),
                    Array.Empty<string>())
                {
                    Experiences = BuildNpcExperiences(npc.Name),
                    ActiveCommitments = ConversationCommitmentPolicy.Active(
                        socialStore.GetOrCreatePlayer(playerId).GetOrCreateNpc(npc.Name), Game1.Date.TotalDays, forFollowUp: true),
                    IsAdult = npc.Age == NPC.adult,
                    IsDating = friendship.IsDating() && !friendship.IsRoommate(),
                    HasRomanticInterest = !friendship.IsDivorced() && WeeklyStoryRomancePolicy.HasObservedInterest(
                        weeklyStoryStore.GetOrCreate(playerId).History, npc.Name),
                    IsSupporting = friendship.IsRoommate() || friendship.IsDivorced()
                        || (!string.IsNullOrWhiteSpace(spouse) && !isSpouse && !config.WeeklyStoryAllowMarriedRivalry),
                },
                score));
        }

        IEnumerable<(WeeklyStoryNpcCandidate Candidate, int Score)> ordered = candidates
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.Name, StringComparer.OrdinalIgnoreCase);
        // Other candidates can witness or misunderstand a scene without becoming romantic rivals.
        // Romance eligibility is explicit, not inferred from mere participation.
        return ordered.Take(4).Select(item => item.Candidate).ToArray();
    }

    private bool TryQueueWeeklyStoryMail(WeeklyStoryPlan story, out string failure)
    {
        failure = string.Empty;
        if (MigrateWeeklyStoryTiming(story)) weeklyStoryDirty = true;
        if (WeeklyStoryInvitationPolicy.CanStartOnSite(story, Game1.Date.TotalDays, Game1.timeOfDay,
            Game1.currentLocation?.NameOrUniqueName ?? "")) return true;
        int previousDelivery = story.InvitationDeliveredDay;
        int previousStart = story.ScheduledDay;
        int previousExpiry = story.ExpiryDay;
        bool queued = false;
        try
        {
            WeeklyStoryInvitationPolicy.MarkDelivered(story, Game1.Date.TotalDays, Game1.timeOfDay);
            weeklyStoryDirty = true;
            InvalidateGiftMailAsset();
            Dictionary<string, string> mailData = Helper.GameContent.Load<Dictionary<string, string>>(GiftMailAssetName);
            if (!mailData.TryGetValue(story.MailId, out string? value)
                || !value.Equals(WeeklyStoryMailService.Build(story), StringComparison.Ordinal))
            {
                failure = "邀请信没有成功注入 Data/mail。";
                return false;
            }
            if (!Game1.player.mailReceived.Contains(story.MailId)
                && !Game1.player.mailbox.Contains(story.MailId))
            {
                Game1.player.mailbox.Insert(0, story.MailId);
            }
            if (!Game1.player.mailReceived.Contains(story.MailId)
                && !Game1.player.mailbox.Contains(story.MailId)
                && !Game1.player.mailForTomorrow.Contains(story.MailId))
            {
                failure = "原版邮箱没有接受邀请信。";
                return false;
            }
            queued = true;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
        finally
        {
            if (!queued)
            {
                story.InvitationDeliveredDay = previousDelivery;
                story.ScheduledDay = previousStart;
                story.ExpiryDay = previousExpiry;
            }
            else
            {
                PlayerWeeklyStoryState state = weeklyStoryStore.GetOrCreate(GetPlayerId());
                state.Planning.CachedPlan = story;
                PersistWeeklyStoryLedger(state.Planning);
                PersistWeeklyStories(force: true);
            }
        }
    }

    private void EditWeeklyStoryMailAsset(AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo(GiftMailAssetName))
            return;
        WeeklyStoryPlan[] stories = (weeklyStoryStore.Players ?? new Dictionary<string, PlayerWeeklyStoryState>())
            .Values
            .Where(state => state?.CurrentStory is not null)
            .Select(state => state.CurrentStory!)
            .Where(story => story.MailId.Length > 0 && story.MailBody.Length > 0)
            .GroupBy(story => story.MailId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (stories.Length == 0)
            return;
        e.Edit(asset =>
        {
            IDictionary<string, string> data = asset.AsDictionary<string, string>().Data;
            foreach (WeeklyStoryPlan story in stories)
                data[story.MailId] = WeeklyStoryMailService.Build(story);
        });
    }

    private void TrackWeeklyStoryMail(MenuChangedEventArgs e)
    {
        if (!Context.IsWorldReady
            || !Context.IsMainPlayer
            || e.NewMenu is not LetterViewerMenu letter
            || letter.isFromCollection
            || string.IsNullOrWhiteSpace(letter.mailTitle))
        {
            return;
        }
        PlayerWeeklyStoryState state = weeklyStoryStore.GetOrCreate(GetPlayerId());
        WeeklyStoryPlan? story = state.CurrentStory;
        if (story is null
            || !story.MailId.Equals(letter.mailTitle, StringComparison.Ordinal)
            || story.Status is not WeeklyStoryStatus.MailQueued and not WeeklyStoryStatus.Ready)
        {
            return;
        }
        story.MailRead = true;
        story.Status = WeeklyStoryStatus.Ready;
        weeklyStoryDirty = true;
        PersistWeeklyStories(force: true);
        Monitor.Log($"玩家已阅读周剧情邀请信：{story.Title}。", LogLevel.Info);
    }

    private void TryStartWeeklyStory()
    {
        ConversationScreenState screenState = screenStates.Value;
        if (!config.EnableWeeklyStories
            || weeklyInvitationMenu is not null
            || weeklyStoryRunner.IsRunning
            || weeklyStoryWarpDelayTicks > 0
            || screenState.HasPendingConversation
            || screenState.PendingSocialScene is not null
            || screenState.QueuedSocialScene is not null
            || screenState.ActiveSocialScene is not null
            || screenState.SocialMenu is not null
            || screenState.PendingProactiveScene is not null
            || screenState.QueuedProactiveScene is not null
            || screenState.ActiveProactiveScene is not null
            || screenState.ProactiveMenu is not null
            || !CanOpenOwnMenu()
            || Utility.isFestivalDay()
            || Utility.IsPassiveFestivalDay()
            || Game1.isFestival())
        {
            return;
        }
        PlayerWeeklyStoryState state = weeklyStoryStore.GetOrCreate(GetPlayerId());
        WeeklyStoryPlan? story = state.CurrentStory;
        if (story is null || !WeeklyStoryInvitationPolicy.IsPending(story)) return;
        if (MigrateWeeklyStoryTiming(story)) weeklyStoryDirty = true;
        int day = Game1.Date.TotalDays;
        if (day > story.ExpiryDay)
        {
            story.Status = WeeklyStoryStatus.Expired;
            weeklyStoryDirty = true;
            return;
        }
        if (!WeeklyStoryInvitationPolicy.CanStart(story, day, Game1.timeOfDay,
                Game1.currentLocation?.NameOrUniqueName ?? string.Empty)) return;
        if (story.Actors.Any(actor => npcMoveToolService.HasActiveSession(actor.Name)
                                      || npcMineGuardService.HasActiveSession(actor.Name)
                                      || npcFishingService.HasActiveSession(actor.Name)
                                      || npcCombatStateService.IsHospitalized(actor.Name)))
        {
            LogWeeklyStoryBlocked(story, "actor is accompanying the player or hospitalized");
            return;
        }

        bool onSite = story.FormatVersion == 3 && !story.MailRead;
        story.CurrentBeatIndex = 0;
        if (!weeklyStoryRunner.TryStart(
                story,
                choice => CompleteWeeklyStory(story, choice),
                reason => { if (story.FormatVersion == 3) InterruptFreeStory(story, reason); else DeferWeeklyStory(story, reason); },
                out string failure))
        {
            LogWeeklyStoryBlocked(story, failure);
            return;
        }
        weeklyStoryDirty = true;
        PersistWeeklyStories(force: true);
        if (story.FormatVersion == 3)
        {
            story.StartedOnSite |= onSite;
            StopWeeklyInvitationPhone();
            if (!string.IsNullOrEmpty(story.MailId))
            {
                Game1.player.mailbox.Remove(story.MailId);
                Game1.player.mailForTomorrow.Remove(story.MailId);
            }
            BeginFreeStory(story);
        }
        Monitor.Log($"周剧情开始演出：{story.Title}。", LogLevel.Info);
    }

    private void CompleteWeeklyStory(WeeklyStoryPlan story, WeeklyStoryChoiceOption? choice)
    {
        if (choice is null || story.Status == WeeklyStoryStatus.Completed)
            return;
        story.Status = WeeklyStoryStatus.Completed;
        story.CompletedDay = Game1.Date.TotalDays;
        story.SelectedChoiceId = choice.Id;
        story.SelectedEnding = choice.Ending;
        story.SelectedChoiceMemory = story.FormatVersion == 2
            ? string.Join("；", story.Selections.Select((selection, index) => $"选择{index + 1}“{selection.Text}”"))
                + $"；结局：{WeeklyStoryPlaybackPolicy.EndingLabel(choice.Ending)}"
            : $"玩家选择“{choice.Text}”。";
        ApplyWeeklyStoryRelationshipEffect(story, choice);
        RecordWeeklyStoryMemory(story, choice);
        TownEchoEvent? echo = TownEchoPolicy.CompletedStory(GetPlayerId(), story, choice);
        if (echo is not null) RecordTownEcho(echo);
        PlayerWeeklyStoryState state = weeklyStoryStore.GetOrCreate(GetPlayerId());
        state.History.Add(new WeeklyStoryHistoryEntry
        {
            StoryId = story.StoryId,
            Title = story.Title,
            LocationId = story.LocationId,
            WeekKey = story.WeekKey,
            CompletedDay = story.CompletedDay,
            Summary = WeeklyStoryPlaybackPolicy.BuildCompletionMemory(story, choice),
            ChoiceMemory = story.SelectedChoiceMemory,
            Selections = story.Selections.ToList(),
            Ending = story.SelectedEnding,
            Participants = story.Actors.Select(actor => actor.Name).ToList(),
        });
        state.Normalize(GetPlayerId());
        weeklyStoryDirty = true;
        memoryDirty = true;
        PersistWeeklyStories(force: true);
        PersistMemory(force: false);
        ShowHud(L.F("text.26f29eee1d7c", $"本周剧情完成：{story.Title}")
            + (story.SelectedEnding.Length > 0 ? $"（{WeeklyStoryPlaybackPolicy.EndingLabel(story.SelectedEnding)}）" : ""), HUDMessage.newQuest_type);
    }

    private void DeferWeeklyStory(WeeklyStoryPlan story, string reason)
    {
        TownEchoEvent? interrupted = TownEchoPolicy.InterruptedDate(GetPlayerId(), story, Game1.Date.TotalDays);
        if (interrupted is not null) { RecordTownEcho(interrupted); PersistMemory(force: false); }
        story.CurrentBeatIndex = 0;
        story.ArrivalDay = -1;
        story.RetryAfterMinutes = Game1.Date.TotalDays * 1440 + WeeklyStoryInvitationPolicy.Minutes(Game1.timeOfDay) + 60;
        story.Selections.Clear();
        story.SelectedEnding = string.Empty;
        story.AttemptCount++;
        story.Status = story.AttemptCount >= 3 || reason.StartsWith("player_skipped:", StringComparison.Ordinal)
            ? WeeklyStoryStatus.Expired
            : WeeklyStoryStatus.Ready;
        weeklyStoryDirty = true;
        PersistWeeklyStories(force: true);
        Monitor.Log($"周剧情已中断：{story.Title}，reason={reason}，attempt={story.AttemptCount}。", LogLevel.Warn);
    }

    private void ApplyWeeklyStoryRelationshipEffect(WeeklyStoryPlan story, WeeklyStoryChoiceOption? choice)
    {
        if (choice is null)
            return;
        if (story.FormatVersion == 2)
        {
            foreach (var change in WeeklyStoryBranchPolicy.RelationshipChanges(story))
            {
                NPC? recipient = Game1.getCharacterFromName(change.Key, mustBeVillager: false, includeEventActors: true);
                if (recipient is not null && change.Value != 0) Game1.player.changeFriendship(change.Value, recipient);
            }
            return;
        }
        int points = choice.Outcome switch
        {
            "romantic" => 50,
            "warm" => 35,
            "playful" => 30,
            "honest" => 25,
            "shared" => 25,
            "distant" => -10,
            _ => 0,
        };
        IEnumerable<WeeklyStoryActor> recipients = choice.Outcome.Equals("shared", StringComparison.Ordinal)
            ? story.Actors
            : story.Actors.Where(actor => actor.Name.Equals(choice.ResponseActor, StringComparison.OrdinalIgnoreCase));
        foreach (WeeklyStoryActor actor in recipients)
        {
            NPC? npc = Game1.getCharacterFromName(actor.Name, mustBeVillager: false, includeEventActors: true);
            if (npc is not null && points != 0)
                Game1.player.changeFriendship(points, npc);
        }
    }

    private void RecordWeeklyStoryMemory(WeeklyStoryPlan story, WeeklyStoryChoiceOption? choice)
    {
        if (choice is null)
            return;
        string result = WeeklyStoryPlaybackPolicy.BuildCompletionMemory(story, choice);
        string durableResult = result;
        string date = $"{Game1.Date} {Game1.timeOfDay}";
        foreach (WeeklyStoryActor actor in story.Actors)
        {
            NpcConversationMemory memory = memoryStore.GetOrCreate(GetPlayerId(), actor.Name);
            memory.Messages.Add(new ConversationMemoryMessage
            {
                Role = "system",
                Content = "[每周剧情已完成] " + LimitNarrativeText(result, story.FormatVersion == 2 ? 1600 : 500),
                GameDate = date,
                Source = ConversationMemorySources.ModAction,
                EventId = story.StoryId,
                LocationName = story.LocationId,
                DedupeKey = story.StoryId,
            });
            memory.Summary = ConversationMemoryPolicy.UpdateLongTermMemory(
                memory.Summary,
                durableResult,
                date,
                GetPlayerId(),
                actor.Name,
                memory.TotalTurns);
            memory.Messages = ConversationTimelinePolicy.RetainRecentMessages(memory);
            memory.LastDate = date;
        }
    }

    private bool RepairWeeklyStoryAfterLoad()
    {
        bool changed = false;
        weeklyStoryStore.Normalize();
        PlayerWeeklyStoryState state = weeklyStoryStore.GetOrCreate(GetPlayerId());
        WeeklyStoryPlan? story = state.CurrentStory;
        if (story is null)
            return changed;
        if (story.Status == WeeklyStoryStatus.Running)
        {
            story.Status = WeeklyStoryStatus.Ready;
            story.CurrentBeatIndex = 0;
            changed = true;
        }
        if (MigrateWeeklyStoryTiming(story))
            changed = true;
        if (story.Status == WeeklyStoryStatus.MailQueued
            && Game1.player.mailReceived.Contains(story.MailId))
        {
            story.MailRead = true;
            story.Status = WeeklyStoryStatus.Ready;
            changed = true;
        }
        if (Game1.Date.TotalDays > story.ExpiryDay
            && story.Status is WeeklyStoryStatus.MailQueued or WeeklyStoryStatus.Ready)
        {
            story.Status = WeeklyStoryStatus.Expired;
            changed = true;
        }
        return changed;
    }

    private void PersistWeeklyStories(bool force)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || (!force && !weeklyStoryDirty))
            return;
        try
        {
            weeklyStoryStore.Normalize();
            PlayerWeeklyStoryState state = weeklyStoryStore.GetOrCreate(GetPlayerId());
            if (state.CurrentStory is { } story && state.Planning.WeekKey == story.WeekKey)
            {
                state.Planning.CachedPlan = story;
                if (force) PersistWeeklyStoryLedger(state.Planning);
            }
            Helper.Data.WriteSaveData(WeeklyStorySaveDataKey, weeklyStoryStore);
            weeklyStoryDirty = false;
        }
        catch (Exception ex)
        {
            Monitor.Log($"保存每周剧情状态失败：{ex}", LogLevel.Error);
        }
    }

    private void CancelWeeklyStoryPlanning()
    {
        if (pendingWeeklyStoryRequest is not null && Context.IsWorldReady && Context.IsMainPlayer)
        {
            WeeklyStoryPlanningProgress progress = weeklyStoryStore.GetOrCreate(GetPlayerId()).Planning;
            WeeklyStoryPlanningPolicy.Fail(progress, "请求因日期或存档切换中断；已发送请求仍计入预算。");
            weeklyStoryDirty = true;
            PersistWeeklyStoryLedger(progress);
        }
        if (!weeklyStoryCancellation.IsCancellationRequested)
            weeklyStoryCancellation.Cancel();
        weeklyStoryCancellation.Dispose();
        weeklyStoryCancellation = new CancellationTokenSource();
        pendingWeeklyStoryTask = null;
        pendingWeeklyStoryRequest = null;
    }

    private string WeeklyStoryLedgerPath()
        => $"data/weekly-story-budget/{CreateWeeklyStorySeed(0)}.json";

    private bool PersistWeeklyStoryLedger(WeeklyStoryPlanningProgress progress)
    {
        try
        {
            Helper.Data.WriteJsonFile(WeeklyStoryLedgerPath(), progress);
            return true;
        }
        catch (Exception ex)
        {
            weeklyStoryLedgerAvailable = false;
            Monitor.Log($"周剧情调用记录保存失败，暂停后续生成：{ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    private string CreateWeeklyStorySeed(int weekKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Game1.uniqueIDForThisGame.ToString(CultureInfo.InvariantCulture)}\u001f{GetPlayerId()}\u001f{weekKey}\u001fweekly-story-v1")))
            .ToLowerInvariant();

    private static string CreateWeeklyStoryMailId(string playerId, string storyId)
        => "firstmod.StardewAIMemories.story."
           + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{playerId}\u001f{storyId}")))[..24]
               .ToLowerInvariant();

    private static int StableByte(string value)
        => SHA256.HashData(Encoding.UTF8.GetBytes(value))[0];

    private void OnWeeklyStoryStatusCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
        {
            Monitor.Log("请先以主机身份载入存档。", LogLevel.Alert);
            return;
        }
        PlayerWeeklyStoryState state = weeklyStoryStore.GetOrCreate(GetPlayerId());
        WeeklyStoryPlan? story = state.CurrentStory;
        if (story is null)
        {
            Monitor.Log(
                $"第 {Game1.Date.TotalDays / 7 + 1} 周尚无剧情；规划状态={state.Planning.Status}；"
                + $"记录周={state.Planning.WeekKey + 1}，已用={state.Planning.Attempts}/2；原因={state.Planning.FailureReason}。",
                LogLevel.Info);
            return;
        }
        Monitor.Log(
            $"周剧情：{story.Title}；格式={story.FormatVersion}；状态={story.Status}；邀请人={story.Inviter}；"
            + (story.FreeScene is { } free ? $"自由输入轮次={free.Turns}；待回应={free.AwaitingReply}；在场={string.Join(",",FreeStoryService.Present(story))}；" : "")
            + $"日期={WeeklyStoryMailService.DescribeDate(story.ScheduledDay)}；地点={story.LocationDisplayName}({story.LocationId})；"
            + $"时间={WeeklyStoryMailService.DescribeTimeSlot(story.TimeSlot)}；已读/接受={story.MailRead}；"
            + $"渠道={story.InvitationKind}；送达日={story.InvitationDeliveredDay}；到期日={story.ExpiryDay}；到场日={story.ArrivalDay}；等待原因={weeklyBlockedReason}；"
            + $"演员={string.Join(",", story.Actors.Select(actor => actor.Name))}；节点={story.CurrentBeatIndex}/{story.Beats.Count}。",
            LogLevel.Info);
    }

    private void OnWeeklyStoryUnlockCommand(string command, string[] args)
    {
        bool success = weeklyStoryRunner.TryEmergencyUnlock(out string result);
        Monitor.Log(
            $"周剧情控制恢复：{result}",
            success ? LogLevel.Info : LogLevel.Warn);
    }
}
