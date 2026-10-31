using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class FreeStoryTests
{
    private const string Opening = """{"formatVersion":3,"title":"The rehearsal","premise":"Sam wants help planning a rehearsal; Abigail questions the timing.","inviter":"Sam","participants":["Sam","Abigail"],"locationId":"Saloon","timeSlot":"evening","mailBody":"Come talk about the rehearsal.","motivations":[{"actor":"Sam","goal":"Ask for help","knownFacts":"No established promise from the player"},{"actor":"Abigail","goal":"Question the plan","knownFacts":"The plan has not been agreed"}],"opening":[{"actor":"Sam","text":"What do you think about rehearsing?","action":"talk","target":"player"}]}""";
    private static void Check(bool value,string message) { if (!value) throw new InvalidOperationException(message); }
    private static WeeklyStoryPlanRequest Request(int scheduled = 15) => new()
    {
        FreeScene = true, WeekKey = 2, CurrentDay = 15, ScheduledDay = scheduled, EarliestDay = scheduled, LatestDay = scheduled,
        Seed = "0123456789abcdef", Npcs = new[]
        {
            new WeeklyStoryNpcCandidate("Sam","Sam",5,"friend",true,false,"Sam identity",Array.Empty<string>()),
            new WeeklyStoryNpcCandidate("Abigail","Abigail",5,"friend",true,false,"Abigail identity",Array.Empty<string>()),
        }, Locations = new[] { new WeeklyStoryLocationCandidate("Saloon","Saloon",Array.Empty<string>(),3,false) },
    };
    public static async Task RunAsync()
    {
        foreach (string locale in new[] { "zh","en" })
        using (L.BeginRequest(locale))
        {
            var plannerClient = new RecordingClient(Opening);
            var planner = new WeeklyStoryPlannerService(plannerClient,new WeeklyStoryValidator());
            var planned = await planner.PlanAsync("test",Request());
            Check(planned.Success,"Free scene rejected: " + planned.FailureReason);
            Check(plannerClient.Requests.Single().Messages.Last().Content.Contains("Default flexible:")
                && !plannerClient.Requests.Single().Messages.Last().Content.Contains("Repeat the chosen time slot"),
                "Generation payload contradicts flexible timing prompt.");
            var plan = planned.Plan!;
            Check(FreeStoryService.TryCreate(Opening,Request(17),out var future,out _) && future!.ScheduledDay == 15
                && WeeklyStoryInvitationPolicy.CanStart(future,15,700,"Saloon"), "Planner-selected future date still delays ordinary scenes.");
            Check(plan.TimingMode == "flexible" && plan.StartTime == 600 && plan.EndTime == 2600,
                "Missing timing fields retained arbitrary evening slot.");
            string environmental = Opening.Replace("\"timeSlot\":\"evening\"", "\"timeSlot\":\"evening\",\"timingMode\":\"environment\",\"requiredEnvironment\":\"night\",\"timingReason\":\"Watch stars\"");
            Check(FreeStoryService.TryCreate(environmental,Request(),out var night,out _) && night!.TimingMode == "environment"
                && !WeeklyStoryInvitationPolicy.CanStart(night,15,900,"Saloon")
                && WeeklyStoryInvitationPolicy.CanStart(night,15,2100,"Saloon"), "Explicit night dependency ignored.");
            string noReason = environmental.Replace("Watch stars", "");
            Check(FreeStoryService.TryCreate(noReason,Request(),out var plain,out _) && plain!.TimingMode == "flexible",
                "Unsupported timing dependency imposed a wait.");
            string fakeAppointment = environmental.Replace("environment", "appointment");
            Check(FreeStoryService.TryCreate(fakeAppointment,Request(),out var fake,out _) && fake!.TimingMode == "flexible",
                "Model fabricated a player-confirmed appointment.");
            Check(plan.FormatVersion==3 && plan.Beats.Count==0 && plan.Selections.Count==0 && plan.FreeScene!.Events.Count==0,
                "Opening became a completed scene or predetermined choice graph.");
            Check(WeeklyStoryPlaybackPolicy.TryValidatePlan(plan,out _),"Saved V3 scene rejected.");
            Check(plannerClient.Requests.Single().NpcName is null,"Group planning accidentally routed to one NPC.");
            Check(plannerClient.Requests.Single().Messages[0].Content.Contains("formatVersion"),"Free opening prompt was not loaded.");
            foreach (string bad in new[]
            {
                Opening.Replace("\"actor\":\"Sam\",\"text\"","\"actor\":\"player\",\"text\""),
                Opening.Replace("\"locationId\":\"Saloon\"","\"locationId\":\"UnknownMap\""),
                Opening.Replace("\"action\":\"talk\"","\"action\":\"give_gift\""),
                Opening.Replace("\"formatVersion\":3","\"formatVersion\":2"),
                Opening.Replace("\"target\":\"player\"","\"target\":\"Lewis\""),
            }) Check(!planner.TryParse(bad,Request(),out _,out _),"Unsafe/non-V3 opening accepted.");

            var scene=plan.FreeScene!;scene.Started=true;
            var first=FreeStoryService.Record(plan,"Sam",scene.Opening[0].Text);
            Check(first.Witnesses.Count==2,"Opening witness roster lost.");
            FreeStoryService.Record(plan,"player","Tell me more about rehearsal.",target:"Sam");
            scene.Target="Sam";
            scene.AwaitingReply=true;scene.Turns=1;
            var response="""{"lines":[{"actor":"Sam","text":"We could start small.","action":"talk","target":"player"}],"suggestEnd":false}""";
            var client=new RecordingClient(response);
            var result=await new FreeStoryService(client).ReplyAsync("test","group-model",plan,new[]
            {
                new FreeStoryActorContext("Sam","SAM_IDENTITY TONE_MARKER","rehearsal SAM_PRIVATE","five hearts"),
                new FreeStoryActorContext("Abigail","ABIGAIL_IDENTITY","rehearsal ABIGAIL_PRIVATE","five hearts"),
            },"live location weather",CancellationToken.None);
            Check(result.Lines.Count==1 && scene.Events.Count==2 && scene.Departed.Count==0,
                "Unplayed AI response changed witnessed history or presence.");
            Check(client.Requests.Single().NpcName is null && client.Requests.Single().Model=="group-model",
                "Interactive group reply lost default-pool routing/model.");
            string payload=client.Requests.Single().Messages.Last().Content;
            Check(payload.Contains("SAM_PRIVATE") && !payload.Contains("ABIGAIL_PRIVATE") && payload.Contains("privateMemory") && payload.Contains("playerInput"),"Actor-owned context missing.");
            scene.PendingLines=result.Lines;
            var saved=JsonSerializer.Deserialize<WeeklyStoryPlan>(JsonSerializer.Serialize(plan))!;saved.Normalize();
            Check(saved.FreeScene!.Events.Count==2 && saved.FreeScene.PendingLines.Count==1,
                "Saving an unread response fabricated speech or lost the pending lines.");
            FreeStoryService.Record(plan,"Abigail","I need to go.");
            FreeStoryService.Record(plan,"Abigail","Left the scene.","leave");
            var later=FreeStoryService.Record(plan,"player","One last question for Sam.");
            Check(later.Witnesses.SequenceEqual(new[]{"Sam"}),"Departed character learned a later line.");
            Check(!FreeStoryService.ValidateLines(new[]{new FreeStoryLine { Actor="Abigail",Text="Still here." }},FreeStoryService.Present(plan),true,out _),
                "Departed character could reply.");
            Check(!FreeStoryService.ValidateLines(new[]{new FreeStoryLine { Actor="Sam",Text="Done",Action="move_to" }},new[]{"Sam"},true,out _),
                "Unsupported game action was accepted.");
            Check(!FreeStoryService.ValidateLines(new[]{new FreeStoryLine{Actor="Sam",Text="Bye",Action="leave"},new FreeStoryLine{Actor="Sam",Text="Again"}},new[]{"Sam"},true,out _),
                "Same response spoke after leaving.");
            scene.PendingLines.Clear();
            var badClient=new RecordingClient("{\"lines\":[{\"actor\":\"player\",\"text\":\"I agree\"}],\"suggestEnd\":false}");
            bool rejected=false;
            try { await new FreeStoryService(badClient).ReplyAsync("test","model",plan,Array.Empty<FreeStoryActorContext>(),"",CancellationToken.None); }
            catch(InvalidOperationException){rejected=true;}
            Check(rejected && scene.AwaitingReply,"Invalid response consumed the pending player input.");
            var cancelledClient = new RecordingClient(response);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool wasCancelled = false;
            try { await new FreeStoryService(cancelledClient).ReplyAsync("test","model",plan,Array.Empty<FreeStoryActorContext>(),"",cancelled.Token); }
            catch(OperationCanceledException) { wasCancelled = true; }
            Check(wasCancelled && cancelledClient.Requests.Count == 0 && scene.AwaitingReply,
                "Cancelled work submitted another request or consumed the player's input.");
            foreach(string key in new[]{"free-scene.end","free-scene.retry","free-scene.target"}) Check(L.T(key)!=key,"Missing scene UI locale.");
        }
        Console.WriteLine("Free scenes: V3 opening validation, group routing, pending output, save roundtrip, witnesses/departure, invalid reply and bilingual UI passed.");
    }
}
