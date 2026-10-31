using VivantValley;
using VivantValley.Services;

internal static class HostedPurchaseTests
{
    public static void Run()
    {
        var zh = HostedPurchaseCatalog.ForLocale("zh-CN");
        var en = HostedPurchaseCatalog.ForLocale("en-US");
        Check(zh.Select(x => x.Amount).SequenceEqual(new[] { 1, 5, 10, 20 }), "Chinese offers changed.");
        Check(zh.Select(x => x.Url).SequenceEqual(new[] { "https://pay.ldxp.cn/item/jxtjmi",
            "https://pay.ldxp.cn/item/eip1kt", "https://pay.ldxp.cn/item/mus6ix", "https://pay.ldxp.cn/item/mpk1fb" }), "Chinese URLs changed.");
        Check(en.Select(x => x.Amount).SequenceEqual(new[] { 5, 10, 20 }), "English must have three offers and no $1.");
        foreach (var offer in en)
            Check(offer.Url == $"https://vivantvalley.sell.app/product/vivantvalley{offer.Amount}", "English amount/URL mismatch.");
        foreach (string locale in new[] { "en", "zh", "en-US", "zh-CN" })
        using (L.BeginRequest(locale))
        {
            bool chinese = L.IsChinese;
            Check(HostedPurchaseCatalog.ForLocale(L.Locale).Count == (chinese ? 4 : 3), "Locale switch retained stale offers.");
            string label = L.F("text.c93f57f25d7e", $"购买 ¥{HostedPurchaseCatalog.PurchaseDisplayAmount(5, L.Locale)}");
            Check(label == (chinese ? "购买 ¥5" : "Buy 15 points"), "Wrong purchase units.");
            Check(HostedPurchaseCatalog.Balance(5_000_000) == (chinese ? "托管余额：¥5.00" : "Balance: 5 points"), "API balance must display 1:1.");
            Check(HostedPurchaseCatalog.Balance(12_500_000) == (chinese ? "托管余额：¥12.50" : "Balance: 12.5 points"), "Fractional balance display failed.");
            Check(HostedPurchaseCatalog.Balance(-1) == L.T("text.7dc03a35bbc2"), "Missing balance became a negative point value.");
            if (chinese) Check(L.T("text.2b6d7a9c37b8").Contains("¥0.89"), "Chinese price changed.");
            else
            {
                Check(L.T("text.2b6d7a9c37b8") == "Cost: 1 point per 1,000,000 tokens.", "English cost differs from agreed rate.");
                Check(L.T("text.523b7f1d570c").Contains("1 USD = 3 points"), "Exchange explanation missing.");
                Check(HostedPurchaseCatalog.Balance(1) == "Balance: 0.000001 points", "Small balance lost precision.");
                Check(HostedPurchaseCatalog.Balance(15_000_000) == "Balance: 15 points", "Backend credited points were multiplied again.");
                Check(HostedPurchaseCatalog.Redeemed(15_000_000) == "Code redeemed. Current balance: 15 points", "Redeemed backend points were multiplied again.");
                Check(en.Select(x => HostedPurchaseCatalog.PurchaseDisplayAmount(x.Amount, L.Locale)).SequenceEqual(new[] { 15m, 30m, 60m }), "Wrong point offers.");
            }
            Check(HostedPurchaseCatalog.Redeemed(5_125_000) == (chinese ? "兑换成功，当前额度：5.125" : "Code redeemed. Current balance: 5.125 points"), "Redeem display mismatch.");
            Check(L.F("text.9d0061822340", $"已打开 ¥{HostedPurchaseCatalog.PurchaseDisplayAmount(10, L.Locale)} 购买页面，购买后请将卡密粘贴到兑换框。")
                .Contains(chinese ? "¥10" : "30-point"), "Purchase confirmation mismatch.");
        }
        Console.WriteLine("Hosted purchase locales, URLs and currency tests passed.");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
