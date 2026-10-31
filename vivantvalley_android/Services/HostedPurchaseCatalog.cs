namespace VivantValley.Services;

internal sealed record HostedPurchaseOption(int Amount, string Url);

/// <summary>Storefront identifiers stay in purchase currency; English labels show points.</summary>
internal static class HostedPurchaseCatalog
{
    private static readonly IReadOnlyList<HostedPurchaseOption> chinese = Array.AsReadOnly(new[]
    {
        new HostedPurchaseOption(1, "https://pay.ldxp.cn/item/jxtjmi"),
        new HostedPurchaseOption(5, "https://pay.ldxp.cn/item/eip1kt"),
        new HostedPurchaseOption(10, "https://pay.ldxp.cn/item/mus6ix"),
        new HostedPurchaseOption(20, "https://pay.ldxp.cn/item/mpk1fb"),
    });
    private static readonly IReadOnlyList<HostedPurchaseOption> english = Array.AsReadOnly(new[]
    {
        new HostedPurchaseOption(5, "https://vivantvalley.sell.app/product/vivantvalley5"),
        new HostedPurchaseOption(10, "https://vivantvalley.sell.app/product/vivantvalley10"),
        new HostedPurchaseOption(20, "https://vivantvalley.sell.app/product/vivantvalley20"),
    });

    public static IReadOnlyList<HostedPurchaseOption> ForLocale(string locale)
        => L.Normalize(locale) == "zh" ? chinese : english;

    // Only storefront prices need conversion. API balances already use account units.
    public static decimal PurchaseDisplayAmount(decimal amount, string locale)
        => L.Normalize(locale) == "zh" ? amount : amount * 3m;

    public static string Balance(long micros)
    {
        if (micros < 0) return L.T("text.7dc03a35bbc2");
        decimal amount = micros / 1_000_000m;
        return L.IsChinese
            ? L.T("text.0dd974d87a2f") + amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            : L.F("hosted.balance.points", $"Balance: {amount:0.######} points");
    }

    public static string Redeemed(long micros)
        => L.F("hosted.redeem.success", $"Balance: {micros / 1_000_000m:0.######}");
}
