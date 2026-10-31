using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace VivantValley.Menus;

internal static class ChatTypography
{
    public const float BodyScale = 1.5f;
    public const float ButtonScale = 1.25f;
    public static int ButtonWidth(string text, int minimum = 80)
        => Math.Max(minimum, (int)Math.Ceiling(Game1.smallFont.MeasureString(text).X * ButtonScale) + 28);

    public static void Draw(SpriteBatch b, string text, Rectangle area, float preferred, Color color, bool center = false)
    {
        float scale = Math.Min(preferred, Math.Max(.5f, area.Height / (float)Game1.smallFont.LineSpacing));
        string fit = NativeDialogueFrame.FitSingleLine(text, Game1.smallFont, Math.Max(1, (int)(area.Width / scale)));
        Vector2 size = Game1.smallFont.MeasureString(fit) * scale;
        b.DrawString(Game1.smallFont, fit, new Vector2(center ? area.Center.X - size.X / 2 : area.X,
            area.Center.Y - size.Y / 2), color, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
    }

    public static void DrawButton(SpriteBatch b, ClickableComponent button, string label)
    {
        var r = button.bounds;
        IClickableMenu.drawTextureBox(b, r.X, r.Y, r.Width, r.Height, Color.White);
        Draw(b,label,new Rectangle(r.X + 10,r.Y + 4,Math.Max(1,r.Width - 20),Math.Max(1,r.Height - 8)),ButtonScale,Game1.textColor,true);
    }
}
