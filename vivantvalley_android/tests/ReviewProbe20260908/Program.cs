using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VivantValley;
using VivantValley.Services;

// Preserve the original report fixtures; --verify now asserts the intended
// corrections and exercises lifecycle, budget and saved-plan boundaries offline.
var findings = new List<object>();
var client = new OfflineClient();
var planner = new WeeklyStoryPlannerService(client, new WeeklyStoryValidator());
var request = new WeeklyStoryPlanRequest
{
    WeekKey = 1, CurrentDay = 7, ScheduledDay = 7, EarliestDay = 7, LatestDay = 7,
    PlayerName = "测试玩家", Seed = "independent-review-20260908",
    Npcs = new[] { new WeeklyStoryNpcCandidate("Alex", "亚历克斯", 2, "朋友", true, false, "保持角色性格", Array.Empty<string>()) },
    Locations = new[] { new WeeklyStoryLocationCandidate("Beach", "海滩", Array.Empty<string>(), 3, false) },
};
const string baseline = """
{"title":"潮声","summary":"Alex邀玩家在海边聊天。","inviter":"Alex","participants":["Alex"],"locationId":"Beach","timeSlot":"evening","mailBody":"想和你说说最近的事情。","beats":[
{"type":"dialogue","actor":"Alex","target":"player","text":"谢谢你来。"},
{"type":"dialogue","actor":"Alex","target":"player","text":"我一直在考虑这件事。"},
{"type":"dialogue","actor":"Alex","target":"player","text":"你想留下还是先回去？"},
{"type":"choice","actor":"Alex","target":"player","choices":[
{"id":"stay","text":"我留下。","responseActor":"Alex","responseText":"谢谢。","memoryText":"玩家留下聊天。","outcome":"warm"},
{"id":"leave","text":"我先走了。","responseActor":"Alex","responseText":"再见。","memoryText":"玩家离开。","outcome":"neutral"}]},
{"type":"end"}]}
""";

void ProbeStory(string name, Action<JsonObject> mutate)
{
    JsonObject json = JsonNode.Parse(baseline)!.AsObject();
    mutate(json);
    bool accepted = planner.TryParse(json.ToJsonString(), request, out var plan, out string failure);
    findings.Add(new { name, accepted, failure,
        beats = plan?.Beats.Select(b => new { b.Type, b.Actor, b.Text, choices = b.Choices.Count }).ToArray(),
        summary = plan?.Summary });
}

ProbeStory("baseline", _ => { });
ProbeStory("end_before_any_dialogue", json => json["beats"]!.AsArray().Insert(0, new JsonObject { ["type"] = "end" }));
ProbeStory("player_key_disclosure_silently_dropped", json =>
{
    json["summary"] = "玩家向Alex承认自己弄坏了纪念品，然后决定留下解释。";
    json["beats"]!.AsArray().Insert(1, new JsonObject
    {
        ["type"] = "dialogue", ["actor"] = "player", ["target"] = "Alex",
        ["text"] = "其实纪念品是我弄坏的。"
    });
});
ProbeStory("unknown_choice_actor", json => json["beats"]![3]!["actor"] = "Wizard");
ProbeStory("missing_choice_id", json => json["beats"]![3]!["choices"]![0]!.AsObject().Remove("id"));
ProbeStory("four_choices_silently_truncated", json =>
{
    JsonArray options = json["beats"]![3]!["choices"]!.AsArray();
    foreach (string id in new[] { "ask", "wait" })
    {
        JsonObject option = JsonNode.Parse(options[0]!.ToJsonString())!.AsObject();
        option["id"] = id;
        options.Add(option);
    }
});
ProbeStory("neutral_common_movement_after_choice", json => json["beats"]!.AsArray().Insert(4,
    new JsonObject { ["type"] = "face", ["actor"] = "Alex", ["target"] = "player" }));
ProbeStory("romance_at_two_hearts", json =>
{
    json["beats"]![0]!["text"] = "亲爱的，从今天起我们就是夫妻了。";
    json["beats"]![3]!["choices"]![0]!["outcome"] = "romantic";
});

