namespace VivantValley.Services;

/// <summary>Saved per NPC: crossing an inner boundary consumes one opportunity, even if busy.
/// Only sustained separation rearms it; standing nearby or closing a menu never rerolls.</summary>
public sealed class EncounterProximityState
{
    public bool Consumed { get; set; }
    public int AwaySeconds { get; set; }
}

public static class EncounterProximityPolicy
{
    public static bool Observe(EncounterProximityState state, float distance, float inner, float outer, int seconds)
    {
        inner = Math.Clamp(inner, 1, 16);
        outer = Math.Max(inner + 2, Math.Clamp(outer, 3, 32));
        seconds = Math.Clamp(seconds, 5, 600);
        if (distance > outer)
        {
            state.AwaySeconds = Math.Min(seconds, state.AwaySeconds + 1);
            if (state.AwaySeconds >= seconds) state.Consumed = false;
            return false;
        }
        state.AwaySeconds = 0;
        if (distance > inner || state.Consumed) return false;
        state.Consumed = true;
        return true;
    }
}
