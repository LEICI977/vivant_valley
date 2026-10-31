using System.Text.Json;
using Microsoft.Xna.Framework;
using VivantValley;
using VivantValley.Menus;
using VivantValley.Services;

internal static class MobileParityTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run()
    {
        foreach (var safe in new[] { new Rectangle(20,20,320,580), new Rectangle(30,10,780,340), new Rectangle(50,30,1180,660), new Rectangle(15,15,440,850) })
        {
            var layout = MobileSharePolicy.Layout(safe, 56);
            Check(safe.Contains(layout.Header) && safe.Contains(layout.Content) && safe.Contains(layout.Footer)
                && layout.Header.Bottom < layout.Content.Top && layout.Content.Bottom < layout.Footer.Top
                && layout.ControlHeight >= 44, "Mobile sharing layout overflows or controls overlap.");
        }
        Check(MobileSharePolicy.NormalizeText("第一行\r\n第二行👩‍🌾", 100, true) == "第一行\n第二行👩‍🌾", "Multiline IME lost newline or Unicode.");
        Check(MobileSharePolicy.NormalizeText("ab👩‍🌾c", 4, true) == "ab", "IME limit split a grapheme.");
        Check(MobileSharePolicy.NormalizeText("one\ntwo", 100, false) == "one two", "Single-line edit gained a newline.");
        var input = new ShareStudioInput { Multiline = true, Limit = 20, Text = "old" };
        int checkpoints = 0; input.BeforeEdit = () => checkpoints++;
        input.ApplyNative("new\n中文"); input.ApplyNative("new\n中文");
        Check(input.Text == "new\n中文" && input.CaretIndex == input.Text.Length && checkpoints == 1, "Native share edit lost undo checkpoint or newline.");
        var originalFont = StardewValley.Game1.smallFont;
        try
        {
            var glyph = new Rectangle(0, 0, 10, 16);
            StardewValley.Game1.smallFont = new Microsoft.Xna.Framework.Graphics.SpriteFont(null!,
                new() { glyph, glyph }, new() { glyph, glyph }, new() { '?', 'A' }, 20, 0,
                new() { new Vector3(0,10,0), new Vector3(0,10,0) }, '?');
            using var renderer = new ShareCardTextRenderer();
            Check(renderer.Measure("AA", 20) == 20 && renderer.Measure("missing中文😀", 20) > 0,
                "Game-font measurement failed or unsupported glyphs throw instead of falling back.");
            Check(renderer.Measure(renderer.Fit("AAAAAAAAAA", 20, 40), 20) <= 40,
                "Mobile font fitting exceeds available width.");
        }
        finally { StardewValley.Game1.smallFont = originalFont; }
        var sticker = new ShareSticker("test", 2, 360, 400);
        var moved = MobileSharePolicy.MoveSticker(sticker, new(-100, 10000), 720, 960);
        Check(moved.Page == 2 && moved.X == moved.Size / 2 && moved.Y + moved.Size / 2 == 960 - 66, "Touch sticker escaped the card or footer.");
        Check(MobileSharePolicy.PortraitPlacement(4096, 2048) == new Rectangle(0,128,512,256)
            && MobileSharePolicy.PortraitPlacement(128,256) == new Rectangle(128,0,256,512), "Portrait resizing distorts or exceeds the mobile texture limit.");
        Check(MobileSharePolicy.IsPortraitFile("A.PNG") && MobileSharePolicy.IsPortraitFile("a.jpeg")
            && !MobileSharePolicy.IsPortraitFile("a.exe") && !MobileSharePolicy.IsPortraitFile("a.png.exe"), "Portrait import file filter failed.");
        var gesture = new TouchTapGesture(); gesture.Press(100,100); gesture.Move(160,100);
        Check(gesture.Dragged && !gesture.Release(160,100), "Horizontal sticker drag became a button tap.");
        // Every mobile template/ratio retains all conversation text with both language samples.
        foreach (var locale in new[] { "zh", "en" }) using (L.BeginRequest(locale))
        {
            string original = string.Concat(Enumerable.Repeat(locale == "zh" ? "一起去海边看星星。" : "Let's watch the stars together. ", 100));
            var turns = new[] { new ShareTurn(original, original) };
            foreach (var template in ShareTemplate.All) foreach (var ratio in ShareRatio.All)
            {
                var pages = ShareStudioLayout.Build(turns, new[] { true }, template, ratio, "Title", "Subtitle", original[..20], false,
                    (text,size,bold) => System.Globalization.StringInfo.ParseCombiningCharacters(text).Length * size * .8f);
                string body = string.Concat(pages.SelectMany(p => p.Messages).SelectMany(m => m.Bubble.Lines));
                static string Compact(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
                Check(Compact(body) == Compact(original + original), "Mobile sharing pagination dropped dialogue.");
                Check(pages.SelectMany(p => p.Messages).All(m => m.Bounds.Y + m.Bounds.Height <= ratio.Height - 65), "Share content crossed the footer.");
            }
        }
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "assets", "share", "catalog.json"))) root = Directory.GetParent(root)?.FullName;
        Check(root is not null, "Cannot find mobile sharing assets for validation.");
        var catalog = ShareCardModel.LoadCatalog(Path.Combine(root!, "assets", "share"));
        Check(catalog.Count > 1000, "Mobile package only contains dream assets, not the share library.");
        foreach (var asset in catalog)
        {
            Check(File.Exists(ShareCardModel.SafePath(Path.Combine(root!, "assets", "share"), asset.Path)), "Missing share asset " + asset.Id);
            Check(File.Exists(ShareCardModel.SafePath(Path.Combine(root!, "assets", "share"), asset.Thumbnail)), "Missing share thumbnail " + asset.Id);
        }
        using var dream = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!, "assets", "dreams", "catalog.json")));
        foreach (var asset in dream.RootElement.EnumerateArray())
            Check(File.Exists(Path.Combine(root!, asset.GetProperty("Path").GetString()!)), "Dream asset was lost while adding sharing.");
        Console.WriteLine("Mobile parity: responsive share geometry, native multiline/Unicode/undo, sticker constraints, all templates/ratios, complete share/dream assets passed.");
    }
}
