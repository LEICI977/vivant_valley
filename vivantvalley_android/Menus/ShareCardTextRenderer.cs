using System.Globalization;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace VivantValley.Menus;

/// <summary>Use the game's localized fonts on Android; no desktop Skia native library is required.</summary>
public sealed class ShareCardTextRenderer : IDisposable
{
    private readonly Dictionary<SpriteFont, HashSet<char>> glyphs = new();
    private readonly Dictionary<(SpriteFont Font, string Text, float Size), float> widths = new();
    private HashSet<char> Glyphs(SpriteFont font)
    { if (!glyphs.TryGetValue(font, out var set)) glyphs[font] = set = font.Characters.ToHashSet(); return set; }
    private SpriteFont? chinese;
    private bool triedChinese;
    public string FontFamily => "Game localized font";
    private IEnumerable<(SpriteFont Font, string Text)> Runs(string text)
    {
        var primary = Game1.smallFont;
        SpriteFont current = primary;
        var buffer = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var font = primary;
            char ch = rune.IsBmp ? (char)rune.Value : '\uFFFD';
            if (!Glyphs(primary).Contains(ch))
            {
                if (!triedChinese)
                {
                    triedChinese = true;
                    try { chinese = Game1.content.Load<SpriteFont>("Fonts/SmallFont.zh-CN"); }
                    catch { /* An optional language font can be absent on some loaders. */ }
                }
                if (chinese is not null && Glyphs(chinese).Contains(ch)) font = chinese;
                else ch = primary.DefaultCharacter ?? (Glyphs(primary).Contains('?') ? '?' : primary.Characters[0]);
            }
            if (!ReferenceEquals(font, current) && buffer.Length > 0)
            { yield return (current, buffer.ToString()); buffer.Clear(); }
            current = font; buffer.Append(ch);
        }
        if (buffer.Length > 0) yield return (current, buffer.ToString());
    }
    private static float Scale(SpriteFont font, float size) => size / Math.Max(1, font.LineSpacing);
    public float Measure(string text, float size, bool bold = false)
    {
        var key = (Game1.smallFont, text, size);
        if (widths.TryGetValue(key, out float width)) return width;
        width = Runs(text).Sum(run => run.Font.MeasureString(run.Text).X * Scale(run.Font, size));
        if (widths.Count >= 2048) widths.Clear();
        widths[key] = width; return width;
    }
    public string Fit(string text, float size, float width, bool bold = false)
    {
        if (Measure(text, size, bold) <= width) return text;
        var buffer = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string next = elements.GetTextElement();
            if (Measure(buffer.ToString() + next + "…", size, bold) > width) break;
            buffer.Append(next);
        }
        return buffer + "…";
    }
    public void Draw(SpriteBatch batch, string text, Vector2 position, float size, Color color, float outputScale, bool bold = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var run in Runs(text))
        {
            float scale = Scale(run.Font, size);
            batch.DrawString(run.Font, run.Text, position, color, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
            if (bold) batch.DrawString(run.Font, run.Text, position + new Vector2(.5f, 0), color, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
            position.X += run.Font.MeasureString(run.Text).X * scale;
        }
    }
    public void Dispose() { widths.Clear(); glyphs.Clear(); } // The game owns its fonts and textures.
}
