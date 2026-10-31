using System.Globalization;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using VivantValley.Services;

namespace VivantValley;

public sealed partial class ModEntry
{
    private bool HasOwnCompanionTrip(NPC npc)
        => Context.IsWorldReady && Game1.IsMasterGame && !npcCombatStateService.IsHospitalized(npc.Name)
            && (npcMoveToolService.HasSessionForPlayer(npc.Name, Game1.player.UniqueMultiplayerID)
                || npcMineGuardService.HasSessionForPlayer(npc.Name, Game1.player.UniqueMultiplayerID)
                || npcFishingService.HasSessionForPlayer(npc.Name, Game1.player.UniqueMultiplayerID));

    private void EndCompanionTrip(NPC npc, Farmer player)
    {
        if (!Context.IsWorldReady || !ReferenceEquals(Game1.player, player) || !HasOwnCompanionTrip(npc))
        {
            ShowHud(L.T("trip.no-active"));
            return;
        }
        try
        {
            // MineShaft is the actual underground map type. Forests and other combat maps
            // must not be treated as mines merely because the guard service supports them.
            bool fromMine = npc.currentLocation is MineShaft;
            Action returnToEntrance = () =>
            {
                if (!fromMine) return;
                GameLocation? entrance = Game1.getLocationFromName("Mine");
                var finder = new NpcTilePathfinder();
                if (entrance is null || !finder.TryFindSafeFollowTile(entrance, npc, new Point(18, 12), 1, 8, out Point tile))
                    throw new InvalidOperationException(L.T("trip.no-landing"));
                // Only the NPC moves. Follow/combat controllers are released by Cancel below.
                Game1.warpCharacter(npc, entrance, new Vector2(tile.X, tile.Y));
                npc.faceDirection(2);
            };
            long id = player.UniqueMultiplayerID;
            bool ended = npcMoveToolService.EndForPlayer(npc.Name, id, returnToEntrance)
                || npcMineGuardService.EndForPlayer(npc.Name, id, returnToEntrance)
                || npcFishingService.EndForPlayer(npc.Name, id, returnToEntrance);
            if (!ended) { ShowHud(L.T("trip.no-active")); return; }
            npcPresentationService.End(npc.Name);
            ClearConversationContinuation(screenStates.Value);
            conversationSessionMemory.RecordTripEnded(id.ToString(CultureInfo.InvariantCulture), npc.Name,
                $"{Game1.Date} {Game1.timeOfDay}", fromMine);
            ShowHud(L.F(fromMine ? "trip.ended-mine" : "trip.ended", $"已结束与 {npc.displayName} 的行程。"));
        }
        catch (Exception ex)
        {
            Monitor.Log($"End companion trip failed for {npc.Name}: {ex}", LogLevel.Error);
            ShowHud(L.T("trip.failed"), HUDMessage.error_type);
        }
    }
}
