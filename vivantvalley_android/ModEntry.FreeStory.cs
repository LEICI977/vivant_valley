using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Menus;
using VivantValley.Services;

namespace VivantValley;

public sealed partial class ModEntry
{
    private WeeklyStoryPlan? activeFreeStory;
    private IClickableMenu? freeStoryMenu;
    private FreeStoryLine? freeStoryDisplayedLine;
    private Task<FreeStoryTurnResult>? freeStoryTask;
    private CancellationTokenSource? freeStoryCancellation;
    private string freeStoryPlayerId = "";
    private string freeStoryDraft = "";
    private bool freeStoryTransition;
    private Action? freeStoryAfterMenu;

    private void BeginFreeStory(WeeklyStoryPlan story)
    {
        activeFreeStory = story;
        freeStoryDraft = "";
        freeStoryPlayerId = GetPlayerId();
        var scene = story.FreeScene!;
        if (!scene.Started)
        {
            scene.Started = true;
            scene.PendingLines = scene.Opening.ToList();
        }
        weeklyStoryDirty = true;
        PersistWeeklyStories(force: true);
        AdvanceFreeStory();
    }

    private void UpdateFreeStory()
    {
        if (activeFreeStory is not { } story) return;
        if (!Context.IsWorldReady || GetPlayerId() != freeStoryPlayerId || !weeklyStoryRunner.IsRunning)
        { CancelFreeStoryRequest(); activeFreeStory = null; return; }
        if (freeStoryAfterMenu is { } after && !ReferenceEquals(Game1.activeClickableMenu, freeStoryMenu))
        {
            freeStoryAfterMenu = null;
            freeStoryMenu = null;
            freeStoryTransition = true;
            try { after(); } finally { freeStoryTransition = false; }
            return;
        }
        if (freeStoryAfterMenu is not null) return;
        if (freeStoryTask is { IsCompleted: true } task)
        {
            freeStoryTask = null;
            try
            {
                FreeStoryTurnResult result = task.GetAwaiter().GetResult();
                story.FreeScene!.PendingLines = result.Lines;
                story.FreeScene.SuggestEnd = result.SuggestEnd;
                story.FreeScene.AwaitingReply = false;
                CloseFreeStoryMenu();
                weeklyStoryDirty = true;
                PersistWeeklyStories(force: true);
                AdvanceFreeStory();
            }
            catch (Exception ex)
            {
                Monitor.Log($"free_scene reply failed story={story.StoryId}: {ex.GetType().Name}", LogLevel.Warn);
                CloseFreeStoryMenu();
                ShowFreeStoryRetry();
            }
            return;
        }
        if (freeStoryDisplayedLine is { } line && !ReferenceEquals(Game1.activeClickableMenu, freeStoryMenu))
        {
            // Commit only after the actual native dialogue has been shown and closed.
            freeStoryDisplayedLine = null;
            freeStoryMenu = null;
            npcPresentationService.End(line.Actor);
            bool left = line.Action == "leave" && weeklyStoryRunner.LeaveFreeSceneActor(line.Actor);
            RecordFreeStoryEvent(story, line.Actor, line.Text, "talk", line.Target);
            if (left) RecordFreeStoryEvent(story, line.Actor, L.IsChinese ? "离开了现场。" : "Left the scene.", "leave");
            story.FreeScene!.PendingLines.RemoveAt(0);
            weeklyStoryDirty = true;
            PersistWeeklyStories(force: true);
            if (Game1.activeClickableMenu is null) AdvanceFreeStory();
            return;
        }
        if (!freeStoryTransition && freeStoryTask is null && freeStoryDisplayedLine is null
            && Game1.activeClickableMenu is null) AdvanceFreeStory();
    }

