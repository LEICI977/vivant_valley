using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Xna.Framework;

namespace VivantValley.Services;

/// <summary>
/// Android bindings are resolved only on Android, so the desktop DLL has no Mono.Android dependency.
/// A native EditText lets the installed IME own composition, selection and clipboard operations.
/// An in-place EditText streams full snapshots to the game thread; text is never logged.
/// </summary>
internal sealed class AndroidTextInput
{
    private readonly ConcurrentQueue<Action> completed = new();
    private readonly Action<string> reportError;
    private Session? session;
    public bool IsOpen => session is not null;
    public bool IsEditing(Rectangle bounds) => session?.Matches(bounds) == true;
    public Rectangle? VisibleUiBounds { get; private set; }
    public (int Left, int Top, int Right, int Bottom, int Width, int Height) Insets { get; private set; }
    private double nextInsetsRead;

    public AndroidTextInput(Action<string> reportError) => this.reportError = reportError;

    public void Open(object owner, string title, string initial, bool password, Action<string> apply, bool email, int limit, Func<Rectangle> bounds, int? caretIndex = null, bool multiline = false)
    {
        if (session is { } previous)
        {
            Finish(previous.Owner, () => Open(owner, title, initial, password, apply, email, limit, bounds, caretIndex, multiline));
            return;
        }
        var next = new Session(this, owner, title, initial, password, email, apply, limit, bounds, caretIndex, multiline);
        session = next;
        try { OnUiThread(next.Show); }
        catch (Exception ex) { session = null; reportError("Android text input could not open: " + ErrorName(ex)); }
    }

    public void Update(object? activeOwner)
    {
        while (completed.TryDequeue(out Action? action)) action();
        if (session is { } current && !ReferenceEquals(current.Owner, activeOwner)) Close(current.Owner);
        session?.RefreshGeometry();
        if (MobileRuntime.Now >= nextInsetsRead)
        {
            nextInsetsRead = MobileRuntime.Now + 1000;
            try { OnUiThread(ReadInsets); }
            catch { /* Older loaders can use the configurable edge padding. */ }
        }
    }

    private void ReadInsets()
    {
        // IME occlusion is handled by the inline field; don't resize the game form under it.
        if (session is not null) return;
        try
        {
            object? window = Activity().GetType().GetProperty("Window")?.GetValue(Activity());
            object? view = window?.GetType().GetProperty("DecorView")?.GetValue(window);
            object? insets = view?.GetType().GetProperty("RootWindowInsets")?.GetValue(view);
            if (view is null || insets is null) return;
            int Read(object value, string property) => value.GetType().GetProperty(property)?.GetValue(value) is int number ? number : 0;
            object? cutout = insets.GetType().GetProperty("DisplayCutout")?.GetValue(insets);
            int left = Read(insets, "SystemWindowInsetLeft"), right = Read(insets, "SystemWindowInsetRight");
            int top = Read(insets, "SystemWindowInsetTop"), bottom = Read(insets, "SystemWindowInsetBottom");
            if (cutout is not null)
            {
                left = Math.Max(left, Read(cutout, "SafeInsetLeft")); right = Math.Max(right, Read(cutout, "SafeInsetRight"));
                top = Math.Max(top, Read(cutout, "SafeInsetTop")); bottom = Math.Max(bottom, Read(cutout, "SafeInsetBottom"));
            }
            int width = Read(view, "Width"), height = Read(view, "Height");
            // Ignore unusually large transient insets while returning from the IME.
            if (width > 0 && height > 0 && bottom < height / 3)
                completed.Enqueue(() => Insets = (left, top, right, bottom, width, height));
        }
        catch { /* Insets are optional on old Android builds. */ }
    }

    public bool Close(object owner)
    {
        if (session is not { } current || !ReferenceEquals(current.Owner, owner)) return false;
        current.Invalidated = true;
        session = null;
        try { OnUiThread(current.Dismiss); }
        catch (Exception ex) { reportError("Android text input could not close: " + ErrorName(ex)); }
        return true;
    }

    public bool DismissKeepingDraft(object owner)
        => Finish(owner, () => { });

