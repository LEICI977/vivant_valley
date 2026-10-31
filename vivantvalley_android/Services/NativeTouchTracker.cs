using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input.Touch;

namespace VivantValley.Services;

internal enum ContactPhase { None, Pressed, Moved, Released, Cancelled }
internal readonly record struct ContactUpdate(ContactPhase Phase, int Id, Vector2 Position);

/// <summary>Tracks contact identity even if another GetState reader consumed the edge states.</summary>
internal sealed class NativeTouchTracker
{
    private int? pointer;
    private Vector2 position;
    private bool waitForNeutral;

    public void Cancel() { pointer = null; waitForNeutral = true; }

    public ContactUpdate Observe(TouchCollection touches, bool enabled = true)
    {
        bool down = touches.Any(t => t.State is TouchLocationState.Pressed or TouchLocationState.Moved);
        if (!enabled || touches.Count > 1)
        {
            Cancel();
            return new(ContactPhase.Cancelled, -1, default);
        }
        if (waitForNeutral)
        {
            if (!down) waitForNeutral = false;
            return default;
        }
        if (touches.Count == 0)
        {
            if (pointer is not int released) return default;
            pointer = null;
            // Only an observed contact may finish here, while the owner is active.
            return new(ContactPhase.Released, released, position);
        }
        TouchLocation touch = touches[0];
        if (pointer is int current && current != touch.Id)
        {
            Cancel();
            return new(ContactPhase.Cancelled, current, position);
        }
        position = touch.Position;
        if (touch.State == TouchLocationState.Released)
        {
            if (pointer is null) return default;
            pointer = null;
            return new(ContactPhase.Released, touch.Id, position);
        }
        if (!down) { Cancel(); return new(ContactPhase.Cancelled, touch.Id, position); }
        bool start = pointer is null;
        pointer = touch.Id;
        return new(start ? ContactPhase.Pressed : ContactPhase.Moved, touch.Id, position);
    }
}
