using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input.Touch;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace VivantValley.Services;

internal static class MobileRuntime
{
    public static bool IsAndroid { get; private set; }
    // This is the dedicated mobile build. No platform probe or config file may disable its UI.
    public static bool Enabled => true;
    // Don't permanently disable input on hosts which never report an initial active window.
    private static readonly MobileFocusState focus = new();
    public static bool InputActive => focus.Observe(Game1.game1?.IsActive == true);
    public static int EdgePadding { get; private set; }
    public static int ControlHeight { get; private set; } = 64;
    public static bool BlockUntilRelease { get; set; }
    public static bool KeyboardOpen => editor?.IsOpen == true;
    public static bool IsEditing(Rectangle bounds) => editor?.IsEditing(bounds) == true;
    public static Rectangle ComposerBounds => editor?.VisibleUiBounds is Rectangle visible
        ? Rectangle.Intersect(SafeBounds, MobileLayout.Inset(visible, EdgePadding)) : SafeBounds;
    public static double Now => System.Diagnostics.Stopwatch.GetTimestamp() * 1000d / System.Diagnostics.Stopwatch.Frequency;
    private static ModConfig settings = null!;
    private static Action saveSettings = null!;
    public static float ButtonScale => float.IsFinite(settings.MobileChatButtonScale) ? Math.Clamp(settings.MobileChatButtonScale, .75f, 1.75f) : 1f;
    public static int ButtonOffsetX => Math.Clamp(settings.MobileChatButtonOffsetX, 0, 4096);
    public static int ButtonOffsetY => Math.Clamp(settings.MobileChatButtonOffsetY, 0, 4096);
    public static Rectangle ButtonBounds => MobileLayout.HudButton(SafeBounds, ControlHeight, ButtonScale, ButtonOffsetX, ButtonOffsetY);
    public static void AdjustButton(float scale = 0, int x = 0, int y = 0, bool reset = false)
    {
        var old = (settings.MobileChatButtonScale, settings.MobileChatButtonOffsetX, settings.MobileChatButtonOffsetY);
        settings.MobileChatButtonScale = reset ? 1f : Math.Clamp(ButtonScale + scale, .75f, 1.75f);
        settings.MobileChatButtonOffsetX = reset ? 24 : Math.Clamp(ButtonOffsetX + x, 0, Math.Max(0, SafeBounds.Width - ButtonBounds.Width));
        settings.MobileChatButtonOffsetY = reset ? 160 : Math.Clamp(ButtonOffsetY + y, 0, Math.Max(0, SafeBounds.Height - ButtonBounds.Height));
        try { saveSettings(); }
        catch (Exception ex)
        {
            (settings.MobileChatButtonScale, settings.MobileChatButtonOffsetX, settings.MobileChatButtonOffsetY) = old;
            monitor?.Log("Mobile button settings could not be saved: " + ex.GetType().Name, LogLevel.Warn);
            Game1.addHUDMessage(new HUDMessage(L.T("mobile.save.failed"), HUDMessage.error_type));
        }
    }
    private static AndroidTextInput? editor;
    private static bool touchUnavailable;
    private static TouchCollection frameTouches;
    private static readonly NativeTouchTracker contacts = new();
    public static ContactUpdate Contact { get; private set; }
    private static IMonitor? monitor;
    private static readonly Dictionary<string, double> nextInputLog = new();
    internal static void TraceInput(string stage, string menu, string detail = "")
    {
        string key = stage + ":" + menu;
        if (nextInputLog.GetValueOrDefault(key) > Now) return;
        nextInputLog[key] = Now + 5000;
        monitor?.Log($"Mobile input: {stage}; menu={menu}; {detail}", LogLevel.Info);
    }

    public static void Initialize(IMonitor log, ModConfig config, Action save)
    {
        monitor = log; settings = config; saveSettings = save;
        log.Log("Mobile UI enabled unconditionally (dedicated Android build); no config switch required.", LogLevel.Info);
        try { IsAndroid = OperatingSystem.IsAndroid() || Type.GetType("Android.OS.Build, Mono.Android", false) is not null; }
        catch (Exception ex)
        {
            IsAndroid = false;
            log.Log("Android native API detection failed; mobile UI remains enabled: " + ex.GetType().Name, LogLevel.Warn);
        }
        EdgePadding = Math.Clamp(config.MobileEdgePadding, 8, 100);
        ControlHeight = (int)(64 * (float.IsFinite(config.MobileUiScale) ? Math.Clamp(config.MobileUiScale, 0.85f, 1.5f) : 1f));
        if (IsAndroid) editor = new AndroidTextInput(message =>
        {
            log.Log(message, LogLevel.Warn);
            Game1.addHUDMessage(new HUDMessage(L.T("mobile.input.error"), HUDMessage.error_type));
        });
        log.Log($"Android native APIs detected={IsAndroid}; NPC chat uses the bottom-right AI button.", LogLevel.Info);
    }

