using System.Globalization;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;

namespace VivantValley.Menus;

/// <summary>Outline-font input for the studio; Unicode caret and multiline titles.</summary>
internal sealed class ShareStudioInput : IKeyboardSubscriber
{
    public Rectangle Bounds;
    public bool Selected { get; set; }
    public int CaretIndex => caret;
    public void ApplyNative(string text)
    { string next = VivantValley.Services.MobileSharePolicy.NormalizeText(text, Limit, Multiline); Change(next, next.Length); }
    public bool Multiline { get; init; }
    public int Limit { get; init; } = 200;
    public Action? BeforeEdit;
    private string value = "";
    private int caret;
    public string Text { get => value; set { this.value = value; caret = value.Length; } }
    private int[] Boundaries => StringInfo.ParseCombiningCharacters(value).Append(value.Length).ToArray();
    private void Change(string next, int position)
    { if (next == value) return; BeforeEdit?.Invoke(); value = next; caret = position; }
    public void RecieveTextInput(char c) { if (!char.IsControl(c)) RecieveTextInput(c.ToString()); }
    public void RecieveTextInput(string text)
    {
        if (!Selected) return;
        text = text.Replace("\r", "");
        if (!Multiline) text = text.Replace('\n', ' ');
        text = string.Concat(text.Where(c => c == '\n' || !char.IsControl(c)));
        if (value.Length + text.Length > Limit) return;
        Change(value.Insert(caret, text), caret + text.Length);
    }
    public void RecieveCommandInput(char c)
    {
        if (!Selected) return;
        if (c == '\b' && caret > 0)
        { int p = Boundaries.Last(b => b < caret); Change(value.Remove(p, caret - p), p); }
        if (c == '\r' && Multiline) RecieveTextInput("\n");
    }
    public void RecieveSpecialInput(Keys key)
    {
        if (!Selected) return;
        if (key == Keys.Left) caret = Boundaries.LastOrDefault(b => b < caret);
        if (key == Keys.Right) caret = Boundaries.FirstOrDefault(b => b > caret, value.Length);
        if (key == Keys.Home) caret = 0;
        if (key == Keys.End) caret = value.Length;
        if (key == Keys.Delete && caret < value.Length) Change(value.Remove(caret, Boundaries.First(b => b > caret) - caret), caret);
    }
    public void SelectAt(int x, int y, ShareCardTextRenderer font)
    {
        Selected = true; int row = Multiline ? Math.Clamp((y - Bounds.Y - 8) / 25, 0, 2) : 0;
        var lines = value.Split('\n'); row = Math.Min(row, lines.Length - 1);
        int start = lines.Take(row).Sum(s => s.Length + 1); caret = start;
        foreach (int boundary in StringInfo.ParseCombiningCharacters(lines[row]).Append(lines[row].Length))
        { if (font.Measure(lines[row][..boundary], 17) <= x - Bounds.X - 12) caret = start + boundary; }
    }
    public void Draw(SpriteBatch b, ShareCardTextRenderer font, Action<Rectangle, Color, int> box)
    {
        box(Bounds, Selected ? new Color(100, 132, 107) : new Color(216, 214, 203), 8);
        box(new Rectangle(Bounds.X + 1, Bounds.Y + 1, Bounds.Width - 2, Bounds.Height - 2), new Color(255, 253, 247), 7);
        if (VivantValley.Services.MobileRuntime.IsEditing(Bounds)) return;
        var lines = value.Split('\n'); int maxRows = Math.Max(1, (Bounds.Height - 12) / 25);
        int caretRow = value[..caret].Count(c => c == '\n'); int first = Math.Max(0, caretRow - maxRows + 1);
        int rowStart = value.LastIndexOf('\n', Math.Max(0, caret - 1), caret);
        string prefix = value[(rowStart + 1)..caret];
        float shift = Math.Max(0, font.Measure(prefix, 17) - Bounds.Width + 32);
        for (int i = first; i < Math.Min(lines.Length, first + maxRows); i++)
        {
            string line = lines[i];
            if (i == caretRow && shift > 0)
                while (line.Length > 0 && font.Measure(line, 17) > Bounds.Width - 26 && font.Measure(prefix, 17) > Bounds.Width - 32)
                { int count = StringInfo.GetNextTextElement(line).Length; line = line[count..]; prefix = prefix.Length >= count ? prefix[count..] : ""; }
            font.Draw(b, font.Fit(line, 17, Bounds.Width - 24), new Vector2(Bounds.X + 12, Bounds.Y + 7 + (i - first) * 25), 17, new Color(53, 66, 53), 1);
        }
        if (Selected && (DateTime.UtcNow.Millisecond < 550))
        {
            int cx = Bounds.X + 12 + (int)Math.Min(Bounds.Width - 26, font.Measure(prefix, 17));
            b.Draw(Game1.staminaRect, new Rectangle(cx, Bounds.Y + 10 + (caretRow - first) * 25, 2, 20), new Color(70, 103, 77));
        }
    }
}
