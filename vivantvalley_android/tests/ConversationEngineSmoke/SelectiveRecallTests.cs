using System.Net;
using System.Text;
using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class SelectiveRecallTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static async Task RunAsync()
    {
        foreach (string language in new[] { "zh", "en" }) using (L.BeginRequest(language))
        {
            var memory = new NpcConversationMemory { FixedConversationSegments = true };
            foreach (string query in new[] { "再多讲一点最后一点", "那我是不是也埋进来啦", "就要嘛宝宝", "然后呢", "tell me more", "why?", "go on darling" })
                Check(!ConversationRecallPolicy.ShouldSearch(memory, query), "A continuation started historical recall.");
            memory.AutomaticRecallCount = 2;
            Check(!ConversationRecallPolicy.ShouldSearch(memory, "海边照片拍摄地点在哪里？"), "Automatic recall exceeded its segment allowance.");
            Check(ConversationRecallPolicy.ShouldSearch(memory, "上次海边拍照在哪里？"), "Explicit history was blocked by the automatic allowance.");
            memory.AutomaticRecallCount = 0;
            var best = new ContextMemoryCandidate("internal-1", "海边照片拍摄地点在海滩北边。");
            var weaker = new ContextMemoryCandidate("internal-2", "海边照片很漂亮。");
            var selected = ConversationRecallPolicy.Select(memory, "上次海边照片拍摄地点在哪里？", new[] { best, weaker });
            Check(selected.SelectedIds.SequenceEqual(new[] { best.Id }), "Recall did not select a single best event.");
            memory.SegmentMemory = best.Text;
            Check(ConversationRecallPolicy.Select(memory, "上次海边照片拍摄地点在哪里？", new[] { best, weaker }).SelectedIds.Count == 0,
                "An already available best match caused a weaker memory to be fished out.");
            string dirty = "[derived_dialogue;id=segment-0123456789abcdef01234567-0123456789abcdef] A stable preference.\n"
                + "[recent_known;heard;echo-0123456789abcdef-in] Sandy shared news.\n"
                + "[dream_only;id=dream:1234567890123456789:692:Haley:player_visits:15:0] A shared dream.";
            string clean = ConversationRecallPolicy.Readable(dirty);
            Check(ConversationRecallPolicy.Readable("[commitment;id=hidden;status=expired] An old promise.")
                .Contains("expired"), "Removing IDs also removed the commitment status.");
            Check(!clean.Contains(";id=") && !clean.Contains("0123456789abcdef") && !clean.Contains("1234567890123456789")
                && clean.Contains("Sandy") && clean.Contains("A shared dream"), "Memory display lost provenance/content or exposed internal identifiers.");

            var dreams = new DreamPlayerState();
            dreams.Episodes.Add(new() { Id = "old", Npc = "Haley", Day = 3, Status = "completed", Summary = "An older snow dream.",
                Lines = new() { new() { Role = "user", Text = "Snow?" }, new() { Role = "assistant", Text = "Yes, snow." } } });
            dreams.Episodes.Add(new() { Id = "latest", Npc = "Haley", Day = 9, Status = "completed", Summary = "We watched the rain together.",
                Lines = new() { new() { Role = "user", Text = "Come outside?" }, new() { Role = "assistant", Text = "No, I want to stay indoors." } } });
            dreams.Episodes.Add(new() { Id = "other-npc", Npc = "Sam", Day = 9, Status = "completed", Summary = "Private.",
                Lines = new() { new() { Role = "assistant", Text = "Private." } } });
            var episode = DreamPolicy.RecallEpisodes(dreams, "Haley", 10, "昨晚的梦是什么？").Single();
            Check(episode.Text.Contains("watched the rain") && !episode.Text.Contains("snow") && !episode.Text.Contains("Private"),
                "Last-night recall mixed episodes or NPCs.");
            var quote = DreamPolicy.RecallEpisodes(dreams, "Haley", 10, "昨晚的梦里你的原话？").Single();
            var detail = DreamPolicy.RecallEpisodes(dreams, "Haley", 10, "昨晚梦里具体怎么回答邀请？").Single();
            Check(detail.Text.Contains("Come outside?") && detail.Text.Contains("No, I want to stay indoors."),
                "A concrete detail request could not reach the original exchange.");
            Check(quote.Text.Contains("Come outside?") && quote.Text.Contains("No, I want to stay indoors."),
                "An explicit quotation lost the adjacent question or refusal.");
            memory.Messages.Add(new() { Role = "system", Source = ConversationMemorySources.Dream, Content = episode.Text, DedupeKey = "latest" });
            Check(ConversationRecallPolicy.Select(memory, "昨晚的梦？", new[] { episode }).Text == "", "An already present dream was recalled again.");
            var recent = SegmentMemoryPolicy.NewContextReferences(Array.Empty<ConversationMemoryMessage>(), "",
                new[] { "[recent_known;dream_only;dream-new] A new shared dream occurred.",
                    "[recent_known;heard;echo-new] Sandy told you the news.",
                    "[recent_evidence;heard;echo-new] Do not automatically append old dialogue." }, "today");
            Check(recent.Count == 2 && recent.Any(m => m.Content.Contains("shared dream"))
                && recent.Any(m => m.Content.Contains("Sandy")), "Recent dream/news was dropped or automatic evidence was appended.");

            var result = new SegmentSummaryResult { Summary = "A conversation about photos.", StableFacts = new() };
            var client = new RecordingClient(JsonSerializer.Serialize(result));
            var records = new[] { new ConversationMemoryMessage { Role = "user", Source = ConversationMemorySources.AiChat, Content = "NEW_OBSERVED_TURN" },
                new ConversationMemoryMessage { Role = "system", Source = SegmentMemoryPolicy.RecalledContextSource, Content = "OLD_RECALLED_SECRET" } };
            await SegmentMemoryPolicy.SummarizeAsync(client, "key", "model", "Haley", "completed_dialogue", "", records, CancellationToken.None);
            string request = client.Requests.Single().Messages.Last().Content;
            Check(request.Contains("NEW_OBSERVED_TURN") && !request.Contains("OLD_RECALLED_SECRET"),
                "Historical recall re-entered the new-segment summarizer as a new record.");
            await VerifyBridgeAndWire(language, dirty);
        }
        Console.WriteLine("Selective recall: bilingual IDs hidden, continuation gating, budget/explicit override, no weaker fallback, single dream, full quoted refusal, recent news and summary isolation passed.");
    }

    private static async Task VerifyBridgeAndWire(string language, string dirty)
    {
        var memory = new NpcConversationMemory { PlayerId = "p", NpcName = "Haley", FixedConversationSegments = true, AutomaticRecallCount = 2 };
        for (int i = 1; i <= 12; i++)
        {
            int start = memory.Messages.Count;
            memory.Messages.Add(new() { Role = "user", Source = ConversationMemorySources.AiChat, Content = "QUESTION_" + i });
            memory.Messages.Add(new() { Role = "assistant", Source = ConversationMemorySources.AiChat, Content = "ANSWER_" + i });
            ConversationTimelinePolicy.RecordCompletedTurn(memory, start);
        }
        var batch = SegmentMemoryPolicy.NextBatch(memory)!;
        string archiveDirectory = Path.Combine(Path.GetTempPath(), "vv-event-recall-" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new MemoryEventArchive(archiveDirectory, "save");
            Check(archive.TryAppend(memory), "Complete-turn archive setup failed.");
            var quotation = archive.RecallEpisodes("p", "Haley", "QUESTION_12 原话").Single();
            Check(quotation.Text.Contains("QUESTION_12") && quotation.Text.Contains("ANSWER_12"),
                "Archive recall returned a fragment instead of the complete exchange.");
            Check(archive.RecallEpisodes("other-player", "Haley", "QUESTION_12 原话").Count == 0,
                "Event recall leaked another player's archive.");
        }
        finally { if (Directory.Exists(archiveDirectory)) Directory.Delete(archiveDirectory, true); }
        Check(SegmentMemoryPolicy.TryCommit(memory, batch, new() { Summary = "Previous summary." }, _ => true), "Bridge setup could not commit.");
            Check(memory.SegmentBridge.Count == 2 && memory.SegmentBridge[0].Content == "QUESTION_12"
            && memory.SegmentBridge[1].Content == "ANSWER_12" && memory.AutomaticRecallCount == 0
            && SegmentMemoryPolicy.NextBatch(memory) is null, "Archived bridge was lost or counted as new dialogue.");
        var restored = JsonSerializer.Deserialize<NpcConversationMemory>(JsonSerializer.Serialize(memory))!.Clone();
        Check(restored.SegmentBridge.Count == 2, "Bridge did not survive save/load and clone.");
        var handler = new RecordingHttpHandler(() => ConversationTurnDecisionTests.Decision("chat", "Continue."));
        using var http = new HttpClient(handler);
        var engine = new ConversationOrchestrator(new ConversationToolProviderClient(http), _ => throw new InvalidOperationException());
        var profile = new AiRuntimeProfile(AiProviderNames.Hosted, "https://example.invalid/v1",
            new Uri("https://example.invalid/v1/chat/completions"), "model", "key", "model", TimeSpan.FromSeconds(30), false, "low");
        var snapshot = new NpcContextSnapshot { FixedConversationSegments = true, NpcName = "Haley", PlayerId = "p",
            SystemPrompt = "NPC identity", SegmentMemory = dirty, SegmentSummary = memory.SegmentSummary,
            SegmentBridge = restored.SegmentBridge, PlayerInput = "Then what?", RequestMetadata = new() { ["save_id"] = "save" } };
        await engine.DecideAsync(snapshot, profile, "bridge-" + language, 512);
        using var json = JsonDocument.Parse(handler.Requests.Single().Body);
        var messages = json.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var content = messages.Select(m => m.GetProperty("content").GetString() ?? "").ToArray();
        Check(content[^3] == "QUESTION_12" && content[^2] == "ANSWER_12" && content[^1] == "Then what?"
            && content.All(s => !s.Contains(";id=") && !s.Contains("1234567890123456789")),
            "Actual request lost the final archived turn or exposed memory storage IDs.");
    }
}
