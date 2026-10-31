using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VivantValley;
using VivantValley.Services;

internal static class LocalizationTests
{
    public static async Task RunAsync()
    {
        string originalLocale = L.Locale;
        try
        {
            Check(L.Normalize("zh-CN") == "zh" && L.Normalize("zh-TW") == "zh"
                && L.Normalize("en") == "en" && L.Normalize("fr") == "en", "Locale mapping failed.");
            L.SetLocale("en");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = Task.Run(async () =>
            {
                using var scope = L.BeginRequest();
                entered.SetResult(true);
                await release.Task;
                Check(L.Locale == "en", "Locale switch altered an in-flight request.");
                using (L.BeginRequest("zh")) Check(L.Locale == "zh", "Nested locale scope failed.");
                Check(L.Locale == "en", "Nested scope did not restore request locale.");
            });
            await entered.Task;
            L.SetLocale("zh");
            release.SetResult(true);
            await inFlight;
            Check(L.Locale == "zh", "Request snapshot leaked into the game locale.");

            var assembly = typeof(L).Assembly;
            Dictionary<string, string> ReadDictionary(string name)
            {
                using var stream = assembly.GetManifestResourceStream("VivantValley.I18n." + name + ".json")!;
                return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            }
            var en = ReadDictionary("default"); var zh = ReadDictionary("zh");
            Check(en.Keys.ToHashSet().SetEquals(zh.Keys), "Translation keys differ by language.");
            foreach (var pair in en)
            {
                Check(!Regex.IsMatch(pair.Value, "[\\p{IsCJKUnifiedIdeographs}]"), "Chinese text in English translation: " + pair.Key);
                // All placeholders, including authored named tokens, must survive translation.
                string[] Tokens(string value) => Regex.Matches(value, @"\{\{?[^{}]+\}\}?")
                    .Select(match => match.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                Check(Tokens(pair.Value).SequenceEqual(Tokens(zh[pair.Key])), "Lost/retyped placeholder: " + pair.Key);
            }

            string[] names = { "conversation.txt", "conversation-final.txt", "weekly-story.txt", "weekly-story-format.txt",
                "memory-summary.txt", "proactive-scene.txt", "social-scene.txt", "overnight-mail.txt", "gift-decision.txt", "game-role.txt", "signal-classifier.txt" };
            L.SetLocale("en");
            foreach (string name in names)
                Check(!Regex.IsMatch(new LocalizedPromptAssets().Read(name), "[\\p{IsCJKUnifiedIdeographs}]"), "Chinese in English prompt: " + name);
            string englishDefault = new ConversationPromptService().Load().SystemRules;
            using var files = new TemporaryConversationPrompts();
            files.Write("conversation.txt", "旧版自定义中文提示");
            var loader = new ConversationPromptService(files.Root);
            Check(loader.Load().SystemRules == englishDefault, "Legacy Chinese override leaked into English.");
            Directory.CreateDirectory(Path.Combine(files.Root, "assets", "prompts", "en"));
            files.Write("en/conversation.txt", "English custom rules.");
            Check(loader.Load().SystemRules == "English custom rules.", "English override ignored.");
            files.Write("en/conversation.txt", "Updated English rules.");
            Check(loader.Load().SystemRules == "Updated English rules.", "Next request did not reload English prompt.");
            files.Write("en/conversation.txt", " ");
            Check(loader.Load().SystemRules == englishDefault, "Empty English file did not fall back to English.");
            L.SetLocale("zh");
            Check(loader.Load().SystemRules == "旧版自定义中文提示", "Legacy Chinese customization lost.");
            Directory.CreateDirectory(Path.Combine(files.Root, "assets", "prompts", "zh"));
            files.Write("zh/conversation.txt", new ConversationPromptService().Load().SystemRules);
            Check(loader.Load().SystemRules == "旧版自定义中文提示", "Installed default shadowed a legacy customization.");
            files.Write("zh/conversation.txt", "新版中文自定义");
            Check(loader.Load().SystemRules == "新版中文自定义", "Explicit locale edit did not take priority.");
            string roleDefault = new LocalizedPromptAssets().Read("game-role.txt");
            files.Write("game-role.txt", "Unrelated root file.");
            var localizedAssets = new LocalizedPromptAssets(files.Root);
            Check(localizedAssets.Read("game-role.txt") == roleDefault, "New prompt incorrectly treated as a legacy override.");
            files.Write("zh/game-role.txt", roleDefault);
            Check(localizedAssets.Read("game-role.txt") == roleDefault, "New prompt tried to load a nonexistent legacy embedded resource.");

            foreach (string locale in new[] { "zh", "en" })
            {
                L.SetLocale(locale);
                string prompt = new WeeklyStoryPromptService().Load();
                string example = prompt.Split('\n').Single(line => line.StartsWith("{\"formatVersion\":2", StringComparison.Ordinal)).Trim();
                var request = new WeeklyStoryPlanRequest
                {
                    MinimumChoices = 2, WeekKey = 1, CurrentDay = 10, ScheduledDay = 10, EarliestDay = 10, LatestDay = 10,
                    Seed = "language-test", PlayerName = "Farmer",
                    Npcs = new[] { new WeeklyStoryNpcCandidate("Alex", "Alex", 8, "dating", true, false, "", Array.Empty<string>()),
                        new WeeklyStoryNpcCandidate("Sam", "Sam", 8, "dating", true, false, "", Array.Empty<string>()) },
                    Locations = new[] { new WeeklyStoryLocationCandidate("Beach", "Beach", Array.Empty<string>(), 3, false) },
                };
                var client = new RecordingClient(example);
                var result = await new WeeklyStoryPlannerService(client, new WeeklyStoryValidator()).PlanAsync("test", request);
                Check(result.Success && result.Plan!.Beats.Count == 11, "Localized weekly example is unplayable: " + result.FailureReason);
                Check(client.Requests.Single().Messages.Count == 2 && client.Requests[0].MaxTokens == 4000
                    && client.Requests[0].Messages[0].Content.Contains(locale == "en" ? "selected game language is English" : "当前游戏语言为简体中文"),
                    "Weekly locale instruction missing or transport changed.");
                if (locale == "en")
                    Check(!Regex.IsMatch(client.Requests[0].Messages[0].Content, "[\\p{IsCJKUnifiedIdeographs}]"), "English system prompt contains Chinese.");
            }
            L.SetLocale("en");
            Check(WeeklyStoryMailService.DescribeDate(0) == "Spring 1, Year 1", "English date formatting failed.");
            Check(WeeklyStoryMailService.DescribeTimeSlot("night") == "Late night", "English time slot failed.");
            var oldStory = new WeeklyStoryPlan { MailBody = "旧剧情正文", ScheduledDay = 0, ExpiryDay = 0, LocationDisplayName = "Beach" };
            string mail = WeeklyStoryMailService.Build(oldStory);
            Check(mail.Contains("旧剧情正文") && mail.Contains("Date:") && mail.Contains("Place:"), "Old generated text was altered or labels not translated.");
            Check(WeeklyStoryPlaybackPolicy.EndingLabel("closer") == "Closer", "Ending label remained Chinese.");

            var socialClient = new RecordingClient("{\"dialogue\":\"Hi!\",\"action\":\"talk_only\",\"giftCandidateId\":null,\"motiveTag\":\"greeting\"}");
            await new AiSocialSceneService(socialClient).GenerateAsync("test", new AiSocialSceneRequest { NpcName = "Alex", NpcDisplayName = "Alex" });
            Check(socialClient.Requests.Single().Messages[0].Content.Contains("selected game language is English"), "Social prompt locale missing.");
            var mailClient = new RecordingClient("{\"gifts\":[]}");
            await new OvernightMailPlannerService(mailClient).PlanAsync("test", new OvernightMailPlanRequest
                { Npcs = new[] { new OvernightMailNpcSnapshot { NpcName = "Alex", NpcDisplayName = "Alex" } } });
            Check(mailClient.Requests.Single().Messages[0].Content.Contains("selected game language is English")
                && !mailClient.Requests[0].Messages[0].Content.Contains("{{maximumGiftCount}}"), "Mail prompt locale or token substitution failed.");
            var proactive = new RecordingClient("Good to see you.");
            await new ProactiveSceneService(proactive).GenerateAsync("test", "You are Alex.", new NpcConversationMemory(),
                new PlannedNpcEncounter(), "", new ConversationEngineOptions(), CancellationToken.None);
            Check(proactive.Requests[0].Messages[0].Content.Contains("selected game language is English"), "Proactive prompt locale missing.");

            var finalArguments = new { schema_version = 1, decision = "reply", reply = "Hello there.", travel_barks = Array.Empty<string>(),
                memory_update = new { summary_patch = "", signal = new { valence = 0.1, warmth = 0.2, concern = 0.0, confidence = 0.9 },
                    topics = Array.Empty<string>(), open_loops = Array.Empty<string>() } };
            var handler = new RecordingHttpHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "",
                tool_calls = new[] { new { id = "final", type = "function", function = new { name = "submit_final_response",
                    arguments = JsonSerializer.Serialize(finalArguments) } } } } } } }), Encoding.UTF8, "application/json") });
            using var http = new HttpClient(handler);
            var orchestrator = new ConversationOrchestrator(new ConversationToolProviderClient(http), _ => throw new Exception("Unexpected tool execution"));
            var profile = new AiRuntimeProfile("DeepSeek", "https://example.test/v1", new Uri("https://example.test/v1/chat/completions"),
                "test-model", "test-key", "test", TimeSpan.FromSeconds(10), false, "low");
            var response = await orchestrator.DecideAsync(new NpcContextSnapshot { NpcName = "Alex", NpcDisplayName = "Alex",
                SystemPrompt = "You are Alex.", PlayerInput = "你好", PlayerId = "test" }, profile, "locale-request", 400);
            using var payload = JsonDocument.Parse(handler.Requests.Single().Body);
            string manualPrompt = payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Check(manualPrompt.Contains("language of the player's current input")
                && manualPrompt.Contains("If the input language cannot be determined")
                && !manualPrompt.Contains(L.OutputInstruction),
                "Manual chat still forces the game language instead of following player input.");

            var summaryClient = new RecordingClient("The player promised to visit.", "Hello again.");
            var memory = new NpcConversationMemory { NpcName = "Alex", PlayerId = "test", Messages = new()
            {
                new ConversationMemoryMessage { Role = "user", Content = "旧对话" },
                new ConversationMemoryMessage { Role = "assistant", Content = "旧回复" },
                new ConversationMemoryMessage { Role = "user", Content = "Later" },
                new ConversationMemoryMessage { Role = "assistant", Content = "Agreed" },
            } };
            await new ConversationEngine(summaryClient).GenerateReplyAsync("test", "You are Alex.", memory, "Hi", "Spring 1",
                new ConversationEngineOptions { SummaryTriggerMessageCount = 2, RecentMessagesToKeep = 0 });
            Check(summaryClient.Requests.Count == 2 && summaryClient.Requests[0].Messages[0].Content.Contains("long-term conversation memory")
                && summaryClient.Requests.All(request => request.Messages[0].Content.Contains("selected game language is English")),
                "Summary/reply generation did not both follow English.");

            var storyDirectory = Path.Combine(FindRoot(), "assets", "stories");
            var englishStories = StoryCatalog.LoadDirectory(storyDirectory);
            Check(englishStories.Count == 4 && englishStories.Issues.Count == 0, "English authored catalog changed graph or failed validation.");
            Check(englishStories.TryGet("abigail.quartz-care.01", out var story)
                && story!.Scene.AcceptText == "Accept {GiftDisplayName}", "Authored story was not translated.");
            L.SetLocale("zh");
            Check(StoryCatalog.LoadDirectory(storyDirectory).TryGet("abigail.quartz-care.01", out story)
                && story!.Scene.AcceptText == "收下{GiftDisplayName}", "Switching back did not restore Chinese story text.");
        }
        finally { L.SetLocale(originalLocale); }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "VivantValley.csproj"))) return dir.FullName;
        throw new InvalidOperationException("Project root not found.");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
