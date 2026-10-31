using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class RecentKnownEventTests
{
    private static void Check(bool value, string failure)
    { if (!value) throw new InvalidOperationException(failure); }
    private static string Wire(DeepSeekChatMessage m) => JsonSerializer.Serialize(m);

    public static async Task RunAsync()
    {
        foreach (string locale in new[] { "zh", "en" }) using (L.BeginRequest(locale))
        {
            bool zh = locale == "zh";
            var episode = new DreamEpisode { Id = "old-night", Npc = "Abigail", Day = 8, MorningMentionUsed = true,
                Lines = new() { new() { Role = "user", Text = "Stay with me?" },
                    new() { Role = "assistant", Text = "I wanted to, but I refused." } } };
            var dreams = new DreamPlayerState { Episodes = new() { episode,
                new() { Id = "other-npc", Npc = "Sam", Day = 9, Lines = new() { new() { Role = "assistant", Text = "SAM_SECRET" } } },
                new() { Id = "future", Npc = "Abigail", Day = 11, Lines = new() { new() { Role = "assistant", Text = "FUTURE_SECRET" } } } } };
            var store = new TownEchoStore();
            var gift = new TownEchoEvent { Id = "gift", PlayerId = "player-a", Source = "Sam", Day = 8,
                Kind = "gift_declined", Detail = "Amethyst", Deliveries = new()
                {
                    new() { Sender = "Sam", Recipient = "Abigail", Day = 9, Presented = true },
                    new() { Sender = "Abigail", Recipient = "Penny", Day = 10, Hop = 2 },
                    new() { Sender = "Abigail", Recipient = "Leah", Day = 11, Hop = 2 },
                } };
            store.Events.Add(gift);
            var all = RecentKnownEventPolicy.Dreams(dreams, "Abigail", 10, zh)
                .Concat(RecentKnownEventPolicy.Echoes(store, "player-a", "Abigail", 10, zh, n => n)).ToArray();
            var known = RecentKnownEventPolicy.Select(all, zh ? "早上好" : "Hello");
            string text = string.Join("\n", known);
            Check(known.Count(s => s.StartsWith("[recent_known;")) == 3 && text.Contains("dream_only")
                && text.Contains("heard") && text.Contains("shared"), "Non-keyword greeting lost a recent event category.");
            Check(text.Contains("Sam") && text.Contains("Penny") && !text.Contains("Leah")
                && !text.Contains("SAM_SECRET") && !text.Contains("FUTURE_SECRET"), "Wrong NPC or future knowledge leaked.");
            Check(text.Contains(zh ? "未交付" : "not delivered")
                && !text.Contains(zh ? "已收下" : "accepted it"), "Declined gift became a delivered gift.");
            Check(known.Sum(ContextMemoryCompiler.EstimateTokens) <= 500, "Recent knowledge exceeded its shared budget.");
            Check(!RecentKnownEventPolicy.Echoes(store, "other-player", "Abigail", 10, zh, n => n).Any()
                && !RecentKnownEventPolicy.Echoes(store, "player-a", "Haley", 10, zh, n => n).Any(),
                "A different player or uninformed NPC received news.");
            Check(!RecentKnownEventPolicy.Dreams(dreams, "Abigail", 10, zh, episode.Id).Any()
                && !RecentKnownEventPolicy.Dreams(dreams, "Abigail", 14, zh).Any(), "Active or expired dreams entered recent knowledge.");
            var restored = JsonSerializer.Deserialize<DreamPlayerState>(JsonSerializer.Serialize(dreams))!;
            Check(RecentKnownEventPolicy.Select(RecentKnownEventPolicy.Dreams(restored, "Abigail", 10, zh), "")
                .SequenceEqual(RecentKnownEventPolicy.Select(RecentKnownEventPolicy.Dreams(dreams, "Abigail", 10, zh), "")),
                "Save reload forgot a previously mentioned dream.");
            var focused = RecentKnownEventPolicy.Select(RecentKnownEventPolicy.Dreams(dreams, "Abigail", 10, zh),
                zh ? "昨晚你说的话算数吗？" : "What you said last night?");
            Check(focused.Any(s => s.Contains("Stay with me?") && s.Contains("but I refused.")),
                "An indirect dream follow-up lost the complete question/refusal pair.");
            string completeQuote = "玩家：你愿意吗？\n你：" + new string('想', 115) + "，但我拒绝了。";
            var constrained = all.Select(e => e.Kind == "dream_only" ? e with { Evidence = new[] { completeQuote } } : e);
            Check(RecentKnownEventPolicy.Select(constrained, "昨晚你说的话算数吗？")
                .Any(s => s.EndsWith(completeQuote, StringComparison.Ordinal)),
                "An explicit follow-up spent its budget on unrelated categories instead of the whole refusal.");
            var longEvidence = new RecentKnownEvent("long", "dream_only", 10, "We talked in a dream.",
                new[] { new string('x', 3000) + " but I refused." });
            Check(RecentKnownEventPolicy.Select(new[] { longEvidence }, "").Count == 1,
                "A long quotation was truncated rather than omitted intact.");
            var sameDay = new[] { new RecentKnownEvent("older", "dream_only", 10, "OLD", Array.Empty<string>(), 0),
                new RecentKnownEvent("newer", "dream_only", 10, "NEW", Array.Empty<string>(), 1) };
            Check(RecentKnownEventPolicy.Select(sameDay, "Hello").Single().Contains("NEW"),
                "Multiple dreams on the same night chose arbitrary hash order instead of the newest.");
            gift.Deliveries.Add(new() { Sender = "Abigail", Recipient = "Emily", Day = 10, Hop = 2 });
            var revised = RecentKnownEventPolicy.Select(RecentKnownEventPolicy.Echoes(store, "player-a", "Abigail", 10, zh, n => n), "");
            Check(revised.Any(s => s.Contains("Penny") && s.Contains("Emily") && !s.Contains("Leah")),
                "Forwarding receipts were not merged, or a future recipient was invented.");
            Check(RecentKnownEventPolicy.Echoes(store, "player-a", "Abigail", 8, zh, n => n).Count() == 0,
                "Loading an earlier day disclosed future deliveries.");

            // An exact prior request stays a prefix; only newly received knowledge is appended.
            var context = new AppendOnlyContextSession<DeepSeekChatMessage>(Wire);
            var rules = new[] { new DeepSeekChatMessage("system", "NPC identity") };
            var knowledge = known.Select(t => new DeepSeekChatMessage("user", t)).ToArray();
            var first = context.Begin(rules, () => Array.Empty<DeepSeekChatMessage>(),
                new[] { new DeepSeekChatMessage("user", "Hello") }, "empty", knownMessages: knowledge);
            context.Complete(first, first.Messages.Append(new DeepSeekChatMessage("assistant", "Hello!")).ToArray(), "seen");
            var updated = knowledge.Append(new DeepSeekChatMessage("user", "[recent_known;heard;new] NEW_RECEIPT")).ToArray();
            var second = context.Begin(rules, () => Array.Empty<DeepSeekChatMessage>(),
                new[] { new DeepSeekChatMessage("user", "Something else") }, "seen", knownMessages: updated);
            Check(!second.IsColdStart && first.Messages.Select(Wire).SequenceEqual(second.Messages.Take(first.Messages.Count).Select(Wire))
                && known.All(k => second.Messages.Count(m => m.Content == k) == 1)
                && second.Messages[^2].Content.Contains("NEW_RECEIPT"), "Knowledge append rewrote/repeated the prefix.");
            var rejected = context.Begin(rules, () => Array.Empty<DeepSeekChatMessage>(),
                new[] { new DeepSeekChatMessage("user", "Retry") }, "unread", knownMessages: knowledge);
            Check(rejected.IsColdStart && !rejected.Messages.Any(m => m.Content == "Hello!")
                && known.All(k => rejected.Messages.Count(m => m.Content == k) == 1), "Unread rollback lost real knowledge or imported an unseen reply.");
            var rollover = context.Begin(rules, () => Array.Empty<DeepSeekChatMessage>(),
                new[] { new DeepSeekChatMessage("user", new string('x', 18000)) }, "seen", knownMessages: knowledge);
            Check(rollover.IsColdStart && known.All(k => rollover.Messages.Any(m => m.Content == k)),
                "Context rollover forgot still-recent events.");

            // Actual dream and free-scene request builders carry knowledge without relevance filtering.
            var active = new DreamEpisode { Id = "active", Day = 10, Npc = "Abigail" };
            var dreamClient = new RecordingClient("{\"text\":\"Hello.\"}", "{\"text\":\"Of course.\"}");
            var reply = await new DreamService(dreamClient).ReplyAsync("key", "model", active, "NPC", "LIVE", "", "", CancellationToken.None,
                recentKnownEvents: known);
            active.Lines.Add(new() { Role = "assistant", Text = reply.Text });
            active.Lines.Add(new() { Role = "user", Text = "Anything else?" });
            await new DreamService(dreamClient).ReplyAsync("key", "model", active, "NPC", "LIVE2", "", "", CancellationToken.None,
                recentKnownEvents: known);
            var visibleKnown = known.Select(ConversationRecallPolicy.Readable).ToArray();
            Check(visibleKnown.All(k => dreamClient.Requests[1].Messages.Count(m => m.Content == k) == 1)
                && dreamClient.Requests[1].Messages.All(m => !m.Content.Contains("[recent_known;")),
                "Dream dialogue filtered or repeated recent knowledge.");
            var scene = new WeeklyStoryPlan { Actors = new() { new() { Name = "Abigail" }, new() { Name = "Sam" } },
                FreeScene = new() { Premise = "A conversation", AwaitingReply = true, Turns = 1 } };
            FreeStoryService.Record(scene, "player", "Hello");
            var sceneClient = new RecordingClient(SceneLine("Abigail"), SceneLine("Sam"));
            await new FreeStoryService(sceneClient).ReplyAsync("key", "model", scene, new[] {
                new FreeStoryActorContext("Abigail", "ABIGAIL", "", "", RecentKnownEvents: known),
                new FreeStoryActorContext("Sam", "SAM", "", "", RecentKnownEvents: new[] { "SAM_ONLY_KNOWLEDGE" }),
            }, "LIVE", CancellationToken.None);
            Check(visibleKnown.All(k => sceneClient.Requests[0].Messages.Any(m => m.Content == k))
                && sceneClient.Requests[0].Messages.All(m => !m.Content.Contains("SAM_ONLY_KNOWLEDGE"))
                && sceneClient.Requests[1].Messages.Any(m => m.Content == "SAM_ONLY_KNOWLEDGE")
                && sceneClient.Requests[1].Messages.All(m => !visibleKnown.Contains(m.Content)),
                "Group scene lost known events or leaked another actor's private knowledge.");
        }
        Console.WriteLine("Recent knowledge: bilingual non-keyword grounding, provenance, dates, flags, budgets, observed append, rollback, and scene isolation passed.");
    }

    private static string SceneLine(string actor) => JsonSerializer.Serialize(new { lines = new[] {
        new { actor, text = "Hello.", action = "talk", target = "player" } }, suggestEnd = false });
}
