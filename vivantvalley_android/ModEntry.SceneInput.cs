using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Menus;

namespace VivantValley;

public sealed partial class ModEntry
{
    private bool HandleSceneReplyEnter(SButton button)
    {
        if (button != SButton.Enter) return false;
        if (Game1.activeClickableMenu is AiStreamingDialogueMenu reply)
        {
            // Dispatch once and suppress the physical press through release. The same
            // Enter must not reach the new composer or the game's NPC interaction path.
            Helper.Input.Suppress(button);
            reply.receiveKeyPress(Microsoft.Xna.Framework.Input.Keys.Enter);
            return true;
        }
        if (Game1.activeClickableMenu is not DialogueBox box || box.isQuestion) return false;
        bool owned = (dream is { DisplayedText: not null } session && ReferenceEquals(session.Menu,box))
            || (activeFreeStory is not null && freeStoryDisplayedLine is not null && ReferenceEquals(freeStoryMenu,box))
            || weeklyStoryRunner.OwnsReply(box);
        if (!owned) return false;

        // Consume once, including when Enter is also bound to the game's action key.
        // Do not intercept composers, IME confirmation, choice menus or other mods' dialogue.
        Helper.Input.Suppress(button);
        switch (NativeReplyPolicy.ReadScene(box.transitioning,
            box.characterIndexInDialogue >= box.getCurrentString().Length - 1,box.safetyTimer > 0))
        {
            case ReplyReadAction.Reveal:
                box.finishTyping();
                break;
            case ReplyReadAction.NextPage:
                // Native pagination/outro still owns closing. Session updates then show
                // the next actor or composer, after the old menu has finished cleaning up.
                box.receiveLeftClick(box.x + 16,box.y + 16,true);
                break;
        }
        return true;
    }
}
