using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class SceneContextTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static string Wire(DeepSeekChatMessage message) => JsonSerializer.Serialize(message);
    private static string Line(string actor, string text)
        => JsonSerializer.Serialize(new { lines = new[] { new { actor, text, action = "talk", target = "player" } }, suggestEnd = false });

    public static async Task RunAsync()
    {
        foreach (string locale in new[] { "zh", "en" }) using (L.BeginRequest(locale))
        {
            const string raw = "{\"text\":\"The blue shell makes me smile.\"}";
            var episode = new DreamEpisode { Id = "active-dream", Npc = "Haley", Day = 8 };
            var client = new RecordingClient(raw, "{\"text\":\"You kept it.\"}", "{\"text\":\"It still shines.\"}");
            var first = await new DreamService(client).ReplyAsync("key", "model", episode, "IDENTITY", "facts:1", "", "", CancellationToken.None);
            episode.Lines.Add(new() { Role = "assistant", Text = first.Text.Replace("blue shell", "blue\nshell") });
            episode.Lines.Add(new() { Role = "user", Text = "I kept the blue shell." });
            var secondReply = await new DreamService(client).ReplyAsync("key", "model", episode, "IDENTITY", "facts:1", "", "", CancellationToken.None);
            var a = client.Requests[0]; var b = client.Requests[1];
            Check(b.Messages.Take(a.Messages.Count).Select(Wire).SequenceEqual(a.Messages.Select(Wire))
                && b.Messages[a.Messages.Count].Role == "assistant" && b.Messages[a.Messages.Count].Content == raw,
                "A dream continuation rewrote its prior provider prefix or reconstructed the assistant JSON.");
            Check(b.Context!.SessionId == a.Context!.SessionId && b.Context.ContextEpoch == a.Context.ContextEpoch
                && b.Messages.Count(m => m.Content.Contains("facts:1")) == 1
                && b.Messages.Last().Content == "I kept the blue shell.", "Dream epoch, session, current input or single facts missing.");
            episode.Lines.Add(new() { Role = "assistant", Text = secondReply.Text });
            episode.Lines.Add(new() { Role = "user", Text = "Shall we look at it?" });
            await new DreamService(client).ReplyAsync("key", "model", episode, "IDENTITY", "{\"place\":\"农场\"}", "", "", CancellationToken.None);
            var changed = client.Requests[2];
            using (var payload = JsonDocument.Parse(changed.Messages[3].Content))
                Check(payload.RootElement.GetProperty("wakingWorldFacts").GetProperty("place").GetString() == "农场",
                    "Facts were double-encoded as a JSON string.");
            Check(changed.Messages[3].Content.Contains("农场") && !changed.Messages.Any(m => m.Content.Contains("facts:1"))
                && changed.Messages.Any(m => m.Content == raw) && changed.Messages.Any(m => m.Content == b.Messages.Last().Content),
                "Live fact refresh duplicated stale facts, escaped Chinese or dropped observed history.");

            var unread = new DreamEpisode { Id = "unread", Npc = "Haley" };
            var unreadClient = new RecordingClient("{\"text\":\"UNSEEN_GENERATED_TEXT\"}", "{\"text\":\"Hello.\"}");
            await new DreamService(unreadClient).ReplyAsync("key", "model", unread, "I", "", "", "", CancellationToken.None);
            unread.Lines.Add(new() { Role = "user", Text = "Hello." });
            await new DreamService(unreadClient).ReplyAsync("key", "model", unread, "I", "", "", "", CancellationToken.None);
            Check(unreadClient.Requests[1].Messages.All(m => !m.Content.Contains("UNSEEN_GENERATED_TEXT"))
                && unreadClient.Requests[1].Context!.ContextEpoch > unreadClient.Requests[0].Context!.ContextEpoch,
                "Unread dream text entered history instead of resetting the epoch.");

            await CheckDreamRecallAndRetryAsync();

            var previous = new DreamEpisode { Id = "old-dream", Npc = "Haley", Day = 7,
                Lines = new() { new() { Role = "assistant", Text = "The blue shell glows." } } };
            var state = new DreamPlayerState { Day = 8, Episodes = new() { previous, episode } };
            Check(DreamPolicy.RecallCandidates(state, "Haley", 8, "blue shell dream", episode.Id)
                .All(c => c.Id.StartsWith(previous.Id)), "The current dream was recalled as an older dream.");
            Check(!DreamPolicy.Recall(state, "Haley", 8, "dream", episode.Id).Contains("You kept it"), "Excluded dream re-entered recall.");

            var plan = new WeeklyStoryPlan { StoryId = "private-scene", Actors = new()
            {
                new() { Name = "Sam" }, new() { Name = "Abigail" },
            }, FreeScene = new() { Premise = "A public rehearsal discussion.", AwaitingReply = true, Turns = 1 } };
            FreeStoryService.Record(plan, "Sam", "Shall we rehearse?");
            FreeStoryService.Record(plan, "player", "How about the rehearsal?");
            var profiles = new[]
            {
                new FreeStoryActorContext("Sam", "SAM_RULES", "rehearsal SAM_PRIVATE_SECRET", "five hearts"),
                new FreeStoryActorContext("Abigail", "ABIGAIL_RULES", "rehearsal ABIGAIL_PRIVATE_SECRET", "five hearts"),
            };
            var group = new RecordingClient(Line("Abigail", "Let's choose a song."), Line("Sam", "Sounds good."),
                Line("Abigail", "I can play that."), Line("Sam", "I'll bring the guitar."));
            var reply = await new FreeStoryService(group).ReplyAsync("key", "model", plan, profiles, "LIVE", CancellationToken.None);
            Check(group.Requests.Count == 2 && reply.Lines.Count == 2 && plan.FreeScene.Events.Count == 2,
                "Group generation did not stay within two isolated calls or changed observed events.");
            Check(group.Requests[0].Messages.Any(m => m.Content.Contains("ABIGAIL_PRIVATE_SECRET"))
                && group.Requests[0].Messages.All(m => !m.Content.Contains("SAM_PRIVATE_SECRET") && !m.Content.Contains("SAM_RULES"))
                && group.Requests[1].Messages.Any(m => m.Content.Contains("SAM_PRIVATE_SECRET"))
                && group.Requests[1].Messages.All(m => !m.Content.Contains("ABIGAIL_PRIVATE_SECRET") && !m.Content.Contains("ABIGAIL_RULES")),
                "A scene request saw another actor's private memory or duplicate persona rules.");
            foreach (var line in reply.Lines) FreeStoryService.Record(plan, line.Actor, line.Text, target: line.Target);
            FreeStoryService.Record(plan, "player", "Which rehearsal song?"); plan.FreeScene.Turns++;
            await new FreeStoryService(group).ReplyAsync("key", "model", plan, profiles, "LIVE2", CancellationToken.None);
            Check(group.Requests[2].Messages.Take(group.Requests[0].Messages.Count).Select(Wire)
                .SequenceEqual(group.Requests[0].Messages.Select(Wire)), "Observed scene dialogue did not preserve the actor prefix.");
            Check(group.Requests[2].Messages.Last().Content.Contains("Sounds good")
                && group.Requests[3].Messages.Last().Content.Contains("choose a song"),
                "A participant never learned the other actor's actually observed prior-batch reply.");

            plan.FreeScene.Target = "Sam";
            Check(FreeStoryService.SelectSpeakers(plan).SequenceEqual(new[] { "Sam" }), "Direct address did not use one speaker request.");
            var wrongSpeaker = new RecordingClient(Line("Abigail", "I know Sam's private thought."));
            bool rejected = false;
            try { await new FreeStoryService(wrongSpeaker).ReplyAsync("key", "model", plan, profiles, "", CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && wrongSpeaker.Requests.Count == 1, "Private actor generation accepted another speaker.");

            var departurePlan = new WeeklyStoryPlan { Actors = new() { new() { Name = "Sam" }, new() { Name = "Abigail" } },
                FreeScene = new() { Premise = "Public discussion", AwaitingReply = true, Turns = 1 } };
            FreeStoryService.Record(departurePlan, "player", "How is the rehearsal?");
            var departureClient = new RecordingClient(Line("Abigail", "See you.").Replace("\"talk\"", "\"leave\""), Line("Sam", "Hello."));
            await new FreeStoryService(departureClient).ReplyAsync("key", "model", departurePlan, profiles, "LIVE", CancellationToken.None);
            using (var secondPayload = JsonDocument.Parse(departureClient.Requests[1].Messages.Last().Content))
                Check(secondPayload.RootElement.GetProperty("present").EnumerateArray().Any(name => name.GetString() == "Abigail"),
                    "An unplayed departure was sent to another model as an already observed absence.");

            var longDream = new DreamEpisode { Id = "long-dream", Npc = "Sam" };
            var manyReplies = Enumerable.Range(0, 25).Select(i => (object)JsonSerializer.Serialize(new { text = $"Reply {i}: " + new string('好', 580) })).ToArray();
            var longClient = new RecordingClient(manyReplies);
            for (int i = 0; i < manyReplies.Length; i++)
            {
                if (i > 0) longDream.Lines.Add(new() { Role = "user", Text = $"Question {i}: " + new string('问', 780) });
                var answer = await new DreamService(longClient).ReplyAsync("key", "model", longDream, "IDENTITY", "FIXED_FACTS", "", "", CancellationToken.None);
                var request = longClient.Requests.Last();
                Check(request.Messages.Count(m => m.Role == "assistant") == i
                    && request.Messages.Last().Content.Contains(i == 0 ? "FIXED_FACTS" : $"Question {i}:"),
                    "Long dream lost observed replies or its current input.");
                if (i > 0) Check(request.Messages.Take(longClient.Requests[i - 1].Messages.Count).Select(Wire)
                    .SequenceEqual(longClient.Requests[i - 1].Messages.Select(Wire)), "Long dream rewrote its prefix.");
                longDream.Lines.Add(new() { Role = "assistant", Text = answer.Text });
            }
            Check(longClient.Requests.Last().Context!.ContextEpoch == 1 && longDream.Lines.Count == 49,
                "A full 24-turn dream rolled over or pruned observed history.");
            var reload = JsonSerializer.Deserialize<DreamEpisode>(JsonSerializer.Serialize(longDream))!;
            reload.Lines.Add(new() { Role = "user", Text = "Remember our first words?" });
            var reloadedClient = new RecordingClient("{\"text\":\"Yes.\"}");
            await new DreamService(reloadedClient).ReplyAsync("key", "model", reload, "IDENTITY", "FIXED_FACTS", "", "", CancellationToken.None);
            Check(reloadedClient.Requests.Single().Messages.Count(m => m.Role == "assistant") == 25,
                "Save reload discarded dream history when no provider transcript was available.");
            Console.WriteLine($"Dream {locale}: opening plus 24 long Chinese turns retained; unchanged facts preserve every request prefix; reload retained all 25 observed replies.");
        }
        Console.WriteLine("Scene context: full observed dream history, exact JSON prefixes, unread rollback, selective recall, retry, bounded group calls and private speaker isolation passed.");
    }

    private static async Task CheckDreamRecallAndRetryAsync()
    {
        var oldDream = new DreamEpisode { Id = "PRIVATE_INTERNAL_ID", Npc = "Haley", Day = 7, Status = "completed",
            Summary = "Last night we discussed the blue shell and agreed to keep it safe.",
            Lines = new() { new() { Role = "user", Text = "Can I take the blue shell?" },
                new() { Role = "assistant", Text = "No, leave it here with me." } } };
        var episode = new DreamEpisode { Npc = "Haley", Day = 8 };
        var player = new DreamPlayerState { Episodes = new() { oldDream, episode } };
        var client = new RecordingClient("{\"text\":\"Hello.\"}", "{\"text\":\"I remember.\"}",
            new OperationCanceledException(), "{\"text\":\"Please leave it here.\"}", "{\"text\":\"Of course.\"}");
        async Task<DreamReply> Send(string query)
            => await new DreamService(client).ReplyAsync("key", "model", episode,
                DreamService.BuildIdentity("HALey_IDENTITY", new() { Tone = "TONE_MARKER" }), "{\"place\":\"家\"}", "", "DAYTIME_BACKGROUND",
                CancellationToken.None, memoryCandidates: DreamPolicy.RecallEpisodes(player, "Haley", 8, query).ToArray());
        var first = await Send("");
        episode.Lines.Add(new() { Role = "assistant", Text = first.Text });
        episode.Lines.Add(new() { Role = "user", Text = "Remember our dream last night?" });
        var second = await Send(episode.Lines.Last().Text);
        var request = client.Requests.Last();
        Check(request.Messages.Any(m => m.Content.Contains(oldDream.Summary))
            && request.Messages.All(m => !m.Content.Contains(oldDream.Id) && !m.Content.Contains("give_gift") && !m.Content.Contains("summary_patch"))
            && request.Messages[0].Content.Contains("TONE_MARKER"), "Dream summary recall leaked IDs or unrelated rules, or lost personality.");
        episode.Lines.Add(new() { Role = "assistant", Text = second.Text });
        episode.Lines.Add(new() { Role = "user", Text = "What were your exact words in last night's dream?" });
        bool cancelled = false;
        try { await Send(episode.Lines.Last().Text); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && episode.Lines.Count == 4, "Cancellation mutated observed dream lines.");
        var retried = await Send(episode.Lines.Last().Text);
        Check(client.Requests[2].Messages.Select(Wire).SequenceEqual(client.Requests[3].Messages.Select(Wire)),
            "Failed request charged recall quota or changed the retry context.");
        Check(client.Requests[3].Messages.Any(m => m.Content.Contains("Can I take the blue shell?") && m.Content.Contains("No, leave it here with me.")),
            "Explicit quotation recall lost the complete question/refusal pair.");
        episode.Lines.Add(new() { Role = "assistant", Text = retried.Text });
        episode.Lines.Add(new() { Role = "user", Text = "继续" });
        await Send("继续");
        Check(client.Requests.Last().Messages.Count(m => m.Content.Contains(oldDream.Summary)) == 1
            && client.Requests.Last().Messages.Count(m => m.Content.Contains("No, leave it here with me.")) == 1,
            "A continuation repeatedly recalled old dreams.");

        var partial = new DreamEpisode { Npc = "Sam" };
        var partialClient = new RecordingClient("{\"text\":\"VISIBLE_PAGE. UNREAD_PAGE.\"}", "{\"text\":\"OK.\"}");
        await new DreamService(partialClient).ReplyAsync("key", "model", partial, "I", "F", "", "", CancellationToken.None);
        partial.Lines.Add(new() { Role = "assistant", Text = "VISIBLE_PAGE." });
        partial.Lines.Add(new() { Role = "user", Text = "Continue" });
        await new DreamService(partialClient).ReplyAsync("key", "model", partial, "I", "F", "", "", CancellationToken.None);
        Check(partialClient.Requests[1].Messages.Any(m => m.Content.Contains("VISIBLE_PAGE"))
            && partialClient.Requests[1].Messages.All(m => !m.Content.Contains("UNREAD_PAGE")), "Partial display imported an unread page.");

        var oversize = new DreamEpisode { Lines = new() { new() { Role = "user", Text = new string('字', 60000) } } };
        var blockedClient = new RecordingClient();
        bool blocked = false;
        try { await new DreamService(blockedClient).ReplyAsync("key", "model", oversize, "I", "", "", "", CancellationToken.None); }
        catch (InvalidOperationException) { blocked = true; }
        Check(blocked && blockedClient.Requests.Count == 0 && oversize.Lines.Single().Text.Length == 60000,
            "Oversize input silently discarded dialogue or called the provider.");
    }

}
