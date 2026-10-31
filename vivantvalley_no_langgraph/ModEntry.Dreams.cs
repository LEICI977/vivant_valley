using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Menus;
using VivantValley.Services;

namespace VivantValley;

public sealed partial class ModEntry
{
    private const string DreamSaveKey = "shared-dreams-v1";
    private const string DreamDialogueKey = "firstmod.StardewAIMemories.Dream";
    private DreamSaveStore dreamStore = new();
    private bool dreamStorageReady;
    private bool resumingDreamSleep;
    private DreamSession? dream;
    private sealed class DreamSession
    {
        public GameLocation Location = null!;
        public string Player = "";
        public int Day;
        public bool CanMove;
        public int FreezePause;
        public IClickableMenu? Menu;
        public Action? AfterMenu;
        public Task<DreamReply>? Request;
        public SpeechTurn? Speech;
        public CancellationTokenSource? Cancellation;
        public DreamEpisode? Episode;
        public NPC? Npc;
        public string? DisplayedText;
        public List<string> ShownPages = new();
        public DreamBackdrop? Backdrop;
        public string Today = "";
        public double StartedAt;
    }
    private void LoadDreams()
    {
        dreamStorageReady = false;
        try
        {
            dreamStore = Helper.Data.ReadSaveData<DreamSaveStore>(DreamSaveKey) ?? new();
            if (dreamStore.Version != 1 || dreamStore.Players is null
                || dreamStore.Players.Values.Any(p => p is null || p.TalkedToday is null || p.Episodes is null
                    || p.Episodes.Any(e => e is null || e.Lines is null)))
                throw new InvalidOperationException("Unsupported or invalid dream save.");
            foreach (var episode in dreamStore.Players.Values.SelectMany(p => p.Episodes).Where(e => e.Status == "active"))
                episode.Status = "interrupted";
            dreamStorageReady = true;
        }
        catch (Exception ex) { Monitor.Log("Dream data could not be read; existing data will not be overwritten: " + ex.Message,LogLevel.Error); }
    }
    private void PersistDreams()
    {
        if (!dreamStorageReady || !Context.IsWorldReady || !Context.IsMainPlayer) return;
        try { Helper.Data.WriteSaveData(DreamSaveKey,dreamStore); }
        catch (Exception ex) { Monitor.Log("Dream save failed: " + ex.Message,LogLevel.Error); }
    }
    private DreamPlayerState DreamPlayer()
    {
        var state = dreamStore.For(GetPlayerId());
        state.OnDay(Game1.Date.TotalDays);
        return state;
    }
    private void RecordDreamConversation(string npc, bool consumeReminder = false)
    {
        if (dream is not null || !dreamStorageReady || !Context.IsWorldReady || !Context.IsMainPlayer || npc.Length == 0) return;
        var state = DreamPlayer();
        bool changed = state.TalkedToday.Add(npc);
        // A generic conversation is not proof that any particular dream was mentioned.
        // Legacy MorningMentionUsed flags remain readable but never gate recent knowledge.
        if (changed) PersistDreams();
    }
    private IReadOnlyList<string> BuildRecentKnownEvents(string npc, string query, string? excludedDream = null)
    {
        if (!Context.IsWorldReady) return Array.Empty<string>();
        int day = Game1.Date.TotalDays;
        var events = new List<RecentKnownEvent>();
        if (dreamStorageReady)
            events.AddRange(RecentKnownEventPolicy.Dreams(DreamPlayer(), npc, day, L.IsChinese, excludedDream));
        if (config.EnableTownEchoes)
            events.AddRange(RecentKnownEventPolicy.Echoes(memoryStore.TownEchoes, GetPlayerId(), npc, day, L.IsChinese,
                name => Game1.getCharacterFromName(name)?.displayName ?? name));
        return RecentKnownEventPolicy.Select(events, query);
    }

    private string RecallDream(string npc, string query)
        => !dreamStorageReady || !Context.IsWorldReady ? ""
            : DreamPolicy.Recall(DreamPlayer(),npc,Game1.Date.TotalDays,query);

