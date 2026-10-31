using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace VivantValley.Menus;

/// <summary>A native DialogueBox whose last page stays open for the external action bar.</summary>
internal sealed class NativeReplyBox : DialogueBox
{
    public NativeReplyBox(NPC npc, string text, int portrait) : base(new LiteralNpcDialogue(npc, text, portrait)) { }
    public NativeReplyBox(string text) : base(new List<string> { text }) { }

    public bool PageTyped => !transitioning && characterIndexInDialogue >= getCurrentString().Length;
    public bool LastPage => characterDialogue is null ? dialogues.Count <= 1
        : characterDialoguesBrokenUp.Count <= 1 && !characterDialogue.isCurrentStringContinuedOnNextScreen;
    public bool ReadComplete => PageTyped && LastPage;
    public Rectangle FrameBounds => new(x - 48, y - 32, width + 96, height + 72);

    public void SetComposerOutro(ReplyHandoffTransition outro)
    {
        // Only the native draw path reads these fields. Do not call beginOutro/update:
        // its completion closes NPC dialogue, invokes afterDialogues and consumes queues.
        Rectangle bounds = outro.Bounds(new Rectangle(x, y, width, height));
        transitioning = true;
        transitioningBigger = false;
        transitionInitialized = true;
        transitionX = bounds.X;
        transitionY = bounds.Y;
        transitionWidth = bounds.Width;
        transitionHeight = bounds.Height;
        dialogueIcon = null;
    }

    public void ReadPage()
    {
        switch (NativeReplyPolicy.Read(transitioning, PageTyped, LastPage, safetyTimer > 0))
        {
            case ReplyReadAction.Reveal:
                finishTyping();
                break;
            case ReplyReadAction.NextPage:
                // Only native pagination reaches this path. Never advance beyond the last page:
                // vanilla's outro can mutate NPC dialogue queues or invoke Game1.afterDialogues.
                base.receiveLeftClick(x + 16, y + 16, true);
                break;
        }
    }

    public override void update(GameTime time)
    {
        base.update(time);
        if (LastPage) dialogueIcon = null;
    }

    public void DrawFitted(SpriteBatch b, NativeReplyLayout layout)
    {
        Rectangle source = FrameBounds;
        var viewport = b.GraphicsDevice.Viewport;
        float sx = viewport.Width / (float)Math.Max(1, Game1.uiViewport.Width);
        float sy = viewport.Height / (float)Math.Max(1, Game1.uiViewport.Height);
        Matrix transform = Matrix.CreateTranslation(-source.X, -source.Y, 0)
            * Matrix.CreateScale(layout.Scale, layout.Scale, 1)
            * Matrix.CreateTranslation(layout.Frame.X, layout.Frame.Y, 0)
            * Matrix.CreateScale(sx, sy, 1);
        float cursorAlpha = Game1.mouseCursorTransparency;
        b.End();
        b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone, null, transform);
        try
        {
            // The outer menu draws the cursor once, in UI coordinates.
            Game1.mouseCursorTransparency = 0;
            base.draw(b);
        }
        finally
        {
            Game1.mouseCursorTransparency = cursorAlpha;
            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        }
    }

    /// <summary>Only a fixed placeholder enters the vanilla parser. AI text is a literal line,
    /// with no gift tokens, mail flags, events, questions or other side effects.</summary>
    private sealed class LiteralNpcDialogue : Dialogue
    {
        public LiteralNpcDialogue(NPC npc, string text, int portrait)
            : base(npc, "VivantValley.NativeReply", "...")
        {
            dialogues.Clear();
            dialogues.Add(new DialogueLine(text));
            CurrentEmotion = "$" + Math.Max(0, portrait);
        }
        public override void prepareDialogueForDisplay() { }
        public override void prepareCurrentDialogueForDisplay() => showPortrait = true;
    }
}