    public bool Finish(object owner, Action after)
    {
        if (session is not { } current || !ReferenceEquals(current.Owner, owner)) return false;
        if (!current.RequestFinish()) return true;
        try
        {
            OnUiThread(() =>
            {
                try { current.Dismiss(); }
                catch (Exception ex) { completed.Enqueue(() => reportError("Inline editor could not close: " + ErrorName(ex))); }
                // Dismiss queues the final text snapshot first; buttons must use that value.
                completed.Enqueue(() =>
                {
                    if (ReferenceEquals(StardewValley.Game1.activeClickableMenu, owner)) after();
                });
            });
        }
        catch (Exception ex) { reportError("Android text input could not close: " + ErrorName(ex)); }
        return true;
    }

    private static string ErrorName(Exception ex)
        => (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex).GetType().Name;

    private static Type AndroidType(string name)
        => Type.GetType(name + ", Mono.Android", throwOnError: true)!;

    private static object Activity()
    {
        // SMAPI Launcher hosts the game in its own Activity. MainActivity.instance can
        // be a compatibility object rather than the live window used for dialogs.
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? host = assembly.GetType("SMAPIGameLoader.SMAPIActivity", false);
            object? current = StaticMember(host, "Instance");
            if (current is not null) return current;
        }
        Type game = typeof(Microsoft.Xna.Framework.Game);
        object? activity = StaticMember(game, "Activity");
        if (activity is not null) return activity;
        Type? main = typeof(StardewValley.Game1).Assembly.GetType("StardewValley.MainActivity");
        activity = StaticMember(main, "instance");
        if (activity is not null) return activity;
        Type? helper = typeof(StardewModdingAPI.IMod).Assembly.GetType("StardewModdingAPI.Mobile.SMAPIActivityTool");
        return helper?.GetProperty("MainActivity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new NotSupportedException("No Android game activity is available.");
    }

    private static object? StaticMember(Type? type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        return type?.GetProperty(name, flags)?.GetValue(null) ?? type?.GetField(name, flags)?.GetValue(null);
    }

    private static void OnUiThread(Action action)
    {
        object activity = Activity();
        activity.GetType().GetMethod("RunOnUiThread", new[] { typeof(Action) })!.Invoke(activity, new object[] { action });
    }

    private static object? Call(object target, string name, params object[] args)
    {
        MethodInfo method = target.GetType().GetMethods().First(m => m.Name == name && !m.ContainsGenericParameters
            && m.GetParameters().Length == args.Length
            && m.GetParameters().Select((p, i) => p.ParameterType.IsInstanceOfType(args[i])).All(match => match));
        return method.Invoke(target, args);
    }

    private static void Set(object target, string name, object value)
        => target.GetType().GetProperty(name)!.SetValue(target, value);

    public void OpenUrl(string url)
    {
        // The URI is chosen from the mod's existing purchase URL table.
        OnUiThread(() =>
        {
            try
            {
                object uri = AndroidType("Android.Net.Uri").GetMethod("Parse", new[] { typeof(string) })!.Invoke(null, new object[] { url })!;
                object intent = Activator.CreateInstance(AndroidType("Android.Content.Intent"), "android.intent.action.VIEW", uri)!;
                Call(Activity(), "StartActivity", intent);
                (intent as IDisposable)?.Dispose();
            }
            catch (Exception ex) { completed.Enqueue(() => reportError("Android browser could not open: " + ErrorName(ex))); }
        });
    }

    private sealed class Session
    {
        private readonly AndroidTextInput bridge;
        private readonly string title, initial;
        private readonly bool password, email, multiline;
        private readonly Action<string> apply;
        private readonly int limit;
        private readonly int initialCaret;
        private readonly Func<Rectangle> bounds;
        private object? edit, content, layout, window, originalSoftInput;
        private object? filter;
        private EventInfo? changedEvent, actionEvent;
        private Delegate? changedHandler, actionHandler;
        private Rectangle anchor, viewport;
        private double nextGeometryRead;
        private int geometryPending;
        private int finishRequested;
        private bool keyboardSeen;
        private int lastVisibleHeight;
        private Rectangle lastVisibleUi;
        private readonly System.Diagnostics.Stopwatch shown = new();
        private volatile bool finished;
        public object Owner { get; }
        public volatile bool Invalidated;
        public bool RequestFinish() => Interlocked.Exchange(ref finishRequested, 1) == 0;
        public bool Matches(Rectangle rectangle) => !Invalidated && bounds() == rectangle;