    // GetState advances Pressed/Released in MonoGame. All mod consumers share one read per tick.
    public static TouchCollection Touches() => frameTouches;
    public static void CaptureTouches()
    {
        frameTouches = ReadTouches();
        Contact = contacts.Observe(frameTouches, !touchUnavailable && InputActive);
    }

    private static TouchCollection ReadTouches()
    {
        if (!IsAndroid || touchUnavailable) return default;
        try { return TouchPanel.GetState(); }
        catch (Exception ex) when (ex is NotSupportedException or TypeInitializationException or EntryPointNotFoundException)
        {
            touchUnavailable = true;
            monitor?.Log("Native touch polling unavailable; using pointer input. " + ex.GetType().Name, LogLevel.Warn);
            return default;
        }
    }

    public static Vector2 TouchToUi(Vector2 position)
        => new(position.X * Game1.uiViewport.Width / Math.Max(1, TouchPanel.DisplayWidth > 0 ? TouchPanel.DisplayWidth : Game1.graphics.GraphicsDevice.PresentationParameters.BackBufferWidth),
            position.Y * Game1.uiViewport.Height / Math.Max(1, TouchPanel.DisplayHeight > 0 ? TouchPanel.DisplayHeight : Game1.graphics.GraphicsDevice.PresentationParameters.BackBufferHeight));

    public static Rectangle SafeBounds
    {
        get
        {
            Rectangle viewport = new(0, 0, Math.Max(1, Game1.uiViewport.Width), Math.Max(1, Game1.uiViewport.Height));
            if (IsAndroid && editor is not null)
            {
                var insets = editor.Insets;
                if (insets.Width > 0 && insets.Height > 0)
                {
                    int left = insets.Left * viewport.Width / insets.Width, right = insets.Right * viewport.Width / insets.Width;
                    int top = insets.Top * viewport.Height / insets.Height, bottom = insets.Bottom * viewport.Height / insets.Height;
                    viewport = new Rectangle(left, top, Math.Max(1, viewport.Width - left - right), Math.Max(1, viewport.Height - top - bottom));
                }
            }
            return MobileLayout.Inset(viewport, Enabled ? EdgePadding : 0);
        }
    }

    public static void Update() => editor?.Update(Game1.activeClickableMenu);

    public static void Edit(object owner, TextBox box, string title, bool email = false, Func<Rectangle>? bounds = null)
        => Edit(owner, title, box.Text, box.textLimit, box.PasswordBox, text => box.Text = text, email,
            bounds ?? (() => new Rectangle(box.X, box.Y, box.Width, box.Height)));

    public static void Edit(object owner, string title, string text, int limit, bool password, Action<string> apply, bool email = false, Func<Rectangle>? bounds = null, int? caretIndex = null, bool multiline = false)
    {
        if (!IsAndroid || !ReferenceEquals(Game1.activeClickableMenu, owner)) return;
        TraceInput("inline editor requested", owner.GetType().Name);
        Game1.keyboardDispatcher.Subscriber = null;
        BlockUntilRelease = true;
        editor?.Open(owner, title, text, password, value =>
        {
            if (multiline) { apply(MobileSharePolicy.NormalizeText(value, limit > 0 ? limit : 4096, true)); return; }
            var buffer = new SingleLineTextBuffer(limit > 0 ? limit : 4096) { Text = value };
            apply(buffer.Text);
        }, email, limit > 0 ? limit : 4096, bounds ?? (() => SafeBounds), caretIndex, multiline);
    }

    public static bool DismissKeyboard(object owner) => editor?.DismissKeepingDraft(owner) == true;
    public static bool FinishEditing(object owner, Action after) => editor?.Finish(owner, after) == true;
    public static void CloseKeyboard(object owner) => editor?.Close(owner);
    public static void OpenUrl(string url)
    {
        if (IsAndroid) editor!.OpenUrl(url);
        else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
}
