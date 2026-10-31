using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace VivantValley.Services;

public sealed class WeeklyStoryRunner
{
    private const string GeneratedDialogueKey = "firstmod.StardewAIMemories.GeneratedDialogue";
    private readonly IMonitor monitor;
    private readonly NpcPresentationService presentationService;
    private readonly NpcScheduleRecoveryService recoveryService;
    private readonly NpcTilePathfinder pathfinder = new();
    private readonly Dictionary<string, RuntimeActor> actors = new(StringComparer.OrdinalIgnoreCase);
    private WeeklyStoryPlan? plan;
    private Action<WeeklyStoryChoiceOption?>? onCompleted;
    private Action<string>? onInterrupted;
    private WeeklyStoryChoiceOption? selectedChoice;
    private DialogueBox? activeVanillaDialogue;
    private bool vanillaDialogueOpen;
    private bool advanceAfterVanillaDialogue;
    private bool nativeChoiceOpen;
    private bool waitingForNativeChoiceClose;
    private int pendingChoicePage = -1;
    private int choiceMenuRevision;
    private int delayRemainingMs;
    private string navigatingActor = string.Empty;
    private NpcNavigationController? navigation;
    private WeeklyStoryBeat? pendingChoiceResponse;
    private bool executingTransientDialogue;
    private bool choiceResponseDelivered;
    private bool completing;
    private PlayerControlSnapshot originalPlayerControl;
    private PlayerControlSnapshot pendingPlayerControlRelease;
    private int playerControlReleaseFrames;
    private string playerControlReleaseSource = string.Empty;