const string memory = "[Y1 spring 2] 玩家答应和Alex明天去矿洞。";
findings.Add(new { name = "closed_commitment_detected_as_open", open = ConversationMemoryPolicy.IsExplicitOpenLoop("玩家拒绝了明天的约定，不再需要跟进。") });
findings.Add(new { name = "topic_refusal_reopens_recall", recall = ConversationMemoryPolicy.BuildRelevantRecall(memory, "p", "Alex", "Y1 spring 3", "不要再提上次矿洞的约定了，换个话题。", 2) });
string corrected = ConversationMemoryPolicy.UpdateLongTermMemory("[Y1 spring 1] 玩家喜欢咖啡。", "玩家不喜欢咖啡。", "Y1 spring 2", "p", "Alex", 2);
findings.Add(new { name = "preference_correction", memory = corrected });
string oldChoice = "每周剧情“在暴风雨过后一起修复海边那张旧长椅的下午”已经完成；玩家选择留下。";
string newChoice = oldChoice.Replace("留下", "离开", StringComparison.Ordinal);
string coalesced = ConversationMemoryPolicy.UpdateLongTermMemory("[Y1 spring 1] " + oldChoice, newChoice, "Y1 spring 8", "p", "Alex", 3);
findings.Add(new { name = "distinct_story_outcomes_coalesced", memory = coalesced });

Assembly assembly = typeof(WeeklyStoryValidator).Assembly;
Type engine = assembly.GetType("VivantValley.Services.InProcessConversationEngine", throwOnError: true)!;
object? InvokePrivate(Type type, string name, params object[] values)
    => type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, values);
string narrative = "[剧情：事件47；春1；Beach；已完成]\n- 玩家选择：我拒绝这次约会。\n"
                   + string.Join('\n', Enumerable.Range(0, 7).Select(i => "- Alex：后续的普通台词" + i));
findings.Add(new { name = "narrative_key_choice_lost", facts = InvokePrivate(engine, "BuildStoryFacts", narrative) });
string world = "- 当地天气：下雨；今天是否节日：否；当前是否正在节日活动：否\n- 当前任务：" + new string('任', 135)
               + "\n- 医院状态：住院至第30天（还剩2天）；移动和下矿护卫工具当前不可用。\n- 近期战斗经历：在矿井被击败2次；上次事件日期：第1年春季20日\n- 玩家：测试玩家；农场：测试农场；农场类型：标准农场\n"
               + "- 技能：耕种5，采矿5，觅食5，钓鱼5，战斗5。\n- 配偶/室友：Emily\n"
               + "- 社区中心完成：是\n- 关键能力/物品：骷髅钥匙=有\n- 姜岛船已修复：是";
findings.Add(new { name = "world_facts_capacity_loss", facts = InvokePrivate(assembly.GetType("VivantValley.ModEntry", true)!, "BuildManualWorldFacts", world) });