    private void AdvanceFreeStory()
    {
        if (activeFreeStory is not { } story || Game1.activeClickableMenu is not null) return;
        var scene = story.FreeScene!;
        if (scene.PendingLines.Count > 0)
        {
            FreeStoryLine line = scene.PendingLines[0];
            NPC? npc = Game1.getCharacterFromName(line.Actor, mustBeVillager: false, includeEventActors: true);
            if (npc is null || !FreeStoryService.Present(story).Contains(line.Actor))
            { weeklyStoryRunner.Interrupt("free_scene_missing_actor"); return; }
            npc.IsInvisible = false;
            if (line.Target == "player") npc.facePlayer(Game1.player);
            else
            {
                NPC? target = Game1.getCharacterFromName(line.Target, mustBeVillager: false, includeEventActors: true);
                if (target is not null) npc.faceGeneralDirection(target.Position);
            }
            string text = SanitizeForDialogue(line.Text);
            var presentation = NpcPresentationPolicy.CreateForStoryCue(npc.Name, text, line.Cue);
            try
            {
                Game1.DrawDialogue(new Dialogue(npc, "firstmod.StardewAIMemories.FreeScene", text
                    + (presentation.DialogueEmotionToken.Length > 0 ? " " + presentation.DialogueEmotionToken : "")));
                if (Game1.activeClickableMenu is not DialogueBox menu) throw new InvalidOperationException("No native dialogue.");
                freeStoryMenu = menu;
                freeStoryDisplayedLine = line;
                npcPresentationService.Begin(npc.Name, presentation, menu);
            }
            catch (Exception ex)
            {
                Monitor.Log("free_scene display: " + ex.GetType().Name, LogLevel.Warn);
                weeklyStoryRunner.Interrupt("free_scene_display_failed");
            }
            return;
        }
        if (FreeStoryService.Present(story).Length == 0) { FinishFreeStory(); return; }
        if (scene.AwaitingReply) { ShowFreeStoryRetry(); return; }
        if (scene.Turns >= 60)
        {
            ShowFreeStoryQuestion(L.T("free-scene.limit"), new[] { new Response("end", L.T("free-scene.end")) }, _ => FinishFreeStory());
            return;
        }
        if (scene.SuggestEnd)
        {
            ShowFreeStoryQuestion(L.T("free-scene.wrap"), new[]
            {
                new Response("continue", L.T("free-scene.continue")), new Response("end", L.T("free-scene.end")),
            }, answer =>
            {
                if (answer == "end") FinishFreeStory();
                else { scene.SuggestEnd = false; weeklyStoryDirty = true; }
            });
            return;
        }
        ChooseFreeStoryTarget();
    }

    private void OpenFreeStoryInput()
    {
        if (activeFreeStory is not { } story) return;
        string[] names = FreeStoryService.Present(story);
        if (!names.Contains(story.FreeScene!.Target)) story.FreeScene.Target = "";
        string target = story.Actors.FirstOrDefault(a => a.Name == story.FreeScene.Target)?.DisplayName ?? L.T("free-scene.all");
        string caption = L.F("free-scene.addressing", $"对 {target} 说");
        if (string.IsNullOrEmpty(story.FreeScene.Target))
            caption += " · " + string.Join(" / ", story.Actors.Where(a => names.Contains(a.Name)).Select(a => a.DisplayName));
        freeStoryMenu = new AiChatInputMenu(caption, SubmitFreeStory,
            onCancel: FinishFreeStory, onOpenSettings: ChooseFreeStoryTarget,
            conversationUiScale: config.ConversationUiScale, initialText: freeStoryDraft,
            onEndTrip: FinishFreeStory, settingsLabel: L.T("free-scene.target"), endLabel: L.T("free-scene.end"),
            onOpenSettingsDraft: draft => { freeStoryDraft = draft; ChooseFreeStoryTarget(); },
            portraitNpc: string.IsNullOrEmpty(story.FreeScene.Target) ? null
                : Game1.getCharacterFromName(story.FreeScene.Target, mustBeVillager: false, includeEventActors: true));
        Game1.activeClickableMenu = freeStoryMenu;
    }

    private void ChooseFreeStoryTarget()
    {
        if (activeFreeStory is not { } story) return;
        var names = FreeStoryService.Present(story);
        if (names.Length == 0) { FinishFreeStory(); return; }
        if (names.Length == 1)
        {
            story.FreeScene!.Target = names[0];
            weeklyStoryDirty = true;
            OpenFreeStoryInput();
            return;
        }
        var responses = new List<Response> { new("all", L.T("free-scene.all")) };
        responses.AddRange(names.Select((name,index) => new Response("actor" + index, story.Actors.First(a => a.Name == name).DisplayName)));
        responses.Add(new Response("end", L.T("free-scene.end")));
        ShowFreeStoryQuestion(L.T("free-scene.choose-target"), responses.ToArray(), answer =>
        {
            if (answer == "end") { FinishFreeStory(); return; }
            int index = answer.StartsWith("actor",StringComparison.Ordinal) && int.TryParse(answer[5..],out int selected)
                && selected >= 0 && selected < names.Length ? selected : -1;
            story.FreeScene!.Target = index >= 0 ? names[index] : "";
            weeklyStoryDirty = true;
            OpenFreeStoryInput();
        });
    }

