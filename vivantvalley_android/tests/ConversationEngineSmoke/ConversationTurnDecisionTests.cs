using System.Net;
using System.Text;
using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class ConversationTurnDecisionTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static readonly AiRuntimeProfile Profile = new(AiProviderNames.Hosted, "https://example.invalid/v1",
        new Uri("https://example.invalid/v1/chat/completions"), "model", "key", "model", TimeSpan.FromSeconds(30), false, "low");
    private static object Memory => new { summary_patch = "", signal = new { valence = 0, warmth = 0, concern = 0, confidence = 1 },
        topics = Array.Empty<string>(), open_loops = Array.Empty<string>() };
    public static HttpResponseMessage Decision(string decision, string reply, string kind = "none", string[]? travelBarks = null) => Tool("decide_turn", new
    {
        schema_version = 1, decision, reply, action_kind = kind, intent = kind == "none" ? "" : "Start " + kind,
        travel_barks = travelBarks ?? Array.Empty<string>(), memory_update = Memory,
    });
    private static HttpResponseMessage Final(string reply, string[]? travelBarks = null) => Tool("submit_final_response", new
    { schema_version = 1, decision = "reply", reply, travel_barks = travelBarks ?? Array.Empty<string>(), memory_update = Memory });
    private static HttpResponseMessage Tool(string name, object arguments) => Response(new
    { tool_calls = new[] { new { id = "call-" + Guid.NewGuid().ToString("N"), type = "function", function = new { name, arguments = JsonSerializer.Serialize(arguments) } } }, content = "INTERNAL_DRAFT_NEVER_VISIBLE" });
    private static HttpResponseMessage Response(object message) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message } } }), Encoding.UTF8, "application/json") };
    private static NpcContextSnapshot Snapshot(string input = "那我们就出发吧") => new()
    {
        FixedConversationSegments = true, NpcName = "Abigail", NpcDisplayName = "Abigail", PlayerId = "p",
        SystemPrompt = "NPC identity", PlayerInput = input, MineGuardAvailable = true, FishingCompanionAvailable = true,
        SegmentMessages = new[] { new ConversationMemoryMessage { Role = "user", Content = "一起下矿？", Source = ConversationMemorySources.AiChat },
            new ConversationMemoryMessage { Role = "assistant", Content = "可以考虑。", Source = ConversationMemorySources.AiChat } },
        AllowedMoveDestinations = new[] { new LangGraphMoveDestination { DestinationKey = "location:beach", DisplayName = "Beach" } },
        AllowedTools = new[] { new LangGraphGiftCandidate { CandidateKey = "GIFT_SECRET", DisplayName = "Gift" } },
        RequestMetadata = new() { ["save_id"] = "save", ["game_date"] = "Y1 spring 1 1000" },
    };
    public static async Task RunAsync()
    {
        foreach (var language in new[] { "zh", "en" }) using (L.BeginRequest(language))
        {
            foreach (var decision in new[] { "chat", "decline", "clarify", "defer" })
            {
                var handler = new RecordingHttpHandler(() => Decision(decision, "VISIBLE_" + decision));
                using var http = new HttpClient(handler);
                var engine = new ConversationOrchestrator(new ConversationToolProviderClient(http), _ => throw new InvalidOperationException("Non-action executed."));
                var result = await engine.DecideAsync(Snapshot("一起下矿钓鱼？"), Profile, language + decision, 1024);
                Check(result.Decision?.Reply == "VISIBLE_" + decision && result.Confirmation is null && result.ToolExecution is null
                    && handler.Requests.Count == 1, "Explicit non-action decision lost one-call behavior or autonomy.");
                using var request = JsonDocument.Parse(handler.Requests[0].Body);
                var tools = request.RootElement.GetProperty("tools");
                Check(tools.GetArrayLength() == 1 && tools[0].GetProperty("function").GetProperty("name").GetString() == "decide_turn"
                    && request.RootElement.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString() == "decide_turn"
                    && !handler.Requests[0].Body.Contains("GIFT_SECRET"), "First decision exposed action tools/candidates or was not forced.");
            }
            foreach (var kind in new[] { "mine", "fishing", "move" })
                foreach (bool approved in new[] { true, false }) await VerifyAction(language, kind, approved);
            await VerifyAction(language, "mine", true, successful: false);
            await VerifyAction(language, "move", true, successful: false);
            foreach (var decision in new[] { "chat", "decline", "clarify", "defer", "consider_action" })
                await VerifyPrematureTravelBarks(language, decision);
            await VerifyPrematureTravelBarks(language, "chat", repaired: false);
            await VerifyPrematureTravelBarks(language, "consider_action", repaired: false);
            await VerifyPrematureTravelBarks(language, "defer", afterOptions: true);
            await VerifyPrematureTravelBarks(language, "defer", afterOptions: true, repaired: false);
            await VerifyFinalFailureAfterMove(language);
            await VerifySpeechMetadata(language);
            await VerifyRepair(language);
            await VerifyReconsider(language);
            await VerifyInvalid(language, () => Final("Let's go."), Snapshot());
            await VerifyInvalid(language, () => Decision("consider_action", "Let's go.", "mine"), Snapshot());
            await VerifyInvalid(language, () => Tool("invite_mine_guard", new { }), Snapshot());
            await VerifyInvalid(language, () => Decision("consider_action", "", "unknown"), Snapshot());
            await VerifyInvalid(language, () => Decision("decline", "No.", "mine"), Snapshot());
            foreach (var kind in new[] { "mine", "fishing" })
            {
                var snapshot = Snapshot();
                snapshot.MineGuardAvailable = snapshot.FishingCompanionAvailable = false;
                string tool = kind == "mine" ? "invite_mine_guard" : "invite_fishing_companion";
                await VerifyInvalid(language, () => Tool(tool, new { }), snapshot, kind);
            }
            await VerifyInvalid(language, () => Tool("invite_fishing_companion", new { }), Snapshot(), "mine");
            await VerifyInvalid(language, () => Tool("move_to", new { destination_key = "invented:key" }), Snapshot(), "move");
            await VerifyInvalid(language, () => Tool("give_gift", new { candidate_key = "invented:key" }), Snapshot(), "gift");
        }
        Console.WriteLine("Explicit turn decisions: bilingual one-call chat/refusal/clarification/deferral, on-demand action options, mine/fishing/move confirmation, cancellation, failed receipts, premature travel-bark repair/rejection, localized pre-execution errors, post-execution failures, invalid/unavailable tools and no visible drafts passed.");
    }
    private static async Task VerifySpeechMetadata(string language)
    {
        var handler = new RecordingHttpHandler(() => Tool("decide_turn", new
        {
            schema_version = 1, decision = "chat", reply = "VISIBLE_SPEECH", action_kind = "none", intent = "",
            travel_barks = Array.Empty<string>(), memory_update = Memory,
            speech = new { instructions = "INTERNAL_SPEECH_DIRECTION", end_state = "relaxed" }
        }));
        using var http = new HttpClient(handler);
        var engine = new ConversationOrchestrator(new ConversationToolProviderClient(http), _ => throw new InvalidOperationException("Speech triggered action."));
        var snapshot = Snapshot(); snapshot.RequestMetadata["speech_enabled"] = "true";
        var result = await engine.DecideAsync(snapshot, Profile, language + "speech-metadata", 1024);
        Check(result.Decision?.Reply == "VISIBLE_SPEECH" && handler.Requests.Count == 1,
            "Speech metadata broke strict turn parsing or leaked into dialogue.");
        using var request = JsonDocument.Parse(handler.Requests[0].Body);
        Check(request.RootElement.GetProperty("vivant_context").GetProperty("speech_performance").GetBoolean(),
            "Hosted speech capability was not advertised.");
        await VerifyInvalid(language, () => Tool("decide_turn", new
        {
            schema_version = 1, decision = "consider_action", reply = "invalid spoken promise", action_kind = "mine", intent = "mine",
            travel_barks = Array.Empty<string>(), memory_update = Memory,
            speech = new { instructions = "quiet", end_state = "calm" }
        }), Snapshot());
    }
    private static async Task VerifyAction(string language, string kind, bool approved, bool successful = true)
    {
        string tool = kind switch { "mine" => "invite_mine_guard", "fishing" => "invite_fishing_companion", _ => "move_to" };
        object arguments = kind == "move" ? new { destination_key = "location:beach" } : new { };
        var replies = new Queue<HttpResponseMessage>(new[] { Decision("consider_action", "", kind), Tool(tool, arguments),
            Final("AFTER_RECEIPT", new[] { "AFTER_RECEIPT_TRAVEL_BARK" }) });
        var handler = new RecordingHttpHandler(() => replies.Dequeue());
        using var http = new HttpClient(handler);
        int executions = 0;
        var engine = new ConversationOrchestrator(new ConversationToolProviderClient(http), request =>
        {
            executions++;
            return Task.FromResult(new GameBridgeToolResult { RequestId = request.RequestId, ToolCallId = request.ToolCallId,
                ContextVersion = request.ContextVersion, Tool = request.Tool, Ok = successful, Status = successful ? "completed" : "failed",
                DestinationKey = request.DestinationKey, ReceiptId = "AUTHORITATIVE_RECEIPT" });
        });
        string id = language + kind + approved + successful;
        var initial = await engine.DecideAsync(Snapshot("那就一起走吧"), Profile, id, 1024);
        Check(initial.Confirmation is not null && initial.Decision is null && executions == 0 && handler.Requests.Count == 2,
            "Action draft escaped or action bypassed confirmation.");
        using var selection = JsonDocument.Parse(handler.Requests[1].Body);
        var names = selection.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToArray();
        var receipt = selection.RootElement.GetProperty("messages").EnumerateArray().Last();
        Check(names.Length == 2 && names.Contains(tool) && names.Contains("decide_turn")
            && receipt.GetProperty("role").GetString() == "tool"
            && receipt.GetProperty("content").GetString()!.Contains("\"executed\":false"), "On-demand options or tool category restriction missing.");
        var result = await engine.ResumeMoveAsync(id, initial.Confirmation!.ResumeToken, approved);
        Check(executions == (approved ? 1 : 0) && result.Decision?.Reply == "AFTER_RECEIPT"
            && result.ToolExecution?.Ok == (approved && successful) && handler.Requests.Count == 3,
            "Execution/cancellation/receipt flow changed.");
        Check(result.Decision!.TravelBarks.SequenceEqual(kind == "move" && approved && successful
            ? new[] { "AFTER_RECEIPT_TRAVEL_BARK" } : Array.Empty<string>()),
            "Travel dialogue must survive only a successful move receipt, never refusal, failure or another action.");
        using var final = JsonDocument.Parse(handler.Requests[2].Body);
        Check(final.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString() == "submit_final_response"
            && (!approved || handler.Requests[2].Body.Contains("AUTHORITATIVE_RECEIPT")), "Final dialogue lost the execution receipt or final schema.");
        Check(final.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters")
            .GetProperty("properties").GetProperty("travel_barks").GetProperty("maxItems").GetInt32() == 3,
            "The pre-execution restriction leaked into the final response schema.");
        bool rejected = false;
        try { await engine.ResumeMoveAsync(id, initial.Confirmation.ResumeToken, approved); }
        catch (LangGraphValidationException) { rejected = true; }
        Check(rejected && executions == (approved ? 1 : 0), "Confirmation token executed twice.");
    }
    private static async Task VerifyPrematureTravelBarks(string language, string decision, bool afterOptions = false, bool repaired = true)
    {
        const string premature = "PREMATURE_TRAVEL_BARK_NEVER_VISIBLE";
        bool wantsAction = decision == "consider_action";
        string kind = wantsAction ? "move" : "none";
        var replies = new Queue<HttpResponseMessage>();
        if (afterOptions) replies.Enqueue(Decision("consider_action", "", "move"));
        replies.Enqueue(Decision(decision, wantsAction ? "" : "UNOBSERVED_REPLY", kind, new[] { premature }));
        replies.Enqueue(repaired ? Decision(decision, wantsAction ? "" : "REPAIRED_REPLY", kind)
            : Decision(decision, wantsAction ? "" : "UNOBSERVED_REPLY", kind, new[] { premature }));
        if (repaired && wantsAction) replies.Enqueue(Tool("move_to", new { destination_key = "location:beach" }));
        var handler = new RecordingHttpHandler(() => replies.Dequeue());
        using var http = new HttpClient(handler);
        var engine = new ConversationOrchestrator(new ConversationToolProviderClient(http),
            _ => throw new InvalidOperationException("Pre-execution travel dialogue triggered an action."));
        var snapshot = Snapshot("那就一起走吧");
        string original = JsonSerializer.Serialize(snapshot);
        LangGraphResponse? result = null;
        InProcessConversationEngine.ConversationProtocolException? failure = null;
        try { result = await engine.DecideAsync(snapshot, Profile, Guid.NewGuid().ToString("N"), 1024); }
        catch (InProcessConversationEngine.ConversationProtocolException ex) { failure = ex; }
        if (repaired)
        {
            Check(failure is null && result is not null && result.ToolExecution is null,
                "Corrected travel dialogue failed or executed before confirmation.");
            Check(wantsAction ? result!.Confirmation is not null && result.Decision is null
                : result!.Confirmation is null && result.Decision?.Reply == "REPAIRED_REPLY" && result.Decision.TravelBarks.Count == 0,
                "The corrected decision lost autonomy or exposed premature travel dialogue.");
        }
        else
        {
            string expected = language == "zh" ? "AI 返回的行动格式不符合要求，本轮未执行行动，请重试。"
                : "The AI returned an invalid action format. No action was executed this turn; please try again.";
            Check(result is null && failure is { BeforeExecution: true } && failure.PlayerMessage == expected
                && failure.InnerException?.Message == "Travel barks require an executed move receipt.",
                "Repeated invalid travel dialogue lost the safe localized error or original diagnostic.");
        }
        int correctionIndex = afterOptions ? 2 : 1;
        Check(handler.Requests.Count == correctionIndex + 1 + (repaired && wantsAction ? 1 : 0) && replies.Count == 0,
            "Travel-bark repair exceeded its single retry or skipped a decision stage.");
        Check(JsonSerializer.Serialize(snapshot) == original, "Failed/unobserved decisions changed source memory.");
        using var correction = JsonDocument.Parse(handler.Requests[correctionIndex].Body);
        string guidance = correction.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Check(guidance.Contains("travel_barks") && guidance.Contains("[]")
            && guidance.Contains(language == "zh" ? "执行前" : "before execution"),
            "Protocol repair did not identify premature travel dialogue or require an empty array.");
        foreach (var request in handler.Requests.Skip(correctionIndex))
            Check(!request.Body.Contains(premature) && !request.Body.Contains("UNOBSERVED_REPLY"),
                "Rejected dialogue leaked into the retry or subsequent action selection.");
        foreach (var request in handler.Requests)
        {
            using var payload = JsonDocument.Parse(request.Body);
            var tool = payload.RootElement.GetProperty("tools").EnumerateArray().Single(t =>
                t.GetProperty("function").GetProperty("name").GetString() == "decide_turn").GetProperty("function");
            var parameters = tool.GetProperty("parameters");
            var barks = parameters.GetProperty("properties").GetProperty("travel_barks");
            Check(barks.GetProperty("type").GetString() == "array" && barks.GetProperty("maxItems").GetInt32() == 0
                && barks.GetProperty("description").GetString()!.Contains("[]")
                && parameters.GetProperty("required").EnumerateArray().Any(v => v.GetString() == "travel_barks"),
                "A decision-stage schema still permits travel dialogue before execution.");
        }
    }
    private static async Task VerifyFinalFailureAfterMove(string language)
    {
        var replies = new Queue<HttpResponseMessage>(new[] { Decision("consider_action", "", "move"),
            Tool("move_to", new { destination_key = "location:beach" }), Final(""), Final("") });
        var handler = new RecordingHttpHandler(() => replies.Dequeue());
        using var http = new HttpClient(handler);
        int executions = 0;
        var engine = new ConversationOrchestrator(new ConversationToolProviderClient(http), request =>
        {
            executions++;
            return Task.FromResult(new GameBridgeToolResult { RequestId = request.RequestId, ToolCallId = request.ToolCallId,
                ContextVersion = request.ContextVersion, Tool = request.Tool, Ok = true, Status = "completed",
                DestinationKey = request.DestinationKey, ReceiptId = "AUTHORITATIVE_RECEIPT" });
        });
        string id = language + "final-failure-after-move";
        var initial = await engine.DecideAsync(Snapshot("那就一起走吧"), Profile, id, 1024);
        InProcessConversationEngine.ConversationProtocolException? failure = null;
        try { await engine.ResumeMoveAsync(id, initial.Confirmation!.ResumeToken, approved: true); }
        catch (InProcessConversationEngine.ConversationProtocolException ex) { failure = ex; }
        Check(failure is { BeforeExecution: false, PlayerMessage: null } && executions == 1 && handler.Requests.Count == 4,
            "A failed final reply incorrectly claimed that the committed move never executed.");
    }
    private static async Task VerifyRepair(string language)
    {
        int requests = 0;
        var handler = new RecordingHttpHandler(() => requests++ == 0 ? Response(new { content = "Let's go mining!" }) : Decision("decline", "Actually, no."));
        using var http = new HttpClient(handler);
        var engine = new ConversationOrchestrator(new ConversationToolProviderClient(http), _ => throw new InvalidOperationException());
        var result = await engine.DecideAsync(Snapshot(), Profile, language + "repair", 1024);
        Check(result.Decision?.Reply == "Actually, no." && requests == 2 && !handler.Requests[1].Body.Contains("Let's go mining!"),
            "Plain-text acceptance bypassed explicit decision or leaked into repair history.");
    }
    private static async Task VerifyReconsider(string language)
    {
        var replies = new Queue<HttpResponseMessage>(new[] { Decision("consider_action", "", "fishing"),
            Response(new { content = "Sure, let's fish." }), Decision("defer", "Let's try tomorrow.") });
        var handler = new RecordingHttpHandler(() => replies.Dequeue());
        using var http = new HttpClient(handler);
        var engine = new ConversationOrchestrator(new ConversationToolProviderClient(http), _ => throw new InvalidOperationException());
        var result = await engine.DecideAsync(Snapshot(), Profile, language + "reconsider", 1024);
        Check(result.Decision?.Reply == "Let's try tomorrow." && result.Confirmation is null && handler.Requests.Count == 3
            && !handler.Requests[2].Body.Contains("Sure, let's fish."), "Options forced execution or stage-two draft escaped.");
    }
    private static async Task VerifyInvalid(string language, Func<HttpResponseMessage> invalid, NpcContextSnapshot snapshot, string? kind = null)
    {
        int requests = 0;
        var handler = new RecordingHttpHandler(() => requests++ == 0 && kind is not null ? Decision("consider_action", "", kind) : invalid());
        using var http = new HttpClient(handler);
        var engine = new ConversationOrchestrator(new ConversationToolProviderClient(http), _ => throw new InvalidOperationException("Invalid tool executed."));
        bool rejected = false;
        try { await engine.DecideAsync(snapshot, Profile, language + Guid.NewGuid(), 1024); }
        catch (Exception ex) when (ex.GetType().Name == "ConversationProtocolException") { rejected = true; }
        Check(rejected && requests == (kind is null ? 2 : 3), "Malformed decision/tool bypassed validation or retried without a bound.");
    }
}
