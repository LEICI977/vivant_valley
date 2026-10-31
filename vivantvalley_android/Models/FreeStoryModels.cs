namespace VivantValley;

/// <summary>V3 plans hold an opening, never a predicted player choice or ending.</summary>
public sealed class FreeStoryScene
{
    public string Premise { get; set; } = "";
    public List<FreeStoryMotivation> Motivations { get; set; } = new();
    public List<FreeStoryLine> Opening { get; set; } = new();
    public List<FreeStoryEvent> Events { get; set; } = new();
    public List<FreeStoryLine> PendingLines { get; set; } = new();
    public List<string> Departed { get; set; } = new();
    public bool Started { get; set; }
    public bool AwaitingReply { get; set; }
    public bool SuggestEnd { get; set; }
    public int Turns { get; set; }
    public string Target { get; set; } = "";
    public void Normalize()
    {
        Premise = WeeklyStoryPlan.LimitText(Premise, 1200);
        Motivations ??= new(); Opening ??= new(); Events ??= new(); PendingLines ??= new(); Departed ??= new();
        Motivations = Motivations.Where(x => x is not null).Take(3).ToList();
        Opening = Opening.Where(x => x is not null).Take(4).ToList();
        PendingLines = PendingLines.Where(x => x is not null).Take(4).ToList();
        // Preserve the observed transcript. New requests stop at 60 turns; don't silently truncate witnesses.
        Events = Events.Where(x => x is not null).Take(257).ToList();
        Departed = Departed.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }
}
public sealed class FreeStoryMotivation
{
    public string Actor { get; set; } = "";
    public string Goal { get; set; } = "";
    public string KnownFacts { get; set; } = "";
}
public sealed class FreeStoryLine
{
    public string Actor { get; set; } = "";
    public string Text { get; set; } = "";
    public string Cue { get; set; } = "";
    public string Action { get; set; } = "talk";
    public string Target { get; set; } = "player";
}
public sealed class FreeStoryEvent
{
    public int Sequence { get; set; }
    public string Actor { get; set; } = "";
    public string Text { get; set; } = "";
    public string Action { get; set; } = "talk";
    public string Target { get; set; } = "";
    public List<string> Witnesses { get; set; } = new();
}
public sealed record FreeStoryActorContext(string Name, string Identity, string PrivateMemory, string CurrentFacts,
    IReadOnlyList<Services.ContextMemoryCandidate>? RecallCandidates = null,
    IReadOnlyList<string>? RecentKnownEvents = null);
public sealed class FreeStoryTurnResult
{
    public List<FreeStoryLine> Lines { get; set; } = new();
    public bool SuggestEnd { get; set; }
}
internal sealed class FreeStoryOpeningWire
{
    public int FormatVersion { get; set; }
    public string Title { get; set; } = "";
    public string Premise { get; set; } = "";
    public string Inviter { get; set; } = "";
    public List<string> Participants { get; set; } = new();
    public string LocationId { get; set; } = "";
    public string TimeSlot { get; set; } = "";
    public string TimingMode { get; set; } = "flexible";
    public string TimingReason { get; set; } = "";
    public string RequiredEnvironment { get; set; } = "";
    public string MailBody { get; set; } = "";
    public string ActivityKind { get; set; } = "story";
    public List<FreeStoryMotivation> Motivations { get; set; } = new();
    public List<FreeStoryLine> Opening { get; set; } = new();
}
