using Microsoft.Xna.Framework;

namespace VivantValley.Services;

internal enum MobileChatRoute { Settings, Pending, Nearby }
internal enum MobileNpcRoute { None, Single, Choose }

internal sealed class MobileFocusState
{
    private bool reportedActive;
    public bool Observe(bool active)
    {
        reportedActive |= active;
        return !reportedActive || active;
    }
}

/// <summary>Visibility is independent of player movement and native platform detection.</summary>
internal readonly record struct MobileSceneState(
    bool WorldReady, bool Event, bool Transition, bool Menu, bool Minigame,
    bool Keyboard, bool InputActive, bool Frozen, bool UsingTool, bool InBed)
{
    public string HiddenReason => !WorldReady ? "world-not-ready" : Event ? "event"
        : Transition ? "transition" : Menu ? "menu" : Minigame ? "minigame"
        : Keyboard ? "keyboard" : "none";
    public bool ShowButton => HiddenReason == "none";
    public bool CanInteract => ShowButton && InputActive && !Frozen && !UsingTool;
    public bool CanAutoSetup => CanInteract && !InBed;
    public string SetupWaitReason => !ShowButton ? HiddenReason : !InputActive ? "background"
        : Frozen ? "controls-frozen" : UsingTool ? "using-tool" : InBed ? "in-bed" : "settling";
}

internal static class MobileChatPolicy
{
    public static MobileNpcRoute Targets(int count) => count <= 0 ? MobileNpcRoute.None : count == 1 ? MobileNpcRoute.Single : MobileNpcRoute.Choose;
    public static MobileChatRoute Route(bool configured, bool pending)
        => pending ? MobileChatRoute.Pending : !configured ? MobileChatRoute.Settings : MobileChatRoute.Nearby;
}

/// <summary>Owns one contact until release, even after it has been cancelled.</summary>
internal sealed class HudButtonGesture
{
    public bool IsDown { get; private set; }
    public bool Cancelled { get; private set; }
    private int pointer;
    private Rectangle bounds;
    public bool Begin(int id, Vector2 point, Rectangle rectangle)
    {
        if (IsDown || !rectangle.Contains(point)) return false;
        pointer = id; bounds = rectangle; IsDown = true; Cancelled = false;
        return true;
    }
    public void Move(int id, Vector2 point)
    {
        if (IsDown && (id != pointer || !bounds.Contains(point))) Cancel();
    }
    public bool AdoptNativePointer(int id, Vector2 point)
    {
        // Android may publish the simulated mouse press before its touch snapshot.
        // Preserve this contact (and any drag cancellation) instead of starting again.
        if (!IsDown || pointer != -1 || id < 0) return false;
        pointer = id;
        Move(id, point);
        return true;
    }
    public bool Release(int id, Vector2 point)
    {
        if (!IsDown || id != pointer) return false;
        Move(id, point);
        bool activate = !Cancelled;
        Reset();
        return activate;
    }
    public void Cancel() => Cancelled = true;
    public void Reset() { IsDown = false; Cancelled = false; }
}

internal sealed class MobileOnboarding
{
    public const int CurrentVersion = 1;
    public bool Pending { get; private set; }
    public bool ShownThisSession { get; private set; }
    private double nextAttempt;
    public void Queue(int savedVersion, bool configured)
        => Pending = !ShownThisSession && (savedVersion < CurrentVersion || !configured);
    public bool CanAttempt(double now, bool safe) => Pending && safe && now >= nextAttempt;
    public void Failed(double now) => nextAttempt = now + 5000;
    public void Shown() { Pending = false; ShownThisSession = true; }
    public void Complete() => Shown();
    public void LeaveSave() => Pending = false;
}
