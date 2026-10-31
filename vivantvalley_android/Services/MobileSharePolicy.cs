using System.Globalization;
using System.Text;
using Microsoft.Xna.Framework;

namespace VivantValley.Services;

internal readonly record struct MobileShareLayout(Rectangle Header, Rectangle Content, Rectangle Footer, int ControlHeight);
internal static class MobileSharePolicy
{
    public static MobileShareLayout Layout(Rectangle safe, int requestedHeight)
    {
        int control = Math.Clamp(requestedHeight, 44, 60);
        control = Math.Min(control, Math.Max(1, (safe.Height - 100) / 2));
        return new(new(safe.X, safe.Y, safe.Width, control),
            new(safe.X, safe.Y + control + 8, safe.Width, Math.Max(1, safe.Height - control * 2 - 40)),
            new(safe.X, safe.Bottom - control, safe.Width, control), control);
    }
    public static string NormalizeText(string text, int limit, bool multiline)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var result = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string next = elements.GetTextElement();
            if (next == "\n") next = multiline ? "\n" : " ";
            else if (next.Any(char.IsControl)) continue;
            if (result.Length + next.Length > limit) break;
            result.Append(next);
        }
        return result.ToString();
    }
    public static Rectangle PortraitPlacement(int width, int height)
    {
        float fit = Math.Min(512f / Math.Max(1, width), 512f / Math.Max(1, height));
        int w = Math.Clamp((int)(width * fit), 1, 512), h = Math.Clamp((int)(height * fit), 1, 512);
        return new((512 - w) / 2, (512 - h) / 2, w, h);
    }
    public static bool IsPortraitFile(string path) => new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static ShareSticker MoveSticker(ShareSticker sticker, Vector2 point, int cardWidth, int cardHeight)
    {
        float half = sticker.Size / 2;
        return sticker with { X = Math.Clamp(point.X, half, cardWidth - half), Y = Math.Clamp(point.Y, half, cardHeight - 66 - half) };
    }
}
