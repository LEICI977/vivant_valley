using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Services;

namespace VivantValley.Menus;

public sealed partial class AiProactiveEncounterMenu
{
    private Rectangle choiceViewport;
    private int choiceContentHeight;
    private float choiceOffset;
    private void RepositionMobile()
    {
        frameLayout = MobileDialogueLayout.Create(MobileRuntime.SafeBounds, proactiveUiScale);
        Rectangle outer = frameLayout.OuterBounds, content = frameLayout.ContentBounds;
        width = outer.Width; height = outer.Height; xPositionOnScreen = outer.X; yPositionOnScreen = outer.Y;
        int columns = content.Width >= 680 && choices.Count > 1 ? 2 : 1;
        int buttonWidth = Math.Max(1, (content.Width - (columns - 1) * 12) / columns);
        int y = 0;
        for (int first = 0; first < choices.Count; first += columns)
        {
            int h = MobileRuntime.ControlHeight;
            for (int i = first; i < Math.Min(choices.Count, first + columns); i++)
            {
                string wrapped = Game1.parseText(choices[i].Text, Game1.smallFont, Math.Max(1, buttonWidth - 24));
                h = Math.Max(h, (int)Game1.smallFont.MeasureString(wrapped).Y + 24);
            }
            for (int i = first; i < Math.Min(choices.Count, first + columns); i++)
                choiceButtons[i].bounds = new Rectangle(content.X + (i - first) * (buttonWidth + 12), y, buttonWidth, h);
            y += h + 12;
        }
        choiceContentHeight = Math.Max(1, y - 12);
        int headerHeight = onSkip is null ? 38 : MobileRuntime.ControlHeight + 8;
        int availableChoices = Math.Max(32, content.Height - headerHeight - 64 - 12);
        int visibleChoices = Math.Min(choiceContentHeight, Math.Min(availableChoices, Math.Max(48, content.Height / 2)));
        choiceViewport = new Rectangle(content.X, content.Bottom - visibleChoices, content.Width, visibleChoices);
        foreach (ClickableComponent button in choiceButtons) button.bounds.Offset(0, choiceViewport.Top);
        choiceOffset = MobileLayout.ClampScroll(choiceOffset, choiceContentHeight, choiceViewport.Height);
        int skipWidth = onSkip is null ? 0 : Math.Min(108, content.Width / 3);
        statusBounds = new Rectangle(content.X, content.Y, content.Width - skipWidth - 8, Game1.smallFont.LineSpacing);
        skipButton.bounds = new Rectangle(content.Right - skipWidth, content.Y, skipWidth, MobileRuntime.ControlHeight);
        int textTop = content.Y + (onSkip is null ? 38 : MobileRuntime.ControlHeight + 8);
        textBounds = new Rectangle(content.X, textTop, content.Width, Math.Max(1, choiceViewport.Top - textTop - 12));
    }

    private bool HandleMobileChoiceTap(int x, int y)
    {
        if (onSkip is not null && skipButton.containsPoint(x, y)) { SkipScene(); return true; }
        if (!typewriter.IsComplete)
        {
            if (textBounds.Contains(x, y) || choiceViewport.Contains(x, y)) typewriter.RevealAll();
            return true;
        }
        if (!choiceViewport.Contains(x, y)) return true;
        for (int i = 0; i < choiceButtons.Count; i++)
            if (choiceButtons[i].containsPoint(x, y + (int)choiceOffset)) { TryChoose(i); break; }
        return true;
    }

    protected override void OnDrag(Vector2 start, float amount)
    {
        if (choiceViewport.Contains(start)) choiceOffset = MobileLayout.ClampScroll(choiceOffset + amount, choiceContentHeight, choiceViewport.Height);
        else if (textBounds.Contains(start)) base.OnDrag(start, amount);
    }

    private void DrawMobileChoices(SpriteBatch b)
    {
        if (!typewriter.IsComplete) return;
        DrawClipped(b, choiceViewport, () =>
        {
            for (int i = 0; i < choices.Count; i++)
            {
                Rectangle rect = choiceButtons[i].bounds;
                IClickableMenu.drawTextureBox(b, rect.X, rect.Y, rect.Width, rect.Height, choices[i].IsDefer ? Color.Wheat : Color.White);
                string text = Game1.parseText(choices[i].Text, Game1.smallFont, Math.Max(1, rect.Width - 24));
                Vector2 size = Game1.smallFont.MeasureString(text);
                b.DrawString(Game1.smallFont, text, new Vector2(rect.X + 12, rect.Center.Y - size.Y / 2), Game1.textColor);
            }
        }, -choiceOffset);
        if (choiceContentHeight > choiceViewport.Height)
        {
            int thumb = Math.Max(12, choiceViewport.Height * choiceViewport.Height / choiceContentHeight);
            int top = choiceViewport.Top + (int)((choiceViewport.Height - thumb) * choiceOffset / (choiceContentHeight - choiceViewport.Height));
            b.Draw(Game1.staminaRect, new Rectangle(choiceViewport.Right - 4, top, 4, thumb), Color.SaddleBrown);
        }
    }
}
