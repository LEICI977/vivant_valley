using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace VivantValley.Menus;

/// <summary>Shared, vanilla-inspired presentation for story and AI dialogue menus.</summary>
internal static class NativeDialogueFrame
{
    private const int OuterMargin = 20;
    private const int FrameInset = 16;

    public static NativeDialogueFrameLayout CreateLayout(
        int viewportWidth,
        int viewportHeight,
        float scale,
        int actionRows)
    {
        int availableWidth = Math.Max(1, viewportWidth - OuterMargin * 2);
        int availableHeight = Math.Max(1, viewportHeight - OuterMargin * 2);
        float normalizedScale = (ConversationUiLayout.ClampScale(scale) - ConversationUiLayout.MinimumScale)
                                / (ConversationUiLayout.MaximumScale - ConversationUiLayout.MinimumScale);
        float widthFactor = MathHelper.Lerp(0.90f, 1f, normalizedScale);
        int naturalWidth = Math.Min(1600, availableWidth);
        int minimumWidth = Math.Min(620, availableWidth);
        int width = Math.Clamp((int)MathF.Round(naturalWidth * widthFactor), minimumWidth, availableWidth);

        int desiredHeight = 342 + Math.Max(0, actionRows - 1) * 54;
        float heightFactor = MathHelper.Lerp(0.92f, 1.08f, normalizedScale);
        int minimumHeight = Math.Min(284, availableHeight);
        int height = Math.Clamp((int)MathF.Round(desiredHeight * heightFactor), minimumHeight, availableHeight);
        int x = (viewportWidth - width) / 2;
        int y = Math.Max(8, viewportHeight - height - OuterMargin);
        Rectangle outer = new(x, y, width, height);

        int innerPadding = width < 760 ? 20 : 28;
        int portraitPanelWidth = Math.Clamp((int)MathF.Round(width * 0.205f), 154, 226);
        int dividerX = outer.Right - FrameInset - portraitPanelWidth;
        Rectangle content = new(
            outer.X + FrameInset + innerPadding,
            outer.Y + FrameInset + 14,
            Math.Max(220, dividerX - outer.X - FrameInset - innerPadding * 2),
            Math.Max(120, height - FrameInset * 2 - 28));

        int portraitSize = Math.Clamp(Math.Min(portraitPanelWidth - 34, height - 128), 112, 176);
        Rectangle portrait = new(
            dividerX + (portraitPanelWidth - portraitSize) / 2,
            outer.Y + 38,
            portraitSize,
            portraitSize);
        Rectangle namePlate = new(
            dividerX + 14,
            Math.Min(outer.Bottom - 70, portrait.Bottom + 14),
            portraitPanelWidth - 28,
            48);

        return new NativeDialogueFrameLayout(outer, content, portrait, namePlate, dividerX);
    }

    public static void Draw(
        SpriteBatch b,
        NativeDialogueFrameLayout layout,
        string npcName,
        string npcDisplayName,
        int portraitIndex)
    {
        Rectangle outer = layout.OuterBounds;
        b.Draw(
            Game1.staminaRect,
            new Rectangle(outer.X + 10, outer.Y + 12, outer.Width, outer.Height),
            Color.Black * 0.42f);
        IClickableMenu.drawTextureBox(b, outer.X, outer.Y, outer.Width, outer.Height, Color.White);

        Rectangle interior = new(
            outer.X + FrameInset,
            outer.Y + FrameInset,
            outer.Width - FrameInset * 2,
            outer.Height - FrameInset * 2);
        DrawParchmentGradient(b, interior);

        DrawDivider(b, layout.DividerX, interior.Y, interior.Bottom);
        DrawPortrait(b, layout.PortraitBounds, npcName, portraitIndex);
        DrawNamePlate(b, layout.NamePlateBounds, npcDisplayName);
    }

    public static void DrawButton(SpriteBatch b, ClickableComponent button, string label, bool subdued = false)
    {
        Rectangle bounds = button.bounds;
        int scaledWidth = Math.Max(1, (int)(bounds.Width * button.scale));
        int scaledHeight = Math.Max(1, (int)(bounds.Height * button.scale));
        int x = bounds.Center.X - scaledWidth / 2;
        int y = bounds.Center.Y - scaledHeight / 2;
        Color tint = subdued ? new Color(235, 210, 170) : Color.White;
        IClickableMenu.drawTextureBox(b, x, y, scaledWidth, scaledHeight, tint);

        string fitted = FitSingleLine(label, Game1.smallFont, Math.Max(24, bounds.Width - 20));
        Vector2 size = Game1.smallFont.MeasureString(fitted);
        b.DrawString(
            Game1.smallFont,
            fitted,
            new Vector2(bounds.Center.X - size.X / 2f, bounds.Center.Y - size.Y / 2f),
            Game1.textColor);
    }

