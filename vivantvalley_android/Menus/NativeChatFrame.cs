using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace VivantValley.Menus;

/// <summary>A real vanilla dialogue surface. Only editing and external actions belong to the composer.</summary>
internal sealed class NativeChatFrame : DialogueBox
{
    public NativeChatFrame(NPC? npc) : base(0, 0, 1200, 384)
    {
        // Do not consume the NPC's dialogue queue, parse player text, or run the native
        // update/outro lifecycle. The host menu owns editing, closing and submission.
        if (npc is not null) characterDialogue = new ComposerDialogue(npc);
        transitionInitialized = true;
        transitioning = false;
        characterIndexInDialogue = 0;
    }

    public Rectangle BodyBounds => new(x, y, width, height);

    public void Reposition()
    {
        // Match vanilla's source position as well as its dimensions. SpriteText and
        // portrait patches may consult viewport coordinates before our fit transform.
        x = (int)Utility.getTopLeftPositionForCenteringOnScreen(width, height).X;
        y = Game1.uiViewport.Height - height - 64;
        friendshipJewel = new Rectangle(x + width - 64, y + 256, 44, 44);
    }

    public void DrawFitted(SpriteBatch b, ChatComposerLayout layout)
    {
        Rectangle source = layout.SourceFrame;
        var viewport = b.GraphicsDevice.Viewport;
        float sx = viewport.Width / (float)Math.Max(1, Game1.uiViewport.Width);
        float sy = viewport.Height / (float)Math.Max(1, Game1.uiViewport.Height);
        Matrix transform = Matrix.CreateTranslation(-source.X, -source.Y, 0)
            * Matrix.CreateScale(layout.Scale, layout.Scale, 1)
            * Matrix.CreateTranslation(layout.Outer.X, layout.Outer.Y, 0)
            * Matrix.CreateScale(sx, sy, 1);
        float cursorAlpha = Game1.mouseCursorTransparency;
        b.End();
        b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone, null, transform);
        try
        {
            Game1.mouseCursorTransparency = 0;
            // This also calls vanilla drawPortrait, preserving its name plate, portrait
            // selection and installed patches. Never draw/crop the portrait ourselves.
            base.draw(b);
        }
        finally
        {
            Game1.mouseCursorTransparency = cursorAlpha;
            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        }
    }

    private sealed class ComposerDialogue : Dialogue
    {
        public ComposerDialogue(NPC npc) : base(npc, "VivantValley.NativeComposer", "...")
        {
            dialogues.Clear();
            dialogues.Add(new DialogueLine("..."));
            showPortrait = true;
            CurrentEmotion = "$0";
        }
        public override void prepareDialogueForDisplay() { }
        public override void prepareCurrentDialogueForDisplay() => showPortrait = true;
    }
}
