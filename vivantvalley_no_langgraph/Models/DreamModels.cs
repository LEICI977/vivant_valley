namespace VivantValley;

public sealed class DreamSaveStore
{
    public int Version { get; set; } = 1;
    public Dictionary<string, DreamPlayerState> Players { get; set; } = new();
    public DreamPlayerState For(string player)
    {
        if (!Players.TryGetValue(player, out var state)) Players[player] = state = new();
        return state;
    }
}
public sealed class DreamPlayerState
{
    public int Day { get; set; } = -1;
    public HashSet<string> TalkedToday { get; set; } = new(StringComparer.Ordinal);
    public bool NightConsumed { get; set; }
    public bool VisitorHandled { get; set; }
    public bool VisitorRolled { get; set; }
    public string Visitor { get; set; } = "";
    public List<DreamEpisode> Episodes { get; set; } = new();
    public void OnDay(int day)
    {
        if (Day == day) return;
        Day = day; TalkedToday.Clear(); NightConsumed = false; VisitorRolled = false; VisitorHandled = false; Visitor = "";
    }
}
public sealed class DreamEpisode
{
    public string Id { get; set; } = "";
    public int Day { get; set; }
    public string Npc { get; set; } = "";
    public string Direction { get; set; } = "player_visits";
    public string Scene { get; set; } = "collage";
    public string Status { get; set; } = "active";
    public bool MorningMentionUsed { get; set; }
    public bool SummaryPending { get; set; }
    public string Summary { get; set; } = "";
    public List<DreamLine> Lines { get; set; } = new();
}
public sealed class DreamLine
{
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
}
public sealed record DreamCandidate(string Name, int Hearts);
public sealed class DreamReply
{
    public string Scene { get; set; } = "collage";
    public string Text { get; set; } = "";
}

// Live relationship context, never inferred from generated dream dialogue.
public sealed record DreamRelationship(string Status = "unknown", int Hearts = 0, bool Adult = false, bool RomanticPartner = false, bool Datable = false)
{
    public static DreamRelationship FromGameState(int hearts, bool adult, bool dating, bool engaged,
        bool married, bool roommate, bool divorced, bool datable = false)
    {
        string status = divorced ? "divorced" : roommate ? "roommate" : married ? "married"
            : engaged ? "engaged" : dating ? "dating" : "friendship";
        return new(status,Math.Max(0,hearts),adult,status is "dating" or "engaged" or "married",datable);
    }
}
