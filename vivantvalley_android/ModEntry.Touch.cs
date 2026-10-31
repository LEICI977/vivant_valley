using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Menus;
using VivantValley.Services;

namespace VivantValley;

public sealed partial class ModEntry
{
    private readonly PerScreen<MobileButtonState> mobileButton = new(() => new());
    private readonly MobileOnboarding mobileOnboarding = new();
    private double lastNativeTouch = double.NegativeInfinity;
    private double nextSettingsAttempt;
    private double suppressHudMouseUntil;
    private double setupSafeSince = -1;
    private double nextMobileDiagnostic;
    private string lastMobileDiagnostic = "";
    private bool mobileButtonDrawn;

    private void InitializeTouch(IModHelper helper)
    {
        MobileRuntime.Initialize(Monitor, config, () => Helper.WriteConfig(config));
        // Registered before the other save handlers so unrelated initialization cannot swallow onboarding.
        helper.Events.GameLoop.SaveLoaded += (_, _) =>
        {
            setupSafeSince = -1;
            nextMobileDiagnostic = 0;
            lastMobileDiagnostic = "";
            mobileButtonDrawn = false;
            mobileOnboarding.Queue(config.MobileOnboardingVersion, HasMobileProfile);
            if (mobileOnboarding.Pending) Monitor.Log("Android onboarding queued; waiting for a playable scene.", LogLevel.Info);
        };
        helper.Events.GameLoop.UpdateTicking += (_, _) => PollTouchInput();
        helper.Events.Display.MenuChanged += (_, e) =>
        {
            if (e.NewMenu is TouchMenu menu) menu.CancelTouch();
        };
        // Separate subscription keeps onboarding independent of failures in other update services.
        helper.Events.GameLoop.UpdateTicked += (_, _) => ProcessSettingsPrompt();
        helper.Events.Display.RenderedHud += (_, e) =>
        {
            if (CanShowMobileButton()) DrawMobileButton(e.SpriteBatch);
        };
    }

    private static Vector2 PointerUi() => new(Game1.getMouseX(true), Game1.getMouseY(true));
    private bool HasMobileProfile => !string.IsNullOrWhiteSpace(runtimeApiKey);
    private bool MobileConfigured => HasMobileProfile && config.MobileOnboardingVersion >= MobileOnboarding.CurrentVersion;
    private static MobileSceneState ReadMobileScene()
    {
        if (!Context.IsWorldReady) return default;
        return new MobileSceneState(
            WorldReady: true,
            Event: Game1.eventUp || Game1.currentLocation?.currentEvent is not null,
            Transition: Game1.fadeToBlack || Game1.globalFade || Game1.isWarping,
            Menu: Game1.activeClickableMenu is not null || Game1.dialogueUp,
            Minigame: Game1.currentMinigame is not null,
            Keyboard: MobileRuntime.KeyboardOpen,
            InputActive: MobileRuntime.InputActive,
            Frozen: Game1.freezeControls,
            UsingTool: Game1.player.UsingTool,
            InBed: Game1.player.isInBed.Value);
    }
    private static bool CanShowMobileButton() => ReadMobileScene().ShowButton;
    private static bool CanUseMobileButton() => ReadMobileScene().CanInteract;

    private bool HandleTouchButton(ButtonPressedEventArgs e)
    {
        if (!MobileRuntime.Enabled || e.Button != SButton.MouseLeft) return false;
        TouchCollection touches = MobileRuntime.Touches();
        if (Game1.activeClickableMenu is TouchMenu) return true;
        if (MobileRuntime.Contact is { Phase: ContactPhase.Pressed } contact)
            BeginMobileButton(contact.Id, MobileRuntime.TouchToUi(contact.Position));
        if (MobileRuntime.Now - lastNativeTouch <= 150 || touches.Count > 0 || MobileRuntime.Now < suppressHudMouseUntil)
        {
            // Suppress only our own HUD/menu contacts. Ordinary taps remain vanilla input.
            if (mobileButton.Value.Capture is not null || MobileRuntime.Now < suppressHudMouseUntil)
                Helper.Input.Suppress(SButton.MouseLeft);
            return true;
        }
        BeginMobileButton(-1, PointerUi());
        return true;
    }

