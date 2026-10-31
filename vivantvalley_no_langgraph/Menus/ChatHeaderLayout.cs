using Microsoft.Xna.Framework;

namespace VivantValley.Menus;

/// <summary>Compact, right-aligned rows and bounded overflow controls.</summary>
internal static class ChatHeaderLayout
{
    public static bool Fits(Rectangle area, IReadOnlyList<int> widths)
        => widths.Sum() + Math.Max(0, widths.Count - 1) * 8 <= area.Width;

    public static Rectangle[] Dropdown(Rectangle body, IReadOnlyList<int> widths, int rowHeight)
    {
        if (widths.Count == 0) return Array.Empty<Rectangle>();
        int width = Math.Min(Math.Max(1, body.Width - 16), widths.Max());
        int height = Math.Min(rowHeight, Math.Max(1, (body.Height - 16 - (widths.Count - 1) * 8) / widths.Count));
        return Enumerable.Range(0, widths.Count).Select(i =>
            new Rectangle(body.Right - width - 8, body.Top + 8 + i * (height + 8), width, height)).ToArray();
    }

    public static Rectangle[] Arrange(Rectangle area, IReadOnlyList<int> preferredWidths)
    {
        if (preferredWidths.Count == 0) return Array.Empty<Rectangle>();
        const int gap = 8;
        int available = Math.Max(preferredWidths.Count, area.Width - gap * (preferredWidths.Count - 1));
        float ratio = Math.Min(1f, available / (float)Math.Max(1, preferredWidths.Sum()));
        int[] widths = preferredWidths.Select(w => Math.Max(1, (int)(w * ratio))).ToArray();
        int left = area.Right - widths.Sum() - gap * (widths.Length - 1);
        var result = new Rectangle[widths.Length];
        for (int i = 0; i < widths.Length; i++)
        {
            result[i] = new Rectangle(left, area.Y, widths[i], area.Height);
            left += widths[i] + gap;
        }
        return result;
    }
}
