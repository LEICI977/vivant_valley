using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace VivantValley.Patches;

internal static class DreamSleepPatch
{
    private static ModEntry? owner;
    internal static void Apply(ModEntry mod)
    {
        owner = mod;
        try
        {
            var method = AccessTools.Method(typeof(GameLocation), nameof(GameLocation.answerDialogueAction),
                new[] { typeof(string), typeof(string[]) });
            if (method is null) throw new MissingMethodException("GameLocation.answerDialogueAction");
            new Harmony("firstmod.StardewAIMemories.Dreams").Patch(method,
                prefix: new HarmonyMethod(typeof(DreamSleepPatch),nameof(Prefix)));
        }
        catch (Exception ex) { mod.Monitor.Log("Dream sleep entry unavailable: " + ex.Message,LogLevel.Error); }
    }
    private static bool Prefix(GameLocation __instance, string __0, ref bool __result)
    {
        if (__0 != "Sleep_Yes" || owner is null) return true;
        try
        {
            if (!owner.TryBeginDreamSleep(__instance)) return true;
            __result = true;
            return false;
        }
        catch (Exception ex)
        {
            owner.AbortDream(false);
            owner.Monitor.Log("Dream entry failed; continuing normal sleep: " + ex.Message,LogLevel.Warn);
            return true;
        }
    }
}
