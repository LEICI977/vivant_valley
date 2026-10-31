using System.Globalization;
using System.Runtime.InteropServices;
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

internal static class ShareImagePicker
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class OpenFile
    {
        public int Size = Marshal.SizeOf<OpenFile>(); public IntPtr Owner, Instance;
        public string Filter = "Images\0*.png;*.jpg;*.jpeg;*.webp\0\0";
        public IntPtr CustomFilter; public int MaxCustomFilter, FilterIndex = 1;
        public StringBuilder File = new(4096); public int MaxFile = 4096;
        public IntPtr FileTitle; public int MaxFileTitle; public string? InitialDirectory;
        public string Title = "Choose NPC portrait"; public int Flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008;
        public short FileOffset, FileExtension; public string? DefaultExtension;
        public IntPtr CustomData, Hook; public string? TemplateName; public IntPtr Reserved; public int Reserved2, FlagsEx;
    }
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetOpenFileName(OpenFile data);
    public static void Choose(Action<string?> done)
    {
        if (!OperatingSystem.IsWindows()) { done(null); return; }
        var thread = new Thread(() => { try { var file = new OpenFile(); done(GetOpenFileName(file) ? file.File.ToString() : null); } catch { done(null); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
}
