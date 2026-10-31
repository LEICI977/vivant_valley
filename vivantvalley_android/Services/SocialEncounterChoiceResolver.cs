namespace VivantValley.Services;

/// <summary>Routes a displayed gift choice; declining never calls gift validation or delivery.</summary>
public static class SocialEncounterChoiceResolver
{
    public static EncounterChoiceResult Resolve(
        DailySocialCandidate? candidate,
        bool alreadyCompleted,
        bool contextCurrent,
        string choiceId,
        Func<EncounterChoiceResult> deliverGift,
        Func<bool, bool> complete)
    {
        if (alreadyCompleted || candidate?.Status == DailySocialCandidateStatus.Completed)
            return EncounterChoiceResult.AlreadyCompleted;
        if (!contextCurrent || candidate?.Status != DailySocialCandidateStatus.Presenting)
            return EncounterChoiceResult.SceneExpired;

        if (choiceId.Equals("decline", StringComparison.OrdinalIgnoreCase))
            return complete(false) ? EncounterChoiceResult.Completed : EncounterChoiceResult.SceneExpired;
        if (!choiceId.Equals("accept", StringComparison.OrdinalIgnoreCase))
            return EncounterChoiceResult.ChoiceUnavailable;

        EncounterChoiceResult delivery = deliverGift();
        if (delivery != EncounterChoiceResult.Completed)
            return delivery;

        // Once delivered, never leave an actionable accept button on screen.
        return complete(true) ? EncounterChoiceResult.Completed : EncounterChoiceResult.SettlementFailed;
    }
}
