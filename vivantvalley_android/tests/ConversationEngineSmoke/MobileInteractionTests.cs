using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input.Touch;
using VivantValley.Menus;
using VivantValley.Services;

internal static class MobileInteractionTests
{
    public static void Run()
    {
        CheckNativeTouchFrames();
        CheckFormTextStorage();
        CheckInlineEditing();
        var focus = new MobileFocusState();
        Check(focus.Observe(false) && focus.Observe(false), "Uninitialized host focus must not disable the mobile entry forever.");
        Check(focus.Observe(true) && !focus.Observe(false) && focus.Observe(true), "Observed background/foreground transitions were ignored.");
        var scene = new MobileSceneState(true, false, false, false, false, false, true, false, false, false);
        Check(scene.ShowButton && scene.CanInteract && scene.CanAutoSetup, "Fresh mobile scene must expose settings without a config switch.");
        var busy = scene with { Frozen = true };
        Check(busy.ShowButton && !busy.CanInteract && !busy.CanAutoSetup, "Frozen controls must disable actions without hiding the entry.");
        busy = scene with { UsingTool = true };
        Check(busy.ShowButton && !busy.CanInteract, "Tool animation must not remove the HUD.");
        busy = scene with { InBed = true };
        Check(busy.ShowButton && busy.CanInteract && !busy.CanAutoSetup, "Bed state must only defer automatic setup.");
        busy = scene with { InputActive = false };
        Check(busy.ShowButton && !busy.CanInteract, "Background focus must not control HUD visibility.");
        foreach (var hidden in new[] { scene with { WorldReady = false }, scene with { Event = true },
            scene with { Transition = true }, scene with { Menu = true }, scene with { Minigame = true }, scene with { Keyboard = true } })
            Check(!hidden.ShowButton && !hidden.CanInteract && !hidden.CanAutoSetup && hidden.HiddenReason != "none",
                "Event/menu/loading must block entry and report why.");
        Check(typeof(VivantValley.ModConfig).GetProperty("MobileUiPreview") is null, "Mobile build must not expose an opt-in UI switch.");

        var button = new HudButtonGesture();
        Rectangle buttonBounds = new(100, 100, 180, 64);
        Vector2 inside = new(120, 120), outside = new(80, 120);
        Check(!button.Begin(1, outside, buttonBounds), "Outside press captured gameplay.");
        Check(!button.Release(1, inside), "Drag into button activated without a press.");
        Check(button.Begin(1, inside, buttonBounds), "HUD press did not start.");
        Check(!button.Begin(-1, inside, buttonBounds), "Simulated mouse duplicated native touch.");
        Check(button.Release(1, inside) && !button.Release(1, inside), "HUD release must activate exactly once.");
        button.Begin(2, inside, buttonBounds); button.Move(2, outside); button.Move(2, inside);
        Check(button.IsDown && button.Cancelled, "Cancelled drag lost capture or revived on re-entry.");
        Check(!button.Release(2, inside), "Drag out and back activated HUD.");
        button.Begin(3, inside, buttonBounds); button.Move(4, inside);
        Check(!button.Release(3, inside), "Pointer change activated HUD.");
        button.Begin(5, inside, buttonBounds); button.Cancel();
        Check(button.IsDown && !button.Release(5, inside), "Multi-touch/focus cancellation leaked or clicked.");
        button.Begin(6, inside, buttonBounds); button.Reset();
        Check(!button.Release(6, inside), "Warp/resize reset activated HUD.");

        button.Begin(-1, inside, buttonBounds);
        Check(button.AdoptNativePointer(10, inside), "Mouse-first Android contact was not adopted.");
        Check(!button.Release(-1, inside), "Duplicate mouse release activated native contact.");
        Check(button.Release(10, inside) && !button.Release(10, inside), "Single mouse-first tap required a second touch.");
        button.Begin(-1, inside, buttonBounds); button.Move(-1, outside);
        Check(button.AdoptNativePointer(12, inside) && !button.Release(12, inside), "Native adoption revived a cancelled drag.");
        button.Begin(13, inside, buttonBounds);
        Check(!button.AdoptNativePointer(14, inside), "Second native finger replaced an existing contact.");
        Check(button.Release(13, inside), "Native-first tap was lost.");

        Check(MobileChatPolicy.Route(false, false) == MobileChatRoute.Settings, "Missing profile must route to settings.");
        Check(MobileChatPolicy.Route(true, false) == MobileChatRoute.Nearby, "Configured player cannot choose NPCs.");
        Check(MobileChatPolicy.Route(true, true) == MobileChatRoute.Pending, "HUD must not cancel a pending reply.");
        Check(MobileChatPolicy.Route(false, true) == MobileChatRoute.Pending, "Settings must not interrupt a pending reply.");

        Check(MobileChatPolicy.Targets(0) == MobileNpcRoute.None, "No NPC must offer settings and a proximity hint.");
        Check(MobileChatPolicy.Targets(1) == MobileNpcRoute.Single, "Single NPC must open directly.");
        Check(MobileChatPolicy.Targets(2) == MobileNpcRoute.Choose && MobileChatPolicy.Targets(20) == MobileNpcRoute.Choose,
            "Crowded scenes must require an explicit NPC choice.");
        var onboarding = new MobileOnboarding();
        onboarding.Queue(0, true);
        Check(onboarding.Pending, "Old credentials incorrectly bypassed first Android setup.");
        Check(!onboarding.CanAttempt(0, false) && onboarding.CanAttempt(0, true), "Setup ignored scene safety.");
        onboarding.Failed(100);
        Check(!onboarding.CanAttempt(5099, true) && onboarding.CanAttempt(5100, true), "Failed setup lost retry or retried every frame.");
        onboarding.Shown();
        Check(!onboarding.Pending, "Successful display still pending.");
        onboarding.LeaveSave(); onboarding.Queue(0, false);
        Check(!onboarding.Pending, "Skipping setup repeated within the same session.");
        var nextLaunch = new MobileOnboarding(); nextLaunch.Queue(0, false);
        Check(nextLaunch.Pending, "Unfinished setup did not return next launch.");
        nextLaunch.Complete(); nextLaunch.Queue(MobileOnboarding.CurrentVersion, true);
        Check(!nextLaunch.Pending, "Completed setup re-opened.");
        var completed = new MobileOnboarding(); completed.Queue(MobileOnboarding.CurrentVersion, true);
        Check(!completed.Pending, "Persisted completion not respected.");
        completed.Queue(MobileOnboarding.CurrentVersion, false);
        Check(completed.Pending, "Missing credentials did not restore setup.");

        var tap = new TouchTapGesture();
        Check(!tap.Release(0, 0), "Menu-opening release activated a button without a press.");
        tap.Press(10, 10); tap.Cancel();
        Check(!tap.Release(10, 10), "Cancelled multi-touch activated a menu button.");
        tap.Press(10, 10); tap.Move(10, 80); tap.Move(10, 10);
        Check(!tap.Release(10, 10), "Scroll ending over a button activated it.");
        tap.Press(10, 10);
        Check(tap.Release(11, 11) && !tap.Release(11, 11), "Tap must activate exactly once.");

        foreach (Point size in new[] { new Point(360, 640), new Point(640, 360), new Point(800, 360),
                     new Point(1280, 720), new Point(2400, 1080), new Point(1024, 768), new Point(400, 300) })
        foreach (int control in new[] { 54, 64, 96 })
        {
            Rectangle viewport = new(0, 0, size.X, size.Y);
            Rectangle safe = MobileLayout.Inset(viewport, 20);
            foreach (float scale in new[] { .75f, 1f, 1.75f, float.NaN })
            foreach (int offset in new[] { -100, 0, 160, 99999 })
                Check(safe.Contains(MobileLayout.HudButton(safe, control, scale, offset, offset)), "HUD button escaped safe area.");
            var personalityComposer = MobileLayout.Composer(safe, control, 140, personality: true);
            foreach (Rectangle rect in new[] {personalityComposer.Input, personalityComposer.Send, personalityComposer.Settings, personalityComposer.Close})
                Check(!rect.Intersects(personalityComposer.Personality), "Personality button overlaps a composer control.");
            Check(personalityComposer.Outer.Contains(personalityComposer.Personality), "Personality button escaped composer.");
            var composer = MobileLayout.Composer(safe, control, 140);
            Check(personalityComposer.Outer == composer.Outer && personalityComposer.Input == composer.Input,
                "Compact personality action added a row or moved the composer input.");
            Check(personalityComposer.Personality.Y == personalityComposer.Settings.Y
                && personalityComposer.Personality.Height == personalityComposer.Settings.Height
                && personalityComposer.Personality.Right < personalityComposer.Settings.Left,
                "Personality action must sit left of Settings on the same row.");

            foreach (Rectangle rect in new[] { composer.Outer, composer.Input, composer.Send, composer.Settings, composer.Close })
                Check(safe.Contains(rect), $"Composer outside safe area: {size}, {control}, {rect}.");
            Check(!composer.Input.Intersects(composer.Send), "Input overlaps send button.");
            Check(!composer.Close.Intersects(composer.Settings), "Close overlaps settings.");
            Check(composer.Close.Width <= 48 && composer.Close.Height <= 48,
                "Composer close icon became an oversized panel.");
            foreach (int count in new[] { 1, 2, 3 })
            {
                Rectangle outer = MobileLayout.Center(safe, 980);
                var form = MobileLayout.Form(outer, control, count);
                Check(outer.Contains(form.Body) && outer.Contains(form.Close), "Form body/close escaped safe bounds.");
                foreach (Rectangle action in form.Actions)
                    Check(outer.Contains(action) && !action.Intersects(form.Body), "Fixed action escaped/overlapped scrolling form.");
                Check(form.Actions.Distinct().Count() == count, "Form actions overlap exactly.");
            }
            var dialogue = MobileDialogueLayout.Create(safe);
            Check(safe.Contains(dialogue.OuterBounds) && dialogue.OuterBounds.Contains(dialogue.ContentBounds), "Dialogue escaped safe bounds.");
            Check(dialogue.OuterBounds.Contains(dialogue.NamePlateBounds), "NPC name escaped dialogue.");


        }

        Rectangle clip = MobileLayout.ToDevice(new Rectangle(20, 30, 100, 50), new Rectangle(0, 0, 800, 400), new Rectangle(10, 20, 1600, 800));
        Check(clip == new Rectangle(50, 80, 200, 100), "UI-to-device clipping conversion is wrong.");
        Check(MobileLayout.ClampScroll(10000, 1200, 300) == 900 && MobileLayout.ClampScroll(100, 100, 300) == 0,
            "Scroll clamp hides last controls or retains stale scroll after resize.");

        var input = new SingleLineTextBuffer(20) { Text = "你好 hello 👩‍🌾" };
        Check(input.Text == "你好 hello 👩‍🌾", "Native committed text lost Unicode characters.");
        input.Text = "line one\r\nline two";
        Check(!input.Text.Contains('\r') && !input.Text.Contains('\n'), "Native paste bypassed single-line normalization.");
        Console.WriteLine("Mobile HUD routing, gesture, onboarding, layout and committed-text tests passed.");
    }
    private static void CheckInlineEditing()
    {
        // Native TextChanged sends complete replacement snapshots, including an empty value.
        // Do not append changes or restore the opening draft after deletion.
        var box = MobileTextField.Create(null!, null!, Color.Black, 320, true);
        foreach (string snapshot in new[] { "password12", "password1", "pass", "", "m", "mm", "m", "",
                     "ni", "nihao", "你好", "你", "", "a👩‍🌾b", "a👩‍🌾", "a", "" })
        {
            box.Text = new SingleLineTextBuffer(box.textLimit) { Text = snapshot }.Text;
            Check(box.Text == snapshot, "Live deletion/composition snapshot changed or restored old password text.");
        }
        Rectangle viewport = new(0, 0, 800, 400), surface = new(0, 0, 1600, 800);
        Rectangle field = new(100, 300, 500, 64);
        Check(MobileLayout.InlineEditor(field, viewport, surface, surface) == new Rectangle(200, 600, 1000, 128),
            "Inline editor did not cover the game field at 2x scale.");
        Rectangle visible = new(20, 10, 1560, 430);
        Rectangle raised = MobileLayout.InlineEditor(field, viewport, surface, visible);
        Check(raised == new Rectangle(200, 312, 1000, 128) && visible.Contains(raised),
            "Keyboard covered the active inline field.");
        foreach (Rectangle area in new[] { visible, new Rectangle(0, 0, 300, 100), surface })
        foreach (Rectangle anchor in new[] { field, new Rectangle(-100, -100, 1500, 1000), new Rectangle(700, 390, 100, 64) })
            Check(area.Contains(MobileLayout.InlineEditor(anchor, viewport, surface, area)),
                "Rotation or small visible area placed the inline field outside the screen.");
    }

