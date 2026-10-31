namespace VivantValley;

/// <summary>The locally inferred mood used only for dialogue presentation.</summary>
public enum NpcPresentationMood
{
    Neutral,
    Happy,
    Sad,
    Angry,
    Love,
    Surprised,
}

/// <summary>An authoritative game result which takes priority over text heuristics.</summary>
public enum NpcPresentationCue
{
    None,
    GiftOffered,
    GiftDelivered,
    GiftDeclined,
    ActivityAccepted,
    ActivityDeclined,
}

/// <summary>A deterministic, cosmetic-only presentation plan for one NPC reply.</summary>
public sealed record NpcPresentationPlan(
    NpcPresentationMood Mood,
    int PortraitIndex,
    string DialogueEmotionToken,
    int? EmoteId,
    bool UseBodyAnimation,
    IReadOnlyList<string> AnimationSuffixes);
