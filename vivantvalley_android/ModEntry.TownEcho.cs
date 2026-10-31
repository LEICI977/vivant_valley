using System.Globalization;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Tools;
using VivantValley.Services;

namespace VivantValley;

public sealed partial class ModEntry
{
    private TownEchoNetwork townEchoNetwork = TownEchoNetwork.Defaults();

    private void LoadTownEchoNetwork()
    {
        try
        {
            string path = Path.Combine(Helper.DirectoryPath, "assets", "social", "town-echo-contacts.json");
            townEchoNetwork = File.Exists(path)
                ? JsonSerializer.Deserialize<TownEchoNetwork>(File.ReadAllText(path))! : TownEchoNetwork.Defaults();
            townEchoNetwork.Validate();
        }
        catch (Exception ex)
        {
            townEchoNetwork = TownEchoNetwork.Defaults();
            Monitor.Log($"Invalid town-echo contacts; using bundled defaults: {ex.Message}", LogLevel.Warn);
        }
        foreach (NPC npc in Game1.locations.SelectMany(location => location.characters).Where(n => n.IsVillager))
            townEchoNetwork.AddFallback(npc.Name);
    }

    // These checks establish a real experience; automatic propagation itself has no location/distance gates.
    private void ObserveTownEchoExperiences()
    {
        if (!config.EnableTownEchoes || !Context.IsWorldReady || !Context.IsMainPlayer
            || Game1.eventUp || Game1.activeClickableMenu is not null) return;
        Farmer player = Game1.player;
        foreach (NPC npc in player.currentLocation.characters.Where(n => n.IsVillager && !n.IsInvisible))
        {
            string? kind = npc.currentLocation is MineShaft
                && npcMineGuardService.HasSessionForPlayer(npc.Name, player.UniqueMultiplayerID) ? "mine"
                : npcFishingService.HasSessionForPlayer(npc.Name, player.UniqueMultiplayerID)
                    && player.CurrentTool is FishingRod rod && rod.isFishing ? "fishing" : null;
            if (kind is not null)
                RecordTownEcho(new TownEchoEvent { PlayerId = GetPlayerId(), Source = npc.Name, Kind = kind,
                    EventKey = $"observed:{npc.Name}:{kind}:{Game1.Date.TotalDays}", Day = Game1.Date.TotalDays });
        }
    }

    private void SpreadTownEchoes()
    {
        if (!config.EnableTownEchoes || !Context.IsWorldReady || !Context.IsMainPlayer) return;
        foreach (var item in memoryStore.TownEchoes.Events)
        {
            townEchoNetwork.AddFallback(item.Source);
            foreach (var d in item.Deliveries) townEchoNetwork.AddFallback(d.Recipient);
        }
        // New recipients also need five contacts before their forwarding day.
        foreach (string name in townEchoNetwork.Contacts.Values.SelectMany(v => v).Distinct().ToArray())
            townEchoNetwork.AddFallback(name);
        if (memoryStore.TownEchoes.Advance(Game1.Date.TotalDays, townEchoNetwork))
        {
            memoryDirty = true;
            PersistMemory(force: false);
        }
    }

    private void RecordTownEcho(TownEchoEvent item)
    {
        if (!config.EnableTownEchoes || !Context.IsWorldReady || !Context.IsMainPlayer) return;
        townEchoNetwork.AddFallback(item.Source);
        if (memoryStore.TownEchoes.Record(item)) memoryDirty = true;
    }

    private void RecordTownGift(string playerId, string npc, string key, string kind, string gift, int quantity = 1)
        => RecordTownEcho(new TownEchoEvent { PlayerId = playerId, Source = npc, EventKey = key,
            Kind = kind, Day = Game1.Date.TotalDays, Detail = gift, Quantity = Math.Max(1, quantity) });

    private TownEchoRecall? RecallTownEcho(string playerId, string npcName, string input)
        => config.EnableTownEchoes ? memoryStore.TownEchoes.Recall(playerId, npcName, input,
            Game1.Date.TotalDays, L.IsChinese, name => Game1.getCharacterFromName(name)?.displayName ?? name) : null;

    private void OnTownEchoStatus(string command, string[] args)
    {
        if (!Context.IsWorldReady) return;
        if (args.Length >= 2 && args[0].Equals("contacts", StringComparison.OrdinalIgnoreCase))
        {
            Monitor.Log($"{args[1]}: {string.Join(", ", townEchoNetwork.For(args[1]))}", LogLevel.Info);
            return;
        }
        var events = memoryStore.TownEchoes.Events.Where(e => e.PlayerId == GetPlayerId()
            && Game1.Date.TotalDays - e.Day <= TownEchoPolicy.RetentionDays).ToArray();
        Monitor.Log($"Town echoes: enabled={config.EnableTownEchoes}, events={events.Length}, day={Game1.Date.TotalDays}.", LogLevel.Info);
        foreach (var item in events)
            Monitor.Log($"{item.Source}: {item.Kind}, day={item.Day}; transfers=[{string.Join(", ", item.Deliveries.Select(d => $"{d.Sender}->{d.Recipient} hop={d.Hop} day={d.Day} presented={d.Presented}"))}]", LogLevel.Info);
    }

    private void CommitTownEchoOpportunity(PendingConversationInfo info)
    {
        if (config.EnableTownEchoes && info.GraphSnapshot is not null
            && memoryStore.TownEchoes.MarkPresented(info.PlayerId, info.NpcName, info.GraphSnapshot.TownEchoEventId))
            memoryDirty = true;
    }
}
