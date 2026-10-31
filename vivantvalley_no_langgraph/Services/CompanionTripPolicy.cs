namespace VivantValley.Services;

internal interface IPlayerCompanionSession
{
    long LeaderId { get; }
    bool IsComplete { get; }
    void Cancel(string reason);
}

/// <summary>Ownership checks and one-shot removal shared by all three companion services.</summary>
internal static class CompanionTripPolicy
{
    public const string PlayerEnded = "ended_by_player";

    public static bool IsOwned<T>(Dictionary<string, T> sessions, string npcName, long playerId)
        where T : IPlayerCompanionSession
        => sessions.TryGetValue(npcName, out T? session) && !session.IsComplete && session.LeaderId == playerId;

    public static bool End<T>(Dictionary<string, T> sessions, string npcName, long playerId,
        bool authoritative, Action beforeEnd) where T : IPlayerCompanionSession
    {
        if (!authoritative || !IsOwned(sessions, npcName, playerId)) return false;
        T session = sessions[npcName];
        // Resolve and validate the return point before committing the cancellation.
        // If it fails, retain the session so the player can retry.
        beforeEnd();
        session.Cancel(PlayerEnded);
        sessions.Remove(npcName);
        return true;
    }
}