        public Session(AndroidTextInput bridge, object owner, string title, string initial, bool password, bool email, Action<string> apply, int limit, Func<Rectangle> bounds, int? caretIndex = null, bool multiline = false)
        {
            this.bridge = bridge; Owner = owner; this.title = title; this.initial = initial;
            var selection = new SingleLineTextBuffer(Math.Max(1, initial.Length)) { Text = initial };
            selection.SetCaret(caretIndex ?? initial.Length);
            initialCaret = selection.CaretIndex;
            this.multiline = multiline;
            if (multiline) initialCaret = System.Globalization.StringInfo.ParseCombiningCharacters(initial).Append(initial.Length).Last(i => i <= Math.Clamp(caretIndex ?? initial.Length, 0, initial.Length));
            this.password = password; this.email = email; this.apply = apply; this.limit = limit; this.bounds = bounds;
            anchor = bounds(); viewport = new Rectangle(0, 0, StardewValley.Game1.uiViewport.Width, StardewValley.Game1.uiViewport.Height);
        }

        public void Show()
        {
            if (Invalidated) return;
            try
            {
                object activity = Activity();
                edit = Activator.CreateInstance(AndroidType("Android.Widget.EditText"), activity)!;
                // TextVariationPassword suppresses suggestions and conceals password/key fields.
                Call(edit, "SetSingleLine", !multiline);
                // Email gets its own IME layout and no dictionary correction. Chat keeps composition.
                Set(edit, "InputType", Enum.ToObject(AndroidType("Android.Text.InputTypes"), password ? 0x80081 : email ? 0x80021 : multiline ? 0x20001 : 0x1));
                // Keep editing in place in landscape instead of switching to an extracted editor.
                Set(edit, "ImeOptions", Enum.ToObject(AndroidType("Android.Views.InputMethods.ImeAction"), multiline ? 0x52000000 : 0x12000006));
                Set(edit, "Text", initial);
                Call(edit, "SetSelection", initialCaret);
                // Install the actual Android editor at the game's field, not in another window.
                Set(edit, "ContentDescription", title);
                Set(edit, "FocusableInTouchMode", true);
                Call(edit, "SetPadding", 0, 0, 0, 0);
                // The game owns the frame and fill; Android draws only text and caret.
                object background = Activator.CreateInstance(AndroidType("Android.Graphics.Color"), 0)!;
                object foreground = Activator.CreateInstance(AndroidType("Android.Graphics.Color"), unchecked((int)0xFF30251D))!;
                Call(edit, "SetBackgroundColor", background);
                Call(edit, "SetTextColor", foreground);
                // Length is enforced in the IME's editable buffer, before live snapshots are sent.
                filter = Activator.CreateInstance(AndroidType("Android.Text.InputFilterLengthFilter"), limit)!;
                Array filters = Array.CreateInstance(AndroidType("Android.Text.IInputFilter"), 1);
                filters.SetValue(filter, 0);
                Call(edit, "SetFilters", (object)filters);
                changedEvent = edit.GetType().GetEvent("TextChanged")!;
                changedHandler = Delegate.CreateDelegate(changedEvent.EventHandlerType!, this,
                    GetType().GetMethod(nameof(OnTextChanged))!);
                changedEvent.AddEventHandler(edit, changedHandler);
                actionEvent = edit.GetType().GetEvent("EditorAction")!;
                actionHandler = Delegate.CreateDelegate(actionEvent.EventHandlerType!, this,
                    GetType().GetMethod(nameof(OnEditorAction))!);
                actionEvent.AddEventHandler(edit, actionHandler);
                content = Call(activity, "FindViewById", 16908290)!; // android.R.id.content (FrameLayout)
                window = activity.GetType().GetProperty("Window")!.GetValue(activity)!;
                object? attributes = window.GetType().GetProperty("Attributes")?.GetValue(window);
                originalSoftInput = attributes?.GetType().GetProperty("SoftInputMode")?.GetValue(attributes);
                // Pan ourselves using the visible area; don't resize the game surface or alter touch scaling.
                Call(window, "SetSoftInputMode", Enum.ToObject(AndroidType("Android.Views.SoftInput"), 0x35));
                layout = Activator.CreateInstance(AndroidType("Android.Widget.FrameLayout+LayoutParams"), 1, 1)!;
                Set(layout, "Gravity", Enum.ToObject(AndroidType("Android.Views.GravityFlags"), 51));
                Call(content, "AddView", edit, layout);
                shown.Start();
                Position(anchor, viewport);
                Call(edit, "RequestFocus");
                object? input = Call(activity, "GetSystemService", "input_method");
                if (input is not null)
                    Call(input, "ShowSoftInput", edit, Enum.ToObject(AndroidType("Android.Views.InputMethods.ShowFlags"), 1));
                bridge.completed.Enqueue(() => MobileRuntime.TraceInput("inline editor shown", Owner.GetType().Name));
                if (Invalidated) Dismiss();
            }
            catch (Exception ex)
            {
                Invalidated = true;
                try { Dismiss(); } catch { }
                bridge.completed.Enqueue(() =>
                {
                    if (ReferenceEquals(bridge.session, this)) bridge.session = null;
                    bridge.reportError("Android native editor unavailable: " + ErrorName(ex));
                });
            }
        }

