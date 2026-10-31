using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SkiaSharp;

namespace VivantValley.Menus;

/// <summary>Rasterizes outline fonts at the destination resolution, independently of game UI fonts.</summary>
public sealed class ShareCardTextRenderer : IDisposable
{
    private readonly SKTypeface regular, heading;
    private readonly Dictionary<(string Text, bool Bold), TextRun[]> runs = new();
    private readonly Dictionary<(int Codepoint, bool Bold), SKTypeface> fallbacks = new();
    private readonly Dictionary<(SKTypeface Face, float Size), SKPaint> paints = new();
    private readonly Dictionary<(string Text, float Size, float Scale, bool Bold), Texture2D> textures = new();
    private readonly Queue<(string Text, float Size, float Scale, bool Bold)> textureOrder = new();
    private long textureBytes;
    private sealed record TextRun(string Text, SKTypeface Face);
    public string FontFamily => regular.FamilyName;

    public ShareCardTextRenderer()
    {
        var available = SKFontManager.Default.FontFamilies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? family = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC",
            "Noto Sans SC", "WenQuanYi Micro Hei", "Arial Unicode MS" }.FirstOrDefault(available.Contains);
        regular = family is not null ? SKTypeface.FromFamilyName(family, SKFontStyle.Normal)
            : SKFontManager.Default.MatchCharacter('中') ?? SKTypeface.FromFamilyName(null);
        heading = SKTypeface.FromFamilyName(regular.FamilyName, SKFontStyle.Bold);
    }

    private SKPaint Paint(SKTypeface face, float size)
    {
        var key = (face, size);
        if (!paints.TryGetValue(key, out var paint))
            paints[key] = paint = new SKPaint { Typeface = face, TextSize = size, IsAntialias = true,
                SubpixelText = true, LcdRenderText = false, IsEmbeddedBitmapText = false,
                IsLinearText = true, Color = SKColors.White };
        return paint;
    }

    private TextRun[] Runs(string text, bool bold)
    {
        var key = (text, bold);
        if (runs.TryGetValue(key, out var cached)) return cached;
        var result = new List<TextRun>();
        SKTypeface primary = bold ? heading : regular;
        SKTypeface current = primary;
        var buffer = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            SKTypeface face = primary;
            if (!primary.ContainsGlyph(rune.Value))
            {
                var fallbackKey = (rune.Value, bold);
                if (!fallbacks.TryGetValue(fallbackKey, out face!))
                    fallbacks[fallbackKey] = face = SKFontManager.Default.MatchCharacter(rune.Value) ?? primary;
            }
            if (!ReferenceEquals(face, current) && buffer.Length > 0)
            {
                result.Add(new TextRun(buffer.ToString(), current));
                buffer.Clear();
            }
            current = face;
            buffer.Append(rune.ToString());
        }
        if (buffer.Length > 0) result.Add(new TextRun(buffer.ToString(), current));
        // Wrap measurement visits many prefixes of long messages; keep the cache bounded.
        if (runs.Count >= 2048) runs.Clear();
        return runs[key] = result.ToArray();
    }

    public float Measure(string text, float size, bool bold = false)
        => Runs(text, bold).Sum(run => Paint(run.Face, size).MeasureText(run.Text));

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

    public void Draw(SpriteBatch batch, string text, Vector2 position, float size, Color color,
        float outputScale, bool bold = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var key = (text, size, outputScale, bold);
        if (!textures.TryGetValue(key, out var texture))
        {
            texture = Rasterize(batch.GraphicsDevice, text, size, outputScale, bold);
            long bytes = texture.Width * (long)texture.Height * 4;
            while (textures.Count > 0 && (textures.Count >= 160 || textureBytes + bytes > 24 * 1024 * 1024))
            {
                var oldest = textureOrder.Dequeue();
                var old = textures[oldest];
                textureBytes -= old.Width * (long)old.Height * 4;
                old.Dispose(); textures.Remove(oldest);
            }
            textures[key] = texture; textureOrder.Enqueue(key); textureBytes += bytes;
        }
        // The sprite's inverse scale cancels the canvas transform. Each freshly
        // rasterized text pixel maps to one output pixel, with no bitmap enlargement.
        position = new Vector2(MathF.Round(position.X * outputScale) / outputScale,
            MathF.Round(position.Y * outputScale) / outputScale);
        batch.Draw(texture, position, null, color, 0, Vector2.Zero, 1 / outputScale, SpriteEffects.None, 0);
    }

    private Texture2D Rasterize(GraphicsDevice device, string text, float size, float scale, bool bold)
    {
        float pixelSize = size * scale;
        var textRuns = Runs(text, bold);
        float ascent = textRuns.Max(run => -Paint(run.Face, pixelSize).FontMetrics.Ascent);
        float descent = textRuns.Max(run => Paint(run.Face, pixelSize).FontMetrics.Descent);
        int width = (int)MathF.Ceiling(Measure(text, pixelSize, bold)) + 4;
        int height = (int)MathF.Ceiling(Math.Max(ascent + descent, size * 1.45f * scale)) + 4;
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            float x = 1;
            // Position is the top of the line box, not a baseline.
            float baseline = 1 + ascent;
            foreach (var run in textRuns)
            {
                var paint = Paint(run.Face, pixelSize);
                canvas.DrawText(run.Text, x, baseline, paint);
                x += paint.MeasureText(run.Text);
            }
            canvas.Flush();
        }
        var pixels = new byte[width * height * 4];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        var texture = new Texture2D(device, width, height);
        try { texture.SetData(pixels); return texture; }
        catch { texture.Dispose(); throw; }
    }

    public void Dispose()
    {
        foreach (var texture in textures.Values) texture.Dispose();
        foreach (var paint in paints.Values) paint.Dispose();
        foreach (var face in fallbacks.Values.Append(regular).Append(heading).Distinct()) face.Dispose();
        textures.Clear(); paints.Clear(); fallbacks.Clear(); runs.Clear(); textureOrder.Clear();
    }
}
