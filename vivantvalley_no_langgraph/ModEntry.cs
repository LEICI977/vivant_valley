using System.Globalization;
using System.Net;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VivantValley.Menus;
using VivantValley.Patches;
using VivantValley.Services;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;

namespace VivantValley;

/// <summary>The SMAPI entry point.</summary>
public sealed partial class ModEntry : Mod
{
    private const string HostedServiceOrigin = "https://www.vivantvalley.com.cn";
    private const string SaveDataKey = "npc-memories";
    private const string NarrativeSaveDataKey = "npc-narrative-state-v1";
    private const string GeneratedDialogueKey = "firstmod.StardewAIMemories.GeneratedDialogue";
    private const string CombatSaveDataKey = "npc-combat-state-v1";

    private ModConfig config = null!;
    private GameContextBuilder contextBuilder = null!;
    private readonly NarrativeContextService narrativeContextService = new();
    private IDeepSeekClient deepSeekClient = null!;
    private HttpClient aiHttpClient = null!;
    private AiRequestDebugWriter aiRequestDebugWriter = null!;
    private AiRuntimeProfile currentAiProfile = null!;
    private ConversationEngine conversationEngine = null!;
    private ConversationOrchestrator conversationOrchestrator = null!;
    private readonly ConcurrentQueue<GameBridgeWorkItem> gameBridgeWorkItems = new();
    private readonly Dictionary<string, GameBridgeReceipt> gameBridgeReceipts = new(StringComparer.Ordinal);
    private readonly DecisionValidator decisionValidator = new();
    private NpcCombatStateService npcCombatStateService = null!;
    private CompanionFormationService companionFormationService = null!;
    private NpcMoveToolService npcMoveToolService = null!;
    private NpcMineGuardService npcMineGuardService = null!;
    private NpcFishingService npcFishingService = null!;
    private NpcPresentationService npcPresentationService = null!;
    private ProactiveSceneService proactiveSceneService = null!;
    private readonly PilotNarrativePlanner pilotNarrativePlanner = new();
    private readonly NarrativeChoiceResolver narrativeChoiceResolver = new();
    private StoryCatalog storyCatalog = StoryCatalog.Empty;
    private ConversationMemoryStore memoryStore = new();
    private MemoryEventArchive? memoryArchive;
    private bool memoryStorageReady;
    private readonly ConversationSessionMemoryStore conversationSessionMemory = new();
    private NarrativeSaveStore narrativeStore = new();
    private readonly PerScreen<ConversationScreenState> screenStates = new(() => new ConversationScreenState());

    private string runtimeApiKey = string.Empty;
    private string apiKeySource = "未设置";
    private bool memoryDirty;
    private bool narrativeDirty;
    private bool warnedFarmhandPersistence;

    public override void Entry(IModHelper helper)
    {
        L.Initialize(helper.DirectoryPath, helper.Translation.Locale);
        helper.Events.Content.LocaleChanged += (_, e) =>
        {
            L.SetLocale(e.NewLocale);
            storyCatalog = StoryCatalog.LoadDirectory(Path.Combine(helper.DirectoryPath, "assets", "stories"));
            helper.GameContent.InvalidateCache("Data/mail");
        };
        npcCombatStateService = new NpcCombatStateService(Monitor);
        npcCombatStateService.Defeated += OnNpcDefeated;
        companionFormationService = new CompanionFormationService();
        npcMoveToolService = new NpcMoveToolService(
            Monitor,
            npcCombatStateService,
            conversationSessionMemory,
            companionFormationService);
        npcMineGuardService = new NpcMineGuardService(
            Monitor,
            npcCombatStateService,
            companionFormationService);
        npcFishingService = new NpcFishingService(
            Monitor,
            npcCombatStateService,
            conversationSessionMemory,
            companionFormationService);
        npcPresentationService = new NpcPresentationService(
            Monitor,
            npcName => npcMoveToolService.HasActiveSession(npcName)
                       || npcMineGuardService.HasActiveSession(npcName)
                       || npcFishingService.HasActiveSession(npcName));
        config = helper.ReadConfig<ModConfig>();
        bool migratedChatDefaults = ConversationUiLayout.MigrateDefaults(config);
        NormalizeConfig();
        bool migratedAiSettings = NormalizeAiSettings();
        int previousWeeklyTokenLimit = config.WeeklyStoryMaxOutputTokens;
        bool migratedWeeklySettings = WeeklyStoryPlanningPolicy.MigrateGenerationSettings(config);
        if (previousWeeklyTokenLimit != config.WeeklyStoryMaxOutputTokens)
            Monitor.Log($"周剧情旧输出默认值已迁移：{previousWeeklyTokenLimit} → {config.WeeklyStoryMaxOutputTokens} tokens；每周仍最多两次自动请求。", LogLevel.Info);
        contextBuilder = new GameContextBuilder(config, npcCombatStateService);
        LoadStartupApiKey();
        aiHttpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        speechClient = new HostedSpeechClient(aiHttpClient, HostedServiceOrigin);
        RefreshSpeechCatalog();
        aiRequestDebugWriter = new AiRequestDebugWriter(
            helper.DirectoryPath,
            () => config.WriteAiRequestDebugFiles,
            message => Monitor.Log(message, LogLevel.Warn));
        deepSeekClient = new AiProviderClient(
            aiHttpClient,
            () => Volatile.Read(ref currentAiProfile),
            aiRequestDebugWriter);
        conversationEngine = new ConversationEngine(deepSeekClient);
        conversationOrchestrator = new ConversationOrchestrator(
            new ConversationToolProviderClient(aiHttpClient, aiRequestDebugWriter),
            EnqueueGameBridgeToolAsync,
            new ConversationPromptService(helper.DirectoryPath, message => Monitor.Log(message, LogLevel.Warn)));
        storyCatalog = StoryCatalog.LoadDirectory(Path.Combine(helper.DirectoryPath, "assets", "stories"));
        foreach (string issue in storyCatalog.Issues)
            Monitor.Log($"Story catalog: {issue}", LogLevel.Warn);
        InitializeSocialDirector(helper);
        InitializeWeeklyStories();
        VanillaInteractionPatches.Apply(this, "firstmod.StardewAIMemories");
        DreamSleepPatch.Apply(this);

        if (migratedAiSettings || migratedWeeklySettings || migratedChatDefaults)
        {
            try
            {
                helper.WriteConfig(config);
            }
            catch (Exception ex)
            {
                Monitor.Log($"写入迁移后的配置失败：{ex.Message}", LogLevel.Warn);
            }
        }

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.Saved += OnSaved;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Player.Warped += OnWarped;
        helper.Events.Player.InventoryChanged += OnInventoryChanged;
        helper.Events.Player.LevelChanged += OnLevelChanged;
        helper.Events.GameLoop.TimeChanged += OnTimeChanged;
        helper.ConsoleCommands.Add("vivant_echo", "Show current player town-echo events and their recipients.", OnTownEchoStatus);
        helper.Events.Content.AssetRequested += OnAssetRequested;
        helper.Events.Display.MenuChanged += OnMenuChanged;
        helper.Events.Display.RenderedWorld += OnRenderedWorld;
        helper.Events.Display.RenderingActiveMenu += DrawDream;
        helper.Events.Display.RenderedActiveMenu += TrackDreamPage;

        helper.ConsoleCommands.Add(
            "vivant_settings",
            "打开 Vivant Valley 的全局 AI 提供商设置。",
            OnPromptKeyCommand);
        helper.ConsoleCommands.Add(
            "vivant_status",
            "显示 Vivant Valley 的当前状态。",
            OnStatusCommand);
        helper.ConsoleCommands.Add(
            "vivant_ai_debug",
            "Toggle complete local AI request capture: vivant_ai_debug on|off|status",
            OnAiDebugCommand);
        helper.ConsoleCommands.Add(
            "vivant_ai_usage",
            "显示本次启动服务端已报告的非流式响应 token 用量（缺失值不估算）。",
            (_, _) => Monitor.Log(AiUsageDiagnostics.Describe(), LogLevel.Info));
        helper.ConsoleCommands.Add(
            "vivant_forget",
            "清除记忆。用法：vivant_forget <NPC内部名|all>",
            OnForgetCommand);
        helper.ConsoleCommands.Add(
            "vivant_social_status",
            "显示当前玩家今天的社交导演候选。用法：vivant_social_status [NPC内部名]",
            OnSocialStatusCommand);
        helper.ConsoleCommands.Add(
            "vivant_weekly_story_status",
            "显示当前每周剧情的邀请、地点、时间和演出状态。",
            OnWeeklyStoryStatusCommand);
        helper.ConsoleCommands.Add(
            "vivant_weekly_unlock",
            "安全中断残留的每周剧情并恢复玩家控制。",
            OnWeeklyStoryUnlockCommand);
        helper.ConsoleCommands.Add(
            "aimemory_key",
            "兼容命令：打开 Vivant Valley 的全局 AI 提供商设置。",
            OnPromptKeyCommand);
        helper.ConsoleCommands.Add(
            "aimemory_settings",
            "兼容命令：打开 Vivant Valley 的全局 AI 提供商设置。",
            OnPromptKeyCommand);
        helper.ConsoleCommands.Add(
            "aimemory_status",
            "兼容命令：显示 Vivant Valley 的当前状态。",
            OnStatusCommand);
        helper.ConsoleCommands.Add(
            "aimemory_forget",
            "兼容命令：清除记忆。用法：aimemory_forget <NPC内部名|all>",
            OnForgetCommand);
        helper.ConsoleCommands.Add(
            "aisocial_status",
            "兼容命令：显示当前玩家今天的社交导演候选。用法：aisocial_status [NPC内部名]",
            OnSocialStatusCommand);

        if (string.IsNullOrWhiteSpace(runtimeApiKey))
        {
            Monitor.Log(
                "全局 AI 设置尚未完成。进入存档后会打开设置界面。",
                LogLevel.Alert);
        }
        else
        {
            Monitor.Log(
                $"AI 提供商：{currentAiProfile.Provider}；模型：{currentAiProfile.Model}；Key 来源：{apiKeySource}。"
                + $"按 {config.ChatKey} 面向村民开始 AI 对话。",
                LogLevel.Alert);
        }
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        AbortDream(false);
        npcPresentationService.Clear();
        conversationOrchestrator.ClearPending();
        conversationSessionMemory.Clear();
        npcMoveToolService.CancelAll("save_loaded");
        npcMineGuardService.CancelAll("save_loaded");
        npcFishingService.CancelAll("save_loaded");
        ConversationScreenState state = screenStates.Value;
        CancelPendingConversation(state);
        CancelPendingSocialScene(state, retryToday: false);
        state.QueuedDialogue = null;
        ClearConversationContinuation(state);
        ResetSocialBackgroundWork();

        if (Context.IsMainPlayer)
        {
            try
            {
                npcCombatStateService.Load(
                    Helper.Data.ReadSaveData<NpcCombatStateStore>(CombatSaveDataKey)
                    ?? new NpcCombatStateStore());
                npcCombatStateService.EnsureAllDefaultWeapons();
                npcCombatStateService.InitializeDefaultWeaponsForWorld();
                npcCombatStateService.OnDayStarted();
                npcCombatStateService.Update();
            }
            catch (Exception ex)
            {
                npcCombatStateService.Load(new NpcCombatStateStore());
                Monitor.Log($"Failed to load NPC combat state; using empty state: {ex}", LogLevel.Error);
            }

            memoryDirty = false;
            socialDirty = false;
            memoryStorageReady = false;
            memoryArchive = null;
            ConversationTimelinePolicy.ArchiveBeforePrune = _ => false;
            ConversationMemoryPolicy.ArchiveBeforeSummaryCompaction = (_, _, _) => false;
            try
            {
                memoryStore = Helper.Data.ReadSaveData<ConversationMemoryStore>(SaveDataKey)
                    ?? new ConversationMemoryStore();
                memoryStore.Normalize();
                if (NormalizeConversationTimelineMetadata())
                    memoryDirty = true;
                memoryStorageReady = true;
                ConfigureMemoryArchive();
            }
            catch (Exception ex)
            {
                memoryStorageReady = false;
                memoryArchive = null;
                ConversationTimelinePolicy.ArchiveBeforePrune = _ => false;
                ConversationMemoryPolicy.ArchiveBeforeSummaryCompaction = (_, _, _) => false;
                memoryStore = new ConversationMemoryStore();
                Monitor.Log($"读取 NPC 对话记忆失败，暂停覆盖旧记忆数据：{ex}", LogLevel.Error);
            }

            try
            {
                socialStore = Helper.Data.ReadSaveData<SocialDirectorSaveStore>(SocialDirectorSaveDataKey)
                    ?? new SocialDirectorSaveStore();
                socialStore.Normalize();
                if (RepairSocialStoreAfterLoad())
                    socialDirty = true;
            }
            catch (Exception ex)
            {
                socialStore = new SocialDirectorSaveStore();
                socialDirty = true;
                Monitor.Log($"读取 NPC 社交导演状态失败，将使用空状态：{ex}", LogLevel.Error);
            }

            LoadDreams();
            LoadTownEchoNetwork();
            SpreadTownEchoes();
            EnsureTodaySocialPlan(persistImmediately: true);
            TryStartOvernightMailPlan();
            LoadWeeklyStories();
        }
        else if (!Context.IsSplitScreen)
        {
            npcCombatStateService.Reset();
            memoryStore = new ConversationMemoryStore();
            socialStore = new SocialDirectorSaveStore();
            weeklyStoryStore = new WeeklyStorySaveStore();
            memoryDirty = false;
            socialDirty = false;
            if (!warnedFarmhandPersistence)
            {
                warnedFarmhandPersistence = true;
                Monitor.Log("当前为联机农场助手，AI 对话可用，但此版本只为主机持久化记忆。", LogLevel.Warn);
                ShowHud(L.T("text.55394dc64d7a"), HUDMessage.error_type);
            }
        }

        InvalidateGiftMailAsset();
        ResetVanillaInteractionTracking();
        RequestGiftMailIntegrityCheck();
        overnightMailDeliveryReadyDay = Game1.Date.TotalDays;

        if (string.IsNullOrWhiteSpace(runtimeApiKey))
        {
            if (Context.IsMainPlayer || !Context.IsSplitScreen)
            {
                state.RequestApiKeyPrompt = true;
                ShowHud(L.T("text.c832020ea16e"), HUDMessage.error_type);
            }
            else
            {
                ShowHud(L.T("text.1d9a317979f5"), HUDMessage.error_type);
            }
        }
        else
        {
            ShowHud(L.F("text.aa8eb0c6d90b", $"AI 村民记忆已就绪：面向村民按 {config.ChatKey} 对话。"), HUDMessage.newQuest_type);
        }
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        AbortDream(false);
        PersistDreams();
        if (activeFreeStory is not null) weeklyStoryRunner.Interrupt("saving");
        CloseWeeklyInvitationPresentation();
        RefreshVanillaEventLifecycle();
        FinishCompletedSignalAnalyses();
        PersistMemory(force: true);
        PersistNpcCombatState();
        PersistSocial(force: true);
        PersistWeeklyStories(force: true);
    }

    private void OnSaved(object? sender, SavedEventArgs e)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
            return;

        overnightMailDeliveryReadyDay = Game1.Date.TotalDays;
        RequestGiftMailIntegrityCheck();
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        AbortDream(false);
        PersistDreams();
        OnWeeklyStoryDayEnding();
        npcPresentationService.Clear();
        conversationSessionMemory.Clear();
        npcMoveToolService.CancelAll("day_ending");
        npcMineGuardService.CancelAll("day_ending");
        npcFishingService.CancelAll("day_ending");
        CompleteActiveVanillaEvent("day_ending");
        overnightMailDeliveryReadyDay = -1;
        foreach (ConversationScreenState state in screenStates.GetActiveValues().Select(pair => pair.Value))
        {
            if (state.HasPendingConversation)
                CancelPendingConversation(state);
            ClearConversationContinuation(state);
            CancelPendingSocialScene(state, retryToday: false);
        }

        ExpireTodaySocialPlan();
        FinishCompletedSignalAnalyses();
        PrepareOvernightMailPlan();
        TryStartOvernightMailPlan();
        PersistNpcCombatState();
        PersistSocial(force: true);
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        ConversationScreenState state = screenStates.Value;
        if (!e.IsLocalPlayer)
            return;

        StopNpcSpeech();
        OnWeeklyStoryWarped(e);

        npcPresentationService.Clear();
        ResetVanillaDialoguePageTracking();