    private void SubmitFreeStory(string text)
    {
        if (activeFreeStory is not { } story || story.FreeScene!.AwaitingReply || freeStoryTask is not null) return;
        text = WeeklyStoryPlan.LimitText(text, 800);
        if (text.Length == 0) return;
        freeStoryDraft = "";
        RecordFreeStoryEvent(story, "player", text, "talk", story.FreeScene.Target);
        story.FreeScene.Turns++;
        story.FreeScene.AwaitingReply = true;
        weeklyStoryDirty = true;
        PersistWeeklyStories(force: true);
        StartFreeStoryReply();
    }

    private void StartFreeStoryReply()
    {
        if (activeFreeStory is not { } story || freeStoryTask is not null) return;
        if (string.IsNullOrWhiteSpace(runtimeApiKey))
        { ShowFreeStoryRetry(L.T("free-scene.unavailable")); return; }
        try
        {
            var profiles = new List<FreeStoryActorContext>();
            string query = story.FreeScene!.Events.LastOrDefault()?.Text ?? "";
            foreach (string name in FreeStoryService.Present(story))
            {
                NPC? npc = Game1.getCharacterFromName(name, mustBeVillager: false, includeEventActors: true);
                if (npc is null) continue;
                string identity = ConversationPromptService.BuildSystemPrompt(new ConversationPromptService(Helper.DirectoryPath).Load().SystemRules,
                    RidgesideVillageCompatibility.BuildConversationIdentityPrompt(npc.displayName, name), ReadNpcPersonality(Game1.player,name));
                var snapshot = SocialPromptContext.Parse(BuildNpcGameSnapshot(npc,GetPlayerId()).SystemPrompt);
                profiles.Add(new(name, identity, "", System.Text.Json.JsonSerializer.Serialize(new {
                    worldFacts = snapshot.CurrentFacts, relationship = snapshot.RelationshipText,
                    commitmentStates = BuildMemoryCommitmentStates(socialStore.GetOrCreatePlayer(GetPlayerId()).GetOrCreateNpc(name), Game1.Date.TotalDays) }),
                    BuildSceneRecallCandidates(name, query), BuildRecentKnownEvents(name, query)));
            }
            CancelFreeStoryRequest();
            freeStoryCancellation = new CancellationTokenSource();
            string model = string.IsNullOrWhiteSpace(config.FreeStoryModel) ? currentAiProfile.Model : config.FreeStoryModel.Trim();
            freeStoryTask = new FreeStoryService(deepSeekClient).ReplyAsync(runtimeApiKey, model, story, profiles,
                $"{Game1.Date} {Game1.timeOfDay}; {Game1.currentLocation.NameOrUniqueName}; {DescribeWeeklyStoryWeather(Game1.currentLocation)}",
                freeStoryCancellation.Token, GetMemorySaveScope(memoryStore.GetOrCreate(GetPlayerId(), story.Inviter)));
            ShowFreeStoryQuestion(L.T("free-scene.wait"), new[]
            {
                new Response("cancel", L.T("free-scene.cancel")), new Response("end", L.T("free-scene.end")),
            }, answer => { CancelFreeStoryRequest(); if (answer == "end") FinishFreeStory(); });
        }
        catch (Exception ex)
        {
            CancelFreeStoryRequest();
            Monitor.Log("free_scene request preparation: " + ex.GetType().Name, LogLevel.Warn);
            ShowFreeStoryRetry();
        }
    }

    private void ShowFreeStoryRetry(string? message = null)
        => ShowFreeStoryQuestion(message ?? L.T("free-scene.failed"), new[]
        {
            new Response("retry", L.T("free-scene.retry")), new Response("end", L.T("free-scene.end")),
        }, answer => { if (answer == "retry") StartFreeStoryReply(); else FinishFreeStory(); });

    private void ShowFreeStoryQuestion(string text, Response[] responses, Action<string> callback)
    {
        if (activeFreeStory is not { } story) return;
        Game1.currentLocation.createQuestionDialogue(text, responses, (_, answer) =>
        {
            if (!ReferenceEquals(activeFreeStory,story)) return;
            if (!responses.Any(response => response.responseKey == answer) || freeStoryAfterMenu is not null) return;
            freeStoryAfterMenu = () => callback(answer);
        });
        freeStoryMenu = Game1.activeClickableMenu;
    }

