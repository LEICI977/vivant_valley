using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using VivantValley.Services;

namespace VivantValley.Menus;

public sealed partial class AiChatInputMenu
{
    private bool keyboardRequested;
    private Rectangle composerAvailable;
    private void DrawMobile(SpriteBatch b)
    {
        DrawComposerBody(b);
        toolbar.Draw(b);
        b.Draw(Game1.mouseCursors, upperRightCloseButton.bounds, new Rectangle(337, 494, 12, 12), Color.White);
    }
    public override void update(GameTime time)
    {
        base.update(time);
        if (composerAvailable != MobileRuntime.ComposerBounds) RepositionMobile();
        if (!MobileRuntime.IsAndroid || keyboardRequested || MobileRuntime.BlockUntilRelease || !ReferenceEquals(Game1.activeClickableMenu, this)) return;
        keyboardRequested = true;
        textBox.OpenMobileKeyboard(this, npcDisplayName);
    }

    private void RepositionMobile()
    {
        composerAvailable = MobileRuntime.ComposerBounds;
        ApplyLayout(composerAvailable, MobileRuntime.ControlHeight);
    }
}
