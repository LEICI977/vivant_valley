using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class WeeklyStoryBranchTests
{
    public static async Task RunAsync()
    {
        await TestGenerationRecoveryAsync();
        TestFlexibleStories();
        var service = new WeeklyStoryPlannerService(new RecordingClient("{}"), new WeeklyStoryValidator());
        WeeklyStoryPlan Parse(WeeklyStoryWireResponse wire, WeeklyStoryPlanRequest? request = null)
        {
            Check(service.TryParse(JsonSerializer.Serialize(wire), request ?? Request(), out WeeklyStoryPlan? plan, out string error), error);
            Check(WeeklyStoryPlaybackPolicy.TryValidatePlan(plan!, out error), error);
            return plan!;
        }
        void Reject(Action<WeeklyStoryWireResponse> mutate, string label)
        {
            WeeklyStoryWireResponse wire = Story(); mutate(wire);
            Check(!service.TryParse(JsonSerializer.Serialize(wire), Request(), out _, out _), label);
        }

        WeeklyStoryWireResponse dateWire = Story(); dateWire.ActivityKind = "date";
        Check(Parse(dateWire).ActivityKind == "date", "Explicit date type was lost.");
        Reject(wire => wire.ActivityKind = "invented", "Unknown activity type passed.");
        WeeklyStoryPlan plan = Parse(Story());
        Check(plan.ActivityKind == "story", "Old/ordinary stories were inferred to be dates.");
        Check(plan.FormatVersion == 2 && plan.Beats.Count(beat => beat.Type == "choice") == 3, "Multiple choices were lost.");
        string[] close = Play(plan, "alex", "lean", "close");
        Check(close.Contains("Alex路线反应") && close.Contains("靠近结局") && !close.Contains("Sam路线反应")
            && !close.Contains("距离结局"), "Wrong branch dialogue played.");
        WeeklyStoryChoiceOption final = plan.Beats.Last(beat => beat.Type == "choice").Choices[0];
        Check(WeeklyStoryPlaybackPolicy.CanComplete(plan, final, true), "Completed multi-choice scene did not finish.");
        Check(TownEchoPolicy.CompletedStory("p", plan, final) is null, "An unfinished story became a propagated event.");
        plan.Status = WeeklyStoryStatus.Completed; plan.CompletedDay = 20;
        var echo = TownEchoPolicy.CompletedStory("p", plan, final)!;
        Check(echo.Kind == "story" && echo.Source == plan.Inviter
            && echo.Participants.Count == plan.Actors.Count && !echo.Detail.Contains("不应记录的预想结局"),
            "Story echo included a prospective outcome or duplicated participant events.");
        plan.ActivityKind = "date";
        Check(TownEchoPolicy.CompletedStory("p", plan, final)!.Kind == "date", "Completed explicit date wasn't shareable.");
        plan.Selections[0].ResponseDelivered = false;
        Check(TownEchoPolicy.CompletedStory("p", plan, final) is null, "Undelivered branch became a completed date.");
        plan.Selections[0].ResponseDelivered = true;
        plan.Status = WeeklyStoryStatus.MailQueued;
        Check(TownEchoPolicy.CompletedStory("p", plan, final) is null, "A date invitation became an actual date.");
        Check(TownEchoPolicy.InterruptedDate("p", plan, 20) is null, "An unstarted date was recorded as interrupted.");
        plan.Status = WeeklyStoryStatus.Running;
        Check(TownEchoPolicy.InterruptedDate("p", plan, 20)?.Kind == "date_interrupted", "Observed interrupted date was dropped.");
        var notStarted = Parse(dateWire); notStarted.Status = WeeklyStoryStatus.Running;
        Check(TownEchoPolicy.InterruptedDate("p", notStarted, 20) is null, "Staging alone became an actual date.");
        plan.ActivityKind = "story";
        Check(TownEchoPolicy.InterruptedDate("p", plan, 20) is null, "Ordinary story interruption became a date.");

        Check(!WeeklyStoryPlaybackPolicy.CanComplete(plan, plan.Beats.First(beat => beat.Type == "choice").Choices[0], true),
            "Earlier option was accepted as the final selected outcome.");
        string memory = WeeklyStoryPlaybackPolicy.BuildCompletionMemory(plan, final);
        Check(memory.Contains("选择1") && memory.Contains("选择2") && memory.Contains("选择3") && memory.Contains("关系靠近")
            && !memory.Contains("不应记录的预想结局"), "Choice history or observed ending was lost in memory.");
        Check(WeeklyStoryBranchPolicy.RelationshipChanges(plan)["Alex"] == 50, "Multiple romantic choices stacked friendship rewards.");
        var history = new WeeklyStoryHistoryEntry { Selections = plan.Selections.ToList() };
        Check(WeeklyStoryRomancePolicy.HasObservedInterest(new[] { history }, "Alex"), "Observed reciprocation was not recognized.");

        WeeklyStoryPlan roundTrip = JsonSerializer.Deserialize<WeeklyStoryPlan>(JsonSerializer.Serialize(plan))!;
        roundTrip.Normalize();
        Check(WeeklyStoryBranchPolicy.CanComplete(roundTrip), "Save round-trip lost choice state.");
        roundTrip.Selections[0].ResponseDelivered = false;
        Check(!WeeklyStoryBranchPolicy.CanComplete(roundTrip), "Unseen response was accepted as completed.");
        roundTrip.Selections[0].ResponseDelivered = true;
        roundTrip.Selections[0].OptionId = "forged";
        Check(!WeeklyStoryBranchPolicy.CanComplete(roundTrip), "Forged selected option was accepted.");
        plan.Selections.RemoveAt(0);
        Check(!WeeklyStoryPlaybackPolicy.CanComplete(plan, final, true), "Only the last choice was enough to complete.");

        string[] apart = Play(plan, "distance", "explain", "leave");
        Check(apart.Contains("拒绝路线反应") && apart.Contains("距离结局") && !apart.Contains("靠近结局"), "Refusal did not affect the ending.");
        Check(WeeklyStoryBranchPolicy.RelationshipChanges(plan)["Alex"] == -10, "Refusal ending did not replace earlier positive outcomes.");
        Check(!WeeklyStoryRomancePolicy.HasObservedInterest(new[] { history, new WeeklyStoryHistoryEntry { Selections = plan.Selections.ToList() } }, "Alex"),
            "Latest refusal did not supersede prior interest.");
        plan.Selections.Clear();
        Check(WeeklyStoryBranchPolicy.AvailableOptions(plan, plan.Beats.First(beat => beat.Type == "choice")).Length == 3,
            "Restart retained old branch state.");

        int routeCount = 0;
        var endings = new HashSet<string>();
        Explore(plan, 0);
        Check(routeCount > 3 && routeCount <= 27 && endings.SetEquals(new[] { "closer", "unresolved", "apart" }),
            "Reachable route enumeration did not cover three endings.");
        void Explore(WeeklyStoryPlan story, int choiceIndex)
        {
            WeeklyStoryBeat[] decisions = story.Beats.Where(beat => beat.Type == "choice").ToArray();
            if (choiceIndex == decisions.Length)
            {
                Check(WeeklyStoryBranchPolicy.CanComplete(story), "Reachable path could not complete.");
                endings.Add(story.Selections[^1].Ending); routeCount++; return;
            }
            WeeklyStoryBeat beat = decisions[choiceIndex];
            foreach (WeeklyStoryChoiceOption option in WeeklyStoryBranchPolicy.AvailableOptions(story, beat))
            {
                story.CurrentBeatIndex = story.Beats.IndexOf(beat);
                WeeklyStoryBranchPolicy.RecordSelection(story, beat, option);
                story.Selections[^1].ResponseDelivered = true;
                Explore(story, choiceIndex + 1);
                story.Selections.RemoveAt(story.Selections.Count - 1);
            }
        }

        WeeklyStoryWireResponse two = Story();
        two.Beats!.RemoveAll(beat => beat.Id == "c2" || beat.Text == "第二次决定后的发展");
        two.Beats.First(beat => beat.Id == "c3").Choices[0].When.Remove("c2");
        Parse(two);
        Reject(wire => wire.FormatVersion = 1, "Production accepted old single-choice format.");
        Reject(wire => wire.Beats!.First(beat => beat.Id == "c2").Id = "c1", "Duplicate choice IDs passed.");
        Reject(wire => wire.Beats![0].When["c3"] = "close", "Forward reference passed.");
        Reject(wire => wire.Beats!.First(beat => beat.Id == "c2").Choices[0].When["c1"] = "missing", "Unknown option reference passed.");
        Reject(wire => wire.Beats!.First(beat => beat.Id == "c2").When["c1"] = "alex", "Conditional decision could hide a choice.");
        Reject(wire => wire.Beats!.Insert(1, new WeeklyStoryBeat { Type = "end" }), "Early end passed.");
        Reject(wire => wire.Beats![^1].When["c1"] = "alex", "Conditional end passed.");
        Reject(wire => wire.Beats!.First(beat => beat.Id == "c2").Choices[0].When["c1"] = "alex", "One-option dead end passed.");
        Reject(wire => wire.Beats!.First(beat => beat.Id == "c3").Choices[0].When["c1"] = "distance", "Unreachable intimate option passed.");
        Reject(wire => wire.Beats!.First(beat => beat.Id == "c3").Choices[0].Ending = "invalid", "Unknown ending passed.");
        Reject(wire => wire.Beats!.First(beat => beat.Id == "c3").Choices[0].Ending = "", "Missing ending passed.");
        Reject(wire => wire.Beats!.First(beat => beat.Id == "c1").Choices[0].Ending = "closer", "Premature ending label passed.");
        var metadataOnly = Story(); metadataOnly.Beats!.First(beat => beat.Id == "c2").Choices[1].Boundary = true;
        Parse(metadataOnly);
        Reject(wire => wire.Beats!.First(beat => beat.Id == "c3").Choices[0].Intimacy = "unknown", "Unknown intimacy passed.");

        WeeklyStoryNpcCandidate adult = Request().Npcs[0];
        Parse(Story(), Request(adult with { Hearts = 2, IsDating = false, IsSupporting = true }));
        WeeklyStoryPlan backgroundOnly = Parse(Story()); backgroundOnly.Actors[0].RomanceLevel = 0;
        Check(WeeklyStoryPlaybackPolicy.TryValidatePlan(backgroundOnly, out _), "Playback still rejects relationship background.");
        Check(new WeeklyStoryPromptService().Load().Contains("adult=true"), "Adult intimacy context was not specified in the creative prompt.");

        var client = new RecordingClient(JsonSerializer.Serialize(Story()));
        var prompts = new WeeklyStoryPromptService();
        Check(prompts.Load().Contains("吃醋") && prompts.Load().Contains("formatVersion"), "Weekly assets were not embedded.");
        using var files = new TemporaryConversationPrompts();
        files.Write("weekly-story.txt", "定制周剧情题材");
        files.Write("weekly-story-format.txt", "固定格式提示");
        var editable = new WeeklyStoryPromptService(files.Root);
        var planner = new WeeklyStoryPlannerService(client, new WeeklyStoryValidator(), prompts: editable);
        Check((await planner.PlanAsync("test-key", Request())).Success, "V2 provider plan failed.");
        Check(client.Requests[0].Messages.Count == 2 && !client.Requests[0].Stream
            && client.Requests[0].Messages[0].Content.Contains("定制周剧情题材")
            && client.Requests[0].Messages[1].Content.Contains("adult")
            && !client.Requests[0].Messages[1].Content.Contains("romanceLevel"), "Weekly transport or prompt context changed incorrectly.");
        files.Write("weekly-story.txt", "下一周新题材");
        await planner.PlanAsync("test-key", Request());
        Check(client.Requests[1].Messages[0].Content.Contains("下一周新题材"), "Weekly prompt edits were not reloaded.");
    }

    private static string[] Play(WeeklyStoryPlan plan, params string[] options)
    {
        plan.Selections.Clear(); int choiceIndex = 0; var spoken = new List<string>();
        for (int index = 0; index < plan.Beats.Count; index++)
        {
            WeeklyStoryBeat beat = plan.Beats[index]; plan.CurrentBeatIndex = index;
            if (!WeeklyStoryBranchPolicy.Matches(beat.When, WeeklyStoryBranchPolicy.State(plan))) continue;
            if (beat.Type == "dialogue")
            {
                Check(!WeeklyStoryPlaybackPolicy.IsUnexpectedPostChoiceDialogue(plan.FormatVersion,
                    plan.Selections.Count > 0, isChoiceResponse: false),
                    "Runner would interrupt a valid branch with unexpected_post_choice_dialogue.");
                spoken.Add(beat.Text);
            }
            if (beat.Type != "choice") continue;
            string selectedId = options[choiceIndex++];
            WeeklyStoryChoiceOption selected = WeeklyStoryBranchPolicy.AvailableOptions(plan, beat).Single(option => option.Id == selectedId);
            WeeklyStoryBranchPolicy.RecordSelection(plan, beat, selected);
            Check(!WeeklyStoryPlaybackPolicy.IsUnexpectedPostChoiceDialogue(plan.FormatVersion,
                hasSelectedChoice: true, isChoiceResponse: true), "Runner would reject a choice response.");
            spoken.Add(selected.ResponseText); plan.Selections[^1].ResponseDelivered = true;
        }
        return spoken.ToArray();
    }

    private static void TestFlexibleStories()
    {
        var service = new WeeklyStoryPlannerService(new RecordingClient("{}"), new WeeklyStoryValidator());
        WeeklyStoryWireResponse Linear(int decisionCount, int optionCount)
        {
            var wire = Story(); wire.Beats = new();
            for (int c = 0; c < decisionCount; c++)
                wire.Beats.Add(Choice("q" + c, Enumerable.Range(0, optionCount)
                    .Select(i => Option("o" + i, "选项" + i, "romantic", intimacy: "private",
                        ending: c == decisionCount - 1 ? "closer" : "")).ToArray()));
            wire.Beats.Add(new() { Type = "end" });
            return wire;
        }
        WeeklyStoryPlan Accept(WeeklyStoryWireResponse wire)
        {
            Check(service.TryParse(JsonSerializer.Serialize(wire), Request(Request().Npcs[0] with
                { Hearts = 1, IsDating = false, IsSupporting = true }), out var plan, out var error), error);
            Check(WeeklyStoryPlaybackPolicy.TryValidatePlan(plan!, out error), error);
            return plan!;
        }
        foreach (int count in new[] { 1, 2, 5, 15, 47 })
        {
            var plan = Accept(Linear(count, 4));
            Check(plan.Beats.Count == count + 1 && plan.Beats[0].Choices.Count == 4,
                "Flexible decisions or options were removed.");
            Play(plan, Enumerable.Repeat("o3", count).ToArray());
            var last = plan.Beats.Last(beat => beat.Type == "choice").Choices[3];
            Check(WeeklyStoryPlaybackPolicy.CanComplete(plan, last, true), "Long choice history cannot complete.");
            var saved = JsonSerializer.Deserialize<WeeklyStoryPlan>(JsonSerializer.Serialize(plan))!;
            saved.Normalize();
            Check(saved.Selections.Count == count && WeeklyStoryBranchPolicy.CanComplete(saved), "Save truncated choice history.");
            var history = new WeeklyStoryHistoryEntry { Selections = saved.Selections.ToList() };
            history.Normalize();
            Check(history.Selections.Count == count, "History truncated choices above four.");
            Check(WeeklyStoryBranchPolicy.RelationshipChanges(saved)["Alex"] == 50, "More choices stacked rewards.");
            saved.Selections[^1].ResponseDelivered = false;
            Check(!WeeklyStoryBranchPolicy.CanComplete(saved), "Undelivered final response was accepted.");
        }
        foreach (int count in new[] { 2, 3, 4, 6, 7, 48 })
        {
            var plan = Accept(Linear(1, count));
            var options = plan.Beats[0].Choices;
            var displayed = new List<WeeklyStoryChoiceOption>();
            for (int page = 0; page < (count + 2) / 3; page++)
                displayed.AddRange(WeeklyStoryPlaybackPolicy.ChoicePage(options, page));
            Check(displayed.SequenceEqual(options) && displayed.Count == count, "Pagination dropped, duplicated, or reordered choices.");
            for (int i = 0; i < count; i++)
                Check(ReferenceEquals(WeeklyStoryPlaybackPolicy.ResolveChoicePageAnswer(options, i / 3, "vv_option_" + (i % 3)), options[i]),
                    "UI answer did not resolve to the exact original option.");
            Check(WeeklyStoryPlaybackPolicy.ResolveChoicePageAnswer(options, 0, "vv_next") is null
                && WeeklyStoryPlaybackPolicy.ResolveChoicePageAnswer(options, 0, "vv_previous") is null
                && WeeklyStoryPlaybackPolicy.ResolveChoicePageAnswer(options, 0, "vv_option_3") is null,
                "Page navigation or forged tokens resolved as a story selection.");
            Check(WeeklyStoryPlaybackPolicy.ChoicePage(options, -1).SequenceEqual(options.Take(3)), "Negative page was not clamped.");
            Play(plan, "o" + (count - 1));
            Check(plan.Selections.Single().OptionId == "o" + (count - 1), "Final-page choice ID was lost.");
        }
        Check(!service.TryParse(JsonSerializer.Serialize(Linear(1, 49)), Request(), out _, out _), "Option capacity guard missing.");
        Check(!service.TryParse(JsonSerializer.Serialize(Linear(0, 2)), Request(), out _, out _), "No-choice interactive story accepted.");
        Check(!service.TryParse(JsonSerializer.Serialize(Linear(1, 1)), Request(), out _, out _), "Single-option decision accepted.");

        var npcDialogue = Linear(2, 2);
        npcDialogue.Beats!.Insert(0, new() { Type = "dialogue", Actor = "Alex", Target = "Sam", Text = "你怎么也来了？" });
        Accept(npcDialogue); // No three-line opening, player-facing ratio or mandatory when.
        var boundary = Linear(2, 2);
        boundary.Beats![0].Choices[0].Boundary = true;
        var boundaryPlan = Accept(boundary);
        Play(boundaryPlan, "o0", "o1");
        Check(WeeklyStoryBranchPolicy.CanComplete(boundaryPlan), "Boundary still implicitly hides later options.");

        // Only authored when changes availability; a later lost reference still fails after state merging.
        var conditional = Linear(6, 3);
        conditional.Beats![5].Choices[2].When["q0"] = "o1";
        var conditionalPlan = Accept(conditional);
        conditionalPlan.CurrentBeatIndex = 0;
        WeeklyStoryBranchPolicy.RecordSelection(conditionalPlan, conditionalPlan.Beats[0], conditionalPlan.Beats[0].Choices[0]);
        Check(WeeklyStoryBranchPolicy.AvailableOptions(conditionalPlan, conditionalPlan.Beats[5]).Length == 2,
            "Explicit condition no longer filters options.");
        conditional.Beats[5].Choices[1].When["q0"] = "o1";
        Check(!service.TryParse(JsonSerializer.Serialize(conditional), Request(), out _, out _), "State merging hid a dead end.");
        conditional = Linear(6, 3);
        conditional.Beats![5].Choices[2].When["q0"] = "missing";
        Check(!service.TryParse(JsonSerializer.Serialize(conditional), Request(), out _, out _), "Unknown reference passed.");

        // Differential oracle: state projection must agree with complete path enumeration on small graphs.
        var random = new Random(91426);
        for (int sample = 0; sample < 150; sample++)
        {
            var wire = Linear(5, 3);
            for (int c = 1; c < 5; c++)
                foreach (var option in wire.Beats![c].Choices)
                    if (random.Next(3) == 0) option.When["q" + random.Next(c)] = "o" + random.Next(3);
            var paths = new List<Dictionary<string, string>> { new() };
            bool expected = true;
            foreach (var beat in wire.Beats!.Where(beat => beat.Type == "choice"))
            {
                var next = new List<Dictionary<string, string>>();
                var reached = new HashSet<string>();
                foreach (var state in paths)
                {
                    var available = beat.Choices.Where(option => option.When.All(pair => state[pair.Key] == pair.Value)).ToArray();
                    if (available.Length < 2) expected = false;
                    foreach (var option in available)
                    {
                        reached.Add(option.Id);
                        next.Add(new Dictionary<string, string>(state) { [beat.Id] = option.Id });
                    }
                }
                if (reached.Count != beat.Choices.Count) expected = false;
                paths = next;
            }
            bool accepted = service.TryParse(JsonSerializer.Serialize(wire), Request(), out _, out var graphError);
            Check(accepted == expected, "Projected-state validation disagrees with exhaustive oracle: " + graphError);
        }

        // 13 mutually relevant binary choices exceed 4096 states; unrelated 47-choice stories above stay cheap.
        var complex = Linear(14, 2);
        for (int i = 0; i < 13; i++) complex.Beats![13].Choices[0].When["q" + i] = "o0";
        Check(!service.TryParse(JsonSerializer.Serialize(complex), Request(), out _, out var complexityError)
            && complexityError.Contains("4096"), "Condition complexity was not bounded.");
    }

    private static async Task TestGenerationRecoveryAsync()
    {
        var service = new WeeklyStoryPlannerService(new RecordingClient("{}"), new WeeklyStoryValidator());
        string example = new WeeklyStoryPromptService().Load().Split('\n')
            .Single(line => line.StartsWith("{\"formatVersion\":2", StringComparison.Ordinal)).Trim();
        Check(service.TryParse(example, Request(), out WeeklyStoryPlan? shortPlan, out string error), error);
        Check(shortPlan!.Beats.Count == 11 && shortPlan.Beats.Count(beat => beat.Type == "choice") == 2,
            "The actual prompt example is not a complete short story.");
        foreach (string first in new[] { "explain", "distance" })
        foreach (string last in new[] { "talk", "leave" })
        {
            Play(shortPlan, first, last);
            Check(WeeklyStoryBranchPolicy.CanComplete(shortPlan), "Short example has an unplayable route.");
            var saved = JsonSerializer.Deserialize<WeeklyStoryPlan>(JsonSerializer.Serialize(shortPlan))!;
            saved.Normalize();
            Check(WeeklyStoryBranchPolicy.CanComplete(saved), "Short story lost completion after save/load.");
        }

        WeeklyStoryWireResponse repeatedEndings = Story();
        foreach (WeeklyStoryChoiceOption option in repeatedEndings.Beats!.Last(beat => beat.Type == "choice").Choices)
            option.Ending = "unresolved";
        Check(service.TryParse(JsonSerializer.Serialize(repeatedEndings), Request(), out var repeatedPlan, out error), error);
        Play(repeatedPlan!, "distance", "explain", "leave");
        Check(WeeklyStoryBranchPolicy.CanComplete(repeatedPlan!), "Same-label endings cannot complete.");

        foreach (int count in new[] { 33, 48, 49 })
        {
            WeeklyStoryWireResponse wire = Story();
            while (wire.Beats!.Count < count) wire.Beats.Insert(0, Line("完整保留的台词"));
            bool accepted = service.TryParse(JsonSerializer.Serialize(wire), Request(), out var extended, out error, out var kind);
            if (count > 48)
            {
                Check(!accepted && extended is null && kind == "too_many_beats", "Overflow was not classified or rejected.");
                continue;
            }
            Check(accepted && extended!.Beats.Count == count, "A complete longer story was rejected or shortened: " + error);
            var saved = JsonSerializer.Deserialize<WeeklyStoryPlan>(JsonSerializer.Serialize(extended))!;
            saved.Normalize();
            Check(saved.Beats.Count == count && WeeklyStoryPlaybackPolicy.TryValidatePlan(saved, out error), error);
            if (count == 48)
            {
                saved.Beats.Insert(0, Line("超限存档节点")); saved.Normalize();
                Check(saved.Beats.Count == 49 && !WeeklyStoryPlaybackPolicy.TryValidatePlan(saved, out _),
                    "Save normalization silently shortened an oversized story.");
            }
        }

        foreach (string prefix in new[] { "{\"formatVersion\":2,\"beats\":[{", "{\"beats\":[{\"target\":\"play" })
        {
            Check(!service.TryParse(prefix, Request(), out var incomplete, out error, out var kind)
                && incomplete is null && kind == "truncated_json" && error.Contains("4000"),
                "Truncated JSON was repaired, misclassified, or missing its token limit.");
        }
        foreach (string invalid in new[] { "{\"beats\":123}", "{\"beats\":[oops]}", "{} trailing" })
        {
            Check(!service.TryParse(invalid, Request(), out _, out _, out var kind)
                && kind == (invalid == "{} trailing" ? "validation" : "invalid_json"), "Malformed JSON classification changed.");
        }

        foreach (var retry in new[]
        {
            ("truncated_json", "JSON未完整结束", "8至12"),
            ("too_many_beats", "剧情演出节点不能超过 48 个。", "10至14"),
            ("invalid_json", "字段类型错误", "字段类型重新生成"),
            ("validation", "未知人物", "修正对应人物"),
            ("", "Expected end of string, but instead reached end of data.", "8至12"),
            ("", "剧情演出节点不能超过 32 个。", "10至14"),
        })
        {
            var client = new RecordingClient(example);
            var planner = new WeeklyStoryPlannerService(client, new WeeklyStoryValidator());
            var result = await planner.PlanAsync("test-key", Request(previousFailureKind: retry.Item1,
                previousFailureReason: retry.Item2, maxOutputTokens: 2400));
            Check(result.Success && client.Requests.Count == 1, "Retry introduced hidden requests or rejected valid short output.");
            var sent = client.Requests.Single();
            Check(sent.Messages.Count == 2 && !sent.Stream && sent.MaxTokens == 2400
                && sent.Messages[1].Content.Contains(retry.Item3), "Retry lost targeted instructions or changed transport/budget.");
        }
        var failedClient = new RecordingClient("{\"beats\":[{");
        var failedResult = await new WeeklyStoryPlannerService(failedClient, new WeeklyStoryValidator()).PlanAsync("test-key", Request());
        Check(!failedResult.Success && failedResult.FailureKind == "truncated_json" && failedClient.Requests.Count == 1,
            "PlanAsync lost failure classification or issued a hidden retry.");

        var config = new ModConfig { WeeklyStoryMaxOutputTokens = 2400 };
        Check(WeeklyStoryPlanningPolicy.MigrateGenerationSettings(config) && config.WeeklyStoryMaxOutputTokens == 4000,
            "Old output default was not migrated.");
        config.WeeklyStoryMaxOutputTokens = 2400;
        Check(!WeeklyStoryPlanningPolicy.MigrateGenerationSettings(config) && config.WeeklyStoryMaxOutputTokens == 2400,
            "One-time migration overwrote a later explicit preference.");
        foreach (int customLimit in new[] { 1800, 3000, 5000 })
        {
            var custom = new ModConfig { WeeklyStoryMaxOutputTokens = customLimit };
            WeeklyStoryPlanningPolicy.MigrateGenerationSettings(custom);
            Check(custom.WeeklyStoryMaxOutputTokens == customLimit, "Migration overwrote custom tokens.");
        }
        var progress = new WeeklyStoryPlanningProgress();
        Check(WeeklyStoryPlanningPolicy.TryReserve(progress, 10), "First weekly request was unavailable.");
        WeeklyStoryPlanningPolicy.Fail(progress, "JSON未完整结束", "truncated_json");
        progress = JsonSerializer.Deserialize<WeeklyStoryPlanningProgress>(JsonSerializer.Serialize(progress))!;
        progress.Normalize();
        Check(progress.Attempts == 1 && progress.FailureKind == "truncated_json", "Reload lost failure or budget.");
        Check(WeeklyStoryPlanningPolicy.TryReserve(progress, 10), "Second weekly request was unavailable.");
        WeeklyStoryPlanningPolicy.Fail(progress, "节点过多", "too_many_beats");
        progress = JsonSerializer.Deserialize<WeeklyStoryPlanningProgress>(JsonSerializer.Serialize(progress))!;
        progress.Normalize();
        Check(!WeeklyStoryPlanningPolicy.TryReserve(progress, 10) && !WeeklyStoryPlanningPolicy.TryReserve(progress, 9),
            "Reload or earlier date restored an exhausted budget.");
        Check(WeeklyStoryPlanningPolicy.TryReserve(progress, 11) && progress.Attempts == 1
            && progress.FailureKind.Length == 0 && progress.FailureReason.Length == 0, "New week retained stale failure state.");
    }

    private static WeeklyStoryPlanRequest Request(WeeklyStoryNpcCandidate? alex = null,
        string previousFailureReason = "", string previousFailureKind = "", int maxOutputTokens = 4000) => new()
    {
        MinimumChoices = 2, WeekKey = 1, CurrentDay = 10, ScheduledDay = 10, EarliestDay = 10, LatestDay = 10,
        Seed = "branch-test", PlayerName = "Farmer", MaxOutputTokens = maxOutputTokens,
        PreviousFailureReason = previousFailureReason, PreviousFailureKind = previousFailureKind,
        Npcs = new[]
        {
            alex ?? new WeeklyStoryNpcCandidate("Alex", "Alex", 10, "恋爱中", true, false, "原版性格", Array.Empty<string>()) { IsAdult = true, IsDating = true },
            new WeeklyStoryNpcCandidate("Sam", "Sam", 8, "恋爱中", true, false, "原版性格", Array.Empty<string>()) { IsAdult = true, IsDating = true },
        },
        Locations = new[] { new WeeklyStoryLocationCandidate("Beach", "海滩", Array.Empty<string>(), 3, false) },
    };

    private static WeeklyStoryWireResponse Story() => new()
    {
        FormatVersion = 2, Title = "错开的邀约", Summary = "不应记录的预想结局", Inviter = "Alex",
        Participants = new() { "Alex", "Sam" }, LocationId = "Beach", TimeSlot = "evening", MailBody = "来海边谈谈。",
        Beats = new()
        {
            Line("你不是说今天有空吗？"), Line("我以为是约我来的。", actor: "Sam"), Line("现在我们都在这里了。"),
            Choice("c1", Option("alex", "我先听Alex解释。", "warm"), Option("sam", "我先听Sam解释。", "warm", actor: "Sam"),
                Option("distance", "我想和Alex保持距离。", "distant", boundary: true)),
            Line("Alex路线反应", when: new() { ["c1"] = "alex" }),
            Line("Sam路线反应", actor: "Sam", when: new() { ["c1"] = "sam" }),
            Line("拒绝路线反应", when: new() { ["c1"] = "distance" }),
            Choice("c2", Option("explain", "我想先说清楚。", "honest"),
                Option("lean", "我愿意牵住你的手。", "romantic", intimacy: "affection", when: new() { ["c1"] = "alex" }),
                Option("decline", "我不想进一步接触。", "distant", boundary: true)),
            Line("第二次决定后的发展"),
            Choice("c3", Option("close", "我愿意吻你。", "romantic", ending: "closer", intimacy: "kiss", when: new() { ["c1"] = "alex", ["c2"] = "lean" }),
                Option("wait", "我还需要时间。", "neutral", ending: "unresolved"),
                Option("leave", "我决定保持距离。", "distant", ending: "apart", boundary: true)),
            Line("靠近结局", when: new() { ["c3"] = "close" }),
            Line("悬而未决结局", when: new() { ["c3"] = "wait" }),
            Line("距离结局", when: new() { ["c3"] = "leave" }), new() { Type = "end" },
        },
    };

    private static WeeklyStoryBeat Line(string text, string actor = "Alex", Dictionary<string, string>? when = null)
        => new() { Type = "dialogue", Actor = actor, Target = "player", Text = text, When = when ?? new() };
    private static WeeklyStoryBeat Choice(string id, params WeeklyStoryChoiceOption[] options)
        => new() { Type = "choice", Id = id, Actor = "Alex", Target = "player", Text = "你怎么决定？", Choices = options.ToList() };
    private static WeeklyStoryChoiceOption Option(string id, string text, string outcome, string actor = "Alex", string ending = "",
        string intimacy = "none", bool boundary = false, Dictionary<string, string>? when = null)
        => new() { Id = id, Text = text, ResponseActor = actor, ResponseText = "我明白你的决定了。", Outcome = outcome,
            Ending = ending, Intimacy = intimacy, Boundary = boundary, When = when ?? new() };
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