    private void BeginMobileButton(int id, Vector2 point)
    {
        MobileButtonState state = mobileButton.Value;
        if (!CanUseMobileButton() || MobileRuntime.BlockUntilRelease || state.Capture is not null) return;
        if (!state.Gesture.Begin(id, point, MobileRuntime.ButtonBounds)) return;
        state.Pointer = id;
        state.Bounds = MobileRuntime.ButtonBounds;
        state.Location = Game1.currentLocation;
        state.Capture = new MobileHudCaptureMenu(DrawMobileButton, () => state.Gesture.Cancel());
        // An active menu shields Android's native world controls for the whole contact.
        // No NPC or tool methods are intercepted or replayed.
        Game1.activeClickableMenu = state.Capture;
        SuppressMobileContact();
    }

    private void SuppressMobileContact()
    {
        suppressHudMouseUntil = MobileRuntime.Now + 150;
        Helper.Input.Suppress(SButton.MouseLeft);
        Helper.Input.Suppress(SButton.MouseRight);
    }

    private void PollTouchInput()
    {
        MobileRuntime.Update();
        if (MobileRuntime.KeyboardOpen)
        {
            Game1.keyboardDispatcher.Subscriber = null;
            foreach (Keys key in Keyboard.GetState().GetPressedKeys())
                Helper.Input.Suppress(key.ToSButton());
        }
        MobileRuntime.CaptureTouches();
        TouchCollection touches = MobileRuntime.Touches();
        if (touches.Count > 0) lastNativeTouch = MobileRuntime.Now;
        bool nativeDown = touches.Any(t => t.State is TouchLocationState.Pressed or TouchLocationState.Moved);
        bool mouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;
        bool down = nativeDown || (MobileRuntime.Now - lastNativeTouch > 150 && mouseDown);
        if (!down) MobileRuntime.BlockUntilRelease = false;
        MobileButtonState state = mobileButton.Value;
        if (state.Capture is not null)
        {
            SuppressMobileContact();
            if (!ReferenceEquals(Game1.activeClickableMenu, state.Capture)) { ResetMobileButton(); return; }
            if (!Context.IsWorldReady || !MobileRuntime.InputActive || Game1.eventUp
                || Game1.currentLocation != state.Location || MobileRuntime.ButtonBounds != state.Bounds)
                state.Gesture.Cancel();
            if (touches.Count > 1) state.Gesture.Cancel();
            ContactUpdate contact = MobileRuntime.Contact;
            if (state.Pointer == -1 && !state.Released && touches.Count == 1
                && contact.Phase is ContactPhase.Pressed or ContactPhase.Moved or ContactPhase.Released
                && state.Gesture.AdoptNativePointer(contact.Id, MobileRuntime.TouchToUi(contact.Position)))
            {
                state.Pointer = contact.Id;
                MobileRuntime.TraceInput("HUD mouse joined native contact", nameof(ModEntry));
            }
            if (state.Pointer == -1)
            {
                state.Gesture.Move(-1, PointerUi());
                if (touches.Count > 0) state.Gesture.Cancel();
                if (!down) ReleaseMobileButton(-1, PointerUi());
            }
            else
            {
                if (contact.Id == state.Pointer && contact.Phase is ContactPhase.Pressed or ContactPhase.Moved or ContactPhase.Released)
                {
                    Vector2 ui = MobileRuntime.TouchToUi(contact.Position);
                    if (contact.Phase == ContactPhase.Released) ReleaseMobileButton(contact.Id, ui);
                    else state.Gesture.Move(contact.Id, ui);
                }
                else { state.Gesture.Cancel(); state.Released = true; state.Activate = false; }
            }
            // Keep the capture until every finger is up; a second finger must never leak into the world.
            state.CanFinish = !down;
            return;
        }
        if (Game1.activeClickableMenu is TouchMenu menu)
        {
            MobileRuntime.TraceInput("sample", menu.GetType().Name,
                $"touches={touches.Count}, mouse={mouseDown}, active={MobileRuntime.InputActive}, "
                + $"keyboard={MobileRuntime.KeyboardOpen}, blocked={MobileRuntime.BlockUntilRelease}, "
                + $"touchDisplay={TouchPanel.DisplayWidth}x{TouchPanel.DisplayHeight}, ui={Game1.uiViewport.Width}x{Game1.uiViewport.Height}");
            if (!MobileRuntime.InputActive) { menu.CancelTouch(); return; }
            menu.PollInput(touches, mouseDown, PointerUi());
            return;
        }
        if (MobileRuntime.Contact is { Phase: ContactPhase.Pressed } start)
            BeginMobileButton(start.Id, MobileRuntime.TouchToUi(start.Position));
        // Mouse starts use SMAPI's edge-triggered event; never treat a drag into the HUD as a new press.
    }

