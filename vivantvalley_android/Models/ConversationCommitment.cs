namespace VivantValley;

/// <summary>Local, backwards-compatible social state; not an AI wire field.</summary>
public sealed class ConversationCommitment
{
    public string Key { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public int CreatedDay { get; set; }
    public int UpdatedDay { get; set; }
    public int? DueDay { get; set; }
    public long UpdatedTurn { get; set; }
    public bool SuppressFollowUp { get; set; }
}