    private static void CheckFormTextStorage()
    {
        // Exercise the real game's Text setter with a deterministic, GPU-free font.
        var chars = Enumerable.Range(32, 95).Select(c => (char)c).ToList();
        var glyphs = chars.Select(c => new Rectangle(0, 0, c == 'm' ? 14 : 10, 16)).ToList();
        var font = new SpriteFont(null!, glyphs, glyphs, chars, 16, 0,
            chars.Select(c => new Vector3(0, c == 'm' ? 14 : 10, 0)).ToList(), '?');
        const string email = "test@example.com";
        var legacy = new StardewValley.Menus.TextBox(null!, null!, font, Color.Black) { Width = 185, textLimit = 320 };
        legacy.Text = email;
        Check(legacy.Text == "test@example.co", "Regression fixture did not reproduce width-based loss of the final m.");
        foreach (bool password in new[] { false, true })
        foreach (int width in new[] { 0, 40, 185, 900 })
        {
            var box = MobileTextField.Create(null!, font, Color.Black, 320, password);
            box.Width = width;
            foreach (string value in new[] { email, "m", "mm", new string('m', 250) })
            {
                // Same normalization and assignment path as a native editor commit.
                box.Text = new SingleLineTextBuffer(box.textLimit) { Text = value }.Text;
                Check(box.Text == value, "Form width or password masking truncated a committed value.");
            }
            box.Text = new SingleLineTextBuffer(box.textLimit) { Text = new string('x', 321) }.Text;
            Check(box.Text.Length == 320, "Removing width truncation removed the explicit character limit.");
        }
    }