    private void ReleaseMobileButton(int id, Vector2 point)
    {
        MobileButtonState state = mobileButton.Value;
        if (state.Capture is null || state.Released || id != state.Pointer) return;
        state.Activate = state.Gesture.Release(id, point);
        state.Released = true;
        MobileRuntime.TraceInput("HUD release", nameof(ModEntry), $"activate={state.Activate}");
    }

    private void CompleteTouchInput()
    {
        if (!MobileRuntime.Enabled) return;
        MobileButtonState state = mobileButton.Value;
        if (state.Capture is null || !state.Released || !state.CanFinish) return;
        bool activate = state.Activate && ReferenceEquals(Game1.activeClickableMenu, state.Capture)
            && MobileRuntime.InputActive && state.Location == Game1.currentLocation;
        ResetMobileButton();
        // Release is consumed by the transparent menu in this frame before we replace it.
        MobileRuntime.BlockUntilRelease = true;
        if (activate && CanUseMobileButton()) OpenMobileChat();
    }

    private void ResetMobileButton()
    {
        MobileButtonState state = mobileButton.Value;
        if (state.Capture is not null && ReferenceEquals(Game1.activeClickableMenu, state.Capture))
            Game1.activeClickableMenu = null;
        state.Capture = null; state.Location = null; state.Gesture.Reset();
        state.Released = state.Activate = state.CanFinish = false;
    }

