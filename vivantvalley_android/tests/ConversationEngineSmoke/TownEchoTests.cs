using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class TownEchoTests
{
    public static void Run()
    {
        Check(!TownEchoPolicy.IsRelevant("hello", "mine", new[] { "E" })
            && !TownEchoPolicy.IsRelevant("same", "mine", new[] { "Sam" }), "Partial Latin names made unrelated input relevant.");
        Check(TownEchoPolicy.IsRelevant("I saw Sam.", "mine", new[] { "Sam" })
            && TownEchoPolicy.IsRelevant("我见过阿比盖尔", "mine", new[] { "阿比盖尔" }), "Explicit names stopped recalling known events.");
        var defaults = TownEchoNetwork.Defaults(); defaults.Validate();
        Check(defaults.Contacts.Count >= 34 && defaults.Contacts.All(p => p.Value.Count == 5), "Default NPCs lack five contacts.");
        defaults.AddFallback("Custom_Ridgeside_Test");
        Check(defaults.For("Custom_Ridgeside_Test").Count == 5, "Mod NPC fallback is missing.");
        bool rejected = false;
        try { new TownEchoNetwork { Contacts = new() { ["S"] = new() { "S", "A", "A", "B", "C" } } }.Validate(); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Invalid contact lists were accepted.");
        var network = new TownEchoNetwork { Contacts = new() { ["S"] = new() { "A", "B", "C", "D", "E" } } };
        foreach (string n in network.For("S").ToArray()) network.Contacts[n] = Enumerable.Range(1, 5).Select(i => n + i).ToList();
        network.Validate();
        var save = JsonSerializer.Deserialize<ConversationMemoryStore>("{\"Players\":{}}")!; save.Normalize();
        var store = save.TownEchoes;
        Check(store.Events.Count == 0, "Old saves must load empty echoes.");
        Check(!store.Observe("p", "S", "invitation", 10), "An invitation became a completed event.");
        Check(store.Observe("p", "S", "mine", 10) && !store.Observe("p", "S", "mine", 10), "Polling failed to dedupe.");
        var ev = store.Events.Single();
        Check(!store.Advance(10, network) && ev.Deliveries.Count == 0, "Same-day propagation occurred.");
        Check(store.Advance(11, network) && ev.Deliveries.Count == 3 && ev.Deliveries.All(d => d.Hop == 1 && d.Day == 11),
            "First wave must deliver to three people next day without meetings.");
        string first = ev.Deliveries[0].Recipient;
        Check(!store.Advance(11, network), "Same-day reload repeated or forwarded first-wave messages.");
        Check(store.Advance(12, network) && ev.Deliveries.Count == 9 && ev.Deliveries.Count(d => d.Hop == 2) == 6,
            "Each of three first-hop listeners must tell two contacts the following day.");
        Check(ev.Deliveries.Where(d => d.Hop == 2).GroupBy(d => d.Sender).All(g => g.Count() == 2), "Second wave fanout was wrong.");
        Check(ev.Deliveries.All(d => network.For(d.Sender).Contains(d.Recipient)), "Transfer ignored the sender's contacts.");
        Check(!store.Advance(13, network) && ev.Deliveries.Count == 9, "A third propagation wave occurred.");
        var second = ev.Deliveries.First(d => d.Hop == 2 && d.Sender == first);
        foreach (bool zh in new[] { true, false })
        {
            var heard = store.Recall("p", second.Recipient, "hello", 12, zh, n => n)!;
            Check(heard.Text.Contains("S → " + first) && heard.Text.Contains(zh ? "未亲历" : "not witnessed")
                && heard.Text.Length <= 900, "Two-hop provenance or complete prompt budget was lost.");
            var outgoing = store.Recall("p", "S", "mine", 12, zh, n => n)!;
            Check(!outgoing.Text.Contains(second.Recipient), "Original source magically knows downstream recipients.");
            var middle = store.Recall("p", first, "mine", 12, zh, n => n)!;
            Check(middle.Text.Contains(second.Recipient) && middle.Text.Contains(zh ? "听说" : "Heard"), "Forwarder lost incoming/outgoing knowledge.");
            Check(store.Recall("other", first, "mine", 12, zh, n => n) is null, "Player isolation failed.");
            Check(store.Recall("p", second.Recipient, "mine", 11, zh, n => n) is null, "Future receipt leaked.");
        }
        Check(!store.MarkPresented("other", second.Recipient, ev.Id), "Another player consumed a recall.");
        Check(store.MarkPresented("p", second.Recipient, ev.Id) && !store.MarkPresented("p", second.Recipient, ev.Id), "Recall consumption isn't idempotent.");
        Check(store.Recall("p", second.Recipient, "hello", 12, false, n => n) is null
            && store.Recall("p", second.Recipient, "mine", 12, false, n => n) is not null, "Consumed recall lost relevance or repeated unsolicited.");
        var loaded = JsonSerializer.Deserialize<ConversationMemoryStore>(JsonSerializer.Serialize(save))!; loaded.Normalize();
        Check(!loaded.TownEchoes.Advance(12, network)
            && JsonSerializer.Serialize(loaded.TownEchoes) == JsonSerializer.Serialize(store), "Save roundtrip rerolled recipients or lost propagation state.");
        Check(loaded.Players.Count == 0, "Hearsay entered firsthand timelines.");
        loaded.TownEchoes.Forget("p", first.ToLowerInvariant());
        Check(!loaded.TownEchoes.ContainsNpc("p", first) && !loaded.TownEchoes.ContainsNpc("p", second.Recipient), "Forget left a sender or its descendant chain.");
        loaded.TownEchoes.Advance(13, network);
        Check(!loaded.TownEchoes.ContainsNpc("p", first), "Forget redelivered old news.");
        loaded.TownEchoes.Forget("p", null);
        Check(loaded.TownEchoes.Events.Count == 0, "Forget all retained echoes.");

        // Multiple gifts on one day remain distinct; daily receiver limit is removed.
        var gifts = new TownEchoStore();
        foreach (string key in new[] { "gift1", "gift2", "mail1" })
            Check(gifts.Record(new() { PlayerId = "p", Source = "S", Kind = "gift_given", Day = 10, EventKey = key,
                Detail = "Quartz", Quantity = 2 }), "Distinct delivered gifts were merged.");
        Check(!gifts.Record(new() { PlayerId = "p", Source = "S", Kind = "gift_given", Day = 10, EventKey = "gift1" }), "Gift receipt duplicate survived.");
        gifts.Advance(11, network);
        Check(gifts.Events.All(e => e.Deliveries.Count == 3)
            && gifts.Events.SelectMany(e => e.Deliveries).GroupBy(d => d.Recipient).Any(g => g.Count() > 1), "Old daily listener limit remains.");
        var late = new TownEchoStore(); late.Observe("p", "S", "fishing", 10); late.Advance(15, network);
        Check(late.Events[0].Deliveries.Count == 3 && late.Events[0].Deliveries.All(d => d.Day == 15), "Late catchup fabricated past receipt times or forwarded twice.");
        late.Advance(16, network); Check(late.Events[0].Deliveries.Count == 9, "Late receipt didn't forward the next day.");
        var overlap = new TownEchoNetwork { Contacts = new() { ["S"] = new() { "A", "B", "C", "D", "E" } } };
        foreach (string n in overlap.For("S").ToArray()) overlap.Contacts[n] = new[] { "S", "A", "B", "C", "D", "E" }.Where(x => x != n).ToList();
        var repeated = new TownEchoStore(); repeated.Observe("p", "S", "mine", 10);
        repeated.Advance(11, overlap); repeated.Advance(12, overlap);
        Check(repeated.Events[0].Deliveries.Count == 5 && repeated.Events[0].Deliveries.All(d => d.Recipient != "S"), "Overlapping contacts caused duplicate/backflow or invented recipients.");

        var legacy = JsonSerializer.Deserialize<TownEchoStore>("{\"Events\":[{\"Id\":\"old\",\"PlayerId\":\"p\",\"Source\":\"S\",\"Kind\":\"mine\",\"Day\":10,\"Deliveries\":[{\"Recipient\":\"A\",\"Day\":11,\"Presented\":true}]}]}")!;
        legacy.Normalize();
        Check(legacy.Events[0].Deliveries[0].Sender == "S" && legacy.Events[0].Deliveries[0].Presented, "V1 source/recall state lost in migration.");
        legacy.Advance(11, network); Check(legacy.Events[0].Deliveries.Count == 3, "V1 first wave didn't top up to three.");
        legacy.Advance(12, network); Check(legacy.Events[0].Deliveries.Count == 9, "V1 event couldn't forward twice.");
        Check(store.Prune(25) && store.Events.Count == 0, "Retention failed.");
        var broken = JsonSerializer.Deserialize<ConversationMemoryStore>("{\"TownEchoes\":{\"Events\":[null,{\"PlayerId\":\"p\",\"Source\":\"S\",\"Kind\":\"mine\",\"Deliveries\":[null]}]}}")!;
        broken.Normalize(); Check(broken.TownEchoes.Events.Count == 1, "Partial data broke normalization.");
        Console.WriteLine("Town echoes: daily 3+3x2 propagation, contacts, provenance, gifts, migration, recall and isolation passed.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
