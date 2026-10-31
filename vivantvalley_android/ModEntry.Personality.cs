using StardewModdingAPI;
using StardewValley;
using VivantValley.Menus;

namespace VivantValley;

public sealed partial class ModEntry
{
    private static NpcPersonalitySettings ReadNpcPersonality(Farmer player, string npcName)
        => NpcPersonalitySettings.Parse(player.modData.TryGetValue(NpcPersonalitySettings.StorageKey(npcName), out string json) ? json : null);

    private void OpenNpcPersonality(NPC npc, string draft = "")
    {
        if (!Context.IsWorldReady) return;
        StopNpcSpeech();
        Farmer player = Game1.player;
        string key = NpcPersonalitySettings.StorageKey(npc.Name);
        NpcPersonalitySettings personalityDraft = ReadNpcPersonality(player, npc.Name);
        void ReturnToChat()
        {
            if (Context.IsWorldReady && ReferenceEquals(player, Game1.player) && IsValidTarget(npc)) OpenMessagePrompt(npc, draft);
        }
        void ShowPersonality()
        {
            Game1.activeClickableMenu = new NpcPersonalityMenu(npc.displayName, personalityDraft, value =>
            {
                if (!Context.IsWorldReady || !ReferenceEquals(player, Game1.player)) return;
                NpcPersonalitySettings normalized = value.Normalize();
                personalityDraft = normalized;
                if (normalized.IsEmpty) player.modData.Remove(key);
                else player.modData[key] = normalized.Serialize();
            }, ReturnToChat, value =>
            {
                personalityDraft = value;
                Game1.activeClickableMenu = new SpeechSettingsMenu(L.F("speech.npcTitle", $"{npc.displayName} · NPC 设置"),
                    SpeechSettings(npc.Name, player), ReturnToChat, ShowPersonality);
            });
        }
        ShowPersonality();
    }
}