    private void DrawMobileButton(SpriteBatch b)
    {
        Rectangle bounds = mobileButton.Value.Capture is null ? MobileRuntime.ButtonBounds : mobileButton.Value.Bounds;
        bool pressed = mobileButton.Value.Gesture.IsDown && !mobileButton.Value.Gesture.Cancelled;
        bool enabled = mobileButton.Value.Capture is not null || CanUseMobileButton();
        IClickableMenu.drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            pressed ? Color.Gold : enabled ? Color.White : Color.LightGray);
        string label = L.T(screenStates.Value.HasPendingConversation ? "mobile.button.pending"
            : MobileConfigured ? "mobile.button.chat" : "mobile.button.settings");
        label = NativeDialogueFrame.FitSingleLine(label, Game1.smallFont, bounds.Width - 20);
        Vector2 size = Game1.smallFont.MeasureString(label);
        b.DrawString(Game1.smallFont, label, new Vector2(bounds.Center.X - size.X / 2, bounds.Center.Y - size.Y / 2), Game1.textColor);
        if (!mobileButtonDrawn)
        {
            mobileButtonDrawn = true;
            Monitor.Log($"Mobile AI button drawn: bounds={bounds}, viewport={Game1.uiViewport.Width}x{Game1.uiViewport.Height}.", LogLevel.Info);
        }
    }

    private List<NPC> NearbyMobileNpcs() => Game1.currentLocation.characters.Where(IsValidTarget)
        .Where(npc => npc.currentLocation == Game1.currentLocation
            && Vector2.DistanceSquared(npc.Tile, Game1.player.Tile) <= config.MaxTalkDistanceTiles * config.MaxTalkDistanceTiles)
        .OrderBy(npc => npc.displayName, StringComparer.CurrentCultureIgnoreCase).ToList();

    private void OpenMobileChat()
    {
        if (!MobileConfigured && !screenStates.Value.HasPendingConversation)
        {
            List<NPC> companions = NearbyMobileNpcs().Where(HasOwnCompanionTrip).ToList();
            if (companions.Count == 1) { OpenMessagePrompt(companions[0]); return; }
            if (companions.Count > 1)
            {
                Game1.activeClickableMenu = new MobileActionMenu(L.T("trip.end"), L.T("mobile.nearby.choose"),
                    companions.Select(npc => (npc.displayName, (Action)(() =>
                    {
                        if (Context.IsWorldReady && HasOwnCompanionTrip(npc)) OpenMessagePrompt(npc);
                    }))), OpenApiKeyPrompt);
                return;
            }
        }
        switch (MobileChatPolicy.Route(MobileConfigured, screenStates.Value.HasPendingConversation))
        {
            case MobileChatRoute.Settings:
                screenStates.Value.RequestApiKeyPrompt = true;
                ProcessSettingsPrompt();
                return;
            case MobileChatRoute.Pending:
                ShowHud(L.T("mobile.pending"), HUDMessage.newQuest_type);
                return;
        }
        List<NPC> npcs = NearbyMobileNpcs();
        if (MobileChatPolicy.Targets(npcs.Count) == MobileNpcRoute.Single) { StartManualChat(npcs[0]); return; }
        Game1.activeClickableMenu = new MobileActionMenu(L.T("mobile.button.chat"),
            L.T(npcs.Count == 0 ? "mobile.nearby.none" : "mobile.nearby.choose"),
            npcs.Select(npc => (npc.displayName, (Action)(() =>
            {
                if (!CanUseMobileButton()) return;
                if (screenStates.Value.HasPendingConversation) { ShowHud(L.T("mobile.pending"), HUDMessage.newQuest_type); return; }
                if (NearbyMobileNpcs().Contains(npc)) StartManualChat(npc);
                else OpenMobileChat();
            }))), OpenApiKeyPrompt);
    }

    private void ProcessSettingsPrompt()
    {
        if (!Context.IsWorldReady) return;
        ConversationScreenState state = screenStates.Value;
        MobileSceneState scene = ReadMobileScene();
        double now = MobileRuntime.Now;
        if (!scene.CanAutoSetup) setupSafeSince = -1;
        else if (setupSafeSince < 0) setupSafeSince = now;
        // Allow the loaded scene to settle before an automatic popup. A deliberate button tap need not wait.
        bool automatic = mobileOnboarding.CanAttempt(now, scene.CanAutoSetup && now - setupSafeSince >= 750);
        LogMobileEntryState(scene, now);
        if (!scene.CanInteract || now < nextSettingsAttempt || (!automatic && !state.RequestApiKeyPrompt)) return;
        try
        {
            OpenApiKeyPrompt();
            if (Game1.activeClickableMenu is not (HostedAccountMenu or AiProviderSettingsMenu))
                throw new InvalidOperationException("Settings menu did not become active.");
            state.RequestApiKeyPrompt = false;
            if (MobileRuntime.Enabled) mobileOnboarding.Shown();
            Monitor.Log("AI settings displayed successfully; no keyboard requested.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            nextSettingsAttempt = now + 5000;
            mobileOnboarding.Failed(now);
            Monitor.Log("AI settings could not open; retrying in 5 seconds: " + ex.GetType().Name, LogLevel.Warn);
        }
    }

    private void LogMobileEntryState(MobileSceneState scene, double now)
    {
        string state = $"button={(scene.ShowButton ? "visible" : scene.HiddenReason)}, clickable={scene.CanInteract}, "
            + $"onboarding={(mobileOnboarding.Pending ? scene.SetupWaitReason : "not-pending")}";
        if (now < nextMobileDiagnostic || state == lastMobileDiagnostic) return;
        nextMobileDiagnostic = now + 10000;
        lastMobileDiagnostic = state;
        Monitor.Log("Mobile entry state: " + state, LogLevel.Info);
    }

    private AiSettingsSaveResult ConfirmMobileConfiguration()
    {
        if (!HasMobileProfile) return new(false, L.T("mobile.config.required"));
        int old = config.MobileOnboardingVersion;
        try
        {
            config.MobileOnboardingVersion = MobileOnboarding.CurrentVersion;
            Helper.WriteConfig(config);
            mobileOnboarding.Complete();
            return new(true, L.T("mobile.config.saved"));
        }
        catch (Exception ex)
        {
            config.MobileOnboardingVersion = old;
            Monitor.Log("Android onboarding confirmation could not be saved: " + ex.GetType().Name, LogLevel.Warn);
            return new(false, L.T("mobile.save.failed"));
        }
    }

    private sealed class MobileButtonState
    {
        public HudButtonGesture Gesture { get; } = new();
        public MobileHudCaptureMenu? Capture;
        public GameLocation? Location;
        public Rectangle Bounds;
        public int Pointer;
        public bool Released, Activate, CanFinish;
    }
}