        // Native text changes include backspace, repeat-delete, selection replacement and IME composition.
        // Never assign Text back into the editor here: doing so resets its selection/composing span.
        public void OnTextChanged(object? sender, EventArgs args) => Publish();

        private void Publish()
        {
            if (Invalidated || finished || edit is null) return;
            string value = edit.GetType().GetProperty("Text")?.GetValue(edit) as string ?? string.Empty;
            bridge.completed.Enqueue(() =>
            {
                if (!Invalidated && ReferenceEquals(StardewValley.Game1.activeClickableMenu, Owner)) apply(value);
            });
        }

        public void OnEditorAction(object? sender, EventArgs args)
        {
            object? action = args.GetType().GetProperty("ActionId")?.GetValue(args);
            if (action is null || Convert.ToInt32(action) != 6) return;
            args.GetType().GetProperty("Handled")?.SetValue(args, true);
            try { Dismiss(); } // IME Done hides the keyboard; it never submits chat or login.
            catch (Exception ex) { bridge.completed.Enqueue(() => bridge.reportError("Inline editor close failed: " + ErrorName(ex))); }
        }

        public void RefreshGeometry()
        {
            if (finished || Invalidated || MobileRuntime.Now < nextGeometryRead) return;
            nextGeometryRead = MobileRuntime.Now + 100;
            Rectangle nextAnchor = bounds();
            Rectangle nextViewport = new(0, 0, StardewValley.Game1.uiViewport.Width, StardewValley.Game1.uiViewport.Height);
            if (Interlocked.Exchange(ref geometryPending, 1) != 0) return;
            try
            {
                OnUiThread(() =>
                {
                    try { Position(nextAnchor, nextViewport); }
                    catch (Exception ex)
                    {
                        bridge.completed.Enqueue(() => bridge.reportError("Inline editor layout failed: " + ErrorName(ex)));
                        try { Dismiss(); } catch { }
                    }
                    finally { Interlocked.Exchange(ref geometryPending, 0); }
                });
            }
            catch { Interlocked.Exchange(ref geometryPending, 0); }
        }