        string warpedPlayerId = e.Player.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture);
        if (state.PendingSocialInfo?.PlayerId.Equals(warpedPlayerId, StringComparison.Ordinal) == true
            || state.QueuedSocialScene?.PlayerId.Equals(warpedPlayerId, StringComparison.Ordinal) == true
            || state.ActiveSocialScene?.PlayerId.Equals(warpedPlayerId, StringComparison.Ordinal) == true
            || state.SocialMenu is not null)
        {
            CancelPendingSocialScene(state, retryToday: true);
        }

        RecordSocialWarp(e);
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        StopAllNpcSpeech();
        speechPlayback.ResetAllScreens();
        ResetSegmentWork();
        AbortDream(false);
        dreamStore = new(); dreamStorageReady = false;
        OnWeeklyStoryReturnedToTitle();
        npcPresentationService.Clear();
        conversationOrchestrator.ClearPending();
        conversationSessionMemory.Clear();
        npcMoveToolService.CancelAll("returned_to_title");
        npcMineGuardService.CancelAll("returned_to_title");
        npcFishingService.CancelAll("returned_to_title");
        foreach (ConversationScreenState state in screenStates.GetActiveValues().Select(pair => pair.Value))
        {
            CancelPendingConversation(state);
            CancelPendingSocialScene(state, retryToday: false);
            state.QueuedDialogue = null;
            ClearConversationContinuation(state);
            state.RequestApiKeyPrompt = false;
        }
        ResetSocialBackgroundWork();
        ResetVanillaInteractionTracking();
        screenStates.ResetAllScreens();
        memoryArchive = null;
        memoryStorageReady = false;
        ConversationTimelinePolicy.ArchiveBeforePrune = null;
        ConversationMemoryPolicy.ArchiveBeforeSummaryCompaction = null;
        memoryStore = new ConversationMemoryStore();
        npcCombatStateService.Reset();
        socialStore = new SocialDirectorSaveStore();
        InvalidateGiftMailAsset();
        memoryDirty = false;
        socialDirty = false;
        gameBridgeReceipts.Clear();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        ProcessGameBridgeWorkItems();
        UpdateNpcSpeech();
        if (!Context.IsWorldReady)
            return;

        UpdateSegmentWork();
        if (UpdateDream()) return;
        npcMoveToolService.Update();
        npcMineGuardService.Update();
        npcFishingService.Update();
        npcPresentationService.Update();
        UpdateWeeklyStories(e);
        if (npcCombatStateService.InitializeDefaultWeaponsForWorld())
            PersistNpcCombatState();
        npcCombatStateService.Update();
        ConversationScreenState state = screenStates.Value;
        TrackVanillaInteractions();
        if (state.RequestApiKeyPrompt && CanOpenOwnMenu())
        {
            state.RequestApiKeyPrompt = false;
            OpenApiKeyPrompt();
            return;
        }

        string currentPlayerId = GetPlayerId();
        if (state.PendingGiftPlan is not null
            && state.PendingGiftPlan.IsCompleted
            && state.PendingInfo?.PlayerId == currentPlayerId)
        {
            FinishPendingGiftPlan(state);
        }

        if (state.PendingGraphDecision is not null
            && state.PendingGraphDecision.IsCompleted
            && state.PendingInfo?.PlayerId == currentPlayerId)
        {
            FinishPendingGraphDecision(state);
        }

        if (state.PendingConversation is not null
            && state.PendingConversation.IsCompleted
            && state.PendingInfo?.PlayerId == currentPlayerId)
        {
            FinishPendingConversation(state);
        }

        FinishCompletedSignalAnalyses();
        EnsureGiftMailInboxIntegrity();
        FinishCompletedOvernightMailPlan();
        TrackOpenGiftMailAttachment();

        if (state.PendingSocialScene is not null
            && state.PendingSocialScene.IsCompleted
            && state.PendingSocialInfo?.PlayerId == currentPlayerId)
        {
            FinishPendingSocialScene(state);
        }

        if (state.QueuedDialogue is not null
            && state.QueuedDialogue.PlayerId == currentPlayerId
            && CanOpenOwnMenu())
        {
            QueuedDialogue dialogue = state.QueuedDialogue;
            state.QueuedDialogue = null;
            ShowNpcDialogue(dialogue);
        }

        if (state.QueuedConversationContinuation is not null)
        {
            if (state.ConversationContinuationDelayUpdates > 0)
            {
                state.ConversationContinuationDelayUpdates--;
            }
            else if (CanOpenOwnMenu())
            {
                ConversationContinuationTarget target = state.QueuedConversationContinuation;
                ClearConversationContinuation(state);
                TryOpenConversationContinuation(target);
            }
        }

        if (state.QueuedSocialScene is not null
            && state.QueuedSocialScene.PlayerId == currentPlayerId
            && CanOpenOwnMenu())
        {
            QueuedSocialScene scene = state.QueuedSocialScene;
            state.QueuedSocialScene = null;
            ShowSocialEncounter(state, scene);
        }

        if (e.IsMultipleOf(60))
        {
            ObserveTownEchoExperiences();
            EnsureTodaySocialPlan(persistImmediately: true);
            TryStartOvernightMailPlan();
            TryStartSocialEncounter(state);
        }

        if (Context.IsMainPlayer && memoryDirty)
            PersistMemory(force: false);
        if (Context.IsMainPlayer && socialDirty)
            PersistSocial(force: false);
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
            return;

        npcMineGuardService.DrawWorld(e.SpriteBatch);
        npcFishingService.DrawWorld(e.SpriteBatch);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;

        if (HandleSceneReplyEnter(e.Button)) return;

        if (e.Button != config.ChatKey)
            return;

        // Don't consume the configured key while the player is typing in or using another menu.
        if (!CanOpenOwnMenu())
            return;

        ConversationScreenState state = screenStates.Value;
        Helper.Input.Suppress(e.Button);
        ClearConversationContinuation(state);

        if (state.PendingSocialScene is not null || state.QueuedSocialScene is not null)
            CancelPendingSocialScene(state, retryToday: true);

        NPC? companionTarget = FindTargetNpc();
        if (!state.HasPendingConversation && companionTarget is not null && HasOwnCompanionTrip(companionTarget))
        {
            OpenMessagePrompt(companionTarget);
            return;
        }

        if (string.IsNullOrWhiteSpace(runtimeApiKey))
        {
            state.RequestApiKeyPrompt = true;
            ShowHud(L.T("text.832e6f9098bf"), HUDMessage.error_type);
            return;
        }

        if (state.HasPendingConversation)
        {
            string npcDisplayName = state.PendingInfo?.NpcDisplayName ?? L.T("text.4781f6b2a158");
            CancelPendingConversation(state);
            ShowHud(L.F("text.7cf46b5ca7a7", $"已取消与 {npcDisplayName} 的请求。"));
            return;
        }

        NPC? npc = FindTargetNpc();
        if (npc is null)
        {
            ShowHud(L.T("text.177dfb5092da"), HUDMessage.error_type);
            return;
        }

        npc.facePlayer(Game1.player);
        Monitor.Log($"已打开与 {npc.Name}（{npc.displayName}）的 AI 输入框。", LogLevel.Debug);
        OpenMessagePrompt(npc);
    }

    private NPC? FindTargetNpc()
    {
        NPC? target = Game1.currentLocation.isCharacterAtTile(Game1.player.GetGrabTile());
        if (IsValidTarget(target))
            return target;

        if (!config.AllowNearbyNpcFallback)
            return null;

        Vector2 playerTile = Game1.player.Tile;
        float maximumSquared = config.MaxTalkDistanceTiles * config.MaxTalkDistanceTiles;
        return Game1.currentLocation.characters
            .Where(IsValidTarget)
            .Select(npc => new { Npc = npc, Distance = Vector2.DistanceSquared(playerTile, npc.Tile) })
            .Where(item => item.Distance <= maximumSquared)
            .OrderBy(item => item.Distance)
            .Select(item => item.Npc)
            .FirstOrDefault();
    }

    private bool IsValidTarget(NPC? npc)
        => npc is not null
           && npc.IsVillager
           && npc.CanSocialize
           && !npc.IsInvisible
           && !npcCombatStateService.IsHospitalized(npc.Name);

    private void OpenMessagePrompt(NPC npc, string? draft = null)
    {
        StopNpcSpeech();
        int screenId = Context.ScreenId;
        string npcName = npc.Name;
        string displayName = npc.displayName;
        Farmer tripPlayer = Game1.player;
        Game1.activeClickableMenu = new AiChatInputMenu(
            displayName,
            text =>
            {
                try
                {
                    BeginConversation(screenId, npcName, displayName, text);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"启动与 {npcName} 的 AI 对话失败：{ex}", LogLevel.Error);
                    ShowHud(CleanErrorForPlayer(ex.Message), HUDMessage.error_type);
                }
            },
            onCancel: () => { },
            onOpenSettings: OpenApiKeyPrompt,
            conversationUiScale: config.ConversationUiScale,
            onShare: () => OpenShareCard(npc),
            initialText: draft ?? string.Empty,
            onPersonality: text => OpenNpcPersonality(npc, text),
            onEndTrip: HasOwnCompanionTrip(npc) ? () => EndCompanionTrip(npc, tripPlayer) : null,
            portraitNpc: npc);
    }

    private void QueueConversationContinuation(
        int screenId,
        ConversationContinuationTarget target)
    {
        ConversationScreenState state = screenStates.GetValueForScreen(screenId);
        if (state.HasPendingConversation)
            return;

        state.QueuedConversationContinuation = target;
        // The reply already finished its outro. Open on the next post-update pass.
        state.ConversationContinuationDelayUpdates = 0;
    }

    private void TryOpenConversationContinuation(ConversationContinuationTarget target)
    {
        NPC? npc = Game1.getCharacterFromName(target.NpcName, mustBeVillager: false, includeEventActors: true);
        bool npcAvailable = IsValidTarget(npc);
        ConversationContinuationBlockReason blockReason = ConversationContinuationPolicy.Evaluate(
            target,
            GetPlayerId(),
            Game1.Date.TotalDays,
            npcAvailable);
        if (blockReason != ConversationContinuationBlockReason.None || npc is null)
        {
            string reason = blockReason switch
            {
                ConversationContinuationBlockReason.DayChanged => L.T("text.0fdc4c6d55b3"),
                ConversationContinuationBlockReason.NpcUnavailable => L.F("text.ba40266c8b4e", $"{target.NpcDisplayName} 已经离开"),
                _ => L.T("text.adefc76795b6"),
            };
            ShowHud(L.F("text.79d670ee019d", $"连续对话已结束：{reason}。"), HUDMessage.error_type);
            return;
        }

        npc.facePlayer(Game1.player);
        Monitor.Log($"继续与 {npc.Name}（{npc.displayName}）的 AI 对话。", LogLevel.Debug);
        OpenMessagePrompt(npc);
    }

    private static void ClearConversationContinuation(ConversationScreenState state)
    {
        state.QueuedConversationContinuation = null;
        state.ConversationContinuationDelayUpdates = 0;
    }

    private void BeginConversation(int screenId, string npcName, string npcDisplayName, string text)
    {
        ConversationScreenState state = screenStates.GetValueForScreen(screenId);
        string userText = NormalizeUserText(text);
        if (userText.Length == 0)
        {
            Monitor.Log("输入框提交了空消息，已忽略。", LogLevel.Warn);
            return;
        }

        if (!Context.IsWorldReady || state.HasPendingConversation)
            return;

        if (string.IsNullOrWhiteSpace(runtimeApiKey))
        {
            state.RequestApiKeyPrompt = true;
            ShowHud(L.T("text.832e6f9098bf"), HUDMessage.error_type);
            return;
        }

        NPC? npc = Game1.getCharacterFromName(npcName, mustBeVillager: false, includeEventActors: true);
        if (npc is null)
        {
            ShowHud(L.T("text.251255c18fb6"), HUDMessage.error_type);
            return;
        }

        string playerId = GetPlayerId();
        var storedMemory = memoryStore.GetOrCreate(playerId, npcName);
        SegmentMemoryPolicy.Enable(storedMemory);
        if (WaitForSegmentSummary(storedMemory, screenId, npcName, npcDisplayName, userText)) return;
        NpcGameSnapshot snapshot = BuildNpcGameSnapshot(npc, playerId, includeLiveActivities: false);
        NpcConversationMemory memory = memoryStore.TryGet(playerId, npcName, out NpcConversationMemory? existingMemory)
            && existingMemory is not null
                ? existingMemory.Clone()
                : new NpcConversationMemory
                {
                    PlayerId = playerId,
                    NpcName = npcName,
                };
        SegmentMemoryPolicy.Enable(memory);
        // Source dialogue remains in save data; disk archival runs only at a segment boundary.
        string gameDate = $"{Game1.Date} {Game1.timeOfDay}";
        string graphRequestId = Guid.NewGuid().ToString("N");
        string graphActionId = "conversation:" + graphRequestId;
        string giftActionId = string.Empty;
        IReadOnlyList<SocialGiftCandidate> giftCandidates = Array.Empty<SocialGiftCandidate>();
        bool directGiftRequest = ConversationActionIntentPolicy.IsDirectGiftRequest(userText);
        bool moveToolRequested = ConversationActionIntentPolicy.ShouldOfferMoveTool(userText);
        IReadOnlyList<ConversationMoveDestination> moveDestinations;
        string? mineGuardBlockReason = npcMineGuardService.GetAvailabilityReason(npc, Game1.currentLocation);
        bool mineGuardAvailable = mineGuardBlockReason is null;
        Monitor.Log(
            $"下矿护卫候选 {npcName}：可用={mineGuardAvailable}，阻止原因={mineGuardBlockReason ?? "无"}。",
            LogLevel.Debug);
        string? fishingBlockReason = npcFishingService.GetAvailabilityReason(
            npc,
            Game1.currentLocation,
            npcMoveToolService.HasActiveSession(npc.Name)
            || npcMineGuardService.HasActiveSession(npc.Name));
        bool fishingCompanionAvailable = fishingBlockReason is null;
        Monitor.Log(
            $"钓鱼同行候选 {npcName}：可用={fishingCompanionAvailable}，阻止原因={fishingBlockReason ?? "无"}。",
            LogLevel.Debug);
        if (moveToolRequested)
        {
            var moveCandidateTimer = System.Diagnostics.Stopwatch.StartNew();
            moveDestinations = npcMoveToolService.BuildDestinations(npc, Game1.currentLocation);
            moveCandidateTimer.Stop();
            Monitor.Log(
                $"当面对话移动候选 {npcName}：候选数={moveDestinations.Count}，耗时={moveCandidateTimer.Elapsed.TotalMilliseconds:F1}ms。",
                LogLevel.Debug);
        }
        else
        {
            moveDestinations = Array.Empty<ConversationMoveDestination>();
            Monitor.Log($"当面对话未检测到明确地点请求，已隐藏 move_to 工具（{npcName}）。", LogLevel.Debug);
        }
        string activitySummary = string.Empty;
        if (Context.IsMainPlayer && config.EnableSocialDirector)
        {
            PlayerSocialDirectorState socialPlayer = socialStore.GetOrCreatePlayer(playerId);
            NpcSocialState npcSocialState = socialPlayer.GetOrCreateNpc(npcName);
            giftActionId = CreateConversationGiftActionId(
                Game1.Date.TotalDays,
                npcName,
                memory.TotalTurns + 1);
            GiftPolicyContext giftContext = CreateGiftPolicyContext(
                giftActionId,
                npcName,
                npcSocialState);
            SocialGiftCandidateSet giftSet = giftPolicyService.BuildCandidateSet(giftContext);
            directGiftRequest = directGiftRequest || ConversationActionIntentPolicy.IsDirectGiftRequest(
                userText,
                giftSet.Candidates.Select(candidate => candidate.DisplayName));
            if (directGiftRequest)
            {
                Monitor.Log(
                    $"当面对话礼物候选 {npcName}：玩家正在直接索要，本轮不向 LLM 提供 give_gift。",
                    LogLevel.Debug);
            }
            else
            {
                giftCandidates = giftSet.Candidates;
                Monitor.Log(
                    $"当面对话礼物候选 {npcName}：候选数={giftSet.Candidates.Count}，阻止原因={giftSet.BlockReason}。",
                    LogLevel.Debug);
            }
        }

        var options = new ConversationEngineOptions
        {
            Model = config.Model,
            ThinkingType = config.EnableThinking ? "enabled" : "disabled",
            ReasoningEffort = config.ReasoningEffort,
            MaxContextMessages = config.MaxContextMessages,
            MaxOutputTokens = config.MaxOutputTokens,
            SummaryTriggerMessageCount = config.SummaryTriggerMessages,
            RecentMessagesToKeep = config.SummaryKeepRecentMessages,
        };

        int npcHearts = Game1.player.getFriendshipHeartLevelForNPC(npcName);
        Game1.player.friendshipData.TryGetValue(npcName, out Friendship? relationshipFriendship);
        int relationshipPoints = relationshipFriendship?.Points ?? 0;
        string relationshipStatus = GameContextBuilder.DescribeRelationship(relationshipFriendship);
        IReadOnlyList<string> timeline = ConversationTimelinePolicy.BuildTimeline(memory);
        List<ConversationMemoryMessage> recentMessages = ConversationMemoryPolicy.KeepRecentConversationTurns(memory.Messages);
        var recallCandidates = new List<ContextMemoryCandidate>();
        TownEchoRecall? townEcho = null;
        if (ConversationRecallPolicy.ShouldSearch(memory, userText))
        {
            if (ConversationRecallPolicy.DreamQuery(userText) && dreamStorageReady)
                recallCandidates.AddRange(DreamPolicy.RecallEpisodes(DreamPlayer(), npcName, Game1.Date.TotalDays, userText));
            else
            {
                if (!ConversationRecallPolicy.NeedsOriginalEvidence(userText))
                    recallCandidates.AddRange(ContextMemoryCompiler.FromSummary(
                        ConversationCommitmentPolicy.PrepareRecall(memory.Summary,
                            socialStore.GetOrCreatePlayer(playerId).GetOrCreateNpc(npcName), Game1.Date.TotalDays)));
                townEcho = RecallTownEcho(playerId, npcName, userText);
                if (memoryArchive is not null)
                    recallCandidates.AddRange(memoryArchive.RecallEpisodes(playerId, npcName, userText));
            }
        }
        string echoId = townEcho is null ? "" : "echo-" + townEcho.EventId;
        if (townEcho is not null) recallCandidates.Add(new(echoId,townEcho.Text,"rumor",Priority:-5));
        var promptMessages = memory.Messages.Where(m => m.TimelineSequence > memory.SummarizedThroughSequence
            && !string.IsNullOrWhiteSpace(m.Content)).ToArray();
        var covered = promptMessages.Select(MemoryEventArchive.EventId).ToArray();
        ContextMemoryResult recall = ConversationRecallPolicy.Select(memory, userText, recallCandidates);
        if (recall.SelectedIds.Count > 0 && !ConversationRecallPolicy.ExplicitHistory(userText))
            memory.AutomaticRecallCount++;
        string recalledMemory = recall.Text;
        var recentKnownEvents = BuildRecentKnownEvents(npcName, "");
        memory.Messages.AddRange(SegmentMemoryPolicy.NewContextReferences(memory.Messages.Concat(memory.SegmentBridge),
            recalledMemory, recentKnownEvents, gameDate, memory.SegmentMemory + "\n" + memory.SegmentSummary));
        // These references belong to the pending snapshot; cancelled/failed requests do not commit them.
        timeline = ConversationTimelinePolicy.BuildTimeline(memory);
        bool echoSelected = townEcho is not null && recall.SelectedIds.Contains(echoId,StringComparer.Ordinal);
        Monitor.Log($"Memory recall {npcName}: candidates={recall.CandidateCount}, selected={recall.SelectedIds.Count}, "
            + $"estimated_tokens={recall.EstimatedTokens}, duplicate_items={recall.DroppedDuplicates}.",LogLevel.Trace);
        var graphSnapshot = new NpcContextSnapshot
        {
            SchemaVersion = 1,
            FixedConversationSegments = true,
            ConversationSegment = memory.ConversationSegment,
            SegmentSummary = memory.SegmentSummary,
            SegmentMemory = memory.SegmentMemory,
            SegmentBridge = memory.SegmentBridge.Select(m => m.Clone()).ToArray(),
            SegmentMessages = memory.Messages.Where(m => m.TimelineSequence > memory.SummarizedThroughSequence).Select(m => m.Clone()).ToArray(),
            NpcName = npcName,
            NpcDisplayName = npcDisplayName,
            Identity = $"{npcDisplayName} ({npcName})",
            Personality = string.Empty,
            PersonalitySettings = ReadNpcPersonality(Game1.player, npcName),
            Memory = recalledMemory,
            RecentKnownEvents = recentKnownEvents,
            Mood = string.Empty,
            Relationship = $"当前游戏好感为 {npcHearts} 心；已经完成 {memory.TotalTurns} 轮 AI 私人对话。"
                           + "私聊轮数不代表关系升级，恋爱、婚姻与其他关系身份只以 SystemPrompt 的实时事实为准。",
            Goal = $"以 {npcDisplayName} 的原版人格自然回应玩家，先服从实时游戏事实，再决定是否使用工具。",
            WorldState = snapshot.WorldFacts.Length > 0 ? snapshot.WorldFacts : BuildManualWorldFacts(snapshot.SystemPrompt),
            PlayerProgress = "详见 systemPrompt 中的玩家进度事实。",
            SystemPrompt = BuildConversationSystemPrompt(npcDisplayName, npcName),
            NarrativeContext = snapshot.NarrativeContext,
            SceneSnapshot = snapshot.SceneSnapshot,
            MemorySummary = recalledMemory,
            MemoryCapsule = ContextMemoryCompiler.BuildCapsule(
                ConversationCommitmentPolicy.PrepareRecall(memory.Summary,
                    socialStore.GetOrCreatePlayer(playerId).GetOrCreateNpc(npcName),Game1.Date.TotalDays),
                ConversationCommitmentPolicy.Active(socialStore.GetOrCreatePlayer(playerId).GetOrCreateNpc(npcName),Game1.Date.TotalDays),
                promptMessages.Select(m=>m.Content)),
            CommitmentStates = BuildMemoryCommitmentStates(
                socialStore.GetOrCreatePlayer(playerId).GetOrCreateNpc(npcName),Game1.Date.TotalDays),
            TownEchoMemory = string.Empty, // Already selected under the single recall budget above.
            TownEchoEventId = echoSelected && townEcho?.Recipient == npcName ? townEcho.EventId : string.Empty,
            RecentSessionFacts = snapshot.RecentSessionFacts ?? Array.Empty<string>(),
            RecentMessages = recentMessages
                .Select(message => new LangGraphConversationMessage
                {
                    Role = message.Role ?? string.Empty,
                    Content = message.Content ?? string.Empty,
                    GameDate = message.GameDate ?? string.Empty,
                })
                .ToArray(),
            Timeline = timeline,
            RelationshipPoints = relationshipPoints,
            RelationshipHearts = npcHearts,
            RelationshipStatus = relationshipStatus,
            RelationshipTalkedToday = relationshipFriendship?.TalkedToToday ?? false,
            RelationshipGiftsThisWeek = relationshipFriendship?.GiftsThisWeek ?? 0,
            ActivitySummary = activitySummary,
            AllowedTools = giftCandidates.Select(gift => new LangGraphGiftCandidate
            {
                CandidateKey = gift.Key,
                DisplayName = gift.DisplayName,
                DisplayHint = gift.DisplayHint,
            }).ToArray(),
            AllowedMoveDestinations = moveDestinations.Select(destination => new LangGraphMoveDestination
            {
                DestinationKey = destination.Key,
                DisplayName = destination.DisplayName,
            }).ToArray(),
            MineGuardAvailable = mineGuardAvailable,
            FishingCompanionAvailable = fishingCompanionAvailable,
            PlayerInput = userText,
            PlayerId = playerId,
            Day = Game1.Date.TotalDays,
            Location = Game1.currentLocation?.NameOrUniqueName ?? string.Empty,
            ActionId = graphActionId,
            ContextVersion = $"{playerId}:{npcName}:{Game1.Date.TotalDays}:{Game1.currentLocation?.NameOrUniqueName}:{graphActionId}",
            Mode = "conversation",
            RequestMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["game_date"] = gameDate,
                ["npc_display_name"] = npcDisplayName,
                ["save_id"] = GetMemorySaveScope(memory),
                ["memory_revision"] = memory.TotalTurns.ToString(CultureInfo.InvariantCulture),
            },
        };

        SpeechTurn speechTurn = CaptureSpeechTurn(npcName, graphRequestId);
        if (speechTurn.Language is not null) graphSnapshot.RequestMetadata["speech_language"] = speechTurn.Language;
        graphSnapshot.RequestMetadata["speech_enabled"] = speechTurn.Enabled ? "true" : "false";
        if (speechTurn.VoiceOptionId is not null) graphSnapshot.RequestMetadata["speech_voice"] = speechTurn.VoiceOptionId;
        var pendingInfo = new PendingConversationInfo(
            playerId,
            npcName,
            npcDisplayName,
            Game1.Date.TotalDays,
            userText,
            graphSnapshot.SystemPrompt,
            gameDate,
            memory,
            options,
            BuildRecentConversationExcerpt(playerId, npcName),
            activitySummary,
            giftActionId,
            giftCandidates,
            moveDestinations,
            graphSnapshot,
            graphRequestId,
            speechTurn);
        state.PendingInfo = pendingInfo;

        var continuationTarget = new ConversationContinuationTarget(
            pendingInfo.PlayerId,
            pendingInfo.NpcName,
            pendingInfo.NpcDisplayName,
            pendingInfo.TotalDays);

        AiStreamingDialogueMenu? streamingMenu = null;
        streamingMenu = new AiStreamingDialogueMenu(
            npcName,
            npcDisplayName,
            config.MaxReplyCharacters,
            onCancel: () =>
            {
                ConversationScreenState currentState = screenStates.GetValueForScreen(screenId);
                if (ReferenceEquals(currentState.StreamingMenu, streamingMenu))
                    CancelPendingConversation(currentState, dismissMenu: false);
            },
            onContinue: () => QueueConversationContinuation(screenId, continuationTarget),
            onClosed: () =>
            {
                npcPresentationService.End(npcName);
                ConversationScreenState currentState = screenStates.GetValueForScreen(screenId);
                if (ReferenceEquals(currentState.StreamingMenu, streamingMenu))
                    currentState.StreamingMenu = null;
            },
            onShare: () =>
            {
                NPC? target = Game1.getCharacterFromName(npcName);
                if (target is not null) OpenShareCard(target);
            },
            onPersonality: () =>
            {
                NPC? target = Game1.getCharacterFromName(npcName);
                if (target is not null) OpenNpcPersonality(target);
            });
        state.StreamingMenu = streamingMenu;
        Game1.activeClickableMenu = streamingMenu;

        Monitor.Log(
            $"开始规划 {npcName} 的本轮行动；真实执行完成后才会生成可见回复。",
            LogLevel.Info);

        state.PendingGraphDecision = conversationOrchestrator.DecideAsync(
            pendingInfo.GraphSnapshot,
            Volatile.Read(ref currentAiProfile),
            pendingInfo.GraphRequestId,
            options.MaxOutputTokens,
            state.SessionCancellation.Token);
    }

    private static string BuildConversationSystemPrompt(string npcDisplayName, string npcName)
        // The engine inserts this opening into the localized conversation template.
        => RidgesideVillageCompatibility.BuildConversationIdentityPrompt(npcDisplayName, npcName);

    private static string BuildManualWorldFacts(string gameContext)
        => ConversationContextBudget.WorldFactsFromLegacy(gameContext);

    private void FinishPendingGraphDecision(ConversationScreenState state)
    {
        Task<LangGraphResponse> task = state.PendingGraphDecision!;
        PendingConversationInfo info = state.PendingInfo!;
        state.PendingGraphDecision = null;

        try
        {
            LangGraphResponse response = task.GetAwaiter().GetResult();
            if (!IsGraphContextCurrent(info))
            {
                string message = $"已丢弃 {info.NpcDisplayName} 的过期回复：玩家或日期状态已经变化。";
                state.StreamingMenu?.SetError(message);
                if (state.StreamingMenu is null)
                    ShowHud(message);
                state.PendingInfo = null;
                state.PendingMoveConfirmation = null;
                state.MoveConfirmationApproved = false;
                state.GiftExecution = null;
                state.MoveExecution = null;
                state.MineGuardExecution = null;
                state.FishingExecution = null;
                ReleaseConversationCancellation(state);
                return;
            }

            if (response.Confirmation is not null)
            {
                LangGraphMoveConfirmation confirmation = ValidateMoveConfirmation(response, info);
                ConversationMoveDestination destination = info.MoveDestinations.FirstOrDefault(value => value.Key.Equals(
                    confirmation.DestinationKey,
                    StringComparison.Ordinal))
                    ?? new ConversationMoveDestination { Key = "mine", DisplayName = "the mines" };
                state.PendingMoveConfirmation = confirmation;
                state.MoveConfirmationApproved = false;

                if (state.StreamingMenu is null)
                {
                    Monitor.Log(
                        $"{info.NpcName} 的移动确认界面已关闭，按玩家拒绝处理。",
                        LogLevel.Debug);
                    ResumePendingMoveConfirmation(state, approved: false);
                    return;
                }

                string confirmationText = confirmation.Kind.Equals("mine_guard_confirmation", StringComparison.Ordinal)
                    ? L.F("text.e2095d0df59a", $"邀请 {info.NpcDisplayName} 一起下矿担任护卫？")
                    : confirmation.Kind.Equals("fishing_confirmation", StringComparison.Ordinal)
                        ? L.F("text.134b3ff00d00", $"邀请 {info.NpcDisplayName} 和你一起钓鱼？")
                        : L.F("text.687702e0d996", $"要和 {info.NpcDisplayName} 一起去{confirmation.DisplayName}吗？");
                state.StreamingMenu.SetActionConfirmation(
                    confirmationText,
                    onApprove: () => ResumePendingMoveConfirmation(state, approved: true),
                    onDecline: () => ResumePendingMoveConfirmation(state, approved: false));
                Monitor.Log(
                    confirmation.Kind.Equals("mine_guard_confirmation", StringComparison.Ordinal)
                        ? $"等待玩家确认是否邀请 {info.NpcName} 一起下矿担任护卫。"
                        : confirmation.Kind.Equals("fishing_confirmation", StringComparison.Ordinal)
                            ? $"等待玩家确认是否邀请 {info.NpcName} 一起钓鱼。"
                        : $"等待玩家确认是否与 {info.NpcName} 同行前往 {destination.Key}（{destination.DisplayName}）。",
                    LogLevel.Info);
                return;
            }

            LangGraphDecision decision = decisionValidator.Validate(
                response,
                info.GraphSnapshot,
                config.MaxReplyCharacters,
                info.GraphRequestId);
            ConversationGiftExecutionResult execution = ValidateGraphToolExecution(
                response,
                decision,
                state.GiftExecution,
                state.MoveExecution,
                state.MineGuardExecution,
                state.FishingExecution,
                info,
                state.PendingMoveConfirmation);
            state.GiftExecution = execution;
            ConversationMoveExecutionResult moveExecution = state.MoveExecution
                ?? ConversationMoveExecutionResult.NoAction();
            ConversationMineGuardExecutionResult mineGuardExecution = state.MineGuardExecution
                ?? ConversationMineGuardExecutionResult.NoAction();
            ConversationFishingExecutionResult fishingExecution = state.FishingExecution
                ?? ConversationFishingExecutionResult.NoAction();
            if (moveExecution.IsCommitted)
                npcMoveToolService.SetTravelBarks(info.NpcName, decision.TravelBarks);

            string reply = NpcGiftToolService.GuardVisibleReply(
                LimitReply(decision.Reply),
                execution,
                info.NpcDisplayName,
                out bool guardReplaced);
            if (guardReplaced)
                Monitor.Log($"{info.NpcName} 的 graph 回复与真实礼物结果冲突，已替换为确定性台词。", LogLevel.Warn);

            NpcConversationMemory updatedMemory = ApplyGraphMemoryUpdate(
                info,
                reply,
                execution,
                moveExecution,
                mineGuardExecution,
                fishingExecution,
                decision.MemoryUpdate);
            GetPlayerMemories(info.PlayerId)[info.NpcName] = updatedMemory;
            CommitTownEchoOpportunity(info);
            memoryDirty = true;
            PersistMemory(force: false);
            RecordCompletedConversationSignalFromGraph(
                info,
                updatedMemory.TotalTurns,
                reply,
                decision.MemoryUpdate);

            PresentConversationReply(
                state,
                info,
                reply,
                GetPresentationCue(execution, moveExecution, mineGuardExecution, fishingExecution));
            state.PendingInfo = null;
            state.PendingMoveConfirmation = null;
            state.MoveConfirmationApproved = false;
            state.GiftExecution = null;
            state.MoveExecution = null;
            state.MineGuardExecution = null;
            state.FishingExecution = null;
            state.FishingExecution = null;
            Monitor.Log(
                $"{info.NpcName} 的 LangGraph 对话完成；礼物结果={execution.Outcome}，移动结果={moveExecution.Outcome}，钓鱼结果={fishingExecution.Outcome}，字符数={reply.Length}。",
                LogLevel.Info);
            ReleaseConversationCancellation(state);
        }
        catch (OperationCanceledException)
        {
            ReleaseConversationCancellation(state);
        }
        catch (Exception ex)
        {
            HandleConversationFailure(
                state,
                info,
                state.GiftExecution ?? ConversationGiftExecutionResult.NoAction(),
                Unwrap(ex),
                state.MoveExecution,
                state.MineGuardExecution,
                state.FishingExecution);
        }
    }

    private static LangGraphMoveConfirmation ValidateMoveConfirmation(
        LangGraphResponse response,
        PendingConversationInfo info)
    {
        LangGraphMoveConfirmation confirmation = response.Confirmation
            ?? throw new LangGraphValidationException("Graph response is missing move confirmation.");
        if (!response.RequestId.Equals(info.GraphRequestId, StringComparison.Ordinal))
            throw new LangGraphValidationException("Move confirmation request ID does not match the active request.");
        if (!response.ContextVersion.Equals(info.GraphSnapshot.ContextVersion, StringComparison.Ordinal))
            throw new LangGraphValidationException("Move confirmation context version is stale.");
        if (!confirmation.Kind.Equals("move_confirmation", StringComparison.Ordinal)
            && !confirmation.Kind.Equals("mine_guard_confirmation", StringComparison.Ordinal)
            && !confirmation.Kind.Equals("fishing_confirmation", StringComparison.Ordinal))
            throw new LangGraphValidationException("Graph returned an unknown confirmation kind.");
        if (string.IsNullOrWhiteSpace(confirmation.ResumeToken)
            || string.IsNullOrWhiteSpace(confirmation.ToolCallId))
        {
            throw new LangGraphValidationException("Move confirmation is missing its secure continuation data.");
        }
        if (confirmation.Kind.Equals("mine_guard_confirmation", StringComparison.Ordinal))
        {
            if (!info.GraphSnapshot.MineGuardAvailable || !string.IsNullOrWhiteSpace(confirmation.DestinationKey))
                throw new LangGraphValidationException("Mine guard confirmation is unavailable or malformed.");
            return confirmation;
        }
        if (confirmation.Kind.Equals("fishing_confirmation", StringComparison.Ordinal))
        {
            if (!info.GraphSnapshot.FishingCompanionAvailable || !string.IsNullOrWhiteSpace(confirmation.DestinationKey))
                throw new LangGraphValidationException("Fishing companion confirmation is unavailable or malformed.");
            return confirmation;
        }
        if (!(info.MoveDestinations ?? Array.Empty<ConversationMoveDestination>()).Any(destination =>
                destination.Key.Equals(confirmation.DestinationKey, StringComparison.Ordinal)))
        {
            throw new LangGraphValidationException("Move confirmation destination is outside the current allowlist.");
        }
        return confirmation;
    }

    private void ResumePendingMoveConfirmation(ConversationScreenState state, bool approved)
    {
        PendingConversationInfo? info = state.PendingInfo;
        LangGraphMoveConfirmation? confirmation = state.PendingMoveConfirmation;
        if (info is null || confirmation is null || state.PendingGraphDecision is not null)
            return;

        if (!IsGraphContextCurrent(info))
        {
            string message = $"无法让 {info.NpcDisplayName} 出发：当前情境已经变化。";
            state.StreamingMenu?.SetError(message);
            if (state.StreamingMenu is null)
                ShowHud(message, HUDMessage.error_type);
            state.PendingInfo = null;
            state.PendingMoveConfirmation = null;
            state.MoveConfirmationApproved = false;
            state.GiftExecution = null;
            state.MoveExecution = null;
            state.MineGuardExecution = null;
            ReleaseConversationCancellation(state);
            return;
        }

        ConversationMoveDestination? destination = info.MoveDestinations.FirstOrDefault(value => value.Key.Equals(
            confirmation.DestinationKey,
            StringComparison.Ordinal));
        if (confirmation.Kind.Equals("mine_guard_confirmation", StringComparison.Ordinal))
        {
            state.MoveConfirmationApproved = approved;
            if (!approved)
            {
                state.MineGuardExecution = new ConversationMineGuardExecutionResult
                {
                    RequestedToolName = NpcMineGuardToolNames.InviteMineGuard,
                    Outcome = ConversationMineGuardOutcome.Rejected,
                    FailureReason = "player_declined",
                };
            }
            state.PendingGraphDecision = conversationOrchestrator.ResumeMoveAsync(
                info.GraphRequestId,
                confirmation.ResumeToken,
                approved,
                state.SessionCancellation.Token);
            return;
        }
        if (confirmation.Kind.Equals("fishing_confirmation", StringComparison.Ordinal))
        {
            state.MoveConfirmationApproved = approved;
            if (!approved)
            {
                state.FishingExecution = new ConversationFishingExecutionResult
                {
                    RequestedToolName = NpcFishingToolNames.InviteFishingCompanion,
                    Outcome = ConversationFishingOutcome.Rejected,
                    FailureReason = "player_declined",
                };
            }
            state.PendingGraphDecision = conversationOrchestrator.ResumeMoveAsync(
                info.GraphRequestId,
                confirmation.ResumeToken,
                approved,
                state.SessionCancellation.Token);
            return;
        }
        if (destination is null)
        {
            HandleConversationFailure(
                state,
                info,
                state.GiftExecution ?? ConversationGiftExecutionResult.NoAction(),
                new LangGraphValidationException("Confirmed move destination is no longer allowed."),
                state.MoveExecution);
            return;
        }

        state.MoveConfirmationApproved = approved;
        if (!approved)
        {
            state.MoveExecution = new ConversationMoveExecutionResult
            {
                RequestedToolName = NpcMoveToolNames.MoveTo,
                Outcome = ConversationMoveOutcome.Rejected,
                Destination = destination,
                FailureReason = "player_declined",
            };
        }

        state.PendingGraphDecision = conversationOrchestrator.ResumeMoveAsync(
            info.GraphRequestId,
            confirmation.ResumeToken,
            approved,
            state.SessionCancellation.Token);
        Monitor.Log(
            $"玩家已{(approved ? "确认" : "拒绝")} {info.NpcName} 前往 {destination.Key}（{destination.DisplayName}）。",
            LogLevel.Info);
    }

    private static ConversationGiftExecutionResult ValidateGraphToolExecution(
        LangGraphResponse response,
        LangGraphDecision decision,
        ConversationGiftExecutionResult? execution,
        ConversationMoveExecutionResult? moveExecution,
        ConversationMineGuardExecutionResult? mineGuardExecution,
        ConversationFishingExecutionResult? fishingExecution,
        PendingConversationInfo info,
        LangGraphMoveConfirmation? moveConfirmation)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(info);

        ConversationGiftExecutionResult resolved = execution
            ?? ConversationGiftExecutionResult.NoAction();
        LangGraphToolExecution? toolExecution = response.ToolExecution;
        if (decision.Action.Name.Equals(NpcGiftToolNames.None, StringComparison.Ordinal))
        {
            if (toolExecution is not null && toolExecution.Ok)
                throw new LangGraphValidationException("Graph reported a successful tool execution for a none action.");
            return resolved;
        }

        if (toolExecution is null)
            throw new LangGraphValidationException("Graph action is missing its authoritative tool execution result.");
        if (!toolExecution.RequestId.Equals(info.GraphRequestId, StringComparison.Ordinal))
            throw new LangGraphValidationException("Graph tool execution request ID does not match the active request.");
        if (!toolExecution.ContextVersion.Equals(info.GraphSnapshot.ContextVersion, StringComparison.Ordinal))
            throw new LangGraphValidationException("Graph tool execution context version is stale.");
        if (string.IsNullOrWhiteSpace(toolExecution.ToolCallId))
            throw new LangGraphValidationException("Graph tool execution is missing its tool call ID.");
        if (!toolExecution.Tool.Equals(decision.Action.Name, StringComparison.OrdinalIgnoreCase))
            throw new LangGraphValidationException("Graph tool execution does not match the selected action.");
        if (decision.Action.Name.Equals(NpcMoveToolNames.MoveTo, StringComparison.Ordinal))
        {
            if (moveConfirmation is null)
                throw new LangGraphValidationException("move_to completed without player confirmation.");
            if (!toolExecution.ToolCallId.Equals(moveConfirmation.ToolCallId, StringComparison.Ordinal))
                throw new LangGraphValidationException("move_to tool call differs from the confirmed action.");
            if (!string.Equals(
                    moveConfirmation.DestinationKey,
                    decision.Action.DestinationKey,
                    StringComparison.Ordinal))
            {
                throw new LangGraphValidationException("move_to destination differs from the confirmed destination.");
            }
            if (!string.Equals(
                    toolExecution.DestinationKey,
                    decision.Action.DestinationKey,
                    StringComparison.Ordinal))
            {
                throw new LangGraphValidationException("Graph tool execution destination does not match the selected action.");
            }
            ConversationMoveExecutionResult movement = moveExecution
                ?? ConversationMoveExecutionResult.NoAction();
            if (!movement.RequestedToolName.Equals(NpcMoveToolNames.MoveTo, StringComparison.Ordinal)
                || movement.Destination is null)
            {
                throw new LangGraphValidationException("Game bridge did not record the move_to execution.");
            }
            if (!movement.Destination.Key.Equals(decision.Action.DestinationKey, StringComparison.Ordinal))
                throw new LangGraphValidationException("Game bridge destination differs from the graph action.");
            if (toolExecution.Ok != movement.IsCommitted)
                throw new LangGraphValidationException("Graph move result differs from the authoritative game result.");
            if (movement.IsCommitted
                && !toolExecution.Status.Equals("following", StringComparison.OrdinalIgnoreCase))
            {
                throw new LangGraphValidationException("Graph move result did not preserve the companion-following state.");
            }
            if (!moveConfirmation.DestinationKey.Equals(movement.Destination.Key, StringComparison.Ordinal))
                throw new LangGraphValidationException("Game move result differs from the confirmed destination.");
            if (movement.FailureReason.Equals("player_declined", StringComparison.Ordinal)
                && (!toolExecution.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(toolExecution.ReasonCode, "player_declined", StringComparison.Ordinal)))
            {
                throw new LangGraphValidationException("Graph did not preserve the player's declined move result.");
            }
            return resolved;
        }
        if (decision.Action.Name.Equals(NpcMineGuardToolNames.InviteMineGuard, StringComparison.Ordinal))
        {
            if (moveConfirmation is null
                || !moveConfirmation.Kind.Equals("mine_guard_confirmation", StringComparison.Ordinal)
                || !toolExecution.ToolCallId.Equals(moveConfirmation.ToolCallId, StringComparison.Ordinal))
            {
                throw new LangGraphValidationException("invite_mine_guard completed without the exact player confirmation.");
            }
            ConversationMineGuardExecutionResult mineGuard = mineGuardExecution
                ?? ConversationMineGuardExecutionResult.NoAction();
            if (!mineGuard.RequestedToolName.Equals(NpcMineGuardToolNames.InviteMineGuard, StringComparison.Ordinal))
                throw new LangGraphValidationException("Game bridge did not record invite_mine_guard execution.");
            if (toolExecution.Ok != mineGuard.IsCommitted)
                throw new LangGraphValidationException("Graph mine guard result differs from the authoritative game result.");
            if (mineGuard.IsCommitted
                && !toolExecution.Status.Equals("guarding", StringComparison.OrdinalIgnoreCase))
                throw new LangGraphValidationException("Graph mine guard result did not preserve the guarding state.");
            return resolved;
        }
        if (decision.Action.Name.Equals(NpcFishingToolNames.InviteFishingCompanion, StringComparison.Ordinal))
        {
            if (moveConfirmation is null
                || !moveConfirmation.Kind.Equals("fishing_confirmation", StringComparison.Ordinal)
                || !toolExecution.ToolCallId.Equals(moveConfirmation.ToolCallId, StringComparison.Ordinal))
            {
                throw new LangGraphValidationException("invite_fishing_companion completed without the exact player confirmation.");
            }
            ConversationFishingExecutionResult fishing = fishingExecution
                ?? ConversationFishingExecutionResult.NoAction();
            if (!fishing.RequestedToolName.Equals(NpcFishingToolNames.InviteFishingCompanion, StringComparison.Ordinal))
                throw new LangGraphValidationException("Game bridge did not record invite_fishing_companion execution.");
            if (toolExecution.Ok != fishing.IsCommitted)
                throw new LangGraphValidationException("Graph fishing result differs from the authoritative game result.");
            if (fishing.IsCommitted
                && !toolExecution.Status.Equals("following", StringComparison.OrdinalIgnoreCase))
            {
                throw new LangGraphValidationException("Graph fishing result did not preserve the following state.");
            }
            return resolved;
        }
        if (!string.Equals(
                toolExecution.CandidateKey,
                decision.Action.CandidateKey,
                StringComparison.Ordinal))
        {
            throw new LangGraphValidationException("Graph tool execution candidate does not match the selected action.");
        }
        if (resolved.RequestedToolName.Equals(NpcGiftToolNames.None, StringComparison.Ordinal))
            throw new LangGraphValidationException("Game bridge did not record the tool execution.");
        if (!resolved.RequestedToolName.Equals(decision.Action.Name, StringComparison.Ordinal))
            throw new LangGraphValidationException("Game bridge tool differs from the graph action.");
        if (resolved.Candidate is not null
            && !resolved.Candidate.Key.Equals(decision.Action.CandidateKey, StringComparison.Ordinal))
        {
            throw new LangGraphValidationException("Game bridge candidate differs from the graph action.");
        }
        return resolved;
    }

    private static NpcConversationMemory ApplyGraphMemoryUpdate(
        PendingConversationInfo info,
        string reply,
        ConversationGiftExecutionResult execution,
        ConversationMoveExecutionResult moveExecution,
        ConversationMineGuardExecutionResult mineGuardExecution,
        ConversationFishingExecutionResult fishingExecution,
        LangGraphMemoryUpdate update)
    {
        NpcConversationMemory memory = info.MemorySnapshot.Clone();
        memory.Messages ??= new List<ConversationMemoryMessage>();
        int firstTurnMessage = memory.Messages.Count;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        memory.Messages.Add(new ConversationMemoryMessage
        {
            Role = "user",
            Content = info.UserText,
            GameDate = info.GameDate,
            CreatedAtUtc = now,
            Source = ConversationMemorySources.AiChat,
        });
        memory.Messages.Add(new ConversationMemoryMessage
        {
            Role = "assistant",
            Content = reply,
            GameDate = info.GameDate,
            CreatedAtUtc = now,
            Source = ConversationMemorySources.AiChat,
        });
        memory.TotalTurns = checked(memory.TotalTurns + 1);
        memory.LastDate = info.GameDate;
        if (!memory.FixedConversationSegments) memory.Summary = ConversationMemoryPolicy.UpdateLongTermMemory(
            memory.Summary,
            update.SummaryPatch,
            info.GameDate,
            info.PlayerId,
            info.NpcName,
            memory.TotalTurns);
        AppendConversationGiftMemory(memory, execution, info.GameDate);
        AppendConversationMoveMemory(memory, moveExecution, info.GameDate);
        AppendConversationMineGuardMemory(memory, mineGuardExecution, info.GameDate);
        AppendConversationFishingMemory(memory, fishingExecution, info.GameDate);
        if (!memory.FixedConversationSegments) memory.Summary = ConversationMemoryPolicy.UpdateLongTermMemory(
            memory.Summary,
            BuildCommittedActionMemoryPatch(execution, moveExecution, mineGuardExecution, fishingExecution),
            info.GameDate,
            info.PlayerId,
            info.NpcName,
            memory.TotalTurns);
        ConversationTimelinePolicy.RecordCompletedTurn(memory, firstTurnMessage);
        memory.Messages = ConversationTimelinePolicy.RetainRecentMessages(memory);
        return memory;
    }

    private void FinishPendingGiftPlan(ConversationScreenState state)
    {
        Task<AiGiftToolDecision> task = state.PendingGiftPlan!;
        PendingConversationInfo info = state.PendingInfo!;
        state.PendingGiftPlan = null;

        try
        {
            AiGiftToolDecision decision = task.GetAwaiter().GetResult();
            Monitor.Log(
                $"{info.NpcName} 当面对话礼物决策：tool={decision.ToolName}，候选={decision.GiftCandidateId ?? "无"}，原因={decision.ReasonTag}。",
                LogLevel.Debug);
            if (!IsConversationContextCurrent(info))
            {
                CancelPendingConversation(state, dismissMenu: false);
                state.StreamingMenu?.SetError(L.F("text.1946f586b81d", $"已取消与 {info.NpcDisplayName} 的对话：玩家或日期状态已经变化。"));
                return;
            }

            ConversationGiftExecutionResult execution = ExecuteConversationGiftTool(info, decision);
            state.GiftExecution = execution;
            string finalSystemPrompt = npcGiftToolService.BuildFinalResponsePrompt(
                info.GameContext,
                execution);
            state.PendingConversation = conversationEngine.GenerateReplyAsync(
                runtimeApiKey,
                finalSystemPrompt,
                info.MemorySnapshot,
                info.UserText,
                info.GameDate,
                info.Options,
                state.SessionCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when returning to title or cancelling the menu.
        }
        catch (Exception ex)
        {
            HandleConversationFailure(state, info, ConversationGiftExecutionResult.NoAction(), Unwrap(ex));
        }
    }

    private void FinishPendingConversation(ConversationScreenState state)
    {
        Task<ConversationEngineResult> task = state.PendingConversation!;
        PendingConversationInfo info = state.PendingInfo!;
        ConversationGiftExecutionResult execution = state.GiftExecution
            ?? ConversationGiftExecutionResult.NoAction();
        state.PendingConversation = null;
        state.PendingInfo = null;
        state.GiftExecution = null;

        try
        {
            ConversationEngineResult result = task.GetAwaiter().GetResult();

            if (!IsConversationContextCurrent(info))
            {
                string message = $"已丢弃 {info.NpcDisplayName} 的过期回复：玩家或日期状态已经变化。";
                if (state.StreamingMenu is not null)
                    state.StreamingMenu.SetError(message);
                else
                    ShowHud(message);
                ReleaseConversationCancellation(state);
                return;
            }

            string reply = NpcGiftToolService.GuardVisibleReply(
                LimitReply(result.Reply),
                execution,
                info.NpcDisplayName,
                out bool guardReplaced);
            reply = LimitReply(reply);
            if (guardReplaced)
            {
                Monitor.Log(
                    $"{info.NpcName} 的最终回复与真实礼物结果冲突，已替换为确定性台词。",
                    LogLevel.Warn);
            }

            if (result.UpdatedMemory.Messages.Count > 0)
                result.UpdatedMemory.Messages[^1].Content = reply;
            AppendConversationGiftMemory(result.UpdatedMemory, execution, info.GameDate);

            Dictionary<string, NpcConversationMemory> playerMemories = GetPlayerMemories(info.PlayerId);
            playerMemories[info.NpcName] = result.UpdatedMemory;
            memoryDirty = true;
            PersistMemory(force: false);
            RecordCompletedConversationSignal(info, result.UpdatedMemory.TotalTurns, reply);

            if (result.Compaction.ContinuedAfterSummaryFailure)
            {
                Monitor.Log(
                    $"{info.NpcName} 的长期记忆摘要暂时失败，本轮已使用原始记忆继续：{result.Compaction.SummaryFailureReason}",
                    LogLevel.Warn);
            }

            PresentConversationReply(state, info, reply, GetPresentationCue(execution));

            Monitor.Log(
                $"{info.NpcName} 的 AI 回复完成；礼物结果={execution.Outcome}，最终字符={reply.Length}。",
                LogLevel.Info);
            ReleaseConversationCancellation(state);
        }
        catch (OperationCanceledException)
        {
            // Expected when returning to title or replacing the session.
            ReleaseConversationCancellation(state);
        }
        catch (Exception ex)
        {
            HandleConversationFailure(state, info, execution, Unwrap(ex));
        }
    }

    private void HandleConversationFailure(
        ConversationScreenState state,
        PendingConversationInfo info,
        ConversationGiftExecutionResult execution,
        Exception actual,
        ConversationMoveExecutionResult? moveExecution = null,
        ConversationMineGuardExecutionResult? mineGuardExecution = null,
        ConversationFishingExecutionResult? fishingExecution = null)
    {
        ConversationMoveExecutionResult movement = moveExecution
            ?? ConversationMoveExecutionResult.NoAction();
        ConversationMineGuardExecutionResult guard = mineGuardExecution
            ?? ConversationMineGuardExecutionResult.NoAction();
        ConversationFishingExecutionResult fishing = fishingExecution
            ?? ConversationFishingExecutionResult.NoAction();
        state.PendingGiftPlan = null;
        state.PendingGraphDecision = null;
        state.PendingConversation = null;
        state.PendingInfo = null;
        state.PendingMoveConfirmation = null;
        state.MoveConfirmationApproved = false;
        state.GiftExecution = null;
        state.MoveExecution = null;
        state.MineGuardExecution = null;
        state.FishingExecution = null;
        state.FishingExecution = null;
        Monitor.Log($"AI 对话失败：{actual}", LogLevel.Error);
        if (actual is DeepSeekApiException apiException
            && apiException.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            DisableCurrentAiKey();
            state.RequestApiKeyPrompt = true;
        }

        if (execution.IsCommitted || movement.IsCommitted || guard.IsCommitted || fishing.IsCommitted)
        {
            string reply = LimitReply(execution.IsCommitted
                ? NpcGiftToolService.CreateFallbackReply(execution, info.NpcDisplayName)
                : movement.IsCommitted
                    ? CreateMoveFallbackReply(movement)
                    : guard.IsCommitted
                        ? CreateMineGuardFallbackReply(info.NpcDisplayName)
                        : CreateFishingFallbackReply(info.NpcDisplayName));
            NpcConversationMemory memory = info.MemorySnapshot.Clone();
            memory.Messages ??= new List<ConversationMemoryMessage>();
            int firstTurnMessage = memory.Messages.Count;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            memory.Messages.Add(new ConversationMemoryMessage
            {
                Role = "user",
                Content = info.UserText,
                GameDate = info.GameDate,
                CreatedAtUtc = now,
                Source = ConversationMemorySources.AiChat,
            });
            memory.Messages.Add(new ConversationMemoryMessage
            {
                Role = "assistant",
                Content = reply,
                GameDate = info.GameDate,
                CreatedAtUtc = now,
                Source = ConversationMemorySources.AiChat,
            });
            memory.TotalTurns = checked(memory.TotalTurns + 1);
            memory.LastDate = info.GameDate;
            AppendConversationGiftMemory(memory, execution, info.GameDate);
            AppendConversationMoveMemory(memory, movement, info.GameDate);
            AppendConversationMineGuardMemory(memory, guard, info.GameDate);
            AppendConversationFishingMemory(memory, fishing, info.GameDate);
            memory.Summary = ConversationMemoryPolicy.UpdateLongTermMemory(
                memory.Summary,
                BuildCommittedActionMemoryPatch(execution, movement, guard, fishing),
                info.GameDate,
                info.PlayerId,
                info.NpcName,
                memory.TotalTurns);
            ConversationTimelinePolicy.RecordCompletedTurn(memory, firstTurnMessage);
            memory.Messages = ConversationTimelinePolicy.RetainRecentMessages(memory);

            Dictionary<string, NpcConversationMemory> playerMemories = GetPlayerMemories(info.PlayerId);
            playerMemories[info.NpcName] = memory;
            memoryDirty = true;
            PersistMemory(force: false);
            RecordCompletedConversationSignal(info, memory.TotalTurns, reply);

            PresentConversationReply(
                state,
                info,
                reply,
                GetPresentationCue(execution, movement, guard, fishing));
            Monitor.Log(
                $"{info.NpcName} 的游戏动作已经提交；最终 AI 请求失败，已使用与真实结果一致的后备台词。",
                LogLevel.Warn);
        }
        else
        {
            string playerMessage = actual is InProcessConversationEngine.ConversationProtocolException { PlayerMessage: { } protocolMessage }
                ? protocolMessage
                : CleanErrorForPlayer(actual.Message);
            if (state.StreamingMenu is not null)
                state.StreamingMenu.SetError(playerMessage);
            else
                ShowHud(playerMessage, HUDMessage.error_type);
        }

        ReleaseConversationCancellation(state);
    }

    private static void AppendConversationGiftMemory(
        NpcConversationMemory memory,
        ConversationGiftExecutionResult execution,
        string gameDate)
    {
        if (!execution.IsCommitted || execution.Candidate is null)
            return;

        string gift = execution.Quantity > 1
            ? $"{execution.Candidate.DisplayName} ×{execution.Quantity}"
            : execution.Candidate.DisplayName;
        string content = execution.IsMail
            ? $"mail_gift 已执行：{gift}已安排在次日进入玩家邮箱。"
            : $"give_gift 已执行：玩家已当面收到{gift}。";
        memory.Messages ??= new List<ConversationMemoryMessage>();
        memory.Messages.Add(new ConversationMemoryMessage
        {
            Role = "system",
            Content = content,
            GameDate = gameDate,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Source = ConversationMemorySources.ModGift,
        });
    }

    private static void AppendConversationMoveMemory(
        NpcConversationMemory memory,
        ConversationMoveExecutionResult execution,
        string gameDate)
    {
        if (!execution.IsCommitted || execution.Destination is null)
            return;

        memory.Messages ??= new List<ConversationMemoryMessage>();
        memory.Messages.Add(new ConversationMemoryMessage
        {
            Role = "system",
            Content = $"move_to 已执行：NPC 已开始和玩家一起前往{execution.Destination.DisplayName}，由玩家带路。",
            GameDate = gameDate,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Source = ConversationMemorySources.ModAction,
        });
    }

    private static string CreateMoveFallbackReply(ConversationMoveExecutionResult execution)
    {
        string destination = execution.Destination?.DisplayName ?? L.T("text.ebdef08347f3");
        return L.F("text.5808eb5d122d", $"好，那就一起去{destination}吧。你带路，我跟着你。");
    }

    private static void AppendConversationMineGuardMemory(
        NpcConversationMemory memory,
        ConversationMineGuardExecutionResult execution,
        string gameDate)
    {
        if (!execution.IsCommitted)
            return;
        memory.Messages ??= new List<ConversationMemoryMessage>();
        memory.Messages.Add(new ConversationMemoryMessage
        {
            Role = "system",
            Content = "下矿护卫已执行：NPC 接受邀请，开始跟随玩家并担任护卫。",
            GameDate = gameDate,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Source = ConversationMemorySources.ModAction,
        });
    }

    private static void AppendConversationFishingMemory(
        NpcConversationMemory memory,
        ConversationFishingExecutionResult execution,
        string gameDate)
    {
        if (!execution.IsCommitted)
            return;
        memory.Messages ??= new List<ConversationMemoryMessage>();
        memory.Messages.Add(new ConversationMemoryMessage
        {
            Role = "system",
            Content = "钓鱼同行已执行：NPC 接受邀请，开始和玩家一起钓鱼，使用铱金鱼竿并把鱼交给玩家。",
            GameDate = gameDate,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Source = ConversationMemorySources.ModAction,
        });
    }

    private static string BuildCommittedActionMemoryPatch(
        ConversationGiftExecutionResult gift,
        ConversationMoveExecutionResult movement,
        ConversationMineGuardExecutionResult guard,
        ConversationFishingExecutionResult fishing)
    {
        if (gift.IsCommitted && gift.Candidate is not null)
        {
            string item = gift.Quantity > 1
                ? $"{gift.Candidate.DisplayName} ×{gift.Quantity}"
                : gift.Candidate.DisplayName;
            return gift.IsMail
                ? $"NPC 已为玩家安排次日邮件礼物：{item}。"
                : $"NPC 已当面送给玩家：{item}。";
        }
        if (movement.IsCommitted && movement.Destination is not null)
            return $"NPC 接受玩家邀请，开始一起前往{movement.Destination.DisplayName}。";
        if (guard.IsCommitted)
            return "NPC 接受玩家邀请，开始陪玩家下矿并担任护卫。";
        if (fishing.IsCommitted)
            return "NPC 接受玩家邀请，开始和玩家一起钓鱼。";
        return string.Empty;
    }

    private static string CreateFishingFallbackReply(string npcDisplayName)
        => L.F("text.eff923eef835", $"好啊，我会跟着你一起钓鱼。你抛竿后我也准备好。");

    private static string CreateMineGuardFallbackReply(string npcDisplayName)
        => L.F("text.c3d1244e507f", $"{npcDisplayName} 答应了，会在你下矿时跟着你并负责警戒。");

    private static void ReleaseConversationCancellation(ConversationScreenState state)
    {
        CancellationTokenSource completed = state.SessionCancellation;
        state.SessionCancellation = new CancellationTokenSource();
        completed.Dispose();
    }

    private void ScheduleStoryEncounter(
        PendingConversationInfo info,
        NpcConversationMemory memory,
        string reply)
    {
        if (!Context.IsMainPlayer
            || !config.EnableProactivePilot)
        {
            return;
        }

        IReadOnlyList<StoryDefinition> stories = storyCatalog.GetForNpc(info.NpcName);
        if (stories.Count == 0)
            return;

        NpcNarrativeState narrative = narrativeStore.GetOrCreate(info.PlayerId, info.NpcName);
        narrative.LastChatDay = Game1.Date.TotalDays;
        narrative.RecentUserExcerpt = LimitNarrativeText(info.UserText, 240);
        narrative.RecentAssistantExcerpt = LimitNarrativeText(reply, 240);

        int hearts = Game1.player.getFriendshipHeartLevelForNPC(info.NpcName);
        StoryDefinition? selectedStory = stories.FirstOrDefault(story =>
            pilotNarrativePlanner.CanSchedule(
                narrative,
                story,
                memory.TotalTurns,
                hearts,
                Game1.Date.TotalDays));
        if (selectedStory is null)
        {
            narrativeDirty = true;
            return;
        }

        narrative.PendingEncounter = pilotNarrativePlanner.CreateEncounter(
            selectedStory,
            memory.TotalTurns,
            Game1.Date.TotalDays,
            info.UserText);
        narrative.LastConversationTurnScheduled = memory.TotalTurns;
        narrativeDirty = true;
        PersistNarrative(force: false);
        Monitor.Log(
            $"已为 {info.NpcName} 安排剧情 {selectedStory.Id}：第 {narrative.PendingEncounter.EarliestDay} 天起可触发。",
            LogLevel.Info);
    }

    private void TryStartProactiveEncounter(ConversationScreenState screenState)
    {
        if (!Context.IsMainPlayer
            || !config.EnableProactivePilot
            || screenState.HasPendingConversation
            || screenState.PendingProactiveScene is not null
            || screenState.QueuedProactiveScene is not null
            || screenState.ActiveProactiveScene is not null
            || screenState.ProactiveMenu is not null
            || !CanOpenOwnMenu()
            || Utility.isFestivalDay()
            || Game1.isFestival())
        {
            return;
        }

        string playerId = GetPlayerId();
        if (narrativeStore.Players is null
            || !narrativeStore.Players.TryGetValue(playerId, out Dictionary<string, NpcNarrativeState>? npcStates)
            || npcStates is null)
        {
            return;
        }

        foreach (NpcNarrativeState narrative in npcStates.Values
                     .Where(state => state?.PendingEncounter is not null)
                     .OrderBy(state => state.PendingEncounter!.EarliestDay)
                     .ThenBy(state => state.NpcName, StringComparer.OrdinalIgnoreCase))
        {
            PlannedNpcEncounter encounter = narrative.PendingEncounter!;
            if (PilotNarrativePlanner.IsExpired(encounter, Game1.Date.TotalDays))
            {
                encounter.Status = PlannedEncounterStatus.Expired;
                narrativeDirty = true;
                continue;
            }

            if (!PilotNarrativePlanner.IsReady(encounter, Game1.Date.TotalDays, Game1.timeOfDay))
                continue;

            NPC? npc = Game1.getCharacterFromName(encounter.NpcName, mustBeVillager: false, includeEventActors: true);
            float activationDistance = Math.Clamp(encounter.ActivationDistanceTiles, 1f, 16f);
            if (!IsValidTarget(npc)
                || npc.currentLocation != Game1.currentLocation
                || Vector2.DistanceSquared(Game1.player.Tile, npc.Tile)
                   > activationDistance * activationDistance)
            {
                continue;
            }

            NpcConversationMemory memory = memoryStore.TryGet(playerId, encounter.NpcName, out NpcConversationMemory? existingMemory)
                && existingMemory is not null
                ? existingMemory.Clone()
                : new NpcConversationMemory
                {
                    PlayerId = playerId,
                    NpcName = encounter.NpcName,
                };
            string giftDisplayName = GetGiftDisplayName(encounter);
            var info = new PendingProactiveInfo(
                playerId,
                encounter.ActionId,
                encounter.NpcName,
                npc.displayName,
                Game1.currentLocation.NameOrUniqueName,
                Game1.Date.TotalDays,
                encounter.GiftItemId,
                giftDisplayName,
                BuildProactiveFallback(encounter, npc.displayName, giftDisplayName),
                ResolveEncounterChoices(encounter, npc.displayName, giftDisplayName));

            npc.facePlayer(Game1.player);
            screenState.PendingProactiveInfo = info;
            if (string.IsNullOrWhiteSpace(runtimeApiKey))
            {
                encounter.Status = PlannedEncounterStatus.Ready;
                screenState.QueuedProactiveScene = CreateQueuedProactiveScene(info, info.FallbackText);
                screenState.PendingProactiveInfo = null;
                narrativeDirty = true;
                return;
            }

            encounter.Status = PlannedEncounterStatus.Generating;
            narrativeDirty = true;
            ConversationEngineOptions options = GetConversationOptions();
            NpcGameSnapshot snapshot = BuildNpcGameSnapshot(npc, playerId);
            screenState.PendingProactiveScene = GenerateProactiveSceneAsync(
                runtimeApiKey,
                snapshot.SystemPrompt,
                memory,
                encounter,
                giftDisplayName,
                options,
                screenState.ProactiveCancellation.Token);
            Monitor.Log($"{encounter.NpcName} 的剧情 {encounter.StoryId} 场景正在生成。", LogLevel.Debug);
            return;
        }
    }

    private async Task<string> GenerateProactiveSceneAsync(
        string apiKey,
        string systemPrompt,
        NpcConversationMemory memory,
        PlannedNpcEncounter encounter,
        string giftDisplayName,
        ConversationEngineOptions options,
        CancellationToken cancellationToken)
    {
        return await proactiveSceneService.GenerateAsync(
                apiKey,
                systemPrompt,
                memory,
                encounter,
                giftDisplayName,
                options,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void FinishPendingProactiveScene(ConversationScreenState screenState)
    {
        Task<string> task = screenState.PendingProactiveScene!;
        PendingProactiveInfo info = screenState.PendingProactiveInfo!;
        screenState.PendingProactiveScene = null;
        screenState.PendingProactiveInfo = null;

        if (!TryGetPlannedEncounter(info.PlayerId, info.NpcName, info.ActionId, out NpcNarrativeState? narrative, out PlannedNpcEncounter? encounter)
            || narrative is null
            || encounter is null)
            return;

        try
        {
            string dialogue = LimitProactiveScene(task.GetAwaiter().GetResult());
            if (!IsProactiveContextCurrent(info))
            {
                DeferProactiveEncounter(info.PlayerId, info.NpcName, info.ActionId);
                return;
            }

            encounter.Status = PlannedEncounterStatus.Ready;
            screenState.QueuedProactiveScene = CreateQueuedProactiveScene(
                info,
                string.IsNullOrWhiteSpace(dialogue) ? info.FallbackText : dialogue);
            narrativeDirty = true;
        }
        catch (OperationCanceledException)
        {
            // Cancellation has already restored or deferred the action.
        }
        catch (Exception ex)
        {
            Exception actual = Unwrap(ex);
            Monitor.Log($"{info.NpcName} 的主动场景 AI 文案失败，将使用静态台词：{actual}", LogLevel.Warn);
            if (actual is DeepSeekApiException apiException
                && apiException.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                DisableCurrentAiKey();
                screenState.RequestApiKeyPrompt = true;
            }

            if (IsProactiveContextCurrent(info))
            {
                encounter.Status = PlannedEncounterStatus.Ready;
                screenState.QueuedProactiveScene = CreateQueuedProactiveScene(info, info.FallbackText);
                narrativeDirty = true;
            }
            else
            {
                DeferProactiveEncounter(info.PlayerId, info.NpcName, info.ActionId);
            }
        }
    }

    private void ShowProactiveEncounter(ConversationScreenState screenState, QueuedProactiveScene scene)
    {
        if (!TryGetPlannedEncounter(scene.PlayerId, scene.NpcName, scene.ActionId, out _, out PlannedNpcEncounter? encounter)
            || encounter is null
            || !IsProactiveContextCurrent(scene))
        {
            DeferProactiveEncounter(scene.PlayerId, scene.NpcName, scene.ActionId);
            return;
        }

        NPC? npc = Game1.getCharacterFromName(scene.NpcName, mustBeVillager: false, includeEventActors: true);
        if (!IsValidTarget(npc) || npc.currentLocation != Game1.currentLocation)
        {
            DeferProactiveEncounter(scene.PlayerId, scene.NpcName, scene.ActionId);
            return;
        }

        encounter.Status = PlannedEncounterStatus.Presenting;
        narrativeDirty = true;
        npc.facePlayer(Game1.player);
        screenState.ActiveProactiveScene = scene;

        string dialogueText = SanitizeForDialogue(scene.DialogueText);
        NpcPresentationPlan presentation = NpcPresentationPolicy.Create(
            scene.NpcName,
            dialogueText,
            string.IsNullOrWhiteSpace(scene.GiftItemId)
                ? NpcPresentationCue.None
                : NpcPresentationCue.GiftOffered);
        AiProactiveEncounterMenu? menu = null;
        menu = new AiProactiveEncounterMenu(
            scene.NpcName,
            scene.NpcDisplayName,
            dialogueText,
            scene.Choices.Select(choice => new AiProactiveChoice(choice.Id, choice.Text, choice.Defer)).ToArray(),
            onChoose: choiceId => TryResolveProactiveChoice(scene, choiceId),
            onCancel: () => DeferProactiveEncounter(scene.PlayerId, scene.NpcName, scene.ActionId),
            onClosed: () =>
            {
                npcPresentationService.End(scene.NpcName);
                if (ReferenceEquals(screenState.ProactiveMenu, menu))
                {
                    screenState.ProactiveMenu = null;
                    screenState.ActiveProactiveScene = null;
                }
            },
            proactiveUiScale: config.ProactiveUiScale,
            portraitIndex: presentation.PortraitIndex);
        screenState.ProactiveMenu = menu;
        Game1.activeClickableMenu = menu;
        npcPresentationService.Begin(scene.NpcName, presentation, menu);
    }

    private EncounterChoiceResult TryResolveProactiveChoice(QueuedProactiveScene scene, string choiceId)
    {
        if (!TryGetPlannedEncounter(scene.PlayerId, scene.NpcName, scene.ActionId, out NpcNarrativeState? narrative, out PlannedNpcEncounter? encounter)
            || narrative is null
            || encounter is null
            || !IsProactiveContextCurrent(scene)
            || encounter.Status is PlannedEncounterStatus.Expired or PlannedEncounterStatus.Cancelled)
        {
            return EncounterChoiceResult.SceneExpired;
        }

        if (narrative.CompletedActionIds.Contains(scene.ActionId)
            || encounter.Status == PlannedEncounterStatus.Completed)
            return EncounterChoiceResult.AlreadyCompleted;

        PlannedStoryChoice? choice = encounter.Choices.FirstOrDefault(candidate =>
            candidate.Id.Equals(choiceId, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
            return EncounterChoiceResult.ChoiceUnavailable;

        if (choice.Defer)
        {
            DeferProactiveEncounter(scene.PlayerId, scene.NpcName, scene.ActionId);
            return EncounterChoiceResult.Completed;
        }

        bool receivedGift = false;
        if (choice.ReceiveGift)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(scene.GiftItemId))
                    return EncounterChoiceResult.GiftUnavailable;

                Item gift = ItemRegistry.Create(scene.GiftItemId, 1);
                if (!Game1.player.couldInventoryAcceptThisItem(gift)
                    || !Game1.player.addItemToInventoryBool(gift, makeActiveObject: false))
                {
                    return EncounterChoiceResult.InventoryFull;
                }
                receivedGift = true;
            }
            catch (Exception ex)
            {
                Monitor.Log($"无法交付 {scene.NpcName} 的剧情礼物：{ex}", LogLevel.Error);
                return EncounterChoiceResult.DeliveryFailed;
            }
        }

        if (!narrativeChoiceResolver.TryApply(
                narrative,
                encounter,
                choice,
                Game1.Date.TotalDays,
                receivedGift))
        {
            return EncounterChoiceResult.SceneExpired;
        }
        narrative.RecentUserExcerpt = LimitNarrativeText(choice.MemoryText, 240);
        narrativeDirty = true;
        if (receivedGift)
            RecordTownGift(scene.PlayerId, scene.NpcName, "proactive:" + scene.ActionId, "gift_given", scene.GiftDisplayName);
        else
            RecordTownEcho(new TownEchoEvent { PlayerId = scene.PlayerId, Source = scene.NpcName,
                EventKey = "proactive:" + scene.ActionId, Kind = "proactive", Day = Game1.Date.TotalDays, Detail = choice.Text });
        RecordProactiveEncounterMemory(scene, choice, receivedGift);
        bool scheduledFollowUp = TryScheduleFollowUpStory(
            narrative,
            choice.NextStoryId,
            choice.MemoryText,
            encounter.SourceConversationTurn);
        PersistNarrative(force: false);
        if (receivedGift)
            ShowHud(L.F("text.49b9f8cee16f", $"{scene.NpcDisplayName} 送给了你：{scene.GiftDisplayName}"), HUDMessage.newQuest_type);
        else if (scheduledFollowUp)
            ShowHud(L.F("text.570e7e6caa7a", $"{scene.NpcDisplayName} 记住了你的回答。"), HUDMessage.newQuest_type);
        return EncounterChoiceResult.Completed;
    }

    private bool TryScheduleFollowUpStory(
        NpcNarrativeState narrative,
        string nextStoryId,
        string triggerExcerpt,
        long sourceConversationTurn)
    {
        if (string.IsNullOrWhiteSpace(nextStoryId))
            return false;
        if (!storyCatalog.TryGet(nextStoryId, out StoryDefinition? nextStory)
            || nextStory is null
            || !nextStory.Npc.Equals(narrative.NpcName, StringComparison.OrdinalIgnoreCase))
        {
            Monitor.Log($"剧情后续节点不存在或 NPC 不匹配：{nextStoryId}", LogLevel.Warn);
            return false;
        }

        int hearts = Game1.player.getFriendshipHeartLevelForNPC(narrative.NpcName);
        if (!pilotNarrativePlanner.CanEnterStory(narrative, nextStory)
            || hearts < nextStory.Trigger.MinHearts)
        {
            Monitor.Log($"剧情后续节点 {nextStory.Id} 的前置条件尚未满足，将等待后续对话重新评估。", LogLevel.Info);
            return false;
        }

        narrative.PendingEncounter = pilotNarrativePlanner.CreateEncounter(
            nextStory,
            sourceConversationTurn,
            Game1.Date.TotalDays,
            triggerExcerpt);
        narrative.LastConversationTurnScheduled = Math.Max(
            narrative.LastConversationTurnScheduled,
            sourceConversationTurn);
        narrativeDirty = true;
        Monitor.Log(
            $"已根据选择安排后续剧情 {nextStory.Id}，第 {narrative.PendingEncounter.EarliestDay} 天起可触发。",
            LogLevel.Info);
        return true;
    }

    private void DeferProactiveEncounter(string playerId, string npcName, string actionId)
    {
        if (!TryGetPlannedEncounter(playerId, npcName, actionId, out _, out PlannedNpcEncounter? encounter)
            || encounter is null
            || encounter.Status is PlannedEncounterStatus.Completed or PlannedEncounterStatus.Expired or PlannedEncounterStatus.Cancelled)
        {
            return;
        }

        PilotNarrativePlanner.Defer(encounter, Game1.Date.TotalDays);
        narrativeDirty = true;
    }

    private void RecordProactiveEncounterMemory(
        QueuedProactiveScene scene,
        PlannedStoryChoice choice,
        bool receivedGift)
    {
        Dictionary<string, NpcConversationMemory> memories = GetPlayerMemories(scene.PlayerId);
        if (!memories.TryGetValue(scene.NpcName, out NpcConversationMemory? memory) || memory is null)
        {
            memory = new NpcConversationMemory
            {
                PlayerId = scene.PlayerId,
                NpcName = scene.NpcName,
            };
            memories[scene.NpcName] = memory;
        }

        string date = $"{Game1.Date} {Game1.timeOfDay}";
        memory.Messages.Add(new ConversationMemoryMessage
        {
            Role = "assistant",
            Content = receivedGift
                ? $"{scene.DialogueText}\n（主动来访时送给了你：{scene.GiftDisplayName}。）"
                : scene.DialogueText,
            GameDate = date,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Source = ConversationMemorySources.ModProactive,
        });
        memory.Messages.Add(new ConversationMemoryMessage
        {
            Role = "user",
            Content = $"（剧情选择：{choice.MemoryText}）",
            GameDate = date,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Source = ConversationMemorySources.ModProactive,
        });
        string durableResult = receivedGift
            ? $"主动剧情完成：{choice.MemoryText}；NPC 送给玩家{scene.GiftDisplayName}。"
            : $"主动剧情完成：{choice.MemoryText}";
        memory.Summary = ConversationMemoryPolicy.UpdateLongTermMemory(
            memory.Summary,
            durableResult,
            date,
            scene.PlayerId,
            scene.NpcName,
            memory.TotalTurns);
        memory.LastDate = date;
        memory.Messages = ConversationTimelinePolicy.RetainRecentMessages(memory);
        memoryDirty = true;
        PersistMemory(force: false);
    }

    private bool TryGetPlannedEncounter(
        string playerId,
        string npcName,
        string actionId,
        out NpcNarrativeState? narrative,
        out PlannedNpcEncounter? encounter)
    {
        narrative = null;
        encounter = null;
        if (!narrativeStore.TryGet(playerId, npcName, out narrative)
            || narrative?.PendingEncounter is null
            || !narrative.PendingEncounter.ActionId.Equals(actionId, StringComparison.Ordinal))
        {
            return false;
        }

        encounter = narrative.PendingEncounter;
        return true;
    }

    private static QueuedProactiveScene CreateQueuedProactiveScene(PendingProactiveInfo info, string dialogueText)
    {
        return new QueuedProactiveScene(
            info.PlayerId,
            info.ActionId,
            info.NpcName,
            info.NpcDisplayName,
            info.GiftItemId,
            info.GiftDisplayName,
            dialogueText,
            info.Choices);
    }

    private static IReadOnlyList<PlannedStoryChoice> ResolveEncounterChoices(
        PlannedNpcEncounter encounter,
        string npcDisplayName,
        string giftDisplayName)
    {
        IReadOnlyList<PlannedStoryChoice> source = encounter.Choices.Count > 0
            ? encounter.Choices
            : new[]
            {
                new PlannedStoryChoice
                {
                    Id = "accept",
                    Text = encounter.AcceptText,
                    MemoryText = encounter.AcceptText,
                    ReceiveGift = !string.IsNullOrWhiteSpace(encounter.GiftItemId),
                    Trust = encounter.TrustOnAccept,
                    Affection = encounter.AffectionOnAccept,
                    SetFlags = new HashSet<string>(encounter.FlagsOnAccept, StringComparer.Ordinal),
                },
                new PlannedStoryChoice
                {
                    Id = "defer",
                    Text = encounter.DeferText,
                    MemoryText = encounter.DeferText,
                    Defer = true,
                },
            };

        return source.Select(choice => new PlannedStoryChoice
        {
            Id = choice.Id,
            Text = ResolveStoryText(choice.Text, npcDisplayName, giftDisplayName),
            MemoryText = ResolveStoryText(choice.MemoryText, npcDisplayName, giftDisplayName),
            ReceiveGift = choice.ReceiveGift,
            Defer = choice.Defer,
            NextStoryId = choice.NextStoryId,
            Trust = choice.Trust,
            Affection = choice.Affection,
            SetFlags = new HashSet<string>(choice.SetFlags, StringComparer.Ordinal),
        }).ToArray();
    }

    private static string BuildProactiveFallback(
        PlannedNpcEncounter encounter,
        string npcDisplayName,
        string giftDisplayName)
    {
        string fallback = encounter.FallbackText;
        if (string.IsNullOrWhiteSpace(fallback))
        {
            string reference = string.IsNullOrWhiteSpace(encounter.TriggerExcerpt)
            ? L.T("text.251f3b3adae5")
            : L.T("text.d9e41b28537e");
            fallback = L.F("text.fa7b9c719f9a", $"{reference} 今天正好遇见你，就想来看看。这个给你，别多想……我只是觉得你可能会用得上。\n\n（{{NpcDisplayName}}递给你一份{{GiftDisplayName}}。）");
        }

        return ResolveStoryText(fallback, npcDisplayName, giftDisplayName);
    }

    private static string ResolveStoryText(string? text, string npcDisplayName, string giftDisplayName)
        => (text ?? string.Empty)
            .Replace("{NpcDisplayName}", npcDisplayName, StringComparison.Ordinal)
            .Replace("{GiftDisplayName}", giftDisplayName, StringComparison.Ordinal);

    private string GetGiftDisplayName(PlannedNpcEncounter encounter)
    {
        if (string.IsNullOrWhiteSpace(encounter.GiftItemId))
            return string.Empty;

        try
        {
            return ItemRegistry.Create(encounter.GiftItemId, 1).DisplayName;
        }
        catch (Exception ex)
        {
            Monitor.Log($"主动礼物 ID 无效：{encounter.GiftItemId}。将使用默认石英。{ex.Message}", LogLevel.Warn);
            encounter.GiftItemId = "(O)80";
            narrativeDirty = true;
            try
            {
                return ItemRegistry.Create(encounter.GiftItemId, 1).DisplayName;
            }
            catch
            {
                return L.T("text.ba001cd013bb");
            }
        }
    }

    private ConversationEngineOptions GetConversationOptions()
    {
        return new ConversationEngineOptions
        {
            Model = config.Model,
            ThinkingType = config.EnableThinking ? "enabled" : "disabled",
            ReasoningEffort = config.ReasoningEffort,
            MaxContextMessages = config.MaxContextMessages,
            MaxOutputTokens = config.MaxOutputTokens,
            SummaryTriggerMessageCount = config.SummaryTriggerMessages,
            RecentMessagesToKeep = config.SummaryKeepRecentMessages,
        };
    }

    private static string LimitNarrativeText(string? value, int maximumCharacters)
    {
        string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maximumCharacters ? normalized : normalized[..maximumCharacters] + "…";
    }

    private string LimitProactiveScene(string value)
        => LimitNarrativeText(value, config.ProactiveSceneMaxCharacters);

    private static bool IsProactiveContextCurrent(PendingProactiveInfo info)
    {
        return Context.IsWorldReady
               && GetPlayerId().Equals(info.PlayerId, StringComparison.Ordinal)
               && Game1.Date.TotalDays == info.TotalDays
               && Game1.currentLocation.NameOrUniqueName.Equals(info.LocationName, StringComparison.Ordinal);
    }

    private static bool IsProactiveContextCurrent(QueuedProactiveScene scene)
    {
        return Context.IsWorldReady
               && GetPlayerId().Equals(scene.PlayerId, StringComparison.Ordinal)
               && Game1.Date.TotalDays == scene.TotalDays
               && Game1.currentLocation.NameOrUniqueName.Equals(scene.LocationName, StringComparison.Ordinal);
    }

    private void ShowNpcDialogue(QueuedDialogue dialogue)
    {
        NPC? npc = Game1.getCharacterFromName(dialogue.NpcName, mustBeVillager: false, includeEventActors: true);
        if (npc is null)
        {
            Game1.drawObjectDialogue($"{dialogue.NpcDisplayName}：{dialogue.Text}");
            return;
        }

        string emotion = dialogue.Presentation.DialogueEmotionToken;
        string displayedText = emotion.Length == 0
            ? dialogue.Text
            : $"{dialogue.Text} {emotion}";
        Game1.DrawDialogue(new Dialogue(npc, GeneratedDialogueKey, displayedText));
        npcPresentationService.Begin(dialogue.NpcName, dialogue.Presentation, Game1.activeClickableMenu);
        PlayNpcSpeech(dialogue.Speech, dialogue.SpokenReply ?? dialogue.Text, Game1.activeClickableMenu);
    }

    private void PresentConversationReply(
        ConversationScreenState state,
        PendingConversationInfo info,
        string reply,
        NpcPresentationCue cue)
    {
        NpcPresentationPlan presentation = NpcPresentationPolicy.Create(info.NpcName, reply, cue);
        if (state.StreamingMenu is not null)
        {
            state.StreamingMenu.SetCompleted(reply, presentation.PortraitIndex);
            if (state.StreamingMenu.HasReply)
            {
                var speechOwner = state.StreamingMenu;
                if (info.Speech is { Enabled: true }) speechOwner.SetSpeechActions(() => PlayNpcSpeech(info.Speech, reply, speechOwner), StopNpcSpeech);
                PlayNpcSpeech(info.Speech, reply, speechOwner);
            }
            npcPresentationService.Begin(info.NpcName, presentation, state.StreamingMenu);
            return;
        }

        state.QueuedDialogue = new QueuedDialogue(
            info.PlayerId,
            info.NpcName,
            info.NpcDisplayName,
            SanitizeForDialogue(reply),
            presentation, info.Speech, reply);
    }

    private static NpcPresentationCue GetPresentationCue(
        ConversationGiftExecutionResult gift,
        ConversationMoveExecutionResult? movement = null,
        ConversationMineGuardExecutionResult? guard = null,
        ConversationFishingExecutionResult? fishing = null)
    {
        if (gift.IsCommitted)
            return NpcPresentationCue.GiftDelivered;
        if (gift.Outcome == ConversationGiftOutcome.Rejected)
            return NpcPresentationCue.GiftDeclined;
        if (movement?.IsCommitted == true || guard?.IsCommitted == true || fishing?.IsCommitted == true)
            return NpcPresentationCue.ActivityAccepted;
        if (movement?.Outcome == ConversationMoveOutcome.Rejected
            || guard?.Outcome == ConversationMineGuardOutcome.Rejected
            || fishing?.Outcome == ConversationFishingOutcome.Rejected)
        {
            return NpcPresentationCue.ActivityDeclined;
        }

        return NpcPresentationCue.None;
    }

    private void OpenApiKeyPrompt()
    {
        if (!Context.IsWorldReady)
            return;

        if (AiProviderNames.Normalize(config.Ai.ActiveProvider) == AiProviderNames.Hosted)
        {
            Game1.activeClickableMenu = new HostedAccountMenu(
                AuthenticateHostedAsync,
                (token, model, balance) => CompleteHostedLogin(token, model, balance),
                RedeemHostedAsync,
                onOpenDirectSettings: OpenDirectProviderSettings,
                onOpenSpeech: OpenSpeechSettings, priceSummary: SpeechPriceSummary,
                speechEnabled: () => config.EnableNpcSpeech,
                toggleSpeech: () => SaveSpeechSettings(!config.EnableNpcSpeech, config.NpcSpeechVolume),
                refreshBalance: FetchHostedBalanceAsync,
                requestVerificationCode: RequestHostedVerificationCodeAsync,
                resetPassword: ResetHostedPasswordAsync,
                initialToken: config.Ai.Hosted.ApiKey,
                initialModel: config.Ai.Hosted.Model);
            return;
        }

        Game1.activeClickableMenu = new AiProviderSettingsMenu(
            config.Ai,
            SaveAiProviderSettings,
            TestAiProviderSettingsAsync,
            onCancel: () => { },
            conversationUiScale: config.ConversationUiScale,
            onSaveConversationUiScale: SaveConversationUiScale,
            proactiveUiScale: config.ProactiveUiScale,
            onSaveProactiveUiScale: SaveProactiveUiScale,
            onOpenHosted: OpenHostedAccountSettings);
    }

    private void OpenHostedAccountSettings()
    {
        Game1.activeClickableMenu = new HostedAccountMenu(
            AuthenticateHostedAsync,
            (token, model, balance) => CompleteHostedLogin(token, model, balance),
            RedeemHostedAsync,
            onOpenDirectSettings: OpenDirectProviderSettings,
            onOpenSpeech: OpenSpeechSettings, priceSummary: SpeechPriceSummary,
            speechEnabled: () => config.EnableNpcSpeech,
            toggleSpeech: () => SaveSpeechSettings(!config.EnableNpcSpeech, config.NpcSpeechVolume),
            refreshBalance: FetchHostedBalanceAsync,
            requestVerificationCode: RequestHostedVerificationCodeAsync,
            resetPassword: ResetHostedPasswordAsync,
            initialToken: config.Ai.Hosted.ApiKey,
            initialModel: config.Ai.Hosted.Model);
    }

    private void OpenDirectProviderSettings()
    {
        Game1.activeClickableMenu = new AiProviderSettingsMenu(
            config.Ai,
            SaveAiProviderSettings,
            TestAiProviderSettingsAsync,
            onCancel: () => { },
            conversationUiScale: config.ConversationUiScale,
            onSaveConversationUiScale: SaveConversationUiScale,
            proactiveUiScale: config.ProactiveUiScale,
            onSaveProactiveUiScale: SaveProactiveUiScale,
            onOpenHosted: OpenHostedAccountSettings);
    }

    private async Task<(bool Success, string Message, string Token, string Model, long BalanceMicros)> AuthenticateHostedAsync(
        string email,
        string password,
        bool register,
        string challengeId,
        string verificationCode)
    {
        string route = register ? "/api/v1/mod/auth/register" : "/api/v1/mod/auth/login";
        using var request = new HttpRequestMessage(HttpMethod.Post, HostedServiceOrigin + route)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    email,
                    password,
                    challenge_id = register ? challengeId : null,
                    verification_code = register ? verificationCode : null,
                }),
                Encoding.UTF8,
                "application/json"),
        };
        using HttpResponseMessage response = await aiHttpClient.SendAsync(request).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!response.IsSuccessStatusCode)
            {
                string message = document.RootElement.TryGetProperty("error", out JsonElement error) && error.TryGetProperty("message", out JsonElement text) ? text.GetString() ?? L.T("text.0755d81df3ff") : L.T("text.0755d81df3ff");
                return (false, message, string.Empty, string.Empty, -1);
            }
            string token = document.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
            string model = "vv-dialogue";
            long balanceMicros = -1;
            try
            {
                using var bootstrap = new HttpRequestMessage(HttpMethod.Get, HostedServiceOrigin + "/api/v1/mod/bootstrap");
                bootstrap.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage bootstrapResponse = await aiHttpClient.SendAsync(bootstrap).ConfigureAwait(false);
                if (bootstrapResponse.IsSuccessStatusCode)
                {
                    using JsonDocument catalog = JsonDocument.Parse(await bootstrapResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
                    if (!speechClient.AcceptBootstrap(catalog.RootElement, token))
                        Monitor.Log("Speech catalog missing or invalid; continuing account synchronization.", LogLevel.Debug);
                    if (catalog.RootElement.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array && models.GetArrayLength() > 0)
                        model = models[0].GetProperty("alias").GetString() ?? model;
                    if (catalog.RootElement.TryGetProperty("wallet", out JsonElement wallet)
                        && wallet.TryGetProperty("available_micros", out JsonElement available)
                        && available.TryGetInt64(out long parsedBalance))
                        balanceMicros = parsedBalance;
                }
            }
            catch (Exception ex) { Monitor.Log($"读取托管模型目录失败，将使用默认模型：{ex.Message}", LogLevel.Debug); }
            return (true, "", token, model, balanceMicros);
        }
        catch (JsonException) { return (false, "服务端返回无效响应。", string.Empty, string.Empty, -1); }
    }

    private async Task<(bool Success, string Message, string ChallengeId)> RequestHostedVerificationCodeAsync(
        string email,
        bool passwordReset)
    {
        string route = passwordReset
            ? "/api/v1/mod/auth/password-reset/request"
            : "/api/v1/mod/auth/register-code";
        using var request = new HttpRequestMessage(HttpMethod.Post, HostedServiceOrigin + route)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { email }),
                Encoding.UTF8,
                "application/json"),
        };
        using HttpResponseMessage response = await aiHttpClient.SendAsync(request).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!response.IsSuccessStatusCode)
            {
                string message = document.RootElement.TryGetProperty("error", out JsonElement error)
                                 && error.TryGetProperty("message", out JsonElement text)
                    ? text.GetString() ?? L.T("text.61a107ff57fc")
                    : L.T("text.61a107ff57fc");
                return (false, message, string.Empty);
            }
            string challenge = document.RootElement.TryGetProperty("challenge_id", out JsonElement id)
                ? id.GetString() ?? string.Empty
                : string.Empty;
            if (challenge.Length == 0)
                return (false, "服务端未返回有效验证码会话。", string.Empty);
            return (true, string.Empty, challenge);
        }
        catch (JsonException)
        {
            return (false, "服务端返回无效响应。", string.Empty);
        }
    }

    private async Task<(bool Success, string Message)> ResetHostedPasswordAsync(
        string email,
        string challengeId,
        string verificationCode,
        string newPassword)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            HostedServiceOrigin + "/api/v1/mod/auth/password-reset/confirm")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    email,
                    challenge_id = challengeId,
                    verification_code = verificationCode,
                    password = newPassword,
                }),
                Encoding.UTF8,
                "application/json"),
        };
        using HttpResponseMessage response = await aiHttpClient.SendAsync(request).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!response.IsSuccessStatusCode)
            {
                string message = document.RootElement.TryGetProperty("error", out JsonElement error)
                                 && error.TryGetProperty("message", out JsonElement text)
                    ? text.GetString() ?? L.T("text.d44ed69ca077")
                    : L.T("text.d44ed69ca077");
                return (false, message);
            }
            string successMessage = document.RootElement.TryGetProperty("message", out JsonElement success)
                ? success.GetString() ?? L.T("text.64994954afed")
                : L.T("text.64994954afed");
            ClearHostedSessionAfterPasswordReset();
            return (true, successMessage);
        }
        catch (JsonException)
        {
            return (false, "服务端返回无效响应。");
        }
    }

    private void ClearHostedSessionAfterPasswordReset()
    {
        StopAllNpcSpeech();
        speechClient.Clear();
        config.Ai.Hosted.ApiKey = string.Empty;
        try
        {
            Helper.WriteConfig(config);
        }
        catch (Exception ex)
        {
            Monitor.Log($"密码重置成功，但清除本地托管会话失败：{ex.Message}", LogLevel.Warn);
        }

        if (AiProviderNames.Normalize(config.Ai.ActiveProvider) != AiProviderNames.Hosted)
            return;

        DisableCurrentAiKey();
        foreach (ConversationScreenState state in screenStates.GetActiveValues().Select(pair => pair.Value))
            state.RequestApiKeyPrompt = true;
    }

    private async Task<long> FetchHostedBalanceAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, HostedServiceOrigin + "/api/v1/mod/bootstrap");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await aiHttpClient.SendAsync(request).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("托管登录状态已失效，请重新登录。");
        if (!speechClient.AcceptBootstrap(document.RootElement, token))
            Monitor.Log("Speech catalog missing or invalid; continuing balance synchronization.", LogLevel.Debug);
        if (!document.RootElement.TryGetProperty("wallet", out JsonElement wallet)
            || !wallet.TryGetProperty("available_micros", out JsonElement available)
            || !available.TryGetInt64(out long balance))
            throw new InvalidOperationException("服务端未返回有效余额。");
        return balance;
    }

    private void CompleteHostedLogin(string token, string model, long balanceMicros)
    {
        StopAllNpcSpeech();
        config.Ai.ActiveProvider = AiProviderNames.Hosted;
        config.Ai.Hosted.BaseUrl = "https://www.vivantvalley.com.cn/v1";
        config.Ai.Hosted.Model = string.IsNullOrWhiteSpace(model) ? "vv-dialogue" : model;
        config.Ai.Hosted.ApiKey = token;
        config.ApiUrl = config.Ai.Hosted.BaseUrl;
        config.Model = config.Ai.Hosted.Model;
        config.ApiKey = string.Empty;
        config.PromptForApiKeyEveryLaunch = false;
        try { Helper.WriteConfig(config); } catch (Exception ex) { Monitor.Log($"保存托管账户失败：{ex.Message}", LogLevel.Warn); }
        if (AiEndpointResolver.TryResolve(AiProviderNames.Hosted, config.Ai.Hosted.BaseUrl, out string normalized, out Uri endpoint, out _))
        {
            Volatile.Write(ref currentAiProfile, new AiRuntimeProfile(AiProviderNames.Hosted, normalized, endpoint, config.Ai.Hosted.Model, token, "托管账户会话", TimeSpan.FromSeconds(config.RequestTimeoutSeconds), config.EnableThinking, config.ReasoningEffort));
            runtimeApiKey = token;
            apiKeySource = "托管账户会话";
        }
        foreach (ConversationScreenState state in screenStates.GetActiveValues().Select(pair => pair.Value)) state.RequestApiKeyPrompt = false;
        ShowHud(L.T("text.1ae9733e93e1"), HUDMessage.newQuest_type);
    }

    private async Task<string> RedeemHostedAsync(string token, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, HostedServiceOrigin + "/api/v1/mod/redeem")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await aiHttpClient.SendAsync(request).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode)
        {
            string message = document.RootElement.TryGetProperty("error", out JsonElement error) && error.TryGetProperty("message", out JsonElement text) ? text.GetString() ?? L.T("text.f217d5539f6d") : L.T("text.f217d5539f6d");
            throw new InvalidOperationException(message);
        }
        long balance = document.RootElement.GetProperty("available_micros").GetInt64();
        return HostedPurchaseCatalog.Redeemed(balance);
    }

    private void SaveConversationUiScale(float value)
    {
        config.ConversationUiScale = ConversationUiLayout.ClampScale(value);
        try
        {
            Helper.WriteConfig(config);
        }
        catch (Exception exception)
        {
            Monitor.Log($"保存对话框大小设置失败：{exception.Message}", LogLevel.Warn);
        }
    }

    private void SaveProactiveUiScale(float value)
    {
        config.ProactiveUiScale = ConversationUiLayout.ClampScale(value);
        try
        {
            Helper.WriteConfig(config);
        }
        catch (Exception exception)
        {
            Monitor.Log($"保存主动对话框大小设置失败：{exception.Message}", LogLevel.Warn);
        }
    }

    private void OnNpcDefeated(NpcCombatDefeatEvent defeat)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
            return;

        NpcConversationMemory memory = memoryStore.GetOrCreate(GetPlayerId(), defeat.NpcName);
        memory.Messages ??= new List<ConversationMemoryMessage>();
        string dedupeKey = $"npc-combat-defeat:{defeat.NpcName}:{Game1.Date.TotalDays}:{defeat.HospitalReleaseDay}";
        if (memory.Messages.Any(message => string.Equals(message.DedupeKey, dedupeKey, StringComparison.Ordinal)))
            return;

        string content = $"I was defeated while guarding the player in the mines and was sent to the hospital. "
                         + $"Friendship fell by {defeat.FriendshipLoss} points; recovery day is {defeat.HospitalReleaseDay}.";
        memory.Messages.Add(new ConversationMemoryMessage
        {
            Role = "system",
            Content = content,
            GameDate = defeat.GameDate,
            Source = ConversationMemorySources.ModAction,
            EventId = "npc-combat-defeat",
            DedupeKey = dedupeKey,
            LocationName = defeat.LocationName,
        });
        memory.Summary = ConversationMemoryPolicy.UpdateLongTermMemory(
            memory.Summary,
            content,
            defeat.GameDate,
            GetPlayerId(),
            defeat.NpcName,
            memory.TotalTurns);
        memory.Messages = ConversationTimelinePolicy.RetainRecentMessages(memory);
        memoryDirty = true;
        PersistMemory(force: false);
        PersistNpcCombatState();
    }

    private AiSettingsSaveResult SaveAiProviderSettings(AiProviderSettingsDraft draft)
    {
        if (!TryBuildDraftRuntimeProfile(draft, out AiRuntimeProfile? candidate, out string failure))
            return new AiSettingsSaveResult(false, failure);

        string provider = candidate!.Provider;
        AiConnectionProfile target = config.Ai.GetProfile(provider);
        string oldProvider = config.Ai.ActiveProvider;
        string oldBaseUrl = target.BaseUrl;
        string oldModel = target.Model;
        string oldKey = target.ApiKey;
        string oldLegacyBaseUrl = config.ApiUrl;
        string oldLegacyModel = config.Model;
        try
        {
            config.Ai.ActiveProvider = provider;
            target.BaseUrl = candidate.BaseUrl;
            target.Model = candidate.Model;
            if (draft.ClearSavedKey)
                target.ApiKey = string.Empty;
            else if (!string.IsNullOrWhiteSpace(draft.ReplacementApiKey))
                target.ApiKey = draft.ReplacementApiKey.Trim();
            config.ApiUrl = candidate.BaseUrl;
            config.Model = candidate.Model;
            config.ApiKey = string.Empty;
            config.PromptForApiKeyEveryLaunch = false;
            Helper.WriteConfig(config);
        }
        catch (Exception ex)
        {
            config.Ai.ActiveProvider = oldProvider;
            target.BaseUrl = oldBaseUrl;
            target.Model = oldModel;
            target.ApiKey = oldKey;
            config.ApiUrl = oldLegacyBaseUrl;
            config.Model = oldLegacyModel;
            Monitor.Log($"保存全局 AI 设置失败：{ex}", LogLevel.Error);
            return new AiSettingsSaveResult(false, L.T("text.cc6fda83a9f6") + ex.Message);
        }

        Volatile.Write(ref currentAiProfile, candidate);
        runtimeApiKey = candidate.ApiKey;
        apiKeySource = candidate.ApiKeySource;
        foreach (ConversationScreenState state in screenStates.GetActiveValues().Select(pair => pair.Value))
            state.RequestApiKeyPrompt = false;
        ShowHud(
            L.F("text.c1ab08861f4a", $"已切换到 {candidate.Provider} / {candidate.Model}，新请求立即生效。"),
            HUDMessage.newQuest_type);
        Monitor.Log(
            $"全局 AI 设置已保存：提供商={candidate.Provider}，Base URL={candidate.BaseUrl}，"
            + $"模型={candidate.Model}，Key 来源={candidate.ApiKeySource}。",
            LogLevel.Info);
        return new AiSettingsSaveResult(true, L.T("text.a9f184b489a8"));
    }

    private async Task<string> TestAiProviderSettingsAsync(
        AiProviderSettingsDraft draft,
        CancellationToken cancellationToken)
    {
        if (!TryBuildDraftRuntimeProfile(draft, out AiRuntimeProfile? candidate, out string failure))
            throw new DeepSeekConfigurationException(failure);

        var testClient = new AiProviderClient(aiHttpClient, () => candidate!);
        var request = new DeepSeekChatRequest
        {
            Model = candidate!.Model,
            Messages = new List<DeepSeekChatMessage>
            {
                new("system", "这是连接测试。只回复 OK。"),
                new("user", "OK"),
            },
            Thinking = new DeepSeekThinkingOptions { Type = "disabled" },
            ReasoningEffort = "low",
            MaxTokens = 64,
            Stream = false,
        };
        await testClient.CompleteChatAsync(candidate.ApiKey, request, cancellationToken).ConfigureAwait(false);
        return L.F("text.2d493822cbd5", $"连接成功：{candidate.Provider} / {candidate.Model}");
    }

    private bool TryBuildDraftRuntimeProfile(
        AiProviderSettingsDraft draft,
        out AiRuntimeProfile? profile,
        out string failure)
    {
        profile = null;
        failure = string.Empty;
        if (!AiProviderNames.IsSupported(draft.Provider))
        {
            failure = L.T("text.14d16c7b7bd9");
            return false;
        }

        string provider = AiProviderNames.Normalize(draft.Provider);
        if (!AiEndpointResolver.TryResolve(
                provider,
                draft.BaseUrl,
                out string normalizedBaseUrl,
                out Uri endpoint,
                out failure))
        {
            return false;
        }

        string model = (draft.Model ?? string.Empty).Trim();
        if (model.Length == 0)
        {
            failure = L.T("text.a121490c894e");
            return false;
        }
        if (model.Length > 160)
        {
            failure = L.T("text.7fa785154ca5");
            return false;
        }

        AiConnectionProfile saved = config.Ai.GetProfile(provider);
        string key = (draft.ReplacementApiKey ?? string.Empty).Trim();
        string source = "config.json";
        if (key.Length == 0 && !draft.ClearSavedKey)
            key = (saved.ApiKey ?? string.Empty).Trim();
        if (key.Length == 0 && provider != AiProviderNames.Hosted)
        {
            string environmentName = provider == AiProviderNames.OpenAI
                ? "OPENAI_API_KEY"
                : "DEEPSEEK_API_KEY";
            key = (Environment.GetEnvironmentVariable(environmentName) ?? string.Empty).Trim();
            source = environmentName + L.T("text.41b8c5aec7a5");
        }
        if (key.Length == 0)
        {
            failure = provider == AiProviderNames.Hosted ? L.T("text.863849ca6d95") : L.T("text.8ba4a1eefd1b");
            return false;
        }

        profile = new AiRuntimeProfile(
            provider,
            normalizedBaseUrl,
            endpoint,
            model,
            key,
            source,
            TimeSpan.FromSeconds(config.RequestTimeoutSeconds),
            config.EnableThinking,
            config.ReasoningEffort);
        return true;
    }

    private void LoadStartupApiKey()
    {
        if (TryBuildRuntimeAiProfile(out AiRuntimeProfile? profile, out string failure))
        {
            Volatile.Write(ref currentAiProfile, profile!);
            runtimeApiKey = profile!.ApiKey;
            apiKeySource = profile.ApiKeySource;
            config.Model = profile.Model;
            return;
        }

        string provider = AiProviderNames.Normalize(config.Ai.ActiveProvider);
        string fallbackBaseUrl = AiEndpointResolver.GetDefaultBaseUrl(provider);
        AiEndpointResolver.TryResolve(provider, fallbackBaseUrl, out _, out Uri fallbackEndpoint, out _);
        AiConnectionProfile configured = config.Ai.GetProfile(provider);
        Volatile.Write(
            ref currentAiProfile,
            new AiRuntimeProfile(
                provider,
                fallbackBaseUrl,
                fallbackEndpoint,
                string.IsNullOrWhiteSpace(configured.Model) ? "未设置" : configured.Model.Trim(),
                string.Empty,
                failure,
                TimeSpan.FromSeconds(config.RequestTimeoutSeconds),
                config.EnableThinking,
                config.ReasoningEffort));

        runtimeApiKey = string.Empty;
        apiKeySource = failure;
    }

    private bool TryBuildRuntimeAiProfile(out AiRuntimeProfile? profile, out string failure)
    {
        profile = null;
        failure = string.Empty;
        string provider = AiProviderNames.Normalize(config.Ai.ActiveProvider);
        AiConnectionProfile configured = config.Ai.GetProfile(provider);
        if (!AiEndpointResolver.TryResolve(
                provider,
                configured.BaseUrl,
                out string normalizedBaseUrl,
                out Uri endpoint,
                out failure))
        {
            return false;
        }

        string model = (configured.Model ?? string.Empty).Trim();
        if (model.Length == 0)
        {
            failure = L.F("text.ced82ae4daa8", $"{provider} 模型尚未设置");
            return false;
        }

        string key = (configured.ApiKey ?? string.Empty).Trim();
        string source = "config.json";
        if (key.Length == 0 && provider != AiProviderNames.Hosted)
        {
            string environmentName = provider == AiProviderNames.OpenAI
                ? "OPENAI_API_KEY"
                : "DEEPSEEK_API_KEY";
            key = (Environment.GetEnvironmentVariable(environmentName) ?? string.Empty).Trim();
            source = environmentName + L.T("text.41b8c5aec7a5");
        }
        if (key.Length == 0)
        {
            failure = provider == AiProviderNames.Hosted ? L.T("text.37f2217eb043") : L.F("text.a06aa0e7f177", $"{provider} API Key 尚未设置");
            return false;
        }

        profile = new AiRuntimeProfile(
            provider,
            normalizedBaseUrl,
            endpoint,
            model,
            key,
            source,
            TimeSpan.FromSeconds(config.RequestTimeoutSeconds),
            config.EnableThinking,
            config.ReasoningEffort);
        return true;
    }

    private void DisableCurrentAiKey()
    {
        AiRuntimeProfile profile = Volatile.Read(ref currentAiProfile);
        Volatile.Write(ref currentAiProfile, profile with { ApiKey = string.Empty, ApiKeySource = "鉴权失败" });
        runtimeApiKey = string.Empty;
        apiKeySource = "鉴权失败";
    }

    private void OnAiDebugCommand(string command, string[] args)
    {
        string option = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "status";
        switch (option)
        {
            case "on":
            case "true":
                config.WriteAiRequestDebugFiles = true;
                TryWriteConfigForAiDebug();
                Monitor.Log($"AI request debug capture enabled. Files: {aiRequestDebugWriter.DirectoryPath}", LogLevel.Info);
                break;
            case "off":
            case "false":
                config.WriteAiRequestDebugFiles = false;
                TryWriteConfigForAiDebug();
                Monitor.Log("AI request debug capture disabled.", LogLevel.Info);
                break;
            case "status":
                Monitor.Log(
                    $"AI request debug capture is {(config.WriteAiRequestDebugFiles ? "enabled" : "disabled")}. Files: {aiRequestDebugWriter.DirectoryPath}",
                    LogLevel.Info);
                break;
            default:
                Monitor.Log("Usage: vivant_ai_debug on|off|status", LogLevel.Info);
                break;
        }
    }

    private void TryWriteConfigForAiDebug()
    {
        try
        {
            Helper.WriteConfig(config);
        }
        catch (Exception exception)
        {
            Monitor.Log($"Could not save AI debug setting: {exception.Message}", LogLevel.Warn);
        }
    }

    private void OnPromptKeyCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Monitor.Log("请先载入一个存档，再运行 AI 设置命令。", LogLevel.Alert);
            return;
        }

        screenStates.Value.RequestApiKeyPrompt = true;
        Monitor.Log("AI 提供商设置会在游戏空闲时打开。", LogLevel.Info);
    }

    private void OnStatusCommand(string command, string[] args)
    {
        ConversationScreenState state = screenStates.Value;
        int npcCount = memoryStore.Players?.Values.Sum(memories => memories?.Count ?? 0) ?? 0;
        int vanillaEpisodeCount = memoryStore.NarrativeEpisodes?.Values.Sum(episodes => episodes?.Count ?? 0) ?? 0;
        int plannedCount = GetCurrentSocialPlan()?.Candidates.Count(candidate =>
            candidate.Status == DailySocialCandidateStatus.Planned) ?? 0;
        Monitor.Log(
            $"API Key：{(runtimeApiKey.Length == 0 ? "未设置" : $"已设置（来源：{apiKeySource}）")}；"
            + $"提供商：{currentAiProfile.Provider}；Base URL：{currentAiProfile.BaseUrl}；模型：{currentAiProfile.Model}；"
            + $"快捷键：{config.ChatKey}；记忆 NPC 数：{npcCount}；原版剧情档案：{vanillaEpisodeCount}；"
            + $"普通请求：{(state.HasPendingConversation ? "进行中" : "空闲")}；"
            + $"今日待相遇 NPC：{plannedCount}；主动场景：{(state.PendingSocialScene is null ? "空闲" : "生成中")}。",
            LogLevel.Info);
    }

    private void OnForgetCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Monitor.Log("请先载入存档。", LogLevel.Alert);
            return;
        }

        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            Monitor.Log("用法：vivant_forget <NPC内部名|all>", LogLevel.Info);
            return;
        }

        string playerId = GetPlayerId();
        Dictionary<string, NpcConversationMemory> memories = GetPlayerMemories(playerId);
        string target = args[0].Trim();
        if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            CancelMatchingConversations(playerId, npcName: null);

            if (memoryArchive is not null && !memoryArchive.TryForget(playerId,null))
            { Monitor.Log("无法删除记忆档案，未清除当前记忆。",LogLevel.Error); return; }
            int count = memories.Count;
            memories.Clear();
            memoryStore.TownEchoes.Forget(playerId, null);
            int episodeCount = memoryStore.ForgetNarrativeEpisodes(playerId, npcName: null);
            memoryDirty = true;
            ForgetSocialSignals(playerId, npcName: null);
            PersistMemory(force: false);
            PersistSocial(force: false);
            Monitor.Log($"已清除当前玩家的 {count} 份 NPC 记忆和 {episodeCount} 个原版剧情档案。", LogLevel.Info);
            return;
        }

        string? memoryKey = memories.Keys.FirstOrDefault(name => name.Equals(target, StringComparison.OrdinalIgnoreCase));
        string? socialKey = socialStore.TryGetPlayer(playerId, out PlayerSocialDirectorState? socialPlayer)
            ? socialPlayer!.NpcStates.Keys.FirstOrDefault(name => name.Equals(target, StringComparison.OrdinalIgnoreCase))
            : null;
        string? narrativeKey = memoryStore.GetNarrativeEpisodes(playerId)
            .SelectMany(episode => episode.ParticipantNames.Concat(episode.Beats.Select(beat => beat.NpcName)))
            .FirstOrDefault(name => name.Equals(target, StringComparison.OrdinalIgnoreCase));
        string? key = memoryKey ?? socialKey ?? narrativeKey
            ?? (memoryStore.TownEchoes.ContainsNpc(playerId, target) ? target : null);
        if (key is null)
        {
            Monitor.Log($"没有找到 NPC“{target}”的记忆。请使用内部英文名，例如 Abigail。", LogLevel.Info);
            return;
        }

        CancelMatchingConversations(playerId, key);

        if (memoryArchive is not null && !memoryArchive.TryForget(playerId,key))
        { Monitor.Log("无法删除记忆档案，未清除当前记忆。",LogLevel.Error); return; }
        if (memoryKey is not null)
            memories.Remove(memoryKey);
        memoryStore.ForgetNarrativeEpisodes(playerId, key);
        memoryStore.TownEchoes.Forget(playerId, key);
        memoryDirty = true;
        ForgetSocialSignals(playerId, key);
        PersistMemory(force: false);
        PersistSocial(force: false);
        Monitor.Log($"已清除 {key} 的记忆。", LogLevel.Info);
    }

    private void OnStoryStatusCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Monitor.Log("请先载入存档。", LogLevel.Alert);
            return;
        }

        string playerId = GetPlayerId();
        string npcName = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0].Trim()
            : GetDefaultStoryNpcName();
        if (!narrativeStore.TryGet(playerId, npcName, out NpcNarrativeState? narrative)
            || narrative is null)
        {
            Monitor.Log($"{npcName} 尚无主动剧情状态。", LogLevel.Info);
            return;
        }

        PlannedNpcEncounter? action = narrative.PendingEncounter;
        string actionText = action is null
            ? "无待触发行动"
            : $"{action.StoryId} / {action.Status}，第 {action.EarliestDay}-{action.ExpiryDay} 天，尝试 {action.Attempts} 次";
        string flagsText = narrative.Flags.Count == 0
            ? "无"
            : string.Join(", ", narrative.Flags.OrderBy(flag => flag, StringComparer.Ordinal));
        Monitor.Log(
            $"{narrative.NpcName}：信任 {narrative.Trust}/100，亲密 {narrative.Affection}/100，"
            + $"完成节点 {narrative.CompletedStoryIds.Count} 个，上次来访第 {narrative.LastEncounterDay} 天，"
            + $"待执行：{actionText}。Flags：{flagsText}。",
            LogLevel.Info);
    }

    private void OnStoryTriggerCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
        {
            Monitor.Log("主动剧情试点目前只能由主玩家在已载入的存档中触发。", LogLevel.Alert);
            return;
        }

        string npcName = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0].Trim()
            : GetDefaultStoryNpcName();
        string? requestedStoryId = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
            ? args[1].Trim()
            : null;
        StoryDefinition? story;
        if (requestedStoryId is not null)
        {
            storyCatalog.TryGet(requestedStoryId, out story);
            if (story is not null
                && (!story.Enabled || !story.Npc.Equals(npcName, StringComparison.OrdinalIgnoreCase)))
                story = null;
        }
        else
            story = storyCatalog.GetFirstForNpc(npcName);

        if (story is null)
        {
            Monitor.Log($"没有找到 NPC {npcName} 的剧情{(requestedStoryId is null ? string.Empty : $" {requestedStoryId}")}。", LogLevel.Info);
            return;
        }

        string playerId = GetPlayerId();
        NpcNarrativeState narrative = narrativeStore.GetOrCreate(playerId, npcName);
        if (narrative.PendingEncounter is not null
            && narrative.PendingEncounter.Status is not PlannedEncounterStatus.Completed
                and not PlannedEncounterStatus.Expired
                and not PlannedEncounterStatus.Cancelled)
        {
            Monitor.Log("已有待触发的主动剧情行动；可先等待、完成或使用 aistory_reset。", LogLevel.Info);
            return;
        }

        long turns = memoryStore.TryGet(playerId, npcName, out NpcConversationMemory? memory) && memory is not null
            ? memory.TotalTurns
            : 0;
        narrative.PendingEncounter = pilotNarrativePlanner.CreateEncounter(
            story,
            turns,
            Game1.Date.TotalDays,
            narrative.RecentUserExcerpt,
            immediate: true);
        narrativeDirty = true;
        PersistNarrative(force: false);
        ShowHud(L.F("text.c9fbbe85f65a", $"{npcName} 的剧情 {story.Id} 已安排；靠近 NPC 即可触发。"), HUDMessage.newQuest_type);
        Monitor.Log($"已立即安排 {npcName} 的剧情 {story.Id}。", LogLevel.Info);
    }

    private void OnStoryResetCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
        {
            Monitor.Log("主动剧情试点目前只能由主玩家在已载入的存档中重置。", LogLevel.Alert);
            return;
        }

        string npcName = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0].Trim()
            : GetDefaultStoryNpcName();

        foreach (ConversationScreenState state in screenStates.GetActiveValues().Select(pair => pair.Value))
            CancelPendingProactiveScene(state, defer: false);

        string playerId = GetPlayerId();
        bool removed = narrativeStore.Players is not null
                       && narrativeStore.Players.TryGetValue(playerId, out Dictionary<string, NpcNarrativeState>? states)
                       && states is not null
                       && states.Remove(npcName);
        if (!removed)
        {
            Monitor.Log($"{npcName} 没有可重置的主动剧情状态。", LogLevel.Info);
            return;
        }

        narrativeDirty = true;
        PersistNarrative(force: false);
        Monitor.Log($"已重置 {npcName} 的主动剧情状态；普通聊天记忆未受影响。", LogLevel.Info);
    }

    private Dictionary<string, NpcConversationMemory> GetPlayerMemories(string playerId)
    {
        memoryStore.Players ??= new Dictionary<string, Dictionary<string, NpcConversationMemory>>(StringComparer.Ordinal);
        if (!memoryStore.Players.TryGetValue(playerId, out Dictionary<string, NpcConversationMemory>? memories)
            || memories is null)
        {
            memories = new Dictionary<string, NpcConversationMemory>(StringComparer.OrdinalIgnoreCase);
            memoryStore.Players[playerId] = memories;
        }

        return memories;
    }

    private bool NormalizeConversationTimelineMetadata()
    {
        bool changed = false;
        foreach ((string playerId, Dictionary<string, NpcConversationMemory>? memories) in memoryStore.Players)
        {
            if (memories is null)
                continue;

            foreach ((string npcName, NpcConversationMemory? memory) in memories)
            {
                if (memory is null)
                    continue;

                long previousSequence = memory.NextTimelineSequence;
                long previousStart = memory.TimelineStartSequence;
                // Initialize optional metadata without deleting or rewriting old save contents.
                ConversationTimelinePolicy.BuildTimeline(memory);
                changed |= previousSequence != memory.NextTimelineSequence || previousStart != memory.TimelineStartSequence;
            }
        }
        return changed;
    }

    private void ConfigureMemoryArchive()
    {
        memoryArchive = new MemoryEventArchive(Path.Combine(Helper.DirectoryPath,"data","memory-archive"),
            $"{Constants.SaveFolderName}|{Game1.uniqueIDForThisGame}", message => Monitor.Log(message,LogLevel.Warn));
        ConversationTimelinePolicy.ArchiveBeforePrune = _ => false;
        ConversationMemoryPolicy.ArchiveBeforeSummaryCompaction = (_, _, _) => false;
        ResetSegmentWork();
        foreach (var memory in memoryStore.Players.Values.SelectMany(entries => entries.Values))
        {
            SegmentMemoryPolicy.Enable(memory);
            memoryArchive.PrimeReadOnly(memory);
        }
        if (memoryArchive.UnavailableCheckpointCount > 0)
            Monitor.Log(L.IsChinese
                ? $"有 {memoryArchive.UnavailableCheckpointCount} 位 NPC 的外部记忆档案缺失或不完整。现有存档记忆保留；下次分段归档将建立新分支并保留旧档案引用。缺失原文需从备份恢复。"
                : $"{memoryArchive.UnavailableCheckpointCount} NPC archive checkpoints are incomplete. Saved memories are retained; the next segment will start a new archive branch while preserving the old reference. Missing originals require a backup.",
                LogLevel.Warn);
    }

    private static IReadOnlyList<string> BuildMemoryCommitmentStates(NpcSocialState state,int day)
    {
        return ConversationCommitmentPolicy.Active(state, day)
            .Select(text => (L.IsChinese ? "待履行：" : "Pending: ") + text).ToArray();
    }

    private string GetMemorySaveScope(NpcConversationMemory memory)
        => MemoryEventArchive.Scope($"{Constants.SaveFolderName}|{Game1.uniqueIDForThisGame}|{memory.ArchiveBranchId}");

    private void PersistMemory(bool force)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || !memoryStorageReady || (!force && !memoryDirty))
            return;

        try
        {
            Helper.Data.WriteSaveData(SaveDataKey, memoryStore);
            memoryDirty = false;
        }
        catch (Exception ex)
        {
            Monitor.Log($"保存 NPC 对话记忆失败：{ex}", LogLevel.Error);
        }
    }

    private void PersistNpcCombatState()
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
            return;

        try
        {
            Helper.Data.WriteSaveData(CombatSaveDataKey, npcCombatStateService.Store);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Failed to save NPC combat state: {ex}", LogLevel.Error);
        }
    }

    private void PersistNarrative(bool force)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || (!force && !narrativeDirty))
            return;

        try
        {
            Helper.Data.WriteSaveData(NarrativeSaveDataKey, narrativeStore);
            narrativeDirty = false;
        }
        catch (Exception ex)
        {
            Monitor.Log($"保存 NPC 主动剧情状态失败：{ex}", LogLevel.Error);
        }
    }

    private bool RepairNarrativeStoreAfterLoad()
    {
        bool changed = false;
        if (narrativeStore.SchemaVersion < 3)
        {
            narrativeStore.SchemaVersion = 3;
            changed = true;
        }

        narrativeStore.Players ??= new Dictionary<string, Dictionary<string, NpcNarrativeState>>(StringComparer.Ordinal);
        foreach (Dictionary<string, NpcNarrativeState>? npcStates in narrativeStore.Players.Values)
        {
            if (npcStates is null)
                continue;

            foreach (NpcNarrativeState? narrative in npcStates.Values)
            {
                if (narrative is null)
                    continue;

                narrative.CompletedActionIds = new HashSet<string>(
                    narrative.CompletedActionIds ?? new HashSet<string>(),
                    StringComparer.Ordinal);
                narrative.CompletedStoryIds = new HashSet<string>(
                    narrative.CompletedStoryIds ?? new HashSet<string>(),
                    StringComparer.OrdinalIgnoreCase);
                narrative.Flags = new HashSet<string>(
                    narrative.Flags ?? new HashSet<string>(),
                    StringComparer.Ordinal);
                if (narrative.CompletedStoryIds.Contains("abigail.quartz-care.01")
                    && !narrative.Flags.Contains("abigail.arc.route-adventure")
                    && !narrative.Flags.Contains("abigail.arc.route-playful"))
                {
                    narrative.Flags.Add("abigail.arc.crystal-resolved");
                    narrative.Flags.Add("abigail.arc.route-adventure");
                    changed = true;
                }
                narrative.RecentUserExcerpt ??= string.Empty;
                narrative.RecentAssistantExcerpt ??= string.Empty;
                PlannedNpcEncounter? encounter = narrative.PendingEncounter;
                if (encounter is null)
                    continue;

                encounter.FlagsOnAccept = new HashSet<string>(
                    encounter.FlagsOnAccept ?? new HashSet<string>(),
                    StringComparer.Ordinal);
                encounter.Choices ??= new List<PlannedStoryChoice>();
                encounter.ActivationDistanceTiles = Math.Clamp(encounter.ActivationDistanceTiles, 1f, 16f);
                if (HydrateEncounterStorySnapshot(encounter))
                    changed = true;
                if (encounter.Status == PlannedEncounterStatus.Completed
                    && !string.IsNullOrWhiteSpace(encounter.StoryId)
                    && narrative.CompletedStoryIds.Add(encounter.StoryId))
                {
                    changed = true;
                    if (encounter.StoryId.Equals("abigail.quartz-care.01", StringComparison.OrdinalIgnoreCase)
                        && !narrative.Flags.Contains("abigail.arc.route-adventure")
                        && !narrative.Flags.Contains("abigail.arc.route-playful"))
                    {
                        narrative.Flags.Add("abigail.arc.crystal-resolved");
                        narrative.Flags.Add("abigail.arc.route-adventure");
                    }
                }
                encounter.Choices = encounter.Choices.Where(choice => choice is not null).ToList();
                foreach (PlannedStoryChoice choice in encounter.Choices)
                {
                    choice.Id ??= string.Empty;
                    choice.Text ??= string.Empty;
                    choice.MemoryText = string.IsNullOrWhiteSpace(choice.MemoryText) ? choice.Text : choice.MemoryText;
                    choice.NextStoryId ??= string.Empty;
                    choice.SetFlags = new HashSet<string>(choice.SetFlags ?? new HashSet<string>(), StringComparer.Ordinal);
                }

                if (encounter.Status is PlannedEncounterStatus.Generating
                    or PlannedEncounterStatus.Ready
                    or PlannedEncounterStatus.Presenting)
                {
                    PilotNarrativePlanner.Defer(encounter, Game1.Date.TotalDays);
                    changed = true;
                }
                else if (PilotNarrativePlanner.IsExpired(encounter, Game1.Date.TotalDays)
                         && encounter.Status != PlannedEncounterStatus.Expired)
                {
                    encounter.Status = PlannedEncounterStatus.Expired;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private bool HydrateEncounterStorySnapshot(PlannedNpcEncounter encounter)
    {
        bool changed = false;
        StoryDefinition? story = null;
        if (!string.IsNullOrWhiteSpace(encounter.StoryId))
            storyCatalog.TryGet(encounter.StoryId, out story);
        else
            story = storyCatalog.GetFirstForNpc(encounter.NpcName);

        if (story is null)
        {
            if (string.IsNullOrWhiteSpace(encounter.StoryId))
            {
                encounter.StoryId = $"legacy.{encounter.NpcName.ToLowerInvariant()}.proactive-gift";
                changed = true;
            }
            if (encounter.Choices.Count == 0)
            {
                encounter.Choices = CreateLegacyEncounterChoices(encounter);
                changed = true;
            }
            return changed;
        }

        bool legacySnapshot = string.IsNullOrWhiteSpace(encounter.StoryId);
        if (legacySnapshot)
        {
            encounter.StoryId = story.Id;
            encounter.StoryVersion = story.Version;
            encounter.Repeatable = story.Repeatable;
            encounter.ActivationDistanceTiles = story.Scene.ActivationDistanceTiles;
            encounter.TrustOnAccept = story.AcceptEffects.Trust;
            encounter.AffectionOnAccept = story.AcceptEffects.Affection;
            encounter.FlagsOnAccept = new HashSet<string>(story.AcceptEffects.SetFlags, StringComparer.Ordinal);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(encounter.AiBrief))
        {
            encounter.AiBrief = story.Scene.AiBrief;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(encounter.FallbackText))
        {
            encounter.FallbackText = story.Scene.FallbackText;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(encounter.AcceptText))
        {
            encounter.AcceptText = story.Scene.AcceptText;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(encounter.DeferText))
        {
            encounter.DeferText = story.Scene.DeferText;
            changed = true;
        }
        if (encounter.Choices.Count == 0)
        {
            encounter.Choices = PilotNarrativePlanner.CreatePlannedChoices(story);
            changed = true;
        }

        return changed;
    }

    private static List<PlannedStoryChoice> CreateLegacyEncounterChoices(PlannedNpcEncounter encounter)
    {
        return new List<PlannedStoryChoice>
        {
            new()
            {
                Id = "accept",
                Text = encounter.AcceptText,
                MemoryText = encounter.AcceptText,
                ReceiveGift = !string.IsNullOrWhiteSpace(encounter.GiftItemId),
                Trust = encounter.TrustOnAccept,
                Affection = encounter.AffectionOnAccept,
                SetFlags = new HashSet<string>(encounter.FlagsOnAccept, StringComparer.Ordinal),
            },
            new()
            {
                Id = "defer",
                Text = encounter.DeferText,
                MemoryText = encounter.DeferText,
                Defer = true,
            },
        };
    }

    private void CancelMatchingConversations(string playerId, string? npcName)
    {
        foreach (ConversationScreenState state in screenStates.GetActiveValues().Select(pair => pair.Value))
        {
            bool matchesPending = state.PendingInfo?.PlayerId == playerId
                                  && (npcName is null
                                      || state.PendingInfo.NpcName.Equals(
                                          npcName,
                                          StringComparison.OrdinalIgnoreCase));
            if (matchesPending)
                CancelPendingConversation(state);

            bool matchesQueued = state.QueuedDialogue?.PlayerId == playerId
                                 && (npcName is null
                                     || state.QueuedDialogue.NpcName.Equals(
                                         npcName,
                                         StringComparison.OrdinalIgnoreCase));
            if (matchesQueued)
                state.QueuedDialogue = null;

            bool matchesContinuation = state.QueuedConversationContinuation?.PlayerId == playerId
                                       && (npcName is null
                                           || state.QueuedConversationContinuation.NpcName.Equals(
                                               npcName,
                                               StringComparison.OrdinalIgnoreCase));
            if (matchesContinuation)
                ClearConversationContinuation(state);
        }
    }

    private static void CancelPendingConversation(
        ConversationScreenState state,
        bool dismissMenu = true)
    {
        CancellationTokenSource cancellation = state.SessionCancellation;
        var activeTasks = new List<Task>(3);
        if (state.PendingGiftPlan is not null)
            activeTasks.Add(state.PendingGiftPlan);
        if (state.PendingGraphDecision is not null)
            activeTasks.Add(state.PendingGraphDecision);
        if (state.PendingConversation is not null)
            activeTasks.Add(state.PendingConversation);

        state.PendingGiftPlan = null;
        state.PendingGraphDecision = null;
        state.PendingConversation = null;
        state.PendingInfo = null;
        state.PendingMoveConfirmation = null;
        state.MoveConfirmationApproved = false;
        state.GiftExecution = null;
        state.MoveExecution = null;
        state.MineGuardExecution = null;
        state.SessionCancellation = new CancellationTokenSource();
        if (!cancellation.IsCancellationRequested)
            cancellation.Cancel();

        if (activeTasks.Count == 0)
        {
            cancellation.Dispose();
        }
        else
        {
            _ = Task.WhenAll(activeTasks).ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        if (dismissMenu && state.StreamingMenu is not null)
        {
            AiStreamingDialogueMenu menu = state.StreamingMenu;
            state.StreamingMenu = null;
            menu.Dismiss();
        }
    }

    private void CancelPendingProactiveScene(ConversationScreenState state, bool defer)
    {
        PendingProactiveInfo? pendingInfo = state.PendingProactiveInfo;
        QueuedProactiveScene? queuedScene = state.QueuedProactiveScene;
        QueuedProactiveScene? activeScene = state.ActiveProactiveScene;
        if (defer && Context.IsWorldReady)
        {
            string? playerId = pendingInfo?.PlayerId ?? queuedScene?.PlayerId ?? activeScene?.PlayerId;
            string? npcName = pendingInfo?.NpcName ?? queuedScene?.NpcName ?? activeScene?.NpcName;
            string? actionId = pendingInfo?.ActionId ?? queuedScene?.ActionId ?? activeScene?.ActionId;
            if (!string.IsNullOrWhiteSpace(playerId)
                && !string.IsNullOrWhiteSpace(npcName)
                && !string.IsNullOrWhiteSpace(actionId))
            {
                DeferProactiveEncounter(playerId, npcName, actionId);
            }
        }

        if (!state.ProactiveCancellation.IsCancellationRequested)
            state.ProactiveCancellation.Cancel();
        state.ProactiveCancellation.Dispose();
        state.ProactiveCancellation = new CancellationTokenSource();
        state.PendingProactiveScene = null;
        state.PendingProactiveInfo = null;
        state.QueuedProactiveScene = null;
        state.ActiveProactiveScene = null;

        if (state.ProactiveMenu is not null)
        {
            AiProactiveEncounterMenu menu = state.ProactiveMenu;
            state.ProactiveMenu = null;
            menu.Dismiss();
        }
    }

    private void NormalizeConfig()
    {
        config.EnableThinking = false;
        config.ReasoningEffort = "low";
        config.MaxTalkDistanceTiles = Math.Clamp(config.MaxTalkDistanceTiles, 1f, 12f);
        config.RequestTimeoutSeconds = Math.Clamp(config.RequestTimeoutSeconds, 10, 600);
        config.MaxContextMessages = Math.Clamp(config.MaxContextMessages, 4, 24);
        config.SummaryTriggerMessages = Math.Clamp(config.SummaryTriggerMessages, 4, 24);
        config.SummaryKeepRecentMessages = Math.Clamp(config.SummaryKeepRecentMessages, 0, config.SummaryTriggerMessages - 1);
        config.MaxSeenEventIdsInContext = Math.Clamp(config.MaxSeenEventIdsInContext, 0, 12);
        config.MaxQuestsInContext = Math.Clamp(config.MaxQuestsInContext, 0, 4);
        config.MaxCompleteNarrativeEpisodesInContext = Math.Clamp(config.MaxCompleteNarrativeEpisodesInContext, 1, 2);
        config.MaxNarrativeEpisodeAnchorsInContext = Math.Clamp(config.MaxNarrativeEpisodeAnchorsInContext, 0, 8);
        config.MaxNarrativeContextCharacters = Math.Clamp(config.MaxNarrativeContextCharacters, 2000, 6000);
        config.MaxReplyCharacters = Math.Clamp(config.MaxReplyCharacters, 100, 6000);
        config.NpcSpeechVolume = float.IsFinite(config.NpcSpeechVolume) ? Math.Clamp(config.NpcSpeechVolume, 0f, 1f) : .8f;
        config.MaxOutputTokens = Math.Clamp(config.MaxOutputTokens, 128, 2048);
        config.ConversationUiScale = float.IsFinite(config.ConversationUiScale)
            ? Math.Clamp(config.ConversationUiScale, 0.75f, 1.5f)
            : 1f;
        config.ProactiveUiScale = float.IsFinite(config.ProactiveUiScale)
            ? Math.Clamp(config.ProactiveUiScale, 0.75f, 1.5f)
            : 1f;
        config.DailyCandidateMin = Math.Clamp(config.DailyCandidateMin, 1, 6);
        config.DailyCandidateMax = Math.Clamp(config.DailyCandidateMax, config.DailyCandidateMin, 6);
        config.DailyEncounterLimit = Math.Clamp(config.DailyEncounterLimit, 1, 10);
        config.ConversationLookbackDays = Math.Clamp(config.ConversationLookbackDays, 1, 112);
        config.PositiveConversationThreshold = Math.Clamp(config.PositiveConversationThreshold, 0d, 1d);
        config.NpcProactiveCooldownDays = 0;
        config.NpcGiftCooldownDays = 0;
        config.DailyGiftLimit = Math.Clamp(config.DailyGiftLimit, 1, 5);
        config.SocialActivationDistanceTiles = Math.Clamp(config.SocialActivationDistanceTiles, 1f, 16f);
        config.ActivityRetentionDays = Math.Clamp(
            config.ActivityRetentionDays,
            1,
            PlayerSocialDirectorState.MaxActivityDays);
        config.SocialSceneMaxCharacters = Math.Clamp(config.SocialSceneMaxCharacters, 100, 1200);
        config.MaxOvernightMailGifts = Math.Clamp(config.MaxOvernightMailGifts, 0, 2);
        config.ProactiveMinimumHearts = Math.Clamp(config.ProactiveMinimumHearts, 0, 14);
        config.ProactiveMinimumConversationTurns = Math.Clamp(config.ProactiveMinimumConversationTurns, 1, 1000);
        config.ProactiveEncounterDelayDays = Math.Clamp(config.ProactiveEncounterDelayDays, 0, 28);
        config.ProactiveEncounterExpiryDays = Math.Clamp(config.ProactiveEncounterExpiryDays, 1, 28);
        config.ProactiveEncounterCooldownDays = Math.Clamp(config.ProactiveEncounterCooldownDays, 0, 56);
        config.ProactiveActivationDistanceTiles = Math.Clamp(config.ProactiveActivationDistanceTiles, 1f, 16f);
        config.ProactiveSceneMaxCharacters = Math.Clamp(config.ProactiveSceneMaxCharacters, 100, 1200);
        config.SocialEncounterChance = double.IsFinite(config.SocialEncounterChance) ? Math.Clamp(config.SocialEncounterChance, 0, 1) : 1d / 3d;
        config.SocialSeparationDistanceTiles = float.IsFinite(config.SocialSeparationDistanceTiles) ? Math.Max(config.SocialActivationDistanceTiles + 2, Math.Clamp(config.SocialSeparationDistanceTiles, 3, 32)) : 10;
        config.SocialReencounterSeconds = Math.Clamp(config.SocialReencounterSeconds, 5, 600);
        config.WeeklyStoryInvitationDays = Math.Clamp(config.WeeklyStoryInvitationDays, 1, 14);
        config.WeeklyStoryMinimumHearts = Math.Clamp(config.WeeklyStoryMinimumHearts, 0, 14);
        config.WeeklyStoryMaxOutputTokens = Math.Clamp(config.WeeklyStoryMaxOutputTokens, 900, 6000);
        config.Model = string.IsNullOrWhiteSpace(config.Model) ? "deepseek-v4-flash" : config.Model.Trim();
        config.ReasoningEffort = string.IsNullOrWhiteSpace(config.ReasoningEffort) ? "low" : config.ReasoningEffort.Trim();
        config.ProactivePilotNpcName = string.IsNullOrWhiteSpace(config.ProactivePilotNpcName)
            ? "Abigail"
            : config.ProactivePilotNpcName.Trim();
        config.ProactiveGiftItemId = string.IsNullOrWhiteSpace(config.ProactiveGiftItemId)
            ? "(O)80"
            : config.ProactiveGiftItemId.Trim();
    }

    private bool NormalizeAiSettings()
    {
        bool changed = false;
        config.Ai ??= new AiProviderSettings();
        config.Ai.Hosted ??= new AiConnectionProfile();
        config.Ai.DeepSeek ??= new AiConnectionProfile();
        config.Ai.OpenAI ??= new AiConnectionProfile();

        if (config.Ai.SchemaVersion < 1)
        {
            // A clean install starts in hosted mode. Only configurations that
            // actually contain a legacy API key are migrated to direct mode.
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
            {
                string legacyBaseUrl = config.ApiUrl;
                if (!AiEndpointResolver.TryResolve(
                        AiProviderNames.DeepSeek,
                        legacyBaseUrl,
                        out legacyBaseUrl,
                        out _,
                        out _))
                {
                    legacyBaseUrl = AiEndpointResolver.GetDefaultBaseUrl(AiProviderNames.DeepSeek);
                }

                config.Ai.ActiveProvider = AiProviderNames.DeepSeek;
                config.Ai.DeepSeek.BaseUrl = legacyBaseUrl;
                config.Ai.DeepSeek.Model = string.IsNullOrWhiteSpace(config.Model)
                    ? "deepseek-v4-flash"
                    : config.Model.Trim();
                config.Ai.DeepSeek.ApiKey = (config.ApiKey ?? string.Empty).Trim();
            }

            config.Ai.Hosted.BaseUrl = "https://www.vivantvalley.com.cn/v1";
            config.Ai.Hosted.Model = string.IsNullOrWhiteSpace(config.Ai.Hosted.Model) ? "vv-dialogue" : config.Ai.Hosted.Model.Trim();
            if (string.IsNullOrWhiteSpace(config.ApiKey))
                config.Ai.ActiveProvider = AiProviderNames.Hosted;
            config.Ai.SchemaVersion = 2;
            config.ApiKey = string.Empty;
            config.PromptForApiKeyEveryLaunch = true;
            changed = true;
        }

        if (config.Ai.SchemaVersion < 2)
        {
            config.Ai.Hosted ??= new AiConnectionProfile();
            config.Ai.Hosted.BaseUrl = string.IsNullOrWhiteSpace(config.Ai.Hosted.BaseUrl)
                ? "https://www.vivantvalley.com.cn/v1"
                : config.Ai.Hosted.BaseUrl;
            config.Ai.Hosted.Model = string.IsNullOrWhiteSpace(config.Ai.Hosted.Model) ? "vv-dialogue" : config.Ai.Hosted.Model.Trim();
            config.Ai.SchemaVersion = 2;
            changed = true;
        }

        string provider = AiProviderNames.Normalize(config.Ai.ActiveProvider);
        if (!provider.Equals(config.Ai.ActiveProvider, StringComparison.Ordinal))
        {
            config.Ai.ActiveProvider = provider;
            changed = true;
        }

        changed |= NormalizeAiConnection(config.Ai.DeepSeek, AiProviderNames.DeepSeek);
        changed |= NormalizeAiConnection(config.Ai.OpenAI, AiProviderNames.OpenAI);
        changed |= NormalizeAiConnection(config.Ai.Hosted, AiProviderNames.Hosted);

        AiConnectionProfile active = config.Ai.GetProfile(provider);
        config.ApiUrl = active.BaseUrl;
        config.Model = string.IsNullOrWhiteSpace(active.Model) ? config.Model : active.Model.Trim();
        return changed;
    }

    private static bool NormalizeAiConnection(AiConnectionProfile profile, string provider)
    {
        bool changed = false;
        string baseUrl = (profile.BaseUrl ?? string.Empty).Trim();
        if (baseUrl.Length == 0)
        {
            baseUrl = AiEndpointResolver.GetDefaultBaseUrl(provider);
            changed = true;
        }
        if (AiEndpointResolver.TryResolve(provider, baseUrl, out string normalized, out _, out _)
            && !normalized.Equals(baseUrl, StringComparison.Ordinal))
        {
            baseUrl = normalized;
            changed = true;
        }

        string model = (profile.Model ?? string.Empty).Trim();
        string key = (profile.ApiKey ?? string.Empty).Trim();
        if (!baseUrl.Equals(profile.BaseUrl, StringComparison.Ordinal)
            || !model.Equals(profile.Model, StringComparison.Ordinal)
            || !key.Equals(profile.ApiKey, StringComparison.Ordinal))
        {
            changed = true;
        }

        profile.BaseUrl = baseUrl;
        profile.Model = model;
        profile.ApiKey = key;
        return changed;
    }

    private string GetDefaultStoryNpcName()
        => storyCatalog.GetFirst()?.Npc ?? config.ProactivePilotNpcName;

    private StoryDefinition CreateLegacyFallbackStory()
    {
        return new StoryDefinition
        {
            Id = "legacy.proactive-care.01",
            Npc = config.ProactivePilotNpcName,
            Priority = 0,
            Enabled = true,
            Repeatable = true,
            Trigger = new StoryTriggerDefinition
            {
                MinHearts = config.ProactiveMinimumHearts,
                MinConversationTurns = config.ProactiveMinimumConversationTurns,
                DelayDays = config.ProactiveEncounterDelayDays,
                ExpiryDays = config.ProactiveEncounterExpiryDays,
                CooldownDays = config.ProactiveEncounterCooldownDays,
            },
            Scene = new StorySceneDefinition
            {
                ActivationDistanceTiles = config.ProactiveActivationDistanceTiles,
                GiftItemId = config.ProactiveGiftItemId,
                AiBrief = L.T("text.580cf89f6902"),
                FallbackText = L.T("text.4c2409d243c1"),
            },
            AcceptEffects = new StoryEffectsDefinition
            {
                Trust = 2,
                Affection = 3,
            },
        };
    }

    private bool CanOpenOwnMenu()
        => dream is null && Context.IsPlayerFree
           && Game1.activeClickableMenu is null
           && Game1.currentLocation?.currentEvent is null;

    private static string GetPlayerId()
        => Game1.player.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture);

    private static bool IsConversationContextCurrent(PendingConversationInfo info)
    {
        if (!Context.IsWorldReady
            || !GetPlayerId().Equals(info.PlayerId, StringComparison.Ordinal)
            || Game1.Date.TotalDays != info.TotalDays)
        {
            return false;
        }

        return true;
    }

    private static bool IsGraphContextCurrent(PendingConversationInfo info)
    {
        if (!IsConversationContextCurrent(info))
            return false;

        string currentLocation = Game1.currentLocation?.NameOrUniqueName ?? string.Empty;
        if (!currentLocation.Equals(info.GraphSnapshot.Location, StringComparison.Ordinal))
            return false;
        if (Game1.getCharacterFromName(info.NpcName, mustBeVillager: false, includeEventActors: true) is null)
            return false;

        string expectedVersion =
            $"{info.PlayerId}:{info.NpcName}:{Game1.Date.TotalDays}:{currentLocation}:{info.GraphSnapshot.ActionId}";
        return info.GraphSnapshot.ContextVersion.Equals(expectedVersion, StringComparison.Ordinal);
    }

    private Task<GameBridgeToolResult> EnqueueGameBridgeToolAsync(GameBridgeToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = new TaskCompletionSource<GameBridgeToolResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        gameBridgeWorkItems.Enqueue(new GameBridgeWorkItem(request, completion));
        return completion.Task;
    }

    private void ProcessGameBridgeWorkItems()
    {
        const int maximumPerTick = 8;
        for (int handled = 0;
             handled < maximumPerTick && gameBridgeWorkItems.TryDequeue(out GameBridgeWorkItem? workItem);
             handled++)
        {
            try
            {
                workItem.Completion.TrySetResult(ExecuteGameBridgeTool(workItem.Request));
            }
            catch (Exception exception)
            {
                workItem.Completion.TrySetException(exception);
            }
        }
    }

    private GameBridgeToolResult ExecuteGameBridgeTool(GameBridgeToolRequest request)
    {
        string receiptKey = $"{request.RequestId}:{request.ToolCallId}";
        if (gameBridgeReceipts.TryGetValue(receiptKey, out GameBridgeReceipt? receipt))
        {
            bool sameCall = receipt.Tool.Equals(request.Tool, StringComparison.Ordinal)
                            && receipt.CandidateKey.Equals(request.CandidateKey, StringComparison.Ordinal)
                            && receipt.DestinationKey.Equals(request.DestinationKey, StringComparison.Ordinal)
                            && receipt.ContextVersion.Equals(request.ContextVersion, StringComparison.Ordinal);
            return sameCall
                ? receipt.Result
                : RejectGameBridgeTool(request, "tool_call_id_conflict", "The tool call ID was reused with different arguments.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId)
            || string.IsNullOrWhiteSpace(request.ToolCallId))
        {
            return RejectGameBridgeTool(request, "invalid_request", "requestId and toolCallId are required.");
        }

        ConversationScreenState? targetState = null;
        PendingConversationInfo? info = null;
        foreach (ConversationScreenState state in screenStates.GetActiveValues().Select(pair => pair.Value))
        {
            if (state.PendingInfo?.GraphRequestId.Equals(request.RequestId, StringComparison.Ordinal) != true)
                continue;

            targetState = state;
            info = state.PendingInfo;
            break;
        }

        if (targetState is null || info is null)
            return RejectGameBridgeTool(request, "request_not_active", "The conversation request is no longer active.");
        if (!IsGraphContextCurrent(info)
            || !request.PlayerId.Equals(info.PlayerId, StringComparison.Ordinal)
            || !request.NpcName.Equals(info.NpcName, StringComparison.Ordinal)
            || !request.ActionId.Equals(info.GraphSnapshot.ActionId, StringComparison.Ordinal)
            || !request.ContextVersion.Equals(info.GraphSnapshot.ContextVersion, StringComparison.Ordinal))
        {
            return RejectGameBridgeTool(request, "stale_context", "The game context changed before the tool could execute.");
        }

        string toolName = (request.Tool ?? string.Empty).Trim().ToLowerInvariant();
        if (toolName is not (
                NpcGiftToolNames.GiveGift
                or NpcGiftToolNames.MailGift
                or NpcMoveToolNames.MoveTo
                or NpcMineGuardToolNames.InviteMineGuard
                or NpcFishingToolNames.InviteFishingCompanion))
            return RejectGameBridgeTool(request, "unknown_tool", "The requested game tool is not registered.");

        if (toolName == NpcMoveToolNames.MoveTo)
        {
            if (!string.IsNullOrWhiteSpace(request.CandidateKey)
                || string.IsNullOrWhiteSpace(request.DestinationKey))
            {
                return RejectGameBridgeTool(request, "invalid_arguments", "move_to requires only destinationKey.");
            }

            ConversationMoveDestination? destination = info.MoveDestinations.FirstOrDefault(value => value.Key.Equals(
                request.DestinationKey,
                StringComparison.Ordinal));
            if (destination is null)
                return RejectGameBridgeTool(request, "destination_not_allowed", "The destination is outside the current allowlist.");
            LangGraphMoveConfirmation? confirmation = targetState.PendingMoveConfirmation;
            if (confirmation is null
                || !targetState.MoveConfirmationApproved
                || !confirmation.ToolCallId.Equals(request.ToolCallId, StringComparison.Ordinal)
                || !confirmation.DestinationKey.Equals(request.DestinationKey, StringComparison.Ordinal))
            {
                return RejectGameBridgeTool(
                    request,
                    "move_not_confirmed",
                    "The player did not approve this exact move request.");
            }

            NPC? npc = Game1.getCharacterFromName(
                info.NpcName,
                mustBeVillager: false,
                includeEventActors: false);
            if (npc is not null)
                SwitchNpcActivity(npc, NpcMoveToolNames.MoveTo);
            ConversationMoveExecutionResult movement = npc is null
                ? new ConversationMoveExecutionResult
                {
                    RequestedToolName = NpcMoveToolNames.MoveTo,
                    Outcome = ConversationMoveOutcome.Rejected,
                    Destination = destination,
                    FailureReason = "npc_unavailable",
                }
                : npcMoveToolService.Execute(npc, Game1.player, destination);
            targetState.MoveExecution = movement;
            var moveResult = new GameBridgeToolResult
            {
                RequestId = request.RequestId,
                ToolCallId = request.ToolCallId,
                ContextVersion = request.ContextVersion,
                Tool = toolName,
                Status = movement.IsCommitted ? "following" : movement.Outcome == ConversationMoveOutcome.Failed ? "failed" : "rejected",
                Ok = movement.IsCommitted,
                DestinationKey = destination.Key,
                DisplayName = destination.DisplayName,
                ReasonCode = movement.IsCommitted ? null : NormalizeOptionalBridgeValue(movement.FailureReason),
                Message = movement.IsCommitted
                    ? $"The NPC started traveling with the player toward {destination.DisplayName}; the player is leading."
                    : $"The shared trip did not start: {movement.FailureReason}",
                ReceiptId = receiptKey,
            };
            gameBridgeReceipts[receiptKey] = new GameBridgeReceipt(
                toolName,
                request.CandidateKey ?? string.Empty,
                request.DestinationKey ?? string.Empty,
                request.ContextVersion ?? string.Empty,
                moveResult);
            return moveResult;
        }

        if (toolName == NpcMineGuardToolNames.InviteMineGuard)
        {
            if (!string.IsNullOrWhiteSpace(request.CandidateKey)
                || !string.IsNullOrWhiteSpace(request.DestinationKey))
                return RejectGameBridgeTool(request, "invalid_arguments", "invite_mine_guard accepts no arguments.");
            LangGraphMoveConfirmation? confirmation = targetState.PendingMoveConfirmation;
            if (confirmation is null
                || !targetState.MoveConfirmationApproved
                || !confirmation.Kind.Equals("mine_guard_confirmation", StringComparison.Ordinal)
                || !confirmation.ToolCallId.Equals(request.ToolCallId, StringComparison.Ordinal))
            {
                return RejectGameBridgeTool(
                    request,
                    "mine_guard_not_confirmed",
                    "玩家没有确认这次下矿护卫邀请。");
            }

            NPC? npc = Game1.getCharacterFromName(
                info.NpcName,
                mustBeVillager: false,
                includeEventActors: false);
            if (npc is not null)
                SwitchNpcActivity(npc, NpcMineGuardToolNames.InviteMineGuard);
            ConversationMineGuardExecutionResult mineGuard = npc is null
                ? new ConversationMineGuardExecutionResult
                {
                    RequestedToolName = NpcMineGuardToolNames.InviteMineGuard,
                    Outcome = ConversationMineGuardOutcome.Rejected,
                    FailureReason = "npc_unavailable",
                }
                : npcMineGuardService.Execute(npc, Game1.player);
            targetState.MineGuardExecution = mineGuard;
            var guardResult = new GameBridgeToolResult
            {
                RequestId = request.RequestId,
                ToolCallId = request.ToolCallId,
                ContextVersion = request.ContextVersion,
                Tool = toolName,
                Status = mineGuard.IsCommitted ? "guarding" : mineGuard.Outcome == ConversationMineGuardOutcome.Failed ? "failed" : "rejected",
                Ok = mineGuard.IsCommitted,
                ReasonCode = mineGuard.IsCommitted ? null : NormalizeOptionalBridgeValue(mineGuard.FailureReason),
                Message = mineGuard.IsCommitted
                    ? "NPC 已接受下矿护卫，会由游戏跟随玩家并攻击附近的怪物。"
                    : $"下矿护卫没有开始：{mineGuard.FailureReason}",
                ReceiptId = receiptKey,
            };
            gameBridgeReceipts[receiptKey] = new GameBridgeReceipt(
                toolName,
                string.Empty,
                string.Empty,
                request.ContextVersion ?? string.Empty,
                guardResult);
            return guardResult;
        }

        if (toolName == NpcFishingToolNames.InviteFishingCompanion)
        {
            if (!string.IsNullOrWhiteSpace(request.CandidateKey)
                || !string.IsNullOrWhiteSpace(request.DestinationKey))
            {
                return RejectGameBridgeTool(request, "invalid_arguments", "invite_fishing_companion accepts no arguments.");
            }
            LangGraphMoveConfirmation? confirmation = targetState.PendingMoveConfirmation;
            if (confirmation is null
                || !targetState.MoveConfirmationApproved
                || !confirmation.Kind.Equals("fishing_confirmation", StringComparison.Ordinal)
                || !confirmation.ToolCallId.Equals(request.ToolCallId, StringComparison.Ordinal))
            {
                return RejectGameBridgeTool(
                    request,
                    "fishing_not_confirmed",
                    "玩家没有确认这次钓鱼同行邀请。");
            }

            NPC? npc = Game1.getCharacterFromName(
                info.NpcName,
                mustBeVillager: false,
                includeEventActors: false);
            if (npc is not null)
                SwitchNpcActivity(npc, NpcFishingToolNames.InviteFishingCompanion);
            ConversationFishingExecutionResult fishing = npc is null
                ? new ConversationFishingExecutionResult
                {
                    RequestedToolName = NpcFishingToolNames.InviteFishingCompanion,
                    Outcome = ConversationFishingOutcome.Rejected,
                    FailureReason = "npc_unavailable",
                }
                : npcFishingService.Execute(
                    npc,
                    Game1.player,
                    controlledByAnotherSession: false);
            targetState.FishingExecution = fishing;
            var fishingResult = new GameBridgeToolResult
            {
                RequestId = request.RequestId,
                ToolCallId = request.ToolCallId,
                ContextVersion = request.ContextVersion,
                Tool = toolName,
                Status = fishing.IsCommitted ? "following" : fishing.Outcome == ConversationFishingOutcome.Failed ? "failed" : "rejected",
                Ok = fishing.IsCommitted,
                ReasonCode = fishing.IsCommitted ? null : NormalizeOptionalBridgeValue(fishing.FailureReason),
                Message = fishing.IsCommitted
                    ? "NPC 已接受钓鱼同行，会靠近玩家等待玩家抛竿，并把钓到的鱼交给玩家。"
                    : $"钓鱼同行没有开始：{fishing.FailureReason}",
                ReceiptId = receiptKey,
            };
            gameBridgeReceipts[receiptKey] = new GameBridgeReceipt(
                toolName,
                string.Empty,
                string.Empty,
                request.ContextVersion ?? string.Empty,
                fishingResult);
            return fishingResult;
        }

        if (string.IsNullOrWhiteSpace(request.CandidateKey)
            || !string.IsNullOrWhiteSpace(request.DestinationKey))
        {
            return RejectGameBridgeTool(request, "invalid_arguments", "Gift tools require only candidateKey.");
        }

        ConversationGiftExecutionResult execution = ExecuteConversationGiftTool(
            info,
            new AiGiftToolDecision
            {
                ToolName = toolName,
                GiftCandidateId = request.CandidateKey,
                ReasonTag = request.ReasonTag,
            });
        targetState.GiftExecution = execution;

        SocialGiftCandidate? candidate = execution.Candidate
                                         ?? info.GiftCandidates.FirstOrDefault(value => value.Key.Equals(
                                             request.CandidateKey,
                                             StringComparison.Ordinal));
        string status = execution.Outcome switch
        {
            ConversationGiftOutcome.ImmediateDelivered or ConversationGiftOutcome.MailScheduled => "completed",
            ConversationGiftOutcome.Failed => "failed",
            ConversationGiftOutcome.Rejected => "rejected",
            _ => "rejected",
        };
        var result = new GameBridgeToolResult
        {
            RequestId = request.RequestId,
            ToolCallId = request.ToolCallId,
            ContextVersion = request.ContextVersion,
            Tool = toolName,
            Status = status,
            Ok = execution.IsCommitted,
            CandidateKey = candidate?.Key ?? NormalizeOptionalBridgeValue(request.CandidateKey),
            DisplayName = candidate?.DisplayName,
            Quantity = execution.Quantity > 0 ? execution.Quantity : candidate?.Quantity ?? 0,
            ReasonCode = execution.IsCommitted ? null : NormalizeOptionalBridgeValue(execution.FailureReason),
            Message = execution.IsCommitted
                ? $"The game completed {toolName} for {candidate?.DisplayName ?? request.CandidateKey}."
                : $"The game did not complete {toolName}: {execution.FailureReason}",
            ReceiptId = receiptKey,
        };
        gameBridgeReceipts[receiptKey] = new GameBridgeReceipt(
            toolName,
            request.CandidateKey ?? string.Empty,
            request.DestinationKey ?? string.Empty,
            request.ContextVersion ?? string.Empty,
            result);
        return result;
    }

    private void SwitchNpcActivity(NPC npc, string nextTool)
    {
        bool moveCanceled = npcMoveToolService.CancelNpc(npc.Name, $"replaced_by_{nextTool}");
        bool mineCanceled = npcMineGuardService.CancelNpc(npc.Name, $"replaced_by_{nextTool}");
        bool fishingCanceled = npcFishingService.CancelNpc(npc.Name, $"replaced_by_{nextTool}");
        bool hadController = npc.controller is not null
                             || npc.temporaryController is not null
                             || npc.queuedSchedulePaths.Count > 0;

        if (moveCanceled || mineCanceled || fishingCanceled || hadController)
        {
            // A completed session may have handed control back to a vanilla
            // schedule route. Clear that route only when a new action was
            // explicitly confirmed; ordinary chat never reaches this method.
            npc.controller = null;
            npc.temporaryController = null;
            npc.queuedSchedulePaths.Clear();
            npc.Halt();
            Monitor.Log(
                $"npc_activity_switch npc={npc.Name} target={nextTool} "
                + $"cancelled(move={moveCanceled},mine={mineCanceled},fishing={fishingCanceled}) "
                + $"controller_cleared={hadController}.",
                LogLevel.Info);
        }
    }

    private static GameBridgeToolResult RejectGameBridgeTool(
        GameBridgeToolRequest request,
        string reasonCode,
        string message)
        => new()
        {
            RequestId = request.RequestId ?? string.Empty,
            ToolCallId = request.ToolCallId ?? string.Empty,
            ContextVersion = request.ContextVersion ?? string.Empty,
            Tool = string.IsNullOrWhiteSpace(request.Tool) ? NpcGiftToolNames.None : request.Tool.Trim(),
            Status = "rejected",
            Ok = false,
            CandidateKey = NormalizeOptionalBridgeValue(request.CandidateKey),
            DestinationKey = NormalizeOptionalBridgeValue(request.DestinationKey),
            ReasonCode = reasonCode,
            Message = message,
            ReceiptId = $"{request.RequestId}:{request.ToolCallId}",
        };

    private static string? NormalizeOptionalBridgeValue(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string NormalizeUserText(string? value)
        => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();

    private string LimitReply(string value)
    {
        string normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= config.MaxReplyCharacters
            ? normalized
            : normalized[..config.MaxReplyCharacters] + "……";
    }

    private static string SanitizeForDialogue(string value)
    {
        // Stardew's dialogue parser treats these as control characters. Full-width
        // variants preserve the visible text without letting generated text run commands.
        return value
            .Replace("$", "＄", StringComparison.Ordinal)
            .Replace("#", "＃", StringComparison.Ordinal)
            .Replace("^", "＾", StringComparison.Ordinal)
            .Replace("%", "％", StringComparison.Ordinal)
            .Trim();
    }

    private static string CleanErrorForPlayer(string value)
    {
        string clean = (value ?? L.T("text.1b09e2da2e97"))
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return clean.Length <= 220 ? clean : clean[..220] + "……";
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            exception = aggregate.InnerExceptions[0];
        return exception;
    }

    private static void ShowHud(string text, int type = 0)
    {
        if (!Context.IsWorldReady)
            return;
        Game1.addHUDMessage(type == 0 ? new HUDMessage(text) : new HUDMessage(text, type));
    }

    private sealed record PendingConversationInfo(
        string PlayerId,
        string NpcName,
        string NpcDisplayName,
        int TotalDays,
        string UserText,
        string GameContext,
        string GameDate,
        NpcConversationMemory MemorySnapshot,
        ConversationEngineOptions Options,
        string RecentConversation,
        string ActivitySummary,
        string GiftActionId,
        IReadOnlyList<SocialGiftCandidate> GiftCandidates,
        IReadOnlyList<ConversationMoveDestination> MoveDestinations,
        NpcContextSnapshot GraphSnapshot,
        string GraphRequestId,
        SpeechTurn? Speech = null);

    private sealed record GameBridgeWorkItem(
        GameBridgeToolRequest Request,
        TaskCompletionSource<GameBridgeToolResult> Completion);

    private sealed record GameBridgeReceipt(
        string Tool,
        string CandidateKey,
        string DestinationKey,
        string ContextVersion,
        GameBridgeToolResult Result);

    private sealed record QueuedDialogue(
        string PlayerId,
        string NpcName,
        string NpcDisplayName,
        string Text,
        NpcPresentationPlan Presentation,
        SpeechTurn? Speech = null,
        string? SpokenReply = null);

    private sealed record PendingProactiveInfo(
        string PlayerId,
        string ActionId,
        string NpcName,
        string NpcDisplayName,
        string LocationName,
        int TotalDays,
        string GiftItemId,
        string GiftDisplayName,
        string FallbackText,
        IReadOnlyList<PlannedStoryChoice> Choices);

    private sealed record QueuedProactiveScene(
        string PlayerId,
        string ActionId,
        string NpcName,
        string NpcDisplayName,
        string GiftItemId,
        string GiftDisplayName,
        string DialogueText,
        IReadOnlyList<PlannedStoryChoice> Choices,
        string LocationName,
        int TotalDays)
    {
        public QueuedProactiveScene(
            string playerId,
            string actionId,
            string npcName,
            string npcDisplayName,
            string giftItemId,
            string giftDisplayName,
            string dialogueText,
            IReadOnlyList<PlannedStoryChoice> choices)
            : this(
                playerId,
                actionId,
                npcName,
                npcDisplayName,
                giftItemId,
                giftDisplayName,
                dialogueText,
                choices,
                Game1.currentLocation.NameOrUniqueName,
                Game1.Date.TotalDays)
        {
        }
    }

    private sealed class ConversationScreenState
    {
        public CancellationTokenSource SessionCancellation { get; set; } = new();

        public bool RequestApiKeyPrompt { get; set; }

        public Task<AiGiftToolDecision>? PendingGiftPlan { get; set; }

        public Task<LangGraphResponse>? PendingGraphDecision { get; set; }

        public Task<ConversationEngineResult>? PendingConversation { get; set; }

        public PendingConversationInfo? PendingInfo { get; set; }

        public LangGraphMoveConfirmation? PendingMoveConfirmation { get; set; }

        public bool MoveConfirmationApproved { get; set; }

        public ConversationGiftExecutionResult? GiftExecution { get; set; }

        public ConversationMoveExecutionResult? MoveExecution { get; set; }

        public ConversationMineGuardExecutionResult? MineGuardExecution { get; set; }

        public ConversationFishingExecutionResult? FishingExecution { get; set; }

        public bool HasPendingConversation
            => PendingGiftPlan is not null
               || PendingGraphDecision is not null
               || PendingConversation is not null
               || PendingMoveConfirmation is not null;

        public QueuedDialogue? QueuedDialogue { get; set; }

        public ConversationContinuationTarget? QueuedConversationContinuation { get; set; }

        public int ConversationContinuationDelayUpdates { get; set; }

        public AiStreamingDialogueMenu? StreamingMenu { get; set; }

        public CancellationTokenSource ProactiveCancellation { get; set; } = new();

        public Task<string>? PendingProactiveScene { get; set; }

        public PendingProactiveInfo? PendingProactiveInfo { get; set; }

        public QueuedProactiveScene? QueuedProactiveScene { get; set; }

        public QueuedProactiveScene? ActiveProactiveScene { get; set; }

        public AiProactiveEncounterMenu? ProactiveMenu { get; set; }

        public CancellationTokenSource SocialCancellation { get; set; } = new();

        public Task<AiSocialSceneDecision>? PendingSocialScene { get; set; }

        public PendingSocialSceneInfo? PendingSocialInfo { get; set; }

        public QueuedSocialScene? QueuedSocialScene { get; set; }

        public QueuedSocialScene? ActiveSocialScene { get; set; }

        public AiProactiveEncounterMenu? SocialMenu { get; set; }
    }
}