    private static void CheckNativeTouchFrames()
    {
        static TouchCollection Frame(int id, TouchLocationState state, float y = 10)
            => new(new[] { new TouchLocation(id, state, new Vector2(10, y)) });
        var tracker = new NativeTouchTracker();
        var tap = new TouchTapGesture();
        var first = tracker.Observe(Frame(1, TouchLocationState.Moved));
        Check(first.Phase == ContactPhase.Pressed, "A press consumed by another GetState reader disabled the menu.");
        tap.Press(first.Position.X, first.Position.Y);
        Check(tracker.Observe(Frame(1, TouchLocationState.Moved)).Phase == ContactPhase.Moved, "Held contact restarted.");
        var release = tracker.Observe(default);
        Check(release.Phase == ContactPhase.Released && tap.Release(release.Position.X, release.Position.Y),
            "A release consumed by another reader swallowed the menu tap.");
        Check(tracker.Observe(default).Phase == ContactPhase.None, "Missing release generated a duplicate tap.");
        Check(tracker.Observe(Frame(1, TouchLocationState.Released)).Phase == ContactPhase.None, "Release without press clicked.");

        first = tracker.Observe(Frame(2, TouchLocationState.Moved)); tap.Press(first.Position.X, first.Position.Y);
        var move = tracker.Observe(Frame(2, TouchLocationState.Moved, 80));
        Check(tap.Move(move.Position.X, move.Position.Y) != 0, "Missing press edge prevented scrolling.");
        release = tracker.Observe(default);
        Check(!tap.Release(release.Position.X, release.Position.Y), "Scroll ending with a consumed release activated a field.");

        tracker.Observe(Frame(3, TouchLocationState.Pressed));
        Check(tracker.Observe(default, false).Phase == ContactPhase.Cancelled, "Focus loss synthesized a click.");
        Check(tracker.Observe(Frame(3, TouchLocationState.Moved)).Phase == ContactPhase.None, "Foreground return revived a held contact.");
        tracker.Observe(default);
        tracker.Observe(Frame(4, TouchLocationState.Pressed));
        var multi = new TouchCollection(new[] { new TouchLocation(4, TouchLocationState.Moved, Vector2.Zero),
            new TouchLocation(5, TouchLocationState.Pressed, Vector2.Zero) });
        Check(tracker.Observe(multi).Phase == ContactPhase.Cancelled, "Multiple fingers were not cancelled.");
        Check(tracker.Observe(Frame(4, TouchLocationState.Moved)).Phase == ContactPhase.None, "Lifting one finger revived a cancelled tap.");
        tracker.Observe(default);
        tracker.Observe(Frame(6, TouchLocationState.Pressed));
        Check(tracker.Observe(Frame(7, TouchLocationState.Moved)).Phase == ContactPhase.Cancelled, "Pointer replacement clicked a field.");
        tracker.Observe(default);
        tracker.Cancel();
        Check(tracker.Observe(Frame(8, TouchLocationState.Moved)).Phase == ContactPhase.None, "Menu-opening contact clicked through.");
        tracker.Observe(default);
        Check(tracker.Observe(Frame(9, TouchLocationState.Pressed)).Phase == ContactPhase.Pressed, "Menu stayed blocked after all fingers lifted.");
        Check(tracker.Observe(Frame(9, TouchLocationState.Released)).Phase == ContactPhase.Released, "Explicit release failed.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
