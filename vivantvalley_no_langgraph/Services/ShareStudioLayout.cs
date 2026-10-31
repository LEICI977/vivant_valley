namespace VivantValley.Services;

public sealed record ShareTemplate(string Id, string Chinese, string English, string Background,
    string Ink, string Accent, string Paper, string PlayerPaper, string Muted,
    string TitleZh, string TitleEn, string SubtitleZh, string SubtitleEn)
{
    public static readonly ShareTemplate[] All =
    {
        new("01", "鹈鹕镇聊天窗", "Pelican conversations", "F5F3E5", "314C37", "527252", "FFFEF8", "DBE8BB", "718069",
            "把这一刻，留在今天。", "A moment worth keeping.", "和你的一段日常。", "A little moment with you."),
        new("02", "春日手账", "Spring scrapbook", "F4EDDE", "644938", "9A684D", "E8EDDB", "F0E9D8", "8C8069",
            "这一页，想好好收藏。", "A page to keep.", "记下我们的小小日常。", "Small moments, carefully kept."),
        new("03", "雨夜私语", "Night letters", "203746", "F8ECCC", "B7CBD0", "DAE6DE", "304B59", "C0CED1",
            "有些话，慢慢说。", "Some words take time.", "写给夜色，也写给你。", "For the night. For you."),
        new("04", "今日份心动", "A little heartbeat", "F2EDB6", "253D30", "B94730", "FFFAE8", "DFE7B1", "74805A",
            "原来，你都记得。", "You remembered.", "一段想反复读的对话。", "Words I want to read again."),
        new("05", "夜色偏爱", "After hours", "241F30", "F9E9E9", "C989A4", "F1E2E9", "403247", "C9ADBF",
            "再靠近一点，\n我就不躲了。", "A little closer.\nI will stay.", "有些距离，只留给你。", "A little space, just for you."),
        new("06", "嘴硬心软", "The soft side", "F2E9D8", "334C63", "A84444", "334C63", "FFFAF0", "887567",
            "嘴上说着随便，\n却把偏爱给了我。", "A softer side,\njust for me.", "把没说出口的，留在这一页。", "Keeping the unspoken on this page."),
        new("07", "脸红日记", "The blush diary", "FBF1E8", "A34757", "C58294", "FFFAF7", "F2DCDC", "A77C7D",
            "别看我，\n会脸红的。", "Don't look.\nI might blush.", "今天的心动，有一点藏不住。", "A little harder to hide today."),
    };
    public static ShareTemplate Get(string id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
    public bool Hero => Id is "02" or "05" or "06" or "07";
    public bool Chat => Id is "01" or "04";
}

public sealed record ShareRatio(string Id, int Height)
{
    public const int Width = 720;
    public static readonly ShareRatio[] All = { new("3:4", 960), new("4:5", 900), new("1:1", 720), new("9:16", 1280) };
    public static ShareRatio Get(string id) => All.FirstOrDefault(r => r.Id == id) ?? All[0];
}

public readonly record struct ShareBox(float X, float Y, float Width, float Height);
public sealed record SharePlacedMessage(ShareBubble Bubble, ShareBox Bounds, float FontSize, float LineHeight, bool Hero = false);
public sealed class ShareStudioPage
{
    public bool Cover { get; init; }
    public bool First { get; init; }
    public string[] TitleLines { get; set; } = Array.Empty<string>();
    public string[] SubtitleLines { get; set; } = Array.Empty<string>();
    public float TitleSize { get; set; }
    public float SubtitleY { get; set; }
    public float BodyTop { get; set; }
    public ShareBox? HeroPortrait { get; set; }
    public List<SharePlacedMessage> Messages { get; } = new();
    public string[] QuoteLines { get; set; } = Array.Empty<string>();
    public float QuoteSize { get; set; }
    public ShareBox QuoteBounds { get; set; }
}

/// <summary>One layout plan drives preview, export and hit regions. No game state or graphics resources.</summary>
public static class ShareStudioLayout
{
    public const float BodySize = 30, LineHeight = 45;
    public static List<ShareStudioPage> Build(IReadOnlyList<ShareTurn> turns, IReadOnlyList<bool> selected,
        ShareTemplate template, ShareRatio ratio, string title, string subtitle, string quote, bool cover,
        Func<string, float, bool, float> measure)
    {
        var pages = new List<ShareStudioPage>();
        float bottom = ratio.Height - 88;
        string[] Wrap(string text, float width, float size, bool bold = false)
            => ShareCardModel.Wrap(text, s => measure(s, size, bold), width).ToArray();
        ShareStudioPage NewPage(bool first, bool isCover = false)
        {
            var p = new ShareStudioPage { First = first, Cover = isCover, TitleSize = ratio.Height == 720 ? 40 : template.Id == "07" ? 56 : 48 };
            if (first && !string.IsNullOrWhiteSpace(title))
            {
                p.TitleLines = Wrap(title, 624, p.TitleSize, true);
                while (p.TitleLines.Length > 2 && p.TitleSize > 34) p.TitleLines = Wrap(title, 624, --p.TitleSize, true);
                if (p.TitleLines.Length > 2) throw new InvalidOperationException(L.IsChinese ? "标题太长，请精简为两行。" : "Please shorten the title to two lines.");
            }
            float y = first ? (ratio.Height == 720 ? 98 : 106) + p.TitleLines.Length * p.TitleSize * (ratio.Height == 720 ? 1.28f : 1.36f) : 108;
            p.SubtitleY = y + 9;
            if (first && !string.IsNullOrWhiteSpace(subtitle))
            {
                p.SubtitleLines = Wrap(subtitle, 624, 19);
                if (p.SubtitleLines.Length > 2) throw new InvalidOperationException(L.IsChinese ? "副标题太长，请精简。" : "Please shorten the subtitle.");
                y = p.SubtitleY + p.SubtitleLines.Length * (ratio.Height == 720 ? 25 : 29);
            }
            p.BodyTop = y + (ratio.Height == 720 ? 20 : 30);
            pages.Add(p); return p;
        }
        var messages = turns.SelectMany((t, i) => i < selected.Count && selected[i]
            ? new[] { (Text: t.Player, Player: true, Index: i), (Text: t.Reply, Player: false, Index: i) }
            : Array.Empty<(string Text, bool Player, int Index)>()).ToList();
        if (cover && template.Id == "04" && messages.Count > 0)
        {
            int qi = messages.FindIndex(m => !m.Player && !string.IsNullOrWhiteSpace(quote) && m.Text.Contains(quote, StringComparison.Ordinal));
            if (qi < 0) throw new InvalidOperationException(L.IsChinese ? "请从已选对话中选择封面原句。" : "Choose a cover quote from the selected replies.");
            var p = NewPage(true, true);
            float heroHeight = Math.Min(276, Math.Max(160, bottom - p.BodyTop - 160));
            p.HeroPortrait = new ShareBox(44, p.BodyTop, 256, heroHeight);
            p.QuoteBounds = new ShareBox(335, p.BodyTop + 12, 336, heroHeight - 48);
            p.QuoteSize = 38;
            p.QuoteLines = Wrap(quote, p.QuoteBounds.Width, p.QuoteSize);
            while (p.QuoteLines.Length * p.QuoteSize * 1.45f > p.QuoteBounds.Height && p.QuoteSize > 28)
                p.QuoteLines = Wrap(quote, p.QuoteBounds.Width, --p.QuoteSize);
            if (p.QuoteLines.Length * p.QuoteSize * 1.45f > p.QuoteBounds.Height)
                throw new InvalidOperationException(L.IsChinese ? "封面原句太长，请选短一些的片段，或关闭封面。" : "Choose a shorter quote, or turn off the cover.");
            var context = messages[qi - 1];
            string[] contextLines = Wrap(context.Text, 514, 25);
            float cy = p.BodyTop + heroHeight + 22;
            if (cy + contextLines.Length * 37 + 78 <= bottom)
                p.Messages.Add(new(new ShareBubble(context.Text, true, context.Index, contextLines, 632, contextLines.Length * 37 + 78, false),
                    new ShareBox(44, cy, 632, contextLines.Length * 37 + 78), 25, 37, true));
        }
        var current = NewPage(pages.Count == 0);
        float used = current.BodyTop;
        if (current.First && template.Hero)
        {
            float heroHeight = Math.Min(230, Math.Max(176, bottom - used - 190));
            current.HeroPortrait = new ShareBox(424, used, 246, heroHeight);
            if (messages.Count > 0)
            {
                var first = messages[0];
                float heroFont = ratio.Height == 720 ? 26 : 28;
                float heroLine = heroFont * 1.5f;
                var lines = Wrap(first.Text, 304, heroFont);
                float height = lines.Length * heroLine + 68;
                if (height <= heroHeight)
                {
                    current.Messages.Add(new(new ShareBubble(first.Text, true, first.Index, lines, 338, height, false),
                        new ShareBox(44, used + (heroHeight - height) / 2, 338, height), heroFont, heroLine, true));
                    messages.RemoveAt(0);
                }
            }
            used += heroHeight + 28;
        }
        foreach (var message in messages)
        {
            bool chat = template.Chat;
            float width = chat ? 432 : 632, textWidth = chat ? width - 40 : width - 112;
            var lines = Wrap(message.Text, textWidth, BodySize);
            float overhead = chat ? 36 : 66;
            float total = lines.Length * LineHeight + overhead;
            if (used + total > bottom && total <= bottom - 108)
            { current = NewPage(false); used = current.BodyTop; }
            for (int offset = 0; offset < lines.Length;)
            {
                int capacity = (int)MathF.Floor((bottom - used - overhead) / LineHeight);
                if (capacity < 1) { current = NewPage(false); used = current.BodyTop; continue; }
                int count = Math.Min(capacity, lines.Length - offset);
                var chunk = lines.Skip(offset).Take(count).ToArray();
                float bw = chat ? Math.Clamp(chunk.Max(s => measure(s, BodySize, false)) + 40, 130, width) : width;
                float bh = count * LineHeight + overhead;
                float x = chat ? message.Player ? 572 - bw : 148 : 44;
                current.Messages.Add(new(new ShareBubble(message.Text, message.Player, message.Index, chunk, bw, bh, offset > 0),
                    new ShareBox(x, used, bw, bh), BodySize, LineHeight));
                used += bh + 26; offset += count;
                if (offset < lines.Length) { current = NewPage(false); used = current.BodyTop; }
            }
        }
        // Short conversations use breathing room across the page, while long
        // conversations retain compact spacing and continue without shrinking.
        foreach (var p in pages.Where(p => p.First))
        {
            float last = Math.Max(p.HeroPortrait is ShareBox hero ? hero.Y + hero.Height : 0,
                p.Messages.Count > 0 ? p.Messages.Max(m => m.Bounds.Y + m.Bounds.Height) : p.BodyTop);
            float shift = Math.Min(105, Math.Max(0, bottom - last - 35) * .4f);
            if (shift <= 0) continue;
            if (p.HeroPortrait is ShareBox hp) p.HeroPortrait = hp with { Y = hp.Y + shift };
            if (p.Cover) p.QuoteBounds = p.QuoteBounds with { Y = p.QuoteBounds.Y + shift };
            for (int i = 0; i < p.Messages.Count; i++) p.Messages[i] = p.Messages[i] with { Bounds = p.Messages[i].Bounds with { Y = p.Messages[i].Bounds.Y + shift } };
        }
        return pages;
    }
}