    private IReadOnlyList<ContextMemoryCandidate> BuildSceneRecallCandidates(string npc, string query, string? excludedDream = null, bool wholeDreams = false)
    {
        var candidates = new List<ContextMemoryCandidate>();
        var social = socialStore.GetOrCreatePlayer(GetPlayerId()).GetOrCreateNpc(npc);
        if (memoryStore.TryGet(GetPlayerId(), npc, out var memory) && memory is not null)
        {
            var active = ConversationCommitmentPolicy.Active(social, Game1.Date.TotalDays, forFollowUp: false);
            foreach (var candidate in ContextMemoryCompiler.FromSummary(
                         ConversationCommitmentPolicy.PrepareRecall(memory.Summary, social, Game1.Date.TotalDays)))
            {
                var commitment = candidate.Status is null ? null : social.Commitments?.FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(item.Text) && candidate.Text.Contains(item.Text, StringComparison.Ordinal));
                candidates.Add(commitment is null ? candidate : candidate with
                {
                    Id = "commitment-" + ContextMemoryCompiler.StableId(commitment.Text), Source = "commitment",
                    Status = commitment.Status == "active" ? active.Contains(commitment.Text) ? "pending" : "expired" : commitment.Status,
                });
            }
        }
        if (dreamStorageReady && Context.IsWorldReady)
            candidates.AddRange(wholeDreams
                ? DreamPolicy.RecallEpisodes(DreamPlayer(), npc, Game1.Date.TotalDays, query)
                : DreamPolicy.RecallCandidates(DreamPlayer(), npc, Game1.Date.TotalDays, query, excludedDream));
        return candidates;
    }

    internal bool TryBeginDreamSleep(GameLocation location)
    {
        // Multiplayer sleep is a shared ready-check. Until per-farmhand persistence
        // and independent night UI exist, keep that original protocol untouched.
        if (resumingDreamSleep || !config.EnableDreams || !dreamStorageReady || !Context.IsWorldReady
            || !Context.IsMainPlayer || Game1.IsMultiplayer || Game1.player.passedOut
            || Game1.timeOfDay >= 2600 || Game1.CurrentEvent is not null || weeklyStoryRunner.IsRunning
            || string.IsNullOrWhiteSpace(runtimeApiKey)) return false;
        if (dream is not null) return true;
        if (DreamPlayer().NightConsumed || screenStates.Value.HasPendingConversation) return false;
        dream = new DreamSession
        {
            Location = location, Player = GetPlayerId(), Day = Game1.Date.TotalDays,
            CanMove = Game1.player.CanMove, FreezePause = Game1.player.freezePause,
            StartedAt = Game1.currentGameTime.TotalGameTime.TotalSeconds,
            Menu = Game1.activeClickableMenu,
        };
        dream.AfterMenu = BeginDreamNight;
        return true;
    }
    private void BeginDreamNight()
    {
        var state = DreamPlayer();
        string visitor = DreamPolicy.TakeAutomaticVisitor(state,Game1.uniqueIDForThisGame + ":" + GetPlayerId(),
            KnownDreamNpcs().Select(n => new DreamCandidate(n.Name,Game1.player.getFriendshipHeartLevelForNPC(n.Name))));
        PersistDreams();
        if (visitor.Length > 0) StartDream(visitor,"npc_visits");
        else ShowDreamChoice();
    }
    private void ShowDreamChoice()
        => DreamQuestion(L.T("dream.choice"), new[]
        {
            new Response("visit",L.T("dream.visit")), new Response("skip",L.T("dream.skip")),
        }, answer =>
        {
            if (answer == "visit") ShowDreamNpcPicker(0);
            else AbortDream(true);
        });
    private NPC[] KnownDreamNpcs()
        => Game1.player.friendshipData.Pairs.Select(pair => Game1.getCharacterFromName(pair.Key,mustBeVillager:false))
            .Where(n => n is not null && n.IsVillager && n.CanSocialize).Cast<NPC>()
            .OrderBy(n => n.displayName,StringComparer.CurrentCulture).ToArray();
    private void ShowDreamNpcPicker(int page)
    {
        var npcs = KnownDreamNpcs();
        const int pageSize = 4;
        page = Math.Clamp(page,0,Math.Max(0,(npcs.Length - 1)/pageSize));
        var responses = npcs.Skip(page * pageSize).Take(pageSize)
            .Select((npc,i) => new Response("npc" + i,npc.displayName)).ToList();
        if (page > 0) responses.Add(new("previous",L.T("dream.previous")));
        if ((page + 1) * pageSize < npcs.Length) responses.Add(new("next",L.T("dream.next")));
        responses.Add(new("back",L.T("dream.back")));
        DreamQuestion(npcs.Length == 0 ? L.T("dream.none-known") : L.T("dream.choose-npc"),responses.ToArray(),answer =>
        {
            if (answer == "previous") ShowDreamNpcPicker(page - 1);
            else if (answer == "next") ShowDreamNpcPicker(page + 1);
            else if (answer == "back") ShowDreamChoice();
            else if (int.TryParse(answer.AsSpan(3),out int i) && i >= 0 && i < Math.Min(pageSize,npcs.Length-page*pageSize))
                StartDream(npcs[page * pageSize + i].Name,"player_visits");
        });
    }
    private void StartDream(string name, string direction)
    {
        if (dream is not { } session) return;
        NPC? npc = Game1.getCharacterFromName(name,mustBeVillager:false);
        if (npc is null) { ShowDreamChoice(); return; }
        var state = DreamPlayer();
        if (direction == "player_visits") state.NightConsumed = true;
        session.Npc = npc;
        session.Today = memoryStore.TryGet(GetPlayerId(),name,out var daytime) && daytime is not null
            ? string.Join("\n",daytime.Messages.Where(m => m.Source != ConversationMemorySources.Dream
                && m.GameDate.StartsWith(Game1.Date.ToString(),StringComparison.Ordinal)).TakeLast(6)
                .Select(m => m.Role + ": " + m.Content)) : "";
        string activities = activityJournal.BuildPromptSummary(socialStore.GetOrCreatePlayer(GetPlayerId()),Game1.Date.TotalDays);
        session.Today += "\n" + activities;
        session.Backdrop?.Dispose(); session.Backdrop = null;
        session.StartedAt = Game1.currentGameTime.TotalGameTime.TotalSeconds;
        session.Episode = new DreamEpisode
        {
            Id = $"dream:{session.Player}:{session.Day}:{name}:{direction}:{state.Episodes.Count}", Day = session.Day,
            Npc = name, Direction = direction, Scene = DreamPolicy.Scene,
        };
        state.Episodes.Add(session.Episode);
        PersistDreams();
        RequestDreamReply();
    }
    private void RequestDreamReply()
    {
        if (dream is not { Npc: { } npc, Episode: { } episode } session || session.Request is not null) return;
        try
        {
            session.Cancellation?.Dispose();
            session.Cancellation = new CancellationTokenSource();
            session.Cancellation.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds,10,600)));
            string identity = DreamService.BuildIdentity(
                RidgesideVillageCompatibility.BuildConversationIdentityPrompt(npc.displayName,npc.Name),ReadNpcPersonality(Game1.player,npc.Name));
            string query = episode.Lines.LastOrDefault(l => l.Role == "user")?.Text ?? "";
            var memory = BuildSceneRecallCandidates(npc.Name, query, episode.Id, wholeDreams: true);
            Game1.player.friendshipData.TryGetValue(npc.Name,out Friendship? friendship);
            var relationship = DreamRelationship.FromGameState(
                Game1.player.getFriendshipHeartLevelForNPC(npc.Name),npc.Age == NPC.adult,
                friendship?.IsDating() == true,friendship?.IsEngaged() == true,friendship?.IsMarried() == true,
                friendship?.IsRoommate() == true,friendship?.IsDivorced() == true,npc.datable.Value);
            var snapshot = SocialPromptContext.Parse(BuildNpcGameSnapshot(npc,GetPlayerId(),includeLiveActivities:false).SystemPrompt);
            StopNpcSpeech();
            session.Speech = CaptureSpeechTurn(npc.Name, Guid.NewGuid().ToString("N"));
            session.Request = new DreamService(deepSeekClient).ReplyAsync(runtimeApiKey,currentAiProfile.Model,episode,identity,
                System.Text.Json.JsonSerializer.Serialize(new { currentGameTime = snapshot.CurrentGameTime, worldFacts = snapshot.CurrentFacts,
                    commitmentStates = BuildMemoryCommitmentStates(socialStore.GetOrCreatePlayer(GetPlayerId()).GetOrCreateNpc(npc.Name), Game1.Date.TotalDays) }),
                "",session.Today,session.Cancellation.Token,relationship,memory,
                GetMemorySaveScope(memoryStore.GetOrCreate(GetPlayerId(), npc.Name)),
                BuildRecentKnownEvents(npc.Name, "", episode.Id), session.Speech);
            DreamQuestion(L.T("dream.wait"),new[] { new Response("wake",L.T("dream.wake")) },_ => EndDream());
        }
        catch (Exception ex) { Monitor.Log("Dream request: " + ex.GetType().Name,LogLevel.Warn); ShowDreamRetry(); }
    }
    private void ShowDreamRetry()
        => DreamQuestion(L.T("dream.failed"),new[] { new Response("retry",L.T("dream.retry")), new Response("wake",L.T("dream.wake")) },
            answer => { if (answer == "retry") RequestDreamReply(); else EndDream(); });
    private void OpenDreamInput()
    {
        StopNpcSpeech();
        if (dream is not { Npc: { } npc, Episode: { } episode } session) return;
        if (episode.Lines.Count(l => l.Role == "user") >= DreamPolicy.MaximumTurns)
        { DreamQuestion(L.T("dream.ending"),new[] { new Response("wake",L.T("dream.wake")) },_ => EndDream()); return; }
        session.Menu = new AiChatInputMenu(L.F("dream.speaking",$"梦中 · {npc.displayName}"),text =>
        {
            if (!ReferenceEquals(dream,session)) return;
            episode.Lines.Add(new DreamLine { Role = "user", Text = WeeklyStoryPlan.LimitText(text,800) });
            PersistDreams();
            RequestDreamReply();
        }, onCancel: () => QueueDreamWake(), onOpenSettings: () => QueueDreamWake(),
            conversationUiScale: config.ConversationUiScale, onEndTrip: () => QueueDreamWake(),
            settingsLabel: L.T("dream.wake"), endLabel: L.T("dream.wake"), portraitNpc:npc);
        Game1.activeClickableMenu = session.Menu;
    }
    private void QueueDreamWake()
    {
        if (dream is { } session) session.AfterMenu = EndDream;
    }
    private void DreamQuestion(string text, Response[] responses, Action<string> callback)
    {
        if (dream is not { } session) return;
        Game1.currentLocation.createQuestionDialogue(text,responses,(_,answer) =>
        {
            if (ReferenceEquals(dream,session) && session.AfterMenu is null && responses.Any(r => r.responseKey == answer))
                session.AfterMenu = () => callback(answer);
        });
        session.Menu = Game1.activeClickableMenu;
    }
    private bool UpdateDream()
    {
        if (dream is not { } session) return false;
        try
        {
            if (!Context.IsWorldReady || GetPlayerId() != session.Player || Game1.Date.TotalDays != session.Day
                || !ReferenceEquals(Game1.currentLocation,session.Location) || Game1.player.passedOut)
            { AbortDream(false); return false; }
            Game1.player.CanMove = false;
            Game1.player.freezePause = 200;
            if (session.AfterMenu is { } after)
            {
                if (Game1.activeClickableMenu is not null)
                {
                    if (!ReferenceEquals(Game1.activeClickableMenu,session.Menu)) AbortDream(false);
                    return true;
                }
                session.AfterMenu = null;
                session.Menu = null;
                after();
                return true;
            }
            if (session.Request is { IsCompleted: true } task)
            {
                session.Request = null;
                try
                {
                    var reply = task.GetAwaiter().GetResult();
                    session.Episode!.Scene = DreamPolicy.Scene;
                    // Replace only our waiting menu. Another mod's menu retains ownership.
                    if (Game1.activeClickableMenu is not null && !ReferenceEquals(Game1.activeClickableMenu,session.Menu))
                    { AbortDream(false); return true; }
                    CloseDreamMenu(session);
                    session.ShownPages.Clear();
                    session.DisplayedText = reply.Text;
                    Game1.DrawDialogue(new Dialogue(session.Npc,DreamDialogueKey,reply.Text));
                    session.Menu = Game1.activeClickableMenu;
                    PlayNpcSpeech(session.Speech, reply.Text, session.Menu);
                }
                catch (Exception ex)
                {
                    Monitor.Log("Dream reply failed: " + ex.GetType().Name,LogLevel.Warn);
                    CloseDreamMenu(session); ShowDreamRetry();
                }
                return true;
            }
            if (session.DisplayedText is { } shown && !ReferenceEquals(Game1.activeClickableMenu,session.Menu))
            {
                if (session.ShownPages.Count > 0) session.Episode!.Lines.Add(new DreamLine { Role = "assistant", Text = string.Join(" ",session.ShownPages) });
                session.DisplayedText = null; session.Menu = null;
                PersistDreams();
                if (Game1.activeClickableMenu is null) OpenDreamInput();
                else AbortDream(false);
                return true;
            }
            if (session.Menu is not null && Game1.activeClickableMenu is not null
                && !ReferenceEquals(Game1.activeClickableMenu,session.Menu))
            { AbortDream(false); return true; }
            if (session.Menu is not null && Game1.activeClickableMenu is null)
            { EndDream(); return true; }
            return true;
        }
        catch (Exception ex)
        {
            Monitor.Log("Dream session interrupted: " + ex.Message,LogLevel.Error);
            AbortDream(true);
            return true;
        }
    }
    private void CloseDreamMenu(DreamSession session)
    {
        var menu = session.Menu; session.Menu = null;
        if (menu is not null && ReferenceEquals(Game1.activeClickableMenu,menu)) menu.exitThisMenuNoSound();
    }
    private void FinishDreamEpisode(DreamSession session, bool completed)
    {
        if (session.Episode is { } episode)
        {
            if (session.DisplayedText is not null && session.ShownPages.Count > 0)
                episode.Lines.Add(new DreamLine {Role="assistant",Text=string.Join(" ",session.ShownPages)});
            episode.Status = completed ? "completed" : "interrupted";
            episode.SummaryPending = episode.Lines.Any(line => line.Role == "assistant");
            DreamService.Forget(episode);
            if (episode.Lines.Count > 0)
            {
                var memory = memoryStore.GetOrCreate(session.Player,episode.Npc);
                SegmentMemoryPolicy.Enable(memory);
                if (!memory.Messages.Any(m => m.DedupeKey == episode.Id))
                {
                    memory.Messages.Add(new ConversationMemoryMessage
                    {
                        Role = "system", Source = ConversationMemorySources.Dream,
                        Content = L.IsChinese ? $"[dream_only] 第{episode.Day}日夜与{episode.Npc}共同做梦；原话待总结，不能推断现实行动。"
                            : $"[dream_only] Shared dream with {episode.Npc}, night {episode.Day}; summary pending, not proof of waking actions.",
                        GameDate = $"{Game1.Date} night", LocationName = "dream:" + episode.Scene,
                        DedupeKey = episode.Id, EventId = episode.Id,
                    });
                    memory.Messages = ConversationTimelinePolicy.RetainRecentMessages(memory);
                    memoryDirty = true;
                    PersistMemory(force:true);
                }
            }
        }
    }
    private void EndDream()
    {
        StopNpcSpeech();
        if (dream is not { } session) return;
        if (!DreamPolicy.ReturnToChoice(session.Episode)) { AbortDream(true); return; }
        // A visitor's departure leaves the player in the same night, with both choices available.
        session.AfterMenu = null;
        CancelDreamRequest(session);
        CloseDreamMenu(session);
        session.AfterMenu = null;
        FinishDreamEpisode(session,completed:true);
        session.Episode = null; session.Npc = null; session.DisplayedText = null;
        session.ShownPages.Clear(); session.Today = "";
        session.Backdrop?.Dispose(); session.Backdrop = null;
        PersistDreams();
        ShowDreamChoice();
    }
    private static void CancelDreamRequest(DreamSession session)
    {
        session.Cancellation?.Cancel();
        if (session.Request is { } pending) _ = pending.ContinueWith(t => { _ = t.Exception; },TaskContinuationOptions.OnlyOnFaulted);
        session.Cancellation?.Dispose(); session.Cancellation = null; session.Request = null;
    }
    internal void AbortDream(bool continueSleep)
    {
        StopNpcSpeech();
        if (dream is not { } session) return;
        dream = null; // Clear before menu cleanup callbacks; don't recursively wake.
        CancelDreamRequest(session);
        session.Backdrop?.Dispose(); session.Backdrop = null;
        CloseDreamMenu(session);
        if (!Context.IsWorldReady || GetPlayerId() != session.Player) return;
        // The old Yes/No sleep menu had control while the snapshot was captured.
        // If it has closed, restore free control, not that menu's temporary lock.
        Game1.player.CanMove = Game1.activeClickableMenu is null && Game1.CurrentEvent is null ? true : session.CanMove;
        Game1.player.freezePause = Game1.activeClickableMenu is null ? 0 : session.FreezePause;
        DreamPlayer().NightConsumed = true;
        FinishDreamEpisode(session,continueSleep);
        PersistDreams();
        if (!continueSleep || Game1.Date.TotalDays != session.Day || !ReferenceEquals(Game1.currentLocation,session.Location)) return;
        resumingDreamSleep = true;
        try { session.Location.answerDialogueAction("Sleep_Yes",Array.Empty<string>()); }
        finally { resumingDreamSleep = false; }
    }
    private void TrackDreamPage(object? sender, RenderedActiveMenuEventArgs e)
    {
        if (dream is null && Game1.activeClickableMenu is AiStreamingDialogueMenu reply && reply.HasReply)
            RecordDreamConversation(reply.NpcName,consumeReminder:true);
        if (dream is not { DisplayedText: not null } session
            || Game1.activeClickableMenu is not DialogueBox box || !ReferenceEquals(box,session.Menu) || box.transitioning) return;
        string text = box.getCurrentString();
        if (box.characterIndexInDialogue >= text.Length - 1 && text.Length > 0 && !session.ShownPages.Contains(text))
            session.ShownPages.Add(text);
    }
    private void DrawDream(object? sender, RenderingActiveMenuEventArgs e)
    {
        if (dream is not { } session) return;
        try
        {
            session.Backdrop ??= new DreamBackdrop(Helper.DirectoryPath,
                Game1.uniqueIDForThisGame + ":" + (session.Episode?.Id ?? $"{session.Player}:{session.Day}:choice"),
                session.Npc?.Name ?? "", message => Monitor.LogOnce(message,LogLevel.Warn));
            session.Backdrop.Draw(e.SpriteBatch,session.Npc,
                Game1.currentGameTime.TotalGameTime.TotalSeconds-session.StartedAt);
        }
        catch (Exception ex) { Monitor.LogOnce("Dream backdrop unavailable: " + ex.GetType().Name,LogLevel.Warn); }
    }
}