    public WeeklyStoryRunner(IMonitor monitor, NpcPresentationService presentationService)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.presentationService = presentationService ?? throw new ArgumentNullException(nameof(presentationService));
        recoveryService = new NpcScheduleRecoveryService(monitor);
    }

    public bool IsRunning => plan is not null;

    internal bool OwnsReply(DialogueBox box)
        => IsRunning && vanillaDialogueOpen && ReferenceEquals(activeVanillaDialogue,box);

    public bool TryStart(
        WeeklyStoryPlan story,
        Action<WeeklyStoryChoiceOption?> completed,
        Action<string> interrupted,
        out string failure)
    {
        failure = string.Empty;
        if (IsRunning)
        {
            failure = "已有周剧情正在演出。";
            return false;
        }
        if (!WeeklyStoryPlaybackPolicy.TryValidatePlan(story, out failure))
            return false;
        if (!Context.IsWorldReady
            || Game1.currentLocation is null
            || !Game1.currentLocation.NameOrUniqueName.Equals(story.LocationId, StringComparison.OrdinalIgnoreCase))
        {
            failure = "玩家当前不在剧情地点。";
            return false;
        }

        var resolved = new List<(WeeklyStoryActor Definition, NPC Npc, Point StagingTile)>();
        int stagingIndex = 0;
        var reservedTiles = new HashSet<Point>();
        foreach (WeeklyStoryActor actor in story.Actors)
        {
            if (story.FormatVersion == 3 && story.FreeScene!.Departed.Contains(actor.Name)) continue;
            NPC? npc = Game1.getCharacterFromName(actor.Name, mustBeVillager: false, includeEventActors: true);
            if (npc is null
                || npc.IsMonster
                || npc.IsInvisible
                || npc.isSleeping.Value
                || npc.swimming.Value
                || npc.doingEndOfRouteAnimation.Value
                || npc.Sprite.CurrentAnimation is not null)
            {
                failure = $"无法加载剧情角色 {actor.DisplayName}。";
                return false;
            }
            int index = stagingIndex++;
            Point staging;
            bool found = story.FormatVersion == 3
                ? pathfinder.TryFindSceneTile(Game1.currentLocation, npc, Game1.player.TilePoint, index, reservedTiles, out staging)
                : (staging = FindStagingTile(npc, index)) != Point.Zero;
            if (!found)
            {
                failure = $"{actor.DisplayName} 附近没有安全的剧情站位。";
                return false;
            }
            reservedTiles.Add(staging);
            resolved.Add((actor, npc, staging));
        }

        this.plan = story;
        this.onCompleted = completed;
        this.onInterrupted = interrupted;
        selectedChoice = null;
        choiceResponseDelivered = false;
        completing = false;
        originalPlayerControl = CapturePlayerControl();
        story.CurrentBeatIndex = 0;
        story.Selections.Clear();
        story.SelectedEnding = string.Empty;
        story.SelectedChoiceId = string.Empty;
        story.SelectedChoiceMemory = string.Empty;
        story.Status = WeeklyStoryStatus.Running;
        actors.Clear();

        try
        {
            foreach ((WeeklyStoryActor definition, NPC npc, Point staging) in resolved)
            {
                var runtime = new RuntimeActor(
                    definition,
                    npc,
                    npc.currentLocation,
                    npc.Position,
                    npc.FacingDirection,
                    npc.ignoreScheduleToday,
                    npc.IsInvisible,
                    npc.Sprite.CurrentFrame);
                actors[npc.Name] = runtime;
                StopNpc(npc);
                npc.ignoreScheduleToday = true;
                if (!ReferenceEquals(npc.currentLocation, Game1.currentLocation) || npc.TilePoint != staging)
                    Game1.warpCharacter(npc, Game1.currentLocation, staging.ToVector2());
                npc.IsInvisible = story.FormatVersion != 3 || story.FreeScene!.Departed.Contains(npc.Name);
                if (story.FormatVersion == 3) npc.facePlayer(Game1.player);
            }
            Game1.player.Halt();
            Game1.player.freezePause = 120;
            Game1.player.temporarilyInvincible = true;
            return true;
        }
        catch (Exception ex)
        {
            failure = "准备剧情演员失败。";
            story.Status = WeeklyStoryStatus.Ready;
            monitor.Log($"weekly_story start failed: {ex}", LogLevel.Error);
            completing = true;
            FinishRuntime("start_failed", forcePlayerFree: false);
            return false;
        }
    }

    public void Update(GameTime time)
    {
        VerifyPendingPlayerControlRelease();
        if (plan is null || completing)
            return;
        if (!Context.IsWorldReady
            || Game1.currentLocation is null
            || !Game1.currentLocation.NameOrUniqueName.Equals(plan.LocationId, StringComparison.OrdinalIgnoreCase)
            || Utility.isFestivalDay()
            || Game1.isFestival())
        {
            Interrupt("scene_context_changed");
            return;
        }

        // Keep control paused between dialogue boxes so the staged actors and camera remain coherent.
        Game1.player.freezePause = Math.Max(Game1.player.freezePause, 120);
        Game1.player.temporarilyInvincible = true;
        Game1.gameTimeInterval = 0;
        if (plan.FormatVersion == 3) return;

        if (nativeChoiceOpen)
        {
            if (Game1.activeClickableMenu is DialogueBox)
                return;

            Interrupt("choice_closed");
            return;
        }
        if (waitingForNativeChoiceClose)
        {
            if (Game1.activeClickableMenu is DialogueBox)
                return;
            waitingForNativeChoiceClose = false;
            if (pendingChoicePage >= 0)
            {
                int page = pendingChoicePage;
                pendingChoicePage = -1;
                ShowChoice(plan.Beats[plan.CurrentBeatIndex], page);
                return;
            }
        }
        if (vanillaDialogueOpen)
        {
            if (Game1.activeClickableMenu is DialogueBox dialogue
                && ReferenceEquals(dialogue, activeVanillaDialogue))
            {
                return;
            }

            presentationService.End(activeVanillaDialogue?.characterDialogue?.speaker?.Name);
            activeVanillaDialogue = null;
            vanillaDialogueOpen = false;
            Game1.dialogueUp = originalPlayerControl.DialogueUp;
            bool shouldAdvance = advanceAfterVanillaDialogue;
            if (!shouldAdvance && selectedChoice is not null)
            {
                choiceResponseDelivered = true;
                if (plan.Selections.Count > 0)
                    plan.Selections[^1].ResponseDelivered = true;
            }
            advanceAfterVanillaDialogue = false;
            if (shouldAdvance)
                Advance();
            return;
        }
        if (navigation is not null)
        {
            if (!actors.TryGetValue(navigatingActor, out RuntimeActor? runtime))
            {
                navigation = null;
                Advance();
                return;
            }
            NpcNavigationStatus status = navigation.Update(runtime.Npc);
            if (status == NpcNavigationStatus.Moving)
                return;
            navigation = null;
            navigatingActor = string.Empty;
            Advance();
            return;
        }
        if (delayRemainingMs > 0)
        {
            delayRemainingMs -= Math.Max(1, time.ElapsedGameTime.Milliseconds);
            return;
        }
        if (pendingChoiceResponse is not null)
        {
            WeeklyStoryBeat response = pendingChoiceResponse;
            pendingChoiceResponse = null;
            executingTransientDialogue = true;
            try
            {
                Execute(response);
            }
            finally
            {
                executingTransientDialogue = false;
            }
            return;
        }
        if (plan.CurrentBeatIndex >= plan.Beats.Count)
        {
            Complete();
            return;
        }

        WeeklyStoryBeat nextBeat = plan.Beats[plan.CurrentBeatIndex];
        if (plan.FormatVersion == 2 && !WeeklyStoryBranchPolicy.Matches(nextBeat.When, WeeklyStoryBranchPolicy.State(plan)))
        {
            Advance();
            return;
        }
        Execute(nextBeat);
    }

    public void FinishFreeScene()
    {
        if (plan?.FormatVersion != 3) return;
        completing = true;
        FinishRuntime("free_scene_finished", forcePlayerFree: true);
    }

    public bool LeaveFreeSceneActor(string name)
    {
        if (plan?.FormatVersion != 3 || !actors.TryGetValue(name, out RuntimeActor? actor)) return false;
        try
        {
            StopNpc(actor.Npc);
            actor.Npc.IsInvisible = actor.OriginalInvisible;
            actor.Npc.ignoreScheduleToday = actor.OriginalIgnoreSchedule;
            if (actor.OriginalLocation is not null)
                Game1.warpCharacter(actor.Npc, actor.OriginalLocation, actor.OriginalPosition / Game1.tileSize);
            actor.Npc.Position = actor.OriginalPosition;
            actor.Npc.faceDirection(actor.OriginalFacing);
            if (!actor.OriginalIgnoreSchedule) recoveryService.Release(actor.Npc, false, "free_scene_left");
            actors.Remove(name);
            return true;
        }
        catch (Exception ex)
        {
            monitor.Log("free_scene departure failed: " + ex.Message, LogLevel.Warn);
            return false;
        }
    }

    public void Interrupt(string reason)
    {
        if (plan is null || completing)
            return;
        completing = true;
        Action<string>? callback = onInterrupted;
        FinishRuntime(reason, forcePlayerFree: false);
        try
        {
            callback?.Invoke(reason);
        }
        catch (Exception ex)
        {
            monitor.Log($"weekly_story interrupted callback failed reason={reason}: {ex}", LogLevel.Error);
        }
    }

    public bool TryEmergencyUnlock(out string result)
    {
        if (!Context.IsWorldReady)
        {
            result = "No save is loaded.";
            return false;
        }
        if (Game1.currentLocation?.currentEvent is not null || Game1.eventUp)
        {
            result = "An event currently owns player control; refusing to override it.";
            return false;
        }

        if (IsRunning)
            Interrupt("manual_unlock");
        if (Game1.activeClickableMenu is not null)
        {
            result = $"Close the active {Game1.activeClickableMenu.GetType().Name} menu before unlocking.";
            return false;
        }

        PlayerControlSnapshot released = CapturePlayerControl() with
        {
            CanMove = true,
            FreezeControls = false,
            FreezePause = 0,
            DialogueUp = false,
            ViewportFreeze = false,
            GameTimeInterval = Math.Max(1, Game1.gameTimeInterval),
        };
        SchedulePlayerControlRelease(released, "manual_unlock", verificationFrames: 3);
        result = "Weekly-story control state was cleared.";
        return true;
    }

    private void Execute(WeeklyStoryBeat beat)
    {
        switch (beat.Type)
        {
            case WeeklyStoryBeatTypes.Enter:
                if (TryGetActor(beat.Actor, out RuntimeActor entering))
                {
                    entering.Npc.IsInvisible = false;
                    StartMovement(entering, ResolveTargetTile(entering, beat.Target));
                }
                else
                {
                    Advance();
                }
                break;
            case WeeklyStoryBeatTypes.Move:
                if (TryGetActor(beat.Actor, out RuntimeActor moving))
                    StartMovement(moving, ResolveTargetTile(moving, beat.Target));
                else
                    Advance();
                break;
            case WeeklyStoryBeatTypes.Face:
                if (TryGetActor(beat.Actor, out RuntimeActor facing))
                    FaceTarget(facing.Npc, beat.Target);
                Advance();
                break;
            case WeeklyStoryBeatTypes.Animation:
                if (TryGetActor(beat.Actor, out RuntimeActor animated))
                {
                    animated.Npc.IsInvisible = false;
                    if (beat.Cue.Equals("look_away", StringComparison.Ordinal))
                    {
                        animated.Npc.faceDirection((animated.Npc.FacingDirection + 2) % 4);
                    }
                    else
                    {
                        presentationService.Begin(
                            animated.Npc.Name,
                            NpcPresentationPolicy.CreateForStoryCue(animated.Npc.Name, beat.Text, beat.Cue),
                            ownerMenu: null);
                    }
                }
                DelayAndAdvance(beat.DurationMs);
                break;
            case WeeklyStoryBeatTypes.Emote:
                if (TryGetActor(beat.Actor, out RuntimeActor emoting))
                    ShowEmote(emoting.Npc, beat.Cue);
                DelayAndAdvance(beat.DurationMs);
                break;
            case WeeklyStoryBeatTypes.Dialogue:
                if (WeeklyStoryPlaybackPolicy.IsUnexpectedPostChoiceDialogue(
                        plan!.FormatVersion, selectedChoice is not null, executingTransientDialogue))
                {
                    Interrupt("unexpected_post_choice_dialogue");
                    break;
                }
                ShowDialogue(beat);
                break;
            case WeeklyStoryBeatTypes.Pause:
                DelayAndAdvance(beat.DurationMs);
                break;
            case WeeklyStoryBeatTypes.Camera:
                FocusCamera(beat.Target);
                DelayAndAdvance(beat.DurationMs);
                break;
            case WeeklyStoryBeatTypes.Sound:
                PlayStorySound(beat.Cue);
                DelayAndAdvance(beat.DurationMs);
                break;
            case WeeklyStoryBeatTypes.Choice:
                ShowChoice(beat);
                break;
            case WeeklyStoryBeatTypes.Exit:
                if (TryGetActor(beat.Actor, out RuntimeActor exiting))
                    exiting.Npc.IsInvisible = true;
                Advance();
                break;
            case WeeklyStoryBeatTypes.End:
                Complete();
                break;
            default:
                Advance();
                break;
        }
    }

    private void ShowDialogue(WeeklyStoryBeat beat)
    {
        bool isTransient = executingTransientDialogue;
        if (!TryGetActor(beat.Actor, out RuntimeActor runtime))
        {
            Interrupt("dialogue_actor_missing");
            return;
        }
        runtime.Npc.IsInvisible = false;
        FaceTarget(runtime.Npc, string.IsNullOrWhiteSpace(beat.Target) ? "player" : beat.Target);
        NpcPresentationPlan presentation = NpcPresentationPolicy.CreateForStoryCue(
            runtime.Npc.Name,
            beat.Text,
            beat.Cue);
        if (ShowVanillaDialogue(runtime, beat, presentation, isTransient))
            return;
    }

    private bool ShowVanillaDialogue(
        RuntimeActor runtime,
        WeeklyStoryBeat beat,
        NpcPresentationPlan presentation,
        bool isTransient)
    {
        // Use the game's actual DialogueBox so story lines match vanilla
        // dialogue framing, typewriter timing, portraits, and input handling.
        string emotion = presentation.DialogueEmotionToken;
        string displayedText = emotion.Length == 0
            ? beat.Text
            : $"{beat.Text} {emotion}";
        try
        {
            Game1.DrawDialogue(new Dialogue(runtime.Npc, GeneratedDialogueKey, displayedText));
            DialogueBox? dialogue = Game1.activeClickableMenu as DialogueBox;
            activeVanillaDialogue = dialogue;
            vanillaDialogueOpen = dialogue is not null;
            advanceAfterVanillaDialogue = WeeklyStoryPlaybackPolicy.ShouldAdvanceAfterDialogue(isTransient);
            if (dialogue is null)
            {
                monitor.Log($"weekly_story dialogue box was not created for {runtime.Npc.Name}", LogLevel.Warn);
                advanceAfterVanillaDialogue = false;
                Interrupt("dialogue_not_displayed");
                return true;
            }

            dialogue.showTyping = true;
            presentationService.Begin(runtime.Npc.Name, presentation, dialogue);
            return true;
        }
        catch (Exception ex)
        {
            monitor.Log($"weekly_story dialogue failed for {runtime.Npc.Name}: {ex.Message}", LogLevel.Warn);
            activeVanillaDialogue = null;
            vanillaDialogueOpen = false;
            advanceAfterVanillaDialogue = false;
            Interrupt("dialogue_display_failed");
            return true;
        }
    }

    private void ShowChoice(WeeklyStoryBeat beat, int page = 0)
    {
        if (plan is null) return;
        WeeklyStoryPlan activePlan = plan;
        int choiceBeatIndex = plan.CurrentBeatIndex;
        int menuRevision = ++choiceMenuRevision;
        WeeklyStoryChoiceOption[] choices = plan.FormatVersion == 2
            ? WeeklyStoryBranchPolicy.AvailableOptions(plan, beat)
            : beat.Choices.Take(3).ToArray();
        if (choices.Length < 2)
        {
            Interrupt("invalid_choice_options");
            return;
        }
        RuntimeActor speaker = TryGetActor(beat.Actor, out RuntimeActor actor)
            ? actor
            : actors.Values.First();
        string prompt = string.IsNullOrWhiteSpace(beat.Text) ? L.T("text.0ea7782f89f9") : beat.Text;
        int pageCount = (choices.Length + WeeklyStoryPlaybackPolicy.ChoicePageSize - 1) / WeeklyStoryPlaybackPolicy.ChoicePageSize;
        page = Math.Clamp(page, 0, pageCount - 1);
        WeeklyStoryChoiceOption[] visible = WeeklyStoryPlaybackPolicy.ChoicePage(choices, page);
        // UI tokens cannot collide with generated story IDs.
        var responses = visible.Select((choice, index) => new Response("vv_option_" + index, choice.Text)).ToList();
        if (page > 0) responses.Add(new Response("vv_previous", L.T("text.c9b9ae7a6144")));
        if (page + 1 < pageCount) responses.Add(new Response("vv_next", L.T("text.8a8542f69648")));
        if (pageCount > 1) prompt += $"（{page + 1}/{pageCount}）";
        nativeChoiceOpen = true;
        waitingForNativeChoiceClose = false;
        Game1.currentLocation.createQuestionDialogue(
            prompt,
            responses.ToArray(),
            (Farmer who, string answer) =>
            {
                if (!ReferenceEquals(plan, activePlan) || completing || !nativeChoiceOpen
                    || choiceMenuRevision != menuRevision || plan.CurrentBeatIndex != choiceBeatIndex)
                    return;
                if ((answer == "vv_previous" && page > 0) || (answer == "vv_next" && page + 1 < pageCount))
                {
                    pendingChoicePage = page + (answer == "vv_next" ? 1 : -1);
                    nativeChoiceOpen = false;
                    waitingForNativeChoiceClose = true;
                    return;
                }
                WeeklyStoryChoiceOption? choice = WeeklyStoryPlaybackPolicy.ResolveChoicePageAnswer(choices, page, answer);
                if (choice is null) return;

                nativeChoiceOpen = false;
                waitingForNativeChoiceClose = true;
                selectedChoice = choice;
                choiceResponseDelivered = false;
                if (plan.FormatVersion == 2)
                    WeeklyStoryBranchPolicy.RecordSelection(plan, beat, choice);
                presentationService.End(speaker.Npc.Name);
                Advance();
                if (choice.ResponseText.Length > 0 && TryGetActor(choice.ResponseActor, out RuntimeActor responder))
                    InsertChoiceResponse(responder, choice);
            });
    }

    private void InsertChoiceResponse(RuntimeActor responder, WeeklyStoryChoiceOption choice)
    {
        pendingChoiceResponse = new WeeklyStoryBeat
        {
            Type = WeeklyStoryBeatTypes.Dialogue,
            Actor = responder.Npc.Name,
            Target = "player",
            Cue = choice.Outcome is "romantic" ? "love" : choice.Outcome is "playful" ? "happy" : string.Empty,
            Text = choice.ResponseText,
        };
    }

    private void StartMovement(RuntimeActor actor, Point destination)
    {
        actor.Npc.IsInvisible = false;
        if (destination == Point.Zero
            || actor.Npc.TilePoint == destination
            || !pathfinder.TryFindPath(
                Game1.currentLocation,
                actor.Npc,
                actor.Npc.TilePoint,
                destination,
                out IReadOnlyList<Point> path))
        {
            Advance();
            return;
        }
        navigation = new NpcNavigationController();
        navigation.Start(path, actor.Npc);
        navigatingActor = actor.Npc.Name;
    }

    private Point ResolveTargetTile(RuntimeActor actor, string? target)
    {
        string key = target?.Trim().ToLowerInvariant() ?? string.Empty;
        Point player = Game1.player.TilePoint;
        Point preferred = key switch
        {
            "player_left" or "left" => player + new Point(-2, 0),
            "player_right" or "right" => player + new Point(2, 0),
            "center" or "player" or "" => player + FormationOffset(actor),
            _ when actors.TryGetValue(target ?? string.Empty, out RuntimeActor? other) => other.Npc.TilePoint + FormationOffset(actor),
            _ => player + FormationOffset(actor),
        };
        if (pathfinder.TryFindPath(Game1.currentLocation, actor.Npc, actor.Npc.TilePoint, preferred, out _))
            return preferred;
        return pathfinder.TryFindSafeFollowTile(Game1.currentLocation, actor.Npc, player, 2, 5, out Point safe)
            ? safe
            : actor.Npc.TilePoint;
    }

    private Point FindStagingTile(NPC npc, int index)
    {
        Point player = Game1.player.TilePoint;
        int minimum = Game1.currentLocation.IsOutdoors ? 5 + Math.Min(index, 2) : 2 + Math.Min(index, 2);
        return pathfinder.TryFindSafeFollowTile(Game1.currentLocation, npc, player, minimum, minimum + 3, out Point safe)
            ? safe
            : Point.Zero;
    }

    private Point FormationOffset(RuntimeActor actor)
    {
        int index = actors.Values.ToList().FindIndex(item => item.Npc.Name.Equals(actor.Npc.Name, StringComparison.OrdinalIgnoreCase));
        return index switch
        {
            0 => new Point(-2, 0),
            1 => new Point(2, 0),
            _ => new Point(0, -2),
        };
    }

    private void FaceTarget(NPC npc, string? target)
    {
        if (!string.IsNullOrWhiteSpace(target)
            && actors.TryGetValue(target, out RuntimeActor? other))
        {
            npc.faceGeneralDirection(other.Npc.Position, 0, opposite: false, useTileCalculations: false);
            return;
        }
        if (target?.Equals("left", StringComparison.OrdinalIgnoreCase) == true)
            npc.faceDirection(3);
        else if (target?.Equals("right", StringComparison.OrdinalIgnoreCase) == true)
            npc.faceDirection(1);
        else
            npc.facePlayer(Game1.player);
    }

    private static void ShowEmote(NPC npc, string? cue)
    {
        int? emote = cue?.Trim().ToLowerInvariant() switch
        {
            "happy" or "cheer" or "wave" => 32,
            "sad" => 28,
            "love" or "shy" => 20,
            "surprised" => 16,
            _ => null,
        };
        if (emote.HasValue && !npc.IsEmoting)
            npc.doEmote(emote.Value, nextEventCommand: false);
    }

    private void FocusCamera(string? target)
    {
        Vector2 focus = Game1.player.Position;
        if (!string.IsNullOrWhiteSpace(target)
            && actors.TryGetValue(target, out RuntimeActor? actor))
        {
            focus = actor.Npc.Position;
        }
        int mapWidth = Game1.currentLocation.Map.Layers[0].LayerWidth * Game1.tileSize;
        int mapHeight = Game1.currentLocation.Map.Layers[0].LayerHeight * Game1.tileSize;
        Game1.viewportFreeze = true;
        Game1.viewport.X = Math.Clamp((int)focus.X - Game1.viewport.Width / 2, 0, Math.Max(0, mapWidth - Game1.viewport.Width));
        Game1.viewport.Y = Math.Clamp((int)focus.Y - Game1.viewport.Height / 2, 0, Math.Max(0, mapHeight - Game1.viewport.Height));
    }

    private static void PlayStorySound(string? cue)
    {
        string sound = cue?.Trim().ToLowerInvariant() switch
        {
            "surprise" => "discoverMineral",
            "mystery" => "wand",
            "storm" => "thunder",
            "footstep" => "stoneStep",
            _ => "dwop",
        };
        Game1.playSound(sound);
    }

    private void DelayAndAdvance(int milliseconds)
    {
        delayRemainingMs = Math.Clamp(milliseconds, 150, 4000);
        Advance();
    }

    private void Advance()
    {
        if (plan is not null)
            plan.CurrentBeatIndex++;
    }

    private void Complete()
    {
        if (plan is null || completing)
            return;
        if (!WeeklyStoryPlaybackPolicy.CanComplete(plan, selectedChoice, choiceResponseDelivered))
        {
            Interrupt("incomplete_choice_or_response");
            return;
        }
        completing = true;
        Action<WeeklyStoryChoiceOption?>? callback = onCompleted;
        WeeklyStoryChoiceOption? choice = selectedChoice;
        FinishRuntime("completed", forcePlayerFree: true);
        try
        {
            callback?.Invoke(choice);
        }
        catch (Exception ex)
        {
            monitor.Log($"weekly_story completed callback failed: {ex}", LogLevel.Error);
        }
    }

    private void SkipScene(string reason)
    {
        Interrupt("player_skipped:" + reason);
    }

    private void FinishRuntime(string source, bool forcePlayerFree)
    {
        try
        {
            DismissMenu();
        }
        catch (Exception ex)
        {
            monitor.Log($"weekly_story menu cleanup failed source={source}: {ex}", LogLevel.Error);
        }

        try
        {
            RestoreActors(source);
        }
        catch (Exception ex)
        {
            monitor.Log($"weekly_story actor cleanup failed source={source}: {ex}", LogLevel.Error);
        }
        finally
        {
            PlayerControlSnapshot target = forcePlayerFree
                ? originalPlayerControl with
                {
                    CanMove = true,
                    FreezeControls = false,
                    FreezePause = 0,
                    DialogueUp = false,
                }
                : originalPlayerControl;
            SchedulePlayerControlRelease(target, source, forcePlayerFree ? 3 : 0);
            ClearRuntime();
        }
    }

    private void DismissMenu()
    {
        DialogueBox? dialogue = activeVanillaDialogue;
        activeVanillaDialogue = null;
        vanillaDialogueOpen = false;
        advanceAfterVanillaDialogue = false;
        if (dialogue is not null && ReferenceEquals(Game1.activeClickableMenu, dialogue))
            dialogue.exitThisMenuNoSound();
        if (nativeChoiceOpen || waitingForNativeChoiceClose)
        {
            nativeChoiceOpen = false;
            waitingForNativeChoiceClose = false;
            if (Game1.activeClickableMenu is DialogueBox)
                Game1.activeClickableMenu.exitThisMenuNoSound();
        }
    }

    private void RestoreActors(string source)
    {
        navigation = null;
        navigatingActor = string.Empty;
        presentationService.Clear();
        foreach (RuntimeActor actor in actors.Values)
        {
            NPC npc = actor.Npc;
            try
            {
                StopNpc(npc);
                npc.IsInvisible = actor.OriginalInvisible;
                npc.Sprite.CurrentFrame = actor.OriginalFrame;
                npc.Sprite.UpdateSourceRect();
                npc.ignoreScheduleToday = actor.OriginalIgnoreSchedule;
                if (actor.OriginalLocation is not null
                    && !ReferenceEquals(npc.currentLocation, actor.OriginalLocation))
                {
                    Game1.warpCharacter(npc, actor.OriginalLocation, actor.OriginalPosition / Game1.tileSize);
                }
                npc.Position = actor.OriginalPosition;
                npc.IsInvisible = actor.OriginalInvisible;
                npc.faceDirection(actor.OriginalFacing);
                if (!actor.OriginalIgnoreSchedule)
                    recoveryService.Release(npc, originalIgnoreScheduleToday: false, $"weekly_story_{source}");
            }
            catch (Exception ex)
            {
                monitor.Log($"weekly_story restore failed npc={npc.Name}: {ex.Message}", LogLevel.Warn);
            }
        }
    }

    private static void StopNpc(NPC npc)
    {
        npc.controller = null;
        npc.temporaryController = null;
        npc.queuedSchedulePaths.Clear();
        npc.Halt();
    }

    private bool TryGetActor(string name, out RuntimeActor actor)
        => actors.TryGetValue(name ?? string.Empty, out actor!);

    private void ClearRuntime()
    {
        plan = null;
        onCompleted = null;
        onInterrupted = null;
        selectedChoice = null;
        choiceResponseDelivered = false;
        actors.Clear();
        navigation = null;
        navigatingActor = string.Empty;
        delayRemainingMs = 0;
        pendingChoiceResponse = null;
        executingTransientDialogue = false;
        activeVanillaDialogue = null;
        vanillaDialogueOpen = false;
        advanceAfterVanillaDialogue = false;
        nativeChoiceOpen = false;
        waitingForNativeChoiceClose = false;
        completing = false;
        pendingChoicePage = -1;
    }

    private static PlayerControlSnapshot CapturePlayerControl()
        => new(
            Game1.player.CanMove,
            Game1.freezeControls,
            Game1.player.freezePause,
            Game1.player.temporarilyInvincible,
            Game1.gameTimeInterval,
            Game1.viewportFreeze,
            Game1.dialogueUp);

    private void SchedulePlayerControlRelease(
        PlayerControlSnapshot target,
        string source,
        int verificationFrames)
    {
        pendingPlayerControlRelease = target;
        playerControlReleaseFrames = Math.Max(0, verificationFrames);
        playerControlReleaseSource = source;
        ApplyPlayerControlRelease(target, source, "immediate");
    }

    private void VerifyPendingPlayerControlRelease()
    {
        if (playerControlReleaseFrames <= 0 || !Context.IsWorldReady)
            return;

        playerControlReleaseFrames--;
        bool hasActiveEvent = Game1.eventUp || Game1.currentLocation?.currentEvent is not null;
        if (WeeklyStoryPlaybackPolicy.CanVerifyPlayerRelease(
                Game1.activeClickableMenu is not null,
                hasActiveEvent))
        {
            ApplyPlayerControlRelease(pendingPlayerControlRelease, playerControlReleaseSource, "verification");
        }

        if (playerControlReleaseFrames == 0)
            playerControlReleaseSource = string.Empty;
    }

    private void ApplyPlayerControlRelease(PlayerControlSnapshot target, string source, string phase)
    {
        Game1.player.Halt();
        Game1.player.temporarilyInvincible = target.TemporarilyInvincible;
        Game1.gameTimeInterval = target.GameTimeInterval;
        Game1.viewportFreeze = target.ViewportFreeze;
        Game1.dialogueUp = target.DialogueUp;
        Game1.player.freezePause = target.FreezePause;
        Game1.freezeControls = target.FreezeControls;
        Game1.player.CanMove = target.CanMove;
        monitor.Log(
            $"weekly_story control_release source={source} phase={phase} "
            + $"canMove={Game1.player.CanMove} freezeControls={Game1.freezeControls} "
            + $"freezePause={Game1.player.freezePause} dialogueUp={Game1.dialogueUp} "
            + $"menu={Game1.activeClickableMenu?.GetType().Name ?? "none"}",
            LogLevel.Debug);
    }

    private readonly record struct PlayerControlSnapshot(
        bool CanMove,
        bool FreezeControls,
        int FreezePause,
        bool TemporarilyInvincible,
        int GameTimeInterval,
        bool ViewportFreeze,
        bool DialogueUp);

    private sealed record RuntimeActor(
        WeeklyStoryActor Definition,
        NPC Npc,
        GameLocation? OriginalLocation,
        Vector2 OriginalPosition,
        int OriginalFacing,
        bool OriginalIgnoreSchedule,
        bool OriginalInvisible,
        int OriginalFrame);
}
