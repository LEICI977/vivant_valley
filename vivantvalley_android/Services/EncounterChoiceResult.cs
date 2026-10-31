namespace VivantValley.Services;

public enum EncounterChoiceResult
{
    Completed,
    AlreadyCompleted,
    SceneExpired,
    GiftUnavailable,
    InventoryFull,
    DeliveryFailed,
    ChoiceUnavailable,
    SettlementFailed,
}

public static class EncounterChoiceResults
{
    public static bool ClosesMenu(this EncounterChoiceResult result)
        => result is EncounterChoiceResult.Completed or EncounterChoiceResult.AlreadyCompleted
            or EncounterChoiceResult.SceneExpired or EncounterChoiceResult.SettlementFailed;

    public static string MessageKey(this EncounterChoiceResult result) => result switch
    {
        EncounterChoiceResult.Completed => string.Empty,
        EncounterChoiceResult.AlreadyCompleted => "encounter.already-completed",
        EncounterChoiceResult.SceneExpired => "encounter.expired",
        EncounterChoiceResult.GiftUnavailable => "encounter.gift-unavailable",
        EncounterChoiceResult.InventoryFull => "encounter.inventory-full",
        EncounterChoiceResult.DeliveryFailed => "encounter.delivery-failed",
        EncounterChoiceResult.SettlementFailed => "encounter.settlement-failed",
        _ => "text.fa4af8917797",
    };
}
