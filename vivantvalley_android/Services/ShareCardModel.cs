using System.Globalization;
using System.Text.Json;

namespace VivantValley.Services;

public sealed record ShareTurn(string Player, string Reply);
public sealed record ShareAsset(string Id, string Kind, string Category, string Name,
    string Path, string Thumbnail, int Width, int Height, string[]? People = null, SharePortrait[]? Portraits = null);
public sealed record SharePortrait(float X, float Y, float Width, float Height);
public sealed record ShareSticker(string AssetId, int Page, float X, float Y, float Size = 100,
    float Rotation = 0, bool Flipped = false);
public sealed record ShareLine(string Text, bool Heading);
public sealed record ShareBubble(string Text, bool IsPlayer, int TurnIndex,
    IReadOnlyList<string> Lines, float Width, float Height, bool IsContinuation);

/// <summary>Shared measurements for pagination and rendering, in 720px canvas coordinates.</summary>
public sealed record ShareChatLayout(float FontLineSpacing)
{
    public const int CardWidth = 720, CardHeight = 960;
    public const float ExportScale = 2f, BodyFontSize = 32f;
    public const int AvatarSize = 88, Margin = 24, AvatarGap = 18;
    public const int HeaderHeight = 136, BodyTop = 168, BodyBottom = 856, FooterTop = 884;
    public const int PaddingX = 22, PaddingY = 18, RowGap = 26;
    public const int BubbleInset = Margin + AvatarSize + AvatarGap;
    public const int MaxBubbleWidth = CardWidth - BubbleInset * 2;
    public const int TextWidth = MaxBubbleWidth - PaddingX * 2;
    public const int PageHeight = BodyBottom - BodyTop;
    public float LineHeight => MathF.Ceiling(FontLineSpacing);
    public float BubbleHeight(int lines) => lines * LineHeight + PaddingY * 2;
    public float RowHeight(int lines) => Math.Max(AvatarSize, BubbleHeight(lines));
}

public static class ShareCardModel
{
    public static IReadOnlyList<ShareTurn> RecentTurns(IEnumerable<ConversationMemoryMessage> messages)
    {
        var turns = new List<ShareTurn>();
        string? pending = null;
        foreach (var message in messages)
        {
            if (message.Source != ConversationMemorySources.AiChat) continue;
            if (message.Role == "user") pending = message.Content;
            else if (message.Role == "assistant" && pending is not null)
            {
                if (!string.IsNullOrWhiteSpace(pending) && !string.IsNullOrWhiteSpace(message.Content))
                    turns.Add(new ShareTurn(pending, message.Content));
                pending = null;
            }
        }
        return turns.TakeLast(5).ToArray();
    }

    public static string SafePath(string root, string relative)
    {
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!full.StartsWith(System.IO.Path.GetFullPath(root) + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid asset path.");
        return full;
    }

    public static IReadOnlyList<ShareAsset> LoadCatalog(string root)
        => JsonSerializer.Deserialize<List<ShareAsset>>(File.ReadAllText(System.IO.Path.Combine(root, "catalog.json")))
           ?? new List<ShareAsset>();

    public static IEnumerable<string> Wrap(string text, Func<string, float> measure, float width)
    {
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            string line = "";
            var elements = StringInfo.GetTextElementEnumerator(paragraph);
            while (elements.MoveNext())
            {
                string element = elements.GetTextElement();
                if (line.Length > 0 && measure(line + element) > width)
                {
                    int space = line.LastIndexOf(' ');
                    if (space > line.Length / 2)
                    {
                        yield return line[..space];
                        line = line[(space + 1)..];
                    }
                    else
                    {
                        int[] starts = StringInfo.ParseCombiningCharacters(line);
                        const string closing = "，。！？、；：）》】」』”’!?.,;:%)]}";
                        const string opening = "（《【「『“‘([{\"";
                        bool keepWithNeighbor = closing.Contains(element, StringComparison.Ordinal)
                            || opening.Contains(line[starts[^1]..], StringComparison.Ordinal);
                        if (keepWithNeighbor && starts.Length > 1)
                        {
                            int split = starts.Length - 1;
                            while (split > 0 && closing.Contains(line[starts[split]..(split + 1 < starts.Length ? starts[split + 1] : line.Length)], StringComparison.Ordinal)) split--;
                            while (split > 0 && opening.Contains(line[starts[split - 1]..starts[split]], StringComparison.Ordinal)) split--;
                            if (split > 0)
                            {
                                yield return line[..starts[split]];
                                line = line[starts[split]..];
                            }
                            else { yield return line; line = ""; }
                        }
                        else { yield return line; line = ""; }
                    }
                }
                line += element;
            }
            yield return line;
        }
    }