if (args.Contains("--verify"))
{
    int assertions = 0;
    void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    var observed = JsonSerializer.SerializeToNode(findings)!.AsArray()
        .ToDictionary(item => item!["name"]!.GetValue<string>(), item => item!);
    foreach (string name in new[] { "baseline", "missing_choice_id", "neutral_common_movement_after_choice" })
        Check(observed[name]["accepted"]!.GetValue<bool>(), name);
    foreach (string name in new[] { "end_before_any_dialogue", "player_key_disclosure_silently_dropped", "unknown_choice_actor", "four_choices_silently_truncated", "romance_at_two_hearts" })
        Check(!observed[name]["accepted"]!.GetValue<bool>(), name);
    Check(!observed["closed_commitment_detected_as_open"]["open"]!.GetValue<bool>(), "Closed promise reopened");
    Check(observed["topic_refusal_reopens_recall"]["recall"]!.GetValue<string>().Length == 0, "Refusal ignored");
    Check(corrected.Split('\n').Length == 1 && corrected.Contains("不喜欢咖啡"), "Preference correction retained contradictory state");
    Check(coalesced.Split('\n').Length == 2, "Different outcomes lost");
    string storyFacts = observed["narrative_key_choice_lost"]["facts"]!.GetValue<string>();
    Check(storyFacts.Contains("事件47") && storyFacts.Contains("我拒绝这次约会") && storyFacts.Length <= 250, "Choice/context lost");
    string worldFacts = observed["world_facts_capacity_loss"]["facts"]!.GetValue<string>();
    Check(worldFacts.Contains("Emily") && worldFacts.Contains("骷髅钥匙") && worldFacts.Contains("姜岛船：是")
        && worldFacts.Contains("移动和下矿护卫工具当前不可用") && worldFacts.Length <= 300, "Protected world facts lost");

    Check(planner.TryParse(baseline, request, out WeeklyStoryPlan? validPlan, out _), "Baseline parse");
    WeeklyStoryChoiceOption choice = validPlan!.Beats.Single(beat => beat.Type == "choice").Choices[0];
    Check(!WeeklyStoryPlaybackPolicy.CanComplete(validPlan, choice, false), "Completed before response");
    Check(!WeeklyStoryPlaybackPolicy.CanComplete(validPlan, null, true), "Completed without choice");
    Check(!WeeklyStoryPlaybackPolicy.CanComplete(validPlan, new WeeklyStoryChoiceOption { Id = choice.Id }, true), "Foreign choice accepted");
    Check(WeeklyStoryPlaybackPolicy.CanComplete(validPlan, choice, true), "Real completion rejected");
    validPlan.Summary = "尚未发生的秘密告白";
    string committed = WeeklyStoryPlaybackPolicy.BuildCompletionMemory(validPlan, choice);
    Check(committed.Contains(choice.Text) && committed.Contains(choice.ResponseText) && !committed.Contains(validPlan.Summary), "Unobserved plan summary committed as fact");
    validPlan.Beats.Add(new WeeklyStoryBeat { Type = "dialogue", Actor = "Alex", Text = "未归属分支的正文" });
    Check(!WeeklyStoryPlaybackPolicy.TryValidatePlan(validPlan, out _), "Corrupt saved sequence accepted");
    JsonObject large = JsonNode.Parse(baseline)!.AsObject();
    while (large["beats"]!.AsArray().Count < 32)
        large["beats"]!.AsArray().Insert(0, new JsonObject { ["type"] = "face", ["actor"] = "Alex", ["target"] = "player" });
    Check(planner.TryParse(large.ToJsonString(), request, out _, out _), "32 supported beats rejected");
    large["beats"]!.AsArray().Insert(0, new JsonObject { ["type"] = "pause" });
    Check(!planner.TryParse(large.ToJsonString(), request, out _, out _), "33 beats accepted");
    Check(planner.TryParse(baseline.Replace("\"title\":", "/* brace } in comment */\"title\":"), request, out _, out _), "JSON comment broke root extraction");
    string systemPrompt = (string)InvokePrivate(typeof(WeeklyStoryPlannerService), "BuildSystemPrompt")!;
    Check(planner.TryParse(systemPrompt[systemPrompt.IndexOf("{\"title\"", StringComparison.Ordinal)..], request, out _, out _), "Planner's own sample violates validator");

    var budget = new WeeklyStoryPlanningProgress();
    Check(WeeklyStoryPlanningPolicy.TryReserve(budget, 4) && budget.Attempts == 1, "First attempt");
    budget = JsonSerializer.Deserialize<WeeklyStoryPlanningProgress>(JsonSerializer.Serialize(budget))!;
    WeeklyStoryPlanningPolicy.Fail(budget, "网络中断");
    Check(WeeklyStoryPlanningPolicy.TryReserve(budget, 4) && budget.Attempts == 2, "Reload lost reserved attempt");
    WeeklyStoryPlanningPolicy.Fail(budget, "校验失败");
    Check(!WeeklyStoryPlanningPolicy.TryReserve(budget, 4) && budget.FailureReason == "校验失败", "Third attempt allowed or reason lost");
    Check(!WeeklyStoryPlanningPolicy.TryReserve(budget, 3), "Rewinding save reset budget");
    Check(WeeklyStoryPlanningPolicy.TryReserve(budget, 5) && budget.Attempts == 1, "New week not reset");
    budget.Status = "succeeded";
    Check(!WeeklyStoryPlanningPolicy.TryReserve(budget, 5), "Successful week purchased twice");
    var lateRequest = new WeeklyStoryPlanRequest { CurrentDay = 7, ScheduledDay = 7, CurrentTimeOfDay = 1700 };
    Check(WeeklyStoryPlanningPolicy.AvailableTimeSlots(lateRequest).SequenceEqual(new[] { "evening", "night" }), "Past time windows still offered");
    Check(WeeklyStoryPlanningPolicy.AvailableTimeSlots(new WeeklyStoryPlanRequest { CurrentDay = 7, ScheduledDay = 8, CurrentTimeOfDay = 2400 }).Count == 4,
        "Tomorrow incorrectly inherits today's clock restrictions");

    var state = new NpcSocialState { NpcName = "Alex" };
    const string mining = "玩家约好明天一起去矿洞";
    const string fishing = "玩家约好第20天一起钓鱼";
    ConversationCommitmentPolicy.Apply(state, new[] { mining }, "约好明天一起去矿洞", "", 10, 1);
    ConversationCommitmentPolicy.Apply(state, Array.Empty<string>(), "今天的天气不错", "", 10, 2);
    Check(ConversationCommitmentPolicy.Active(state, 10).Contains(mining), "Unrelated turn erased future commitment");
    ConversationCommitmentPolicy.Apply(state, new[] { mining, fishing }, "取消矿洞的约定，约好第20天一起钓鱼", "", 10, 3);
    Check(state.Commitments.Single(item => item.Text == mining).Status == "cancelled", "Cancellation not persisted");
    Check(ConversationCommitmentPolicy.Active(state, 10).SequenceEqual(new[] { fishing }), "Cancellation affected unrelated new promise");
    ConversationCommitmentPolicy.Apply(state, new[] { mining }, "约好明天一起去矿洞", "", 10, 2);
    Check(state.Commitments.Single(item => item.Text == mining).Status == "cancelled", "Late reply reopened cancelled promise");
    ConversationCommitmentPolicy.Apply(state, new[] { "约好稍后一起下矿" }, "还记得上次矿洞的约定吗", "", 10, 4);
    Check(ConversationCommitmentPolicy.Active(state, 10).SequenceEqual(new[] { fishing }), "Recall or paraphrase reopened cancellation");
    ConversationCommitmentPolicy.Apply(state, new[] { fishing }, "不要再提上次钓鱼的约定了", "", 10, 5);
    Check(ConversationCommitmentPolicy.Active(state, 10).Count == 0, "Refusal did not suppress proactive follow-up");
    Check(ConversationCommitmentPolicy.Active(state, 10, forFollowUp: false).Contains(fishing), "Refusal deleted valid commitment");
    ConversationCommitmentPolicy.Apply(state, new[] { "玩家承诺明天一起钓鱼" }, "你好", "", 10, 6);
    Check(ConversationCommitmentPolicy.Active(state, 10).Count == 0, "Paraphrase bypassed topic refusal");
    ConversationCommitmentPolicy.Apply(state, new[] { fishing }, "我们重新约好第20天一起钓鱼", "", 10, 7);
    Check(ConversationCommitmentPolicy.Active(state, 20).Contains(fishing), "Explicit renewal or future date lost");
    Check(ConversationCommitmentPolicy.Active(state, 22).Count == 0, "Past-due commitment remained active");
    Check(ConversationCommitmentPolicy.PrepareRecall("[第10天] " + mining, state, 20).Contains("已取消"), "Recall still presents cancellation as pending");
    Check(ConversationCommitmentPolicy.GetResolution("不要取消矿洞的约定") is null, "Negative cancellation interpreted as cancellation");
    Check(ConversationCommitmentPolicy.GetResolution("还没有完成矿洞的约定") is null, "Unfinished commitment marked completed");
    Check(ConversationCommitmentPolicy.GetResolution("矿洞的约定已经完成") == "resolved", "Resolution not recognized");
    var fresh = new NpcSocialState { NpcName = "Alex" };
    ConversationCommitmentPolicy.Apply(fresh, new[] { "约好明天一起去海边", "约好后天一起去海边" }, "约好明天和后天去海边", "", 10, 1);
    Check(fresh.Commitments.Count == 2, "Different dates merged");
    ConversationCommitmentPolicy.Apply(fresh, Array.Empty<string>(), "换个话题", "", 10, 2);
    Check(ConversationCommitmentPolicy.Active(fresh, 10).Count == 0, "Generic topic shift ignored");
    NpcSocialState savedState = JsonSerializer.Deserialize<NpcSocialState>(JsonSerializer.Serialize(state))!;
    savedState.Normalize("Alex");
    Check(savedState.Commitments.Count == state.Commitments.Count && savedState.CommitmentsUpdatedTurn == 7, "Commitment save roundtrip lost data");
    NpcSocialState oldState = JsonSerializer.Deserialize<NpcSocialState>("{\"NpcName\":\"Alex\",\"RecentSignals\":[]}")!;
    oldState.Normalize("Alex");
    Check(oldState.Commitments.Count == 0 && oldState.CommitmentsUpdatedDay == -1, "Old social save incompatible");

    Check(AiUsageDiagnostics.ReadUsage("{\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":40}}") == new AiReportedUsage(120, 40), "Reported usage unreadable");
    Check(AiUsageDiagnostics.ReadUsage("{}") is null && AiUsageDiagnostics.ReadUsage("{\"usage\":null}") is null, "Missing usage fabricated as zero");
    Check(AiUsageDiagnostics.ReadUsage("{\"usage\":{\"prompt_tokens\":-1,\"completion_tokens\":40}}") is null, "Invalid usage accepted");
    findings.Add(new { name = "regression_verification", passed = true, assertions, providerCalls = 0 });
}

Console.WriteLine(JsonSerializer.Serialize(findings, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
}));

sealed class OfflineClient : IDeepSeekClient
{
    public Task<string> CompleteChatAsync(string apiKey, DeepSeekChatRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Review probes never call a provider.");
    public Task<string> StreamChatAsync(string apiKey, DeepSeekChatRequest request, Action<DeepSeekStreamChunk> onChunk, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Review probes never call a provider.");
}
