using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Services;

namespace VivantValley.Menus;

/// <summary>Responsive native-style forms: scrollable body, fixed actions, one coordinate system.</summary>
internal sealed class MobileFormPage
{
    private sealed record Row(string Label, Func<string> Value, Action? Click, Func<bool>? Enabled, bool Field, bool Password, Func<Color>? TextColor = null, Row? Partner = null);
    private readonly TouchMenu owner;
    private readonly List<Row> rows = new();
    private readonly List<Row> actions = new();
    private readonly List<(Row Row, Rectangle Bounds)> bodyItems = new();
    private readonly List<(Row Row, Rectangle Bounds)> footerItems = new();
    private readonly string title;
    private readonly Func<string> status;
    private Rectangle outer, body, close;
    private int contentHeight;
    private float offset;
    public MobileFormPage(TouchMenu owner, string title, Func<string> status)
    { this.owner = owner; this.title = title; this.status = status; }

    public void Field(string label, TextBox box, bool email = false)
    {
        Row? row = null;
        row = new Row(label, () => box.Text, () =>
        {
            if (MobileRuntime.IsAndroid) MobileRuntime.Edit(owner, box, label, email, () => FieldBounds(row!));
            else { box.Selected = true; Game1.keyboardDispatcher.Subscriber = box; }
        }, null, true, box.PasswordBox);
        rows.Add(row);
    }
    public void Field(string label, CaretTextBox box)
    {
        Row? row = null;
        row = new Row(label, () => box.Text, () =>
        {
            if (MobileRuntime.IsAndroid) box.OpenMobileKeyboard(owner, label, () => FieldBounds(row!));
            else { box.Selected = true; Game1.keyboardDispatcher.Subscriber = box; }
        }, null, true, false);
        rows.Add(row);
    }
    private Rectangle FieldBounds(Row row)
    {
        Rectangle rect = bodyItems.FirstOrDefault(item => ReferenceEquals(item.Row, row)).Bounds;
        return new Rectangle(rect.X, rect.Y + 30 - (int)offset, rect.Width, Math.Max(1, rect.Height - 30));
    }
    public void Text(Func<string> value) => rows.Add(new Row("", value, null, null, false, false));
    public void Button(Func<string> label, Action click, Func<bool>? enabled = null)
        => rows.Add(new Row("", label, click, enabled, false, false));
    public void ButtonPair(Func<string> first, Action firstClick, Func<bool> firstEnabled, Func<Color> firstColor,
        Func<string> second, Action secondClick, Func<bool> secondEnabled)
        => rows.Add(new Row("", first, firstClick, firstEnabled, false, false, firstColor,
            new Row("", second, secondClick, secondEnabled, false, false)));
    public void Action(Func<string> label, Action click, Func<bool>? enabled = null)
        => actions.Add(new Row("", label, click, enabled, false, false));

    public void AddHudSettings()
    {
        Text(() => L.T("mobile.controls.title"));
        Text(() => L.T("mobile.controls.hint"));
        Button(() => L.T("mobile.controls.smaller") + $" ({MobileRuntime.ButtonScale:P0})", () => MobileRuntime.AdjustButton(scale: -.1f));
        Button(() => L.T("mobile.controls.larger"), () => MobileRuntime.AdjustButton(scale: .1f));
        Button(() => L.T("mobile.controls.left") + $" ({MobileRuntime.ButtonOffsetX})", () => MobileRuntime.AdjustButton(x: 24));
        Button(() => L.T("mobile.controls.right"), () => MobileRuntime.AdjustButton(x: -24));
        Button(() => L.T("mobile.controls.up") + $" ({MobileRuntime.ButtonOffsetY})", () => MobileRuntime.AdjustButton(y: 24));
        Button(() => L.T("mobile.controls.down"), () => MobileRuntime.AdjustButton(y: -24));
        Button(() => L.T("mobile.controls.reset"), () => MobileRuntime.AdjustButton(reset: true));
    }

    public void Reflow()
    {
        outer = MobileLayout.Center(MobileRuntime.SafeBounds, 980);
        int h = MobileRuntime.ControlHeight;
        var geometry = MobileLayout.Form(outer, h, actions.Count);
        close = geometry.Close;
        body = geometry.Body;
        bodyItems.Clear(); footerItems.Clear();
        int y = body.Top;
        foreach (Row row in rows)
        {
            if (row.Partner is Row partner)
            {
                int fullWidth = body.Width - 10;
                bool sideBySide = fullWidth >= 640;
                int pairWidth = sideBySide ? (fullWidth - 14) / 2 : fullWidth;
                int firstHeight = Math.Max(h, (int)Game1.smallFont.MeasureString(Game1.parseText(row.Value(), Game1.smallFont, pairWidth - 24)).Y + 20);
                int secondHeight = Math.Max(h, (int)Game1.smallFont.MeasureString(Game1.parseText(partner.Value(), Game1.smallFont, pairWidth - 24)).Y + 20);
                if (sideBySide)
                {
                    int pairHeight = Math.Max(firstHeight, secondHeight);
                    bodyItems.Add((row, new Rectangle(body.Left, y, pairWidth, pairHeight)));
                    bodyItems.Add((partner, new Rectangle(body.Left + pairWidth + 14, y, fullWidth - pairWidth - 14, pairHeight)));
                    y += pairHeight + 14;
                }
                else
                {
                    bodyItems.Add((row, new Rectangle(body.Left, y, fullWidth, firstHeight)));
                    y += firstHeight + 14;
                    bodyItems.Add((partner, new Rectangle(body.Left, y, fullWidth, secondHeight)));
                    y += secondHeight + 14;
                }
                continue;
            }
            int itemHeight = row.Field ? h + 34 : row.Click is null
                ? Math.Max(36, (int)Game1.smallFont.MeasureString(Game1.parseText(row.Value(), Game1.smallFont, body.Width - 16)).Y + 16)
                : Math.Max(h, (int)Game1.smallFont.MeasureString(Game1.parseText(row.Value(), Game1.smallFont, body.Width - 24)).Y + 20);
            bodyItems.Add((row, new Rectangle(body.Left, y, body.Width - 10, itemHeight)));
            y += itemHeight + 14;
        }
        contentHeight = y - body.Top;
        offset = MobileLayout.ClampScroll(offset, contentHeight, body.Height);
        for (int i = 0; i < actions.Count; i++)
            footerItems.Add((actions[i], geometry.Actions[i]));
    }