    public static List<List<ShareLine>> Paginate(IReadOnlyList<ShareTurn> turns, IReadOnlyList<bool> selected,
        string player, string npc, Func<string, float> measure, int linesPerPage = 22)
    {
        var pages = new List<List<ShareLine>> { new() };
        void Add(ShareLine line)
        {
            if (pages[^1].Count >= linesPerPage) pages.Add(new());
            pages[^1].Add(line);
        }
        for (int i = 0; i < turns.Count; i++)
        {
            if (!selected[i]) continue;
            var block = new List<ShareLine>();
            foreach (var message in new[] { (player, turns[i].Player), (npc, turns[i].Reply) })
            {
                block.Add(new ShareLine(message.Item1, true));
                block.AddRange(Wrap(message.Item2, measure, 548).Select(text => new ShareLine(text, false)));
                block.Add(new ShareLine("", false));
            }
            if (block.Count <= linesPerPage && pages[^1].Count > 0
                && pages[^1].Count + block.Count > linesPerPage) pages.Add(new());
            foreach (var line in block) Add(line);
        }
        return pages;
    }

    /// <summary>Builds the message stream used by the chat screenshot layout.</summary>
    public static List<List<ShareBubble>> PaginateBubbles(
        IReadOnlyList<ShareTurn> turns,
        IReadOnlyList<bool> selected,
        Func<string, float> measure,
        ShareChatLayout layout)
    {
        var pages = new List<List<ShareBubble>> { new() };
        float usedHeight = 0;
        void NextPage()
        {
            pages.Add(new());
            usedHeight = 0;
        }
        foreach ((ShareTurn turn, int index) in turns.Select((value, index) => (value, index)))
        {
            if (!selected[index])
                continue;

            foreach ((string text, bool isPlayer) in new[]
            {
                (turn.Player, true),
                (turn.Reply, false),
            })
            {
                string[] lines = Wrap(text, measure,
                    ShareChatLayout.TextWidth).ToArray();
                // Keep ordinary messages together. Only messages taller than a
                // whole page are split, with their speaker/avatar repeated.
                if (usedHeight > 0 && layout.RowHeight(lines.Length) <= ShareChatLayout.PageHeight
                    && usedHeight + ShareChatLayout.RowGap + layout.RowHeight(lines.Length) > ShareChatLayout.PageHeight)
                    NextPage();

                for (int offset = 0; offset < lines.Length;)
                {
                    float gap = pages[^1].Count == 0 ? 0 : ShareChatLayout.RowGap;
                    float remaining = ShareChatLayout.PageHeight - usedHeight - gap;
                    int capacity = (int)MathF.Floor((remaining - ShareChatLayout.PaddingY * 2) / layout.LineHeight);
                    if (capacity < 1 || remaining < ShareChatLayout.AvatarSize)
                    {
                        if (pages[^1].Count == 0) throw new InvalidOperationException("Share font exceeds page height.");
                        NextPage();
                        continue;
                    }
                    int count = Math.Min(capacity, lines.Length - offset);
                    string[] chunk = lines.Skip(offset).Take(count).ToArray();
                    float width = Math.Clamp(chunk.Max(measure) + ShareChatLayout.PaddingX * 2,
                        112, ShareChatLayout.MaxBubbleWidth);
                    pages[^1].Add(new ShareBubble(offset == 0 && count == lines.Length ? text : string.Join("\n", chunk),
                        isPlayer, index, chunk, width, layout.BubbleHeight(count), offset > 0));
                    usedHeight += gap + layout.RowHeight(count);
                    offset += count;
                    if (offset < lines.Length) NextPage();
                }
            }
        }

        return pages;
    }
}
