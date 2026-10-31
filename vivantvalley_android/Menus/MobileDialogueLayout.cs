using Microsoft.Xna.Framework;
using VivantValley.Services;

namespace VivantValley.Menus;

internal static class MobileDialogueLayout
{
    public static NativeDialogueFrameLayout Create(Rectangle safe, float scale = 1f)
    {
        scale = ConversationUiLayout.ClampScale(scale);
        Rectangle outer = MobileLayout.Center(safe, (int)(1200 * scale));
        int maximumHeight = Math.Max(320, (int)(640 * scale));
        if (outer.Height > maximumHeight) outer = new Rectangle(outer.X, outer.Bottom - maximumHeight, outer.Width, maximumHeight);
        int pad = Math.Min(24, Math.Max(8, outer.Width / 20));
        bool sidePortrait = outer.Width >= 760;
        int portraitWidth = sidePortrait ? Math.Min(190, outer.Width / 5) : 0;
        int top = sidePortrait ? 24 : 78;
        int divider = outer.Right - portraitWidth - pad;
        Rectangle content = new(outer.Left + pad, outer.Top + top,
            Math.Max(1, outer.Width - pad * 2 - portraitWidth - (sidePortrait ? 16 : 0)), Math.Max(1, outer.Height - top - pad));
        int size = sidePortrait ? Math.Min(144, Math.Max(48, outer.Height - 150)) : 0;
        Rectangle portrait = sidePortrait ? new Rectangle(divider + (portraitWidth - size) / 2, outer.Top + 30, size, size) : Rectangle.Empty;
        Rectangle name = sidePortrait
            ? new Rectangle(divider + 8, portrait.Bottom + 16, portraitWidth - 16, 52)
            : new Rectangle(outer.Left + pad, outer.Top + 14, outer.Width - pad * 2, 48);
        return new NativeDialogueFrameLayout(outer, content, portrait, name, sidePortrait ? divider : 0);
    }
}