    public void Tap(int x, int y)
    {
        if (MobileRuntime.FinishEditing(owner, () => Tap(x, y))) return;
        if (close.Contains(x, y)) { owner.receiveKeyPress(Microsoft.Xna.Framework.Input.Keys.Escape); return; }
        foreach (var item in footerItems)
            if (item.Bounds.Contains(x, y)) { Activate(item.Row); return; }
        if (!body.Contains(x, y)) return;
        foreach (var item in bodyItems)
            if (item.Bounds.Contains(x, y + (int)offset)) { Activate(item.Row); return; }
    }
    private static void Activate(Row row)
    {
        if (row.Click is null || row.Enabled?.Invoke() == false) return;
        Game1.playSound("smallSelect"); row.Click();
    }
    public void Scroll(Vector2 start, float amount)
    {
        if (!body.Contains(start)) return;
        MobileRuntime.DismissKeyboard(owner);
        offset = MobileLayout.ClampScroll(offset + amount, contentHeight, body.Height);
    }
    public void Wheel(int direction) => offset = MobileLayout.ClampScroll(offset - Math.Sign(direction) * 64, contentHeight, body.Height);

    public void Draw(SpriteBatch b)
    {
        // Reflow also measures translated or asynchronously updated labels.
        Reflow();
        b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * .35f);
        IClickableMenu.drawTextureBox(b, outer.X, outer.Y, outer.Width, outer.Height, Color.White);
        b.DrawString(Game1.smallFont, NativeDialogueFrame.FitSingleLine(title, Game1.smallFont, outer.Width - close.Width - 56),
            new Vector2(outer.Left + 24, outer.Top + 24), Game1.textColor);
        NativeDialogueFrame.DrawButton(b, new ClickableComponent(close, "close"), "X");
        owner.DrawClipped(b, body, () =>
        {
            foreach (var item in bodyItems) DrawRow(b, item.Row, item.Bounds);
        }, -offset);
        foreach (var item in footerItems) DrawRow(b, item.Row, item.Bounds);
        int statusY = footerItems.Count > 0 ? footerItems[0].Bounds.Top - 32 : outer.Bottom - 40;
        b.DrawString(Game1.smallFont, NativeDialogueFrame.FitSingleLine(status(), Game1.smallFont, body.Width), new Vector2(body.X, statusY), Game1.textColor);
        if (contentHeight > body.Height)
        {
            int thumb = Math.Max(12, body.Height * body.Height / contentHeight);
            int top = body.Top + (int)((body.Height - thumb) * offset / Math.Max(1, contentHeight - body.Height));
            b.Draw(Game1.staminaRect, new Rectangle(body.Right - 5, top, 4, thumb), Color.SaddleBrown);
        }
    }
    private void DrawRow(SpriteBatch b, Row row, Rectangle rect)
    {
        string value = row.Value();
        if (row.Field)
        {
            b.DrawString(Game1.smallFont, NativeDialogueFrame.FitSingleLine(row.Label, Game1.smallFont, rect.Width), new Vector2(rect.X + 4, rect.Y), Game1.textColor);
            rect = new Rectangle(rect.X, rect.Y + 30, rect.Width, rect.Height - 30);
            IClickableMenu.drawTextureBox(b, rect.X, rect.Y, rect.Width, rect.Height, Color.White);
            if (MobileRuntime.IsEditing(FieldBounds(row))) return;
            if (row.Password) value = new string('*', Math.Min(value.Length, 24));
            value = NativeDialogueFrame.FitSingleLine(value.Length == 0 ? L.T("mobile.input.hint") : value, Game1.smallFont, rect.Width - 24);
            b.DrawString(Game1.smallFont, value, new Vector2(rect.X + 12, rect.Center.Y - Game1.smallFont.LineSpacing / 2), Game1.textColor);
        }
        else if (row.Click is not null)
        {
            bool enabled = row.Enabled?.Invoke() != false;
            IClickableMenu.drawTextureBox(b, rect.X, rect.Y, rect.Width, rect.Height, enabled ? Color.White : Color.Gray);
            string text = Game1.parseText(value, Game1.smallFont, Math.Max(1, rect.Width - 24));
            Vector2 size = Game1.smallFont.MeasureString(text);
            if (size.Y > rect.Height - 12) text = NativeDialogueFrame.FitSingleLine(value, Game1.smallFont, rect.Width - 24);
            size = Game1.smallFont.MeasureString(text);
            b.DrawString(Game1.smallFont, text, new Vector2(rect.Center.X - size.X / 2, rect.Center.Y - size.Y / 2), enabled ? row.TextColor?.Invoke() ?? Game1.textColor : Color.DarkGray);
        }
        else b.DrawString(Game1.smallFont, Game1.parseText(value, Game1.smallFont, rect.Width), new Vector2(rect.X, rect.Y), Game1.textColor);
    }
}
