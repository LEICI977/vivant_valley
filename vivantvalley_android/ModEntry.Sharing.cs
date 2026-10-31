using StardewValley;
using StardewModdingAPI;
using VivantValley.Menus;
using VivantValley.Services;

namespace VivantValley;

public sealed partial class ModEntry
{
    private void OpenShareCard(NPC npc)
    {
        var turns = ShareCardModel.RecentTurns(memoryStore.GetOrCreate(GetPlayerId(), npc.Name).Messages);
        if (turns.Count == 0)
        {
            ShowHud(L.IsChinese ? "还没有完整的 AI 对话可分享。" : "No completed AI conversation to share yet.", HUDMessage.error_type);
            OpenMessagePrompt(npc);
            return;
        }
        try
        {
            Game1.activeClickableMenu = new ShareCardMenu(Helper.DirectoryPath, npc.displayName, Game1.player.Name,
                turns, () => OpenMessagePrompt(npc),
                ex => Monitor.Log($"Share card error: {ex}", LogLevel.Error), npc.Name);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Cannot open share editor: {ex}", LogLevel.Error);
            ShowHud(L.IsChinese ? "分享素材加载失败，请检查安装包是否完整。" : "Sharing assets could not be loaded. Check your installation.", HUDMessage.error_type);
            OpenMessagePrompt(npc);
        }
    }
}
