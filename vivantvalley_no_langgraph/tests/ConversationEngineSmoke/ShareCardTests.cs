using VivantValley;
using VivantValley.Services;
using System.Globalization;

internal static class ShareCardTests
{
    public static void Run()
    {
        var messages = new List<ConversationMemoryMessage>();
        void Add(string role, string content, string source = ConversationMemorySources.AiChat)
            => messages.Add(new() { Role = role, Content = content, Source = source });
        Add("assistant", "orphan");
        for (int i = 0; i < 8; i++)
        {
            Add("user", $"u{i}");
            Add("assistant", "private internal action", ConversationMemorySources.ModAction);
            Add("assistant", $"a{i}");
        }
        Add("user", "incomplete");
        var turns = ShareCardModel.RecentTurns(messages);
        Check(turns.Count == 5 && turns[0].Player == "u3" && turns[^1].Reply == "a7", "Recent completed turns mismatch.");
        messages[0].Content = "changed";
        Check(!turns.Any(t => t.Reply.Contains("private") || t.Player == "incomplete"), "Internal/incomplete content leaked.");
        var pages = ShareCardModel.Paginate(turns, new[] { false, false, false, false, true }, "Me", "NPC", s => s.Length * 12);
        Check(pages.Count == 1 && pages[0].Any(l => l.Text == "u7") && !pages[0].Any(l => l.Text == "u6"), "Selection not respected.");
        Check(ShareChatLayout.CardWidth * 4 == ShareChatLayout.CardHeight * 3, "Share output is not 3:4.");
        var layout = new ShareChatLayout(48);
        var bubbles = ShareCardModel.PaginateBubbles(turns, new[] { false, false, false, false, true }, s => s.Length * 12, layout);
        Check(bubbles.SelectMany(p => p).Select(b => (b.Text, b.IsPlayer)).SequenceEqual(new[]
            { ("u7", true), ("a7", false) }), "Chat bubble order or selection mismatch.");
        string unicode = string.Concat(Enumerable.Repeat("你好👩‍🌾é", 400));
        var wrapped = ShareCardModel.Wrap(unicode, s => StringInfo.ParseCombiningCharacters(s).Length * 20, 120).ToArray();
        Check(string.Concat(wrapped) == unicode && wrapped.All(s => StringInfo.ParseCombiningCharacters(s).Length <= 6), "Unicode wrap lost content.");
        var punctuation = ShareCardModel.Wrap("你好世界？欢迎（回来）！", s => s.Length * 20, 80).ToArray();
        Check(string.Concat(punctuation) == "你好世界？欢迎（回来）！" && punctuation.All(s => s.Length <= 4
            && !"？）！".Contains(s[..1]) && !s.EndsWith("（")), "Chinese punctuation was orphaned during wrapping.");
        pages = ShareCardModel.Paginate(new[] { new ShareTurn(unicode, unicode) }, new[] { true }, "Me", "NPC", s => s.Length * 12);
        Check(pages.Count > 1 && pages.All(p => p.Count <= 22), "Long conversation didn't paginate.");
        Check(string.Concat(pages.SelectMany(p => p).Where(l => !l.Heading).Select(l => l.Text)) == unicode + unicode,
            "Pagination truncated conversation.");
        foreach (float lineSpacing in new[] { 48f, 56f })
        {
            layout = new ShareChatLayout(lineSpacing);
            float Measure(string s) => StringInfo.ParseCombiningCharacters(s).Length * 20;
            bubbles = ShareCardModel.PaginateBubbles(new[] { new ShareTurn(unicode, unicode) }, new[] { true }, Measure, layout);
            Check(bubbles.Count > 1 && bubbles.All(p => p.Count > 0), "Long chat generated empty pages or failed to paginate.");
            Check(string.Concat(bubbles.SelectMany(p => p).SelectMany(b => b.Lines)) == unicode + unicode,
                "Large-text pagination lost or reordered Unicode content.");
            var allBubbles = bubbles.SelectMany(p => p).ToArray();
            int playerParts = allBubbles.TakeWhile(b => b.IsPlayer).Count();
            Check(playerParts > 1 && allBubbles.Skip(playerParts).All(b => !b.IsPlayer), "Split message speakers changed order.");
            Check(allBubbles.Count(b => !b.IsContinuation) == 2, "Long-message continuations were not marked.");
            AssertFits(bubbles, layout);

            var shortTurns = Enumerable.Range(0, 12).Select(i => new ShareTurn($"question {i}", $"reply {i}")).ToArray();
            bubbles = ShareCardModel.PaginateBubbles(shortTurns, Enumerable.Repeat(true, 12).ToArray(), Measure, layout);
            Check(bubbles.Sum(p => p.Count) == 24 && bubbles.SelectMany(p => p).All(b => !b.IsContinuation),
                "Short messages were split or dropped.");
            AssertFits(bubbles, layout);
            var empty = ShareCardModel.PaginateBubbles(turns, new bool[turns.Count], Measure, layout);
            Check(empty.Count == 1 && empty[0].Count == 0, "Empty selection should retain a blank preview page.");
        }
        string root = Path.Combine(Path.GetTempPath(), "vv-share-path-test");
        Check(ShareCardModel.SafePath(root, "images/a.png").StartsWith(root), "Valid asset path rejected.");
        bool blocked = false;
        try { ShareCardModel.SafePath(root, "../private.json"); } catch (InvalidDataException) { blocked = true; }
        Check(blocked, "Path traversal accepted.");
        Console.WriteLine("Sharing turn filtering, selection, Unicode wrapping, pagination and asset path tests passed.");
    }
    private static void AssertFits(List<List<ShareBubble>> pages, ShareChatLayout layout)
    {
        foreach (var page in pages)
        {
            float height = page.Sum(b => Math.Max(ShareChatLayout.AvatarSize, b.Height))
                + Math.Max(0, page.Count - 1) * ShareChatLayout.RowGap;
            Check(height <= ShareChatLayout.PageHeight, "Large text or avatars overflowed the page.");
            Check(page.All(b => b.Width <= ShareChatLayout.MaxBubbleWidth && b.Height == layout.BubbleHeight(b.Lines.Count)),
                "Pagination and rendering measurements differ.");
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
