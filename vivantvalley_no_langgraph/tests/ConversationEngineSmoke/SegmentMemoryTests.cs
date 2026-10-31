using System.Net;
using System.Text;
using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class SegmentMemoryTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void AddTurn(NpcConversationMemory memory, int number)
    {
        int start = memory.Messages.Count;
        memory.Messages.Add(new() { Role = "user", Content = "QUESTION_" + number, Source = ConversationMemorySources.AiChat });
        memory.Messages.Add(new() { Role = "assistant", Content = "ANSWER_" + number, Source = ConversationMemorySources.AiChat });
        ConversationTimelinePolicy.RecordCompletedTurn(memory, start);
        memory.TotalTurns++;
    }

    public static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vv-segment-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            VerifyMissingArchives(directory);
            var memory = new NpcConversationMemory { PlayerId = "p", NpcName = "Haley" };
            SegmentMemoryPolicy.Enable(memory);
            for (int i = 1; i <= 11; i++) AddTurn(memory, i);
            Check(SegmentMemoryPolicy.NextBatch(memory) is null && memory.Messages.Count == 22,
                "A segment archived or pruned before twelve complete turns.");
            AddTurn(memory, 12);
            var batch = SegmentMemoryPolicy.NextBatch(memory)!;
            var result = new SegmentSummaryResult { Summary = "We discussed a birthday. The proposed trip was cancelled.",
                StableFacts = new() { "The player declined the trip." } };
            Check(batch.Records.Count == 24 && ConversationTimelinePolicy.RetainRecentMessages(memory).Count == 24,
                "A full twelve-turn segment was shortened by the old rolling window.");
            Check(!SegmentMemoryPolicy.TryCommit(memory, batch, result, _ => false)
                && memory.Messages.Count == 24 && memory.SegmentSummary == "", "Archive failure deleted or replaced raw dialogue.");
            var bad = new SegmentSummaryResult { Summary = new string('x', 901) };
            bool wrote = false;
            Check(!SegmentMemoryPolicy.TryCommit(memory, batch, bad, _ => { wrote = true; return true; }) && !wrote,
                "Malformed summary was archived/accepted.");
            AddTurn(memory, 13); // A concurrent event/new turn must survive an older batch commit.
            var archive = new MemoryEventArchive(directory, "save");
            Check(SegmentMemoryPolicy.TryCommit(memory, batch, result, archive.TryAppend)
                && memory.Messages.Count == 2 && memory.Messages[0].Content == "QUESTION_13"
                && memory.ConversationSegment == 1 && memory.Summary.Contains("declined"), "Batch commit lost the following turn or stable facts.");
            Check(ConversationTimelinePolicy.BuildTimeline(memory)[0].Contains("cancelled")
                && archive.RecallCandidates("p", "Haley", "QUESTION_1").Count > 0, "Summary was not first in timeline or original source was not archived.");
            var restored = JsonSerializer.Deserialize<NpcConversationMemory>(JsonSerializer.Serialize(memory))!;
            Check(restored.SegmentSummary == memory.SegmentSummary && SegmentMemoryPolicy.NextBatch(restored) is null,
                "Reload repeated an already committed summary.");
            Check(!SegmentMemoryPolicy.TryCommit(memory, batch, result, archive.TryAppend), "Same segment committed twice.");
            var readOnly = new MemoryEventArchive(directory, "save");
            string[] filesBefore = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            readOnly.PrimeReadOnly(restored);
            Check(readOnly.RecallCandidates("p", "Haley", "QUESTION_1").Count > 0
                && filesBefore.SequenceEqual(Directory.GetFiles(directory, "*", SearchOption.AllDirectories)),
                "Loading the saved checkpoint failed to recall history or wrote archive files.");
            var legacy = new NpcConversationMemory { PlayerId = "legacy", NpcName = "Haley", Messages = new()
                { new() { Role = "user", Content = "Legacy source", Source = null!, EventId = null!, GameDate = null! } } };
            var legacyArchive = new MemoryEventArchive(directory, "null-save");
            Check(legacyArchive.TryAppend(legacy), "Nullable legacy source could not be archived.");
            Check(new MemoryEventArchive(directory, "null-save").TryAppend(legacy), "Legacy null clone generated a corrupt checksum.");
            // Reproduce a file written by the old hash-before-clone implementation, then verify exact compatibility.
            string segmentFile = Directory.GetFiles(directory, "segment-*.json", SearchOption.AllDirectories)
                .Single(p => p.Contains(MemoryEventArchive.Scope("null-save"), StringComparison.Ordinal));
            var segment = JsonSerializer.Deserialize<MemoryEventArchive.ArchiveSegment>(File.ReadAllText(segmentFile))!;
            segment.Records[0].Id = MemoryEventArchive.EventId(legacy.Messages[0]);
            File.WriteAllText(segmentFile, JsonSerializer.Serialize(segment));
            Check(new MemoryEventArchive(directory, "null-save").TryAppend(legacy), "Known old null/empty checksum mismatch was not recovered.");
            segment.Records[0].Message!.Content = "Tampered content";
            File.WriteAllText(segmentFile, JsonSerializer.Serialize(segment));
            Check(!new MemoryEventArchive(directory, "null-save").TryAppend(legacy), "Archive compatibility bypassed a real content mismatch.");

            foreach (string locale in new[] { "zh", "en" }) using (L.BeginRequest(locale))
            {
                var summaryClient = new RecordingClient(JsonSerializer.Serialize(result));
                var parsed = await SegmentMemoryPolicy.SummarizeAsync(summaryClient, "key", "model", "Haley", "dream_only", "", batch.Records, CancellationToken.None);
                Check(parsed.Summary == result.Summary && summaryClient.Requests.Count == 1
                    && summaryClient.Requests[0].TaskKind == "memory_summary"
                    && summaryClient.Requests[0].Messages[0].Content.Contains("dream_only"), "Summary request lost source, locale or single-call behavior.");
                await VerifyRequests(locale);
                await VerifyChangingRecall(locale);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("Fixed segments: twelve complete turns, atomic archive/summary, reload, null-checksum recovery, ordered full requests and on-demand actions passed.");
    }

    private static async Task VerifyRequests(string locale)
    {
        var handler = new RecordingHttpHandler(() => ConversationTurnDecisionTests.Decision("chat", "VISIBLE_REPLY"));
        using var http = new HttpClient(handler);
        var engine = new ConversationOrchestrator(new ConversationToolProviderClient(http), _ => throw new InvalidOperationException("No real action expected."));
        var profile = new AiRuntimeProfile(AiProviderNames.Hosted, "https://example.invalid/v1", new Uri("https://example.invalid/v1/chat/completions"),
            "test", "key", "test", TimeSpan.FromSeconds(30), false, "low");
        var snapshot = new NpcContextSnapshot { FixedConversationSegments = true, NpcName = "Haley", NpcDisplayName = "Haley", PlayerId = "p",
            SystemPrompt = "NPC_IDENTITY " + new string('规', 1400), SceneSnapshot = "- 地点：Town", WorldState = "FACTS",
            SegmentSummary = "PREVIOUS_SEGMENT_SUMMARY", SegmentMemory = "STABLE_MEMORY", MemorySummary = "",
            NarrativeContext = "UNRELATED_OLD_STORY", PlayerInput = "FIRST_PLAYER_INPUT", RelationshipStatus = "dating",
            AllowedTools = Enumerable.Range(0, 12).Select(i => new LangGraphGiftCandidate { CandidateKey = "GIFT_SECRET_" + i,
                DisplayName = "Gift", DisplayHint = new string('礼', 80) }).ToArray(),
            RequestMetadata = new() { ["save_id"] = "save", ["game_date"] = "Y7 spring 21 1040" } };
        var history = new List<ConversationMemoryMessage>();
        for (int turn = 0; turn < 12; turn++)
        {
            snapshot.PlayerInput = "CURRENT_QUESTION_" + turn;
            snapshot.SegmentMessages = history.Select(m => m.Clone()).ToArray();
            var response = await engine.DecideAsync(snapshot, profile, locale + "-turn-" + turn, 2048);
            Check(response.Decision!.Reply == "VISIBLE_REPLY", "Explicit single-call chat decision was not accepted.");
            history.Add(new() { Role = "user", Source = ConversationMemorySources.AiChat, Content = snapshot.PlayerInput });
            history.Add(new() { Role = "assistant", Source = ConversationMemorySources.AiChat, Content = "VISIBLE_REPLY" });
        }
        Check(handler.Requests.Count == 12, "Ordinary chat generated a second draft/final request.");
        using var first = JsonDocument.Parse(handler.Requests[0].Body);
        using var second = JsonDocument.Parse(handler.Requests[1].Body);
        using var last = JsonDocument.Parse(handler.Requests[^1].Body);
        var a = first.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var b = second.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Check(a[0].GetProperty("role").GetString() == "system" && a[1].GetProperty("content").GetString()!.Contains("1040")
            && a[2].GetProperty("content").GetString()!.Contains("STABLE_MEMORY")
            && a[3].GetProperty("content").GetString()!.Contains("PREVIOUS_SEGMENT_SUMMARY"), "System/facts/memory/timeline order is wrong.");
        Check(a.Select(m => m.GetRawText()).SequenceEqual(b.Take(a.Length).Select(m => m.GetRawText())),
            "Unchanged facts and consecutive dialogue failed exact request-prefix preservation.");
        Check(handler.Requests.All(r => !r.Body.Contains("GIFT_SECRET") && !r.Body.Contains("UNRELATED_OLD_STORY"))
            && last.RootElement.GetProperty("messages").GetArrayLength() == 27
            && last.RootElement.GetProperty("vivant_context").GetProperty("context_epoch").GetInt64() == 1,
            "Real-sized twelve-turn context lost history, leaked candidates/story, or reset each turn.");
        snapshot.RequestMetadata["game_date"] = "Y7 spring 21 1050";
        snapshot.PlayerInput = "Birthday follow-up";
        snapshot.SegmentMessages = history;
        await engine.DecideAsync(snapshot, profile, "changed-facts-" + locale, 2048);
        using var changed = JsonDocument.Parse(handler.Requests[^1].Body);
        Check(changed.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("1050")
            && changed.RootElement.GetProperty("messages").GetArrayLength() == 29, "Live fact refresh lost the entire dialogue.");

        int requests = 0;
        var actionHandler = new RecordingHttpHandler(() => requests++ == 0
            ? ConversationTurnDecisionTests.Decision("consider_action", "", "gift")
            : ConversationTurnDecisionTests.Decision("chat", "I will just chat."));
        using var actionHttp = new HttpClient(actionHandler);
        var actionEngine = new ConversationOrchestrator(new ConversationToolProviderClient(actionHttp), _ => throw new InvalidOperationException("Inspection must not give a gift."));
        snapshot.SegmentMessages = Array.Empty<ConversationMemoryMessage>();
        await actionEngine.DecideAsync(snapshot, profile, "inspect-" + locale, 512);
        Check(actionHandler.Requests.Count == 2 && !actionHandler.Requests[0].Body.Contains("GIFT_SECRET")
            && actionHandler.Requests[1].Body.Contains("GIFT_SECRET_0"), "Gift candidates were not obtained only after a read-only intent request.");
        var giftResponses = new Queue<HttpResponseMessage>(new[]
        {
            ConversationTurnDecisionTests.Decision("consider_action", "", "gift"),
            ToolResponse("give_gift", new { candidate_key = "GIFT_SECRET_0", reason_tag = "warm_chat" }, "give"),
            ToolResponse("submit_final_response", new { schema_version = 1, decision = "reply",
                reply = "A gift for you.", travel_barks = Array.Empty<string>(),
                memory_update = new { summary_patch = "", signal = new { valence = 0.0, warmth = 0.2, concern = 0.0, confidence = 1.0 },
                    topics = Array.Empty<string>(), open_loops = Array.Empty<string>() } }, "final"),
        });
        var giftHandler = new RecordingHttpHandler(() => giftResponses.Dequeue());
        using var giftHttp = new HttpClient(giftHandler);
        int executed = 0;
        var giftEngine = new ConversationOrchestrator(new ConversationToolProviderClient(giftHttp), request =>
        {
            executed++;
            return Task.FromResult(new GameBridgeToolResult { RequestId = request.RequestId, ToolCallId = request.ToolCallId,
                ContextVersion = request.ContextVersion, Tool = request.Tool, Status = "completed", Ok = true,
                CandidateKey = request.CandidateKey, DisplayName = "Gift", Quantity = 1, ReceiptId = "receipt-gift" });
        });
        var delivered = await giftEngine.DecideAsync(snapshot, profile, "deliver-" + locale, 512);
        Check(executed == 1 && delivered.ToolExecution?.Ok == true
            && delivered.ToolExecution.CandidateKey == "GIFT_SECRET_0" && giftHandler.Requests.Count == 3,
            "On-demand gift options failed validated execution or authoritative finalization.");
        var invalidHandler = new RecordingHttpHandler(() => ToolResponse("give_gift",
            new { candidate_key = "GIFT_SECRET_0", reason_tag = "warm_chat" }, "unread-options"));
        using var invalidHttp = new HttpClient(invalidHandler);
        var invalidEngine = new ConversationOrchestrator(new ConversationToolProviderClient(invalidHttp),
            _ => throw new InvalidOperationException("Uninspected action reached the game."));
        bool rejected = false;
        try { await invalidEngine.DecideAsync(snapshot, profile, "invalid-" + locale, 512); }
        catch (Exception ex) when (ex.GetType().Name == "ConversationProtocolException") { rejected = true; }
        Check(rejected, "A game action without a current options receipt was accepted.");
    }

    private static void VerifyMissingArchives(string directory)
    {
        Directory.CreateDirectory(directory);
        var notices = new List<string>();
        var memory = new NpcConversationMemory { PlayerId = "missing-player", NpcName = "Haley",
            ArchiveBranchId = Guid.NewGuid().ToString("N"), ArchivedRecordCount = 51 };
        string originalBranch = memory.ArchiveBranchId;
        SegmentMemoryPolicy.Enable(memory);
        for (int i = 0; i < 12; i++) AddTurn(memory, i);
        var archive = new MemoryEventArchive(directory, "missing-save", notices.Add);
        int before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length;
        archive.PrimeReadOnly(memory);
        archive.PrimeReadOnly(memory);
        Check(archive.UnavailableCheckpointCount == 1 && notices.Count == 0
            && Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length == before
            && memory.ArchiveBranchId == originalBranch, "Read-only missing-checkpoint check wrote data or spammed warnings.");
        Check(archive.TryAppend(memory) && memory.Messages.Count == 24
            && memory.ArchiveBranchId != originalBranch && memory.ArchivedRecordCount == 24
            && memory.ArchiveRecoveryCheckpoints.Single().Contains(originalBranch)
            && notices.Count == 1, "Missing archive blocked new archival or silently discarded the old checkpoint.");
        var batch = SegmentMemoryPolicy.NextBatch(memory)!;
        Check(SegmentMemoryPolicy.TryCommit(memory, batch, new() { Summary = "Retained conversation summarized." }, archive.TryAppend)
            && memory.Messages.Count == 0 && memory.ConversationSegment == 1 && notices.Count == 1,
            "Missing archive recovery did not permit a twelve-turn commit.");
        var restored = JsonSerializer.Deserialize<NpcConversationMemory>(JsonSerializer.Serialize(memory))!;
        var reloaded = new MemoryEventArchive(directory, "missing-save", notices.Add);
        reloaded.PrimeReadOnly(restored);
        Check(reloaded.UnavailableCheckpointCount == 0
            && reloaded.RecallCandidates(memory.PlayerId, memory.NpcName, "QUESTION_0").Count > 0
            && restored.ArchiveRecoveryCheckpoints.Single().Contains(originalBranch), "Recovery did not survive reload.");

        // Missing later page: preserve the validated prefix and never rewrite the old branch.
        var partial = new NpcConversationMemory { PlayerId = "partial", NpcName = "Sam" };
        for (int i = 0; i < 70; i++) partial.Messages.Add(new() { Role = "user", Content = "old-record-" + i });
        var initial = new MemoryEventArchive(directory, "partial-save");
        Check(initial.TryAppend(partial), "Partial archive setup failed.");
        string partialBranch = partial.ArchiveBranchId;
        string branchPath = Path.Combine(directory, MemoryEventArchive.Scope("partial-save"),
            MemoryEventArchive.Scope("partial"), MemoryEventArchive.Scope("Sam"), partialBranch);
        string prefixFile = Path.Combine(branchPath, "segment-00000000.json");
        byte[] prefixBytes = File.ReadAllBytes(prefixFile);
        string absent = Path.Combine(branchPath, "segment-00000001.json");
        File.Move(absent, absent + ".test-missing");
        partial.Messages.Clear();
        partial.Messages.Add(new() { Role = "user", Content = "new-retained-record" });
        var partialReload = new MemoryEventArchive(directory, "partial-save");
        Check(partialReload.TryAppend(partial) && partial.ArchivedRecordCount == 65
            && File.ReadAllBytes(prefixFile).SequenceEqual(prefixBytes)
            && File.Exists(absent + ".test-missing") && partial.ArchiveBranchId != partialBranch,
            "Partial recovery changed old files, lost the available prefix or invented missing records.");

        // An I/O failure must not publish a replacement checkpoint before durable writes.
        string blockedRoot = Path.Combine(directory, "blocked-recovery");
        File.WriteAllText(blockedRoot, "not a directory");
        var blocked = new NpcConversationMemory { PlayerId = "p", NpcName = "n",
            ArchiveBranchId = Guid.NewGuid().ToString("N"), ArchivedRecordCount = 51,
            Messages = new() { new() { Role = "user", Content = "keep me" } } };
        string blockedBranch = blocked.ArchiveBranchId;
        var failed = new MemoryEventArchive(blockedRoot, "save");
        Check(!failed.TryAppend(blocked) && blocked.ArchiveBranchId == blockedBranch
            && blocked.ArchivedRecordCount == 51 && blocked.ArchiveRecoveryCheckpoints.Count == 0
            && blocked.Messages.Count == 1, "Failed recovery published a false receipt or lost current records.");
    }

    private static async Task VerifyChangingRecall(string locale)
    {
        var handler = new RecordingHttpHandler(() => ConversationTurnDecisionTests.Decision("chat", "REPLY"));
        using var http = new HttpClient(handler);
        var engine = new ConversationOrchestrator(new ConversationToolProviderClient(http),
            _ => throw new InvalidOperationException("No action expected."));
        var profile = new AiRuntimeProfile(AiProviderNames.Hosted, "https://example.invalid/v1",
            new Uri("https://example.invalid/v1/chat/completions"), "model", "key", "model", TimeSpan.FromSeconds(30), false, "low");
        var history = new List<ConversationMemoryMessage>();
        var snapshot = new NpcContextSnapshot { FixedConversationSegments = true, NpcName = "Haley", PlayerId = "p",
            SystemPrompt = "NPC identity", SegmentMemory = "FIXED_BACKGROUND",
            RequestMetadata = new() { ["save_id"] = "save", ["game_date"] = "Y1 spring 1 1000" } };
        for (int i = 0; i < 4; i++)
        {
            snapshot.PlayerInput = "Question " + i;
            snapshot.MemorySummary = i == 3 ? "old topic 0" : "old topic " + i;
            snapshot.RecentKnownEvents = new[] { "[recent_known;heard;one] Received one message." };
            history.AddRange(SegmentMemoryPolicy.NewContextReferences(history, snapshot.MemorySummary,
                snapshot.RecentKnownEvents, snapshot.RequestMetadata["game_date"]));
            snapshot.SegmentMessages = history.Select(m => m.Clone()).ToArray();
            await engine.DecideAsync(snapshot, profile, "recall-" + locale + "-" + i, 512);
            history.Add(new() { Role = "user", Source = ConversationMemorySources.AiChat, Content = snapshot.PlayerInput });
            history.Add(new() { Role = "assistant", Source = ConversationMemorySources.AiChat, Content = "REPLY" });
        }
        for (int i = 1; i < handler.Requests.Count; i++)
        {
            using var prior = JsonDocument.Parse(handler.Requests[i - 1].Body);
            using var next = JsonDocument.Parse(handler.Requests[i].Body);
            var a = prior.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToArray();
            var b = next.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToArray();
            Check(a.SequenceEqual(b.Take(a.Length)), "Changed recall rewrote the stable memory or earlier dialogue prefix.");
        }
        Check(history.Count(m => m.Source == SegmentMemoryPolicy.RecalledContextSource) == 4,
            "A repeat recall or recent event was appended repeatedly.");
    }

    private static HttpResponseMessage ToolResponse(string name, object arguments, string id)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "", tool_calls = new[]
            { new { id, type = "function", function = new { name, arguments = JsonSerializer.Serialize(arguments) } } } } } },
        }), Encoding.UTF8, "application/json") };
}