    public static void DrawStatus(SpriteBatch b, Rectangle bounds, string status, Color color)
    {
        string fitted = FitSingleLine(status, Game1.smallFont, bounds.Width);
        Vector2 size = Game1.smallFont.MeasureString(fitted);
        b.DrawString(
            Game1.smallFont,
            fitted,
            new Vector2(bounds.Right - size.X, bounds.Y),
            color);
    }

    public static string FitSingleLine(string? value, SpriteFont font, float maximumWidth)
    {
        string text = value ?? string.Empty;
        if (font.MeasureString(text).X <= maximumWidth)
            return text;

        const string suffix = "...";
        while (text.Length > 0 && font.MeasureString(text + suffix).X > maximumWidth)
            text = text[..^1];
        return text + suffix;
    }

    public static Rectangle GetPortraitSource(Texture2D portrait, int requestedIndex)
    {
        int columns = Math.Max(1, portrait.Width / 64);
        int rows = Math.Max(1, portrait.Height / 64);
        int frameCount = columns * rows;
        int index = requestedIndex >= 0 && requestedIndex < frameCount ? requestedIndex : 0;
        return new Rectangle(index % columns * 64, index / columns * 64, 64, 64);
    }

    private static void DrawParchmentGradient(SpriteBatch b, Rectangle bounds)
    {
        Color top = new(255, 218, 155);
        Color bottom = new(188, 129, 72);
        const int bands = 14;
        for (int index = 0; index < bands; index++)
        {
            int topY = bounds.Y + bounds.Height * index / bands;
            int bottomY = bounds.Y + bounds.Height * (index + 1) / bands;
            float amount = index / (float)Math.Max(1, bands - 1);
            b.Draw(
                Game1.staminaRect,
                new Rectangle(bounds.X, topY, bounds.Width, Math.Max(1, bottomY - topY)),
                Color.Lerp(top, bottom, amount));
        }

        b.Draw(
            Game1.staminaRect,
            new Rectangle(bounds.X, bounds.Y, bounds.Width, 2),
            new Color(255, 235, 188));
        b.Draw(
            Game1.staminaRect,
            new Rectangle(bounds.X, bounds.Bottom - 3, bounds.Width, 3),
            new Color(118, 68, 34));
    }

    private static void DrawDivider(SpriteBatch b, int x, int top, int bottom)
    {
        b.Draw(Game1.staminaRect, new Rectangle(x - 3, top, 6, bottom - top), new Color(112, 61, 27));
        b.Draw(Game1.staminaRect, new Rectangle(x + 3, top, 2, bottom - top), new Color(238, 174, 97));
    }

    private static void DrawPortrait(SpriteBatch b, Rectangle portraitBounds, string npcName, int portraitIndex)
    {
        IClickableMenu.drawTextureBox(
            b,
            portraitBounds.X - 12,
            portraitBounds.Y - 12,
            portraitBounds.Width + 24,
            portraitBounds.Height + 24,
            Color.White);

        NPC? npc = Game1.getCharacterFromName(npcName, mustBeVillager: false, includeEventActors: true);
        Texture2D? portrait = null;
        try
        {
            portrait = npc?.Portrait;
        }
        catch
        {
            // Custom NPCs may not provide a portrait asset.
        }

        if (portrait is not null && portrait.Width >= 64 && portrait.Height >= 64)
        {
            b.Draw(portrait, portraitBounds, GetPortraitSource(portrait, portraitIndex), Color.White);
            return;
        }

        b.Draw(Game1.staminaRect, portraitBounds, new Color(224, 184, 123));
        Vector2 size = Game1.dialogueFont.MeasureString("?");
        b.DrawString(
            Game1.dialogueFont,
            "?",
            new Vector2(portraitBounds.Center.X - size.X / 2f, portraitBounds.Center.Y - size.Y / 2f),
            Color.Gray);
    }

    private static void DrawNamePlate(SpriteBatch b, Rectangle bounds, string npcDisplayName)
    {
        IClickableMenu.drawTextureBox(
            b,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            new Color(250, 224, 174));
        string fitted = FitSingleLine(npcDisplayName, Game1.dialogueFont, Math.Max(20, bounds.Width - 24));
        Vector2 size = Game1.dialogueFont.MeasureString(fitted);
        b.DrawString(
            Game1.dialogueFont,
            fitted,
            new Vector2(bounds.Center.X - size.X / 2f, bounds.Center.Y - size.Y / 2f - 2),
            Game1.textColor);
    }
}

internal sealed record NativeDialogueFrameLayout(
    Rectangle OuterBounds,
    Rectangle ContentBounds,
    Rectangle PortraitBounds,
    Rectangle NamePlateBounds,
    int DividerX);