    private void RecordFreeStoryEvent(WeeklyStoryPlan story, string actor, string text, string action, string target = "")
    {
        if (actor != "player" && action == "talk") RecordDreamConversation(actor,consumeReminder:story.FreeScene?.Turns > 0);
        FreeStoryEvent entry = FreeStoryService.Record(story,actor,text,action,target);
        string date = $"{Game1.Date} {Game1.timeOfDay}";
        foreach (string name in entry.Witnesses)
        {
            NpcConversationMemory memory = memoryStore.GetOrCreate(GetPlayerId(),name);
            string key = $"{story.StoryId}:scene:{entry.Sequence}";
            if (memory.Messages.Any(m => m.DedupeKey == key)) continue;
            string speaker = actor == "player" ? Game1.player.Name : story.Actors.FirstOrDefault(a => a.Name == actor)?.DisplayName ?? actor;
            memory.Messages.Add(new ConversationMemoryMessage { Role = "system", Content = $"[{story.Title}] {speaker} {(action == "leave" ? "[left]" : "[said]")}: {text}",
                Source = ConversationMemorySources.ModAction, GameDate = date, EventId = key, DedupeKey = key, LocationName = story.LocationId });
            memory.Messages = ConversationTimelinePolicy.RetainRecentMessages(memory);
            memory.LastDate = date;
        }
        memoryDirty = true;
        PersistMemory(force: false);
    }

    private void FinishFreeStory()
    {
        if (activeFreeStory is not { } story) return;
        activeFreeStory = null;
        CancelFreeStoryRequest();
        CloseFreeStoryMenu();
        freeStoryDisplayedLine = null;
        // Release player and NPCs before ancillary history/memory work can fail.
        weeklyStoryRunner.FinishFreeScene();
        story.FreeScene!.PendingLines.Clear();
        FreeStoryService.Forget(story.FreeScene);
        story.FreeScene.AwaitingReply = false;
        story.Status = WeeklyStoryStatus.Completed;
        story.CompletedDay = Game1.Date.TotalDays;
        story.SelectedEnding = "";
        var state = weeklyStoryStore.GetOrCreate(GetPlayerId());
        if (!state.History.Any(h => h.StoryId == story.StoryId))
            state.History.Add(StoryExperiencePolicy.Capture(story, story.CompletedDay));
        // Only publish that an interaction occurred, never the planner's imagined outcome.
        if (story.FreeScene.Events.Any(e => e.Actor == "player") && story.FreeScene.Events.Any(e => e.Actor != "player"))
            RecordTownEcho(new TownEchoEvent { PlayerId = GetPlayerId(), Source = story.Inviter,
                EventKey = story.StoryId, Kind = "proactive", Day = Game1.Date.TotalDays,
                Participants = story.Actors.Select(a => a.Name).ToList(), Place = story.LocationDisplayName });
        weeklyStoryDirty = true; memoryDirty = true;
        PersistWeeklyStories(force: true); PersistMemory(force: true);
        ShowHud(L.T("free-scene.finished"),HUDMessage.newQuest_type);
    }

    private void InterruptFreeStory(WeeklyStoryPlan story, string reason)
    {
        activeFreeStory = null;
        CancelFreeStoryRequest();
        CloseFreeStoryMenu();
        freeStoryDisplayedLine = null;
        story.Status = WeeklyStoryStatus.Ready;
        story.ArrivalDay = -1;
        story.RetryAfterMinutes = Game1.Date.TotalDays * 1440 + WeeklyStoryInvitationPolicy.Minutes(Game1.timeOfDay) + 60;
        weeklyStoryDirty = true;
        PersistWeeklyStories(force:true);
        Monitor.Log($"free_scene suspended story={story.StoryId} reason={reason}",LogLevel.Info);
    }
    private void CancelFreeStoryRequest()
    {
        freeStoryCancellation?.Cancel(); freeStoryCancellation?.Dispose(); freeStoryCancellation = null;
        // Observe errors from an upstream which ignores cancellation; its result cannot mutate the scene.
        if (freeStoryTask is { } old) _ = old.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        freeStoryTask = null;
    }
    private void CloseFreeStoryMenu()
    {
        freeStoryAfterMenu = null;
        var menu = freeStoryMenu; freeStoryMenu = null;
        if (menu is not null && ReferenceEquals(Game1.activeClickableMenu,menu)) Game1.exitActiveMenu();
        Game1.dialogueUp = false;
    }
}
