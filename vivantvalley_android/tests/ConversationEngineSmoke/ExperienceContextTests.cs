using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class ExperienceContextTests
{
    // Optional local regression input; captured player data is never copied into fixtures.
    public static void ReplayCapturedPlanning(string directory)
    {
        JsonElement payload = default; string raw = "";
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("source", out var source) && source.GetString() == "weekly-story-ai-response")
                raw = root.GetProperty("response").GetString() ?? "";
            if (!root.TryGetProperty("request", out var request) || !request.TryGetProperty("vivant_context", out var context)
                || context.GetProperty("task").GetString() != "weekly_story") continue;
            string content = request.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var inner = JsonDocument.Parse(WeeklyStoryPlannerService.ExtractJsonObject(content));
            payload = inner.RootElement.Clone();
        }
        Check(payload.ValueKind == JsonValueKind.Object && raw.Length > 0, "Missing captured weekly request/response.");
        var anchors = payload.GetProperty("recentStories").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var requestData = new WeeklyStoryPlanRequest
        {
            FreeScene = true, CurrentDay = 700, Seed = "replay",
            Npcs = payload.GetProperty("npcs").EnumerateArray().Select(n => new WeeklyStoryNpcCandidate(
                n.GetProperty("name").GetString()!, n.GetProperty("displayName").GetString()!, 5, "", true, false, "", Array.Empty<string>())).ToArray(),
            Locations = payload.GetProperty("locations").EnumerateArray().Select(l => new WeeklyStoryLocationCandidate(
                l.GetProperty("locationId").GetString()!, "", Array.Empty<string>(), l.GetProperty("maximumActors").GetInt32(), false)).ToArray(),
            RecentStoryAnchors = anchors,
            RecentHistory = anchors.Select(a => new WeeklyStoryHistoryEntry { Title = a.Split('@')[0], Summary = a }).ToArray(),
        };
        Check(!FreeStoryService.TryCreate(raw, requestData, out _, out var reason) && reason.Contains("Repeated"),
            "Captured repeated opening was not rejected.");
        Console.WriteLine("Local captured weekly request/response replay: duplicate rejected; no provider call or player file writes.");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private const string Opening = """{"formatVersion":3,"title":"A new rehearsal","premise":"Choose a rehearsal song","inviter":"Sam","participants":["Sam","Abigail"],"locationId":"Saloon","mailBody":"Come discuss a song when free.","motivations":[{"actor":"Sam","goal":"Choose a song","knownFacts":"None"},{"actor":"Abigail","goal":"Share an opinion","knownFacts":"None"}],"opening":[{"actor":"Sam","text":"I brought three songs to choose from. Which one would you like us to rehearse?","action":"talk","target":"player"}]}""";
    private static WeeklyStoryPlanRequest Request(IReadOnlyList<WeeklyStoryHistoryEntry>? history = null,
        IReadOnlyList<ExperienceFact>? experiences = null) => new()
    {
        FreeScene = true, CurrentDay = 30, Seed = "seed", WeekKey = 4,
        Npcs = new[] { new WeeklyStoryNpcCandidate("Sam", "山姆", 6, "friends", true, false, "SAM_IDENTITY", new[] { "RAW_OLD_OPENING_DO_NOT_SEND" })
            { Experiences = experiences ?? Array.Empty<ExperienceFact>(), ActiveCommitments = new[] { "Meet to rehearse when free" } },
            new WeeklyStoryNpcCandidate("Abigail", "阿比盖尔", 6, "friends", true, false, "ABIGAIL_IDENTITY", Array.Empty<string>()) },
        Locations = new[] { new WeeklyStoryLocationCandidate("Saloon", "酒吧", Array.Empty<string>(), 3, false) },
        RecentHistory = history ?? Array.Empty<WeeklyStoryHistoryEntry>(),
    };

    public static async Task RunAsync()
    {
        foreach (string locale in new[] { "zh", "en" }) using (L.BeginRequest(locale))
        {
            var memories = new NpcConversationMemory { SegmentSummary = "Player declined the beach trip; no later outing was agreed.",
                Messages = new() { new() { Role = "system", Source = ConversationMemorySources.ModAction,
                    DedupeKey = "PRIVATE_SCENE:scene:1", Content = "OLD_OPENING_RAW" },
                    new() { Role = "system", Source = SegmentMemoryPolicy.RecalledContextSource, Content = "STALE_RECALL_RAW" } } };
            var dreams = new[] {
                new DreamEpisode { Id = "PRIVATE_DREAM_ID", Npc = "Sam", Day = 29, Status = "completed",
                    Summary = "共同梦见漂浮的吉他，梦中约定不代表现实。", Lines = new() { new() { Role = "assistant", Text = "RAW_DREAM_LINE" } } },
                new DreamEpisode { Npc = "Abigail", Day = 29, Status = "completed", Summary = "PRIVATE_OTHER_DREAM",
                    Lines = new() { new() { Role = "assistant", Text = "PRIVATE_OTHER_DREAM" } } },
            };
            var letters = new[] { new SocialMailGift { NpcName = "Sam", QueuedDay = 28, IsQueued = true,
                GiftDisplayName = "咖啡", LetterBody = "休息一下。", MailId = "PRIVATE_MAIL_ID" } };
            var stories = new[] { new WeeklyStoryHistoryEntry { StoryId = "PRIVATE_STORY_ID", Title = "Old debate", CompletedDay = 29,
                Participants = new() { "Sam", "Abigail" }, WitnessSummaries = new() { ["Sam"] = "Player declined a duet. No follow-up was agreed." } } };
            var facts = ExperienceContextPolicy.Build("Sam", 30, memories, dreams, stories, letters, Array.Empty<RecentKnownEvent>());
            string wire = JsonSerializer.Serialize(facts, ExperienceContextPolicy.ReadableJson);
            Check(facts.Count == 4 && facts.Any(f => f.Source == "dream_only" && f.Date == "night 29")
                && facts.Single(f => f.Source == "mail").Status == "queued_reading_and_claim_not_confirmed"
                && wire.Contains("declined") && wire.Contains("咖啡") && !wire.Contains("\\u"), "Experience source, date, refusal, mail status or encoding lost.");
            Check(!wire.Contains("PRIVATE_") && !wire.Contains("RAW_"), "Experiences leaked private IDs, another NPC's dream or raw fragments.");
            letters[0].RewardDelivered = true; letters[0].RewardDeliveredDay = 30;
            Check(ExperienceContextPolicy.Build("Sam", 30, null, Array.Empty<DreamEpisode>(), Array.Empty<WeeklyStoryHistoryEntry>(), letters,
                Array.Empty<RecentKnownEvent>()).Single().Status == "attachment_claimed", "Mail claim was not distinguished from queuing.");
            var budgeted = ExperienceContextPolicy.Build("Sam", 30, memories, dreams, stories, letters, Array.Empty<RecentKnownEvent>(), tokenBudget: 50);
            Check(budgeted.All(f => facts.Any(full => full.Text == f.Text)), "A summary was truncated, possibly losing its outcome.");
            var rumor = new RecentKnownEvent("SECRET_NEWS_ID", "heard", 30, "Heard from Emily that the player went fishing.", Array.Empty<string>());
            Check(ExperienceContextPolicy.Build("Sam", 30, null, Array.Empty<DreamEpisode>(), Array.Empty<WeeklyStoryHistoryEntry>(), Array.Empty<SocialMailGift>(), new[] { rumor })
                .Single().Status == "hearsay_not_witnessed", "Hearsay became firsthand evidence.");

            var client = new RecordingClient(Opening);
            var planner = new WeeklyStoryPlannerService(client, new WeeklyStoryValidator());
            var planned = await planner.PlanAsync("key", Request(stories, facts));
            Check(planned.Success, "New scene failed validation.");
            string prompt = client.Requests.Single().Messages[1].Content;
            Check(prompt.Contains("experiences") && prompt.Contains("activeCommitments") && !prompt.Contains("RAW_OLD_OPENING")
                && !prompt.Contains("PRIVATE_") && !prompt.Contains("\\u") && !prompt[prompt.IndexOf("recentStories", StringComparison.Ordinal)..]
                    .Contains("Player declined a duet. No follow-up was agreed."), "Planning duplicated raw history, private IDs or witness summaries in global history.");
            var scene = planned.Plan!;
            FreeStoryService.Record(scene, "Sam", scene.FreeScene!.Opening[0].Text);
            FreeStoryService.Record(scene, "player", "No duet today.");
            FreeStoryService.Record(scene, "Abigail", "I'll leave you two to talk.", "leave");
            FreeStoryService.Record(scene, "player", "SAM_ONLY_SECRET");
            var history = StoryExperiencePolicy.Capture(scene, 30);
            Check(history.ObservedEvents.Count == 4 && !history.Summary.Contains("SAM_ONLY_SECRET"), "History lost source records or treated raw quotes as summary.");
            var summaryClient = new RecordingClient("{\"summary\":\"The player declined a duet. Abigail left before the later conversation; no follow-up was agreed.\"}");
            string summary = await StoryExperiencePolicy.SummarizeAsync(summaryClient, "key", "model", history, "Abigail", CancellationToken.None);
            Check(!summaryClient.Requests.Single().Messages[1].Content.Contains("SAM_ONLY_SECRET")
                && summaryClient.Requests.Single().TaskKind == "memory_summary", "Summary exposed unwitnessed dialogue or used the wrong task.");
            string fingerprint = StoryExperiencePolicy.Fingerprint(history, "Abigail");
            var observers = StoryExperiencePolicy.Commit(history, "Abigail", fingerprint, summary);
            Check(observers.SequenceEqual(new[] { "Abigail" }) && !history.WitnessSummaries.ContainsKey("Sam"), "Private observer summaries were shared with incompatible witnesses.");
            var together = new WeeklyStoryHistoryEntry { Participants = new() { "Sam", "Abigail" },
                ObservedEvents = history.ObservedEvents.Take(2).ToList() };
            Check(StoryExperiencePolicy.Commit(together, "Sam", StoryExperiencePolicy.Fingerprint(together, "Sam"), summary).Length == 2,
                "Identical witness records did not share a summary.");
            string original = JsonSerializer.Serialize(history.ObservedEvents);
            var badSummary = new RecordingClient("{\"summary\":\"\"}");
            bool invalid = false;
            try { await StoryExperiencePolicy.SummarizeAsync(badSummary, "key", "model", history, "Sam", CancellationToken.None); }
            catch (InvalidDataException) { invalid = true; }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var noRequest = new RecordingClient(); bool cancelled = false;
            try { await StoryExperiencePolicy.SummarizeAsync(noRequest, "key", "model", history, "Sam", cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(invalid && cancelled && noRequest.Requests.Count == 0 && JsonSerializer.Serialize(history.ObservedEvents) == original,
                "Failed/cancelled summary changed source records or sent a cancelled request.");
            StoryExperiencePolicy.Publish(memories, history, summary); StoryExperiencePolicy.Publish(memories, history, summary);
            Check(memories.Messages.Count(m => m.DedupeKey.StartsWith("story-summary-")) == 1, "Repeated summary publication duplicated timeline events.");
            history.ObservedEvents[0].Text += " changed";
            Check(StoryExperiencePolicy.Commit(history, "Abigail", fingerprint, summary).Length == 0, "Stale summary replaced edited source.");
            var store = new PlayerWeeklyStoryState { History = Enumerable.Range(0, 8).Select(i => new WeeklyStoryHistoryEntry
                { StoryId = "story-" + i, CompletedDay = i, ObservedEvents = history.ObservedEvents }).ToList() };
            store = JsonSerializer.Deserialize<PlayerWeeklyStoryState>(JsonSerializer.Serialize(store))!; store.Normalize("player");
            Check(store.History.Count == 8 && store.History.All(h => h.ObservedEvents.Count == 4), "Normalization or save reload deleted raw story history.");
            Check(!FreeStoryService.TryCreate(Opening, Request(new[] { history }), out _, out var duplicate)
                && duplicate.Contains("Repeated"), "Duplicate title/opening passed validation.");
            string renamed = Opening.Replace("A new rehearsal", "Renamed old rehearsal");
            Check(!FreeStoryService.TryCreate(renamed, Request(new[] { history }), out _, out _), "Renaming a replay bypassed duplicate detection.");
            var mailClient = new RecordingClient("{\"gifts\":[]}");
            await new OvernightMailPlannerService(mailClient).PlanAsync("key", new OvernightMailPlanRequest
            {
                SourceDay = 30, Npcs = new[] { new OvernightMailNpcSnapshot { NpcName = "Sam", NpcDisplayName = "山姆", Identity = "SAM_TONE",
                    Experiences = facts.ToList(), GameContext = "【扮演规则】\ngive_gift move_to OLD_BLOAT",
                    ConversationExcerpt = "玩家：今天不想出门。\n山姆：那就休息一下。", GiftCandidates = new() { new() { CandidateId = "sam_coffee", DisplayName = "咖啡" } } } },
            });
            string mailPrompt = mailClient.Requests.Single().Messages[1].Content;
            Check(mailPrompt.Contains("sam_coffee") && mailPrompt.Contains("SAM_TONE") && mailPrompt.Contains("experiences")
                && !mailPrompt.Contains("OLD_BLOAT") && !mailPrompt.Contains("\\u"), "Mail lost allowed gifts/personality/experiences or retained unrelated rules.");
            var relationship = SocialPromptContext.Parse("【玩家与该村民的关系】\n- 状态：朋友/相识\n- 今天是否完成过原版日常交谈：否");
            Check((string)relationship.Relationship["status"]! == "朋友/相识", "Relationship status swallowed the next line.");
        }
        Console.WriteLine("Shared experience context: provenance, privacy, whole-summary budgets, mail status, duplicate openings, witness summaries, stale rejection, persistence and bilingual prompts passed.");
    }
}