        private void Position(Rectangle nextAnchor, Rectangle nextViewport)
        {
            if (finished || Invalidated || edit is null || content is null || layout is null) return;
            int Read(object obj, string name) => Convert.ToInt32(obj.GetType().GetProperty(name)!.GetValue(obj));
            int width = Read(content, "Width"), height = Read(content, "Height");
            if (width <= 0 || height <= 0) return;
            object rect = Activator.CreateInstance(AndroidType("Android.Graphics.Rect"))!;
            try
            {
                Call(content, "GetWindowVisibleDisplayFrame", rect);
                int[] origin = new int[2];
                Call(content, "GetLocationOnScreen", (object)origin);
                Rectangle visible = new(Read(rect, "Left") - origin[0], Read(rect, "Top") - origin[1],
                    Read(rect, "Right") - Read(rect, "Left"), Read(rect, "Bottom") - Read(rect, "Top"));
                bool keyboard = height - visible.Bottom > Math.Max(80, height / 6);
                Rectangle visibleUi = new(
                    Math.Max(0, visible.Left) * nextViewport.Width / width,
                    Math.Max(0, visible.Top) * nextViewport.Height / height,
                    Math.Min(width, visible.Width) * nextViewport.Width / width,
                    Math.Min(height, visible.Height) * nextViewport.Height / height);
                if (visibleUi != lastVisibleUi)
                {
                    lastVisibleUi = visibleUi;
                    bridge.completed.Enqueue(() =>
                    {
                        if (ReferenceEquals(bridge.session, this)) bridge.VisibleUiBounds = visibleUi;
                    });
                }
                if (keyboard) keyboardSeen = true;
                // Back normally hides the IME without generating a game key event.
                if (keyboardSeen && !keyboard && lastVisibleHeight > 0 && shown.ElapsedMilliseconds > 400)
                { Dismiss(); return; }
                lastVisibleHeight = visible.Height;
                anchor = nextAnchor; viewport = nextViewport;
                Rectangle inner = Owner is VivantValley.Menus.AiChatInputMenu
                    ? new(anchor.X + 16, anchor.Y + 2, Math.Max(1, anchor.Width - 32), Math.Max(1, anchor.Height - 4))
                    : new(anchor.X + 12, anchor.Y + 6, Math.Max(1, anchor.Width - 24), Math.Max(1, anchor.Height - 12));
                Rectangle placement = MobileLayout.InlineEditor(inner, viewport, new Rectangle(0, 0, width, height), visible);
                Set(layout, "Width", placement.Width); Set(layout, "Height", placement.Height);
                Set(layout, "LeftMargin", placement.X); Set(layout, "TopMargin", placement.Y);
                Set(edit, "LayoutParameters", layout);
                Call(edit, "SetTextSize", Enum.ToObject(AndroidType("Android.Util.ComplexUnitType"), 0),
                    Owner is VivantValley.Menus.AiChatInputMenu
                        ? Math.Max(14f, Math.Min(StardewValley.Game1.smallFont.LineSpacing * 1.5f, anchor.Height - 4)
                            * height / Math.Max(1f, viewport.Height))
                        : Math.Max(14f, multiline ? 24f : placement.Height * .4f));
            }
            finally { (rect as IDisposable)?.Dispose(); }
        }

        public void Dismiss()
        {
            if (finished) return;
            Interlocked.Exchange(ref finishRequested, 1);
            Publish();
            finished = true;
            try
            {
                if (edit is not null)
                {
                    if (changedHandler is not null) changedEvent?.RemoveEventHandler(edit, changedHandler);
                    if (actionHandler is not null) actionEvent?.RemoveEventHandler(edit, actionHandler);
                    object? input = Call(Activity(), "GetSystemService", "input_method");
                    object? token = edit.GetType().GetProperty("WindowToken")?.GetValue(edit);
                    if (input is not null && token is not null)
                        Call(input, "HideSoftInputFromWindow", token, Enum.ToObject(AndroidType("Android.Views.InputMethods.HideSoftInputFlags"), 0));
                    Call(edit, "ClearFocus");
                }
            }
            finally
            {
                try
                {
                    if (content is not null && edit is not null) Call(content, "RemoveView", edit);
                    if (window is not null && originalSoftInput is not null) Call(window, "SetSoftInputMode", originalSoftInput);
                }
                finally
                {
                    (edit as IDisposable)?.Dispose(); (layout as IDisposable)?.Dispose(); (filter as IDisposable)?.Dispose();
                    edit = layout = filter = content = window = null;
                    bridge.completed.Enqueue(() =>
                    {
                        if (ReferenceEquals(bridge.session, this)) bridge.session = null;
                        if (bridge.session is null) bridge.VisibleUiBounds = null;
                        MobileRuntime.BlockUntilRelease = true;
                        MobileRuntime.TraceInput("inline editor closed", Owner.GetType().Name);
                    });
                }
            }
        }
    }
}
