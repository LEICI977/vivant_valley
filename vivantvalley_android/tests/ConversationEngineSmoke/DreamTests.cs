using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class DreamTests
{
    private static void Check(bool ok,string message) { if (!ok) throw new InvalidOperationException(message); }
    public static async Task RunAsync()
    {
        Check(DreamPolicy.Probability(0)==0 && DreamPolicy.Probability(5)==.1
            && Math.Abs(DreamPolicy.Probability(14)-14d/50)<.000001,"Heart probability changed.");
        int hits=0;
        for(int i=0;i<12000;i++)
        {
            var state=new DreamPlayerState { Day=8, TalkedToday=new(){"Abigail"} };
            if(DreamPolicy.RollVisitor(state,"save:"+i,new[]{new DreamCandidate("Abigail",10)})=="Abigail") hits++;
        }
        Check(hits is > 2200 and < 2600,"10-heart visitor distribution is not about 20%.");
        var first=new DreamPlayerState {Day=8,TalkedToday=new(){"Sam","Abigail"}};
        var second=JsonSerializer.Deserialize<DreamPlayerState>(JsonSerializer.Serialize(first))!;
        var candidates=new[]{new DreamCandidate("Sam",50),new DreamCandidate("Abigail",50),new DreamCandidate("Lewis",50)};
        string winner=DreamPolicy.RollVisitor(first,"seed",candidates);
        Check(winner is "Sam" or "Abigail","Untalked NPC visited.");
        Check(winner==DreamPolicy.RollVisitor(second,"seed",candidates.Reverse()),"Candidate order rerolls a dream.");
        first=JsonSerializer.Deserialize<DreamPlayerState>(JsonSerializer.Serialize(first))!;
        Check(winner==DreamPolicy.RollVisitor(first,"other",Array.Empty<DreamCandidate>()),"Saved nightly roll reran.");
        first.NightConsumed=true;first.OnDay(8);Check(first.NightConsumed,"Same-day refresh unlocks second dream.");
        first.OnDay(9);Check(!first.NightConsumed&&!first.VisitorRolled&&first.TalkedToday.Count==0,"Next-day state didn't reset.");
        var night = new DreamPlayerState { Day=8,TalkedToday=new(){"Abigail"} };
        Check(DreamPolicy.TakeAutomaticVisitor(night,"night",candidates)=="Abigail" && night.VisitorHandled && !night.NightConsumed,
            "Automatic visit must precede the choices without consuming the manual dream.");
        night=JsonSerializer.Deserialize<DreamPlayerState>(JsonSerializer.Serialize(night))!;
        Check(DreamPolicy.TakeAutomaticVisitor(night,"other",candidates)=="","Visitor replayed after reload/return to choice.");
        Check(DreamPolicy.ReturnToChoice(new(){Direction="npc_visits"})
            && !DreamPolicy.ReturnToChoice(new(){Direction="player_visits"}) && !DreamPolicy.ReturnToChoice(null),
            "Visitor departure must return to choices, manual departure/skip must sleep.");
        var quiet = new DreamPlayerState {Day=8};
        Check(DreamPolicy.TakeAutomaticVisitor(quiet,"night",candidates)=="" && quiet.VisitorHandled && !quiet.NightConsumed,
            "No-visitor night must leave the manual choice available.");
        var legacy = new DreamPlayerState {Day=8,NightConsumed=true,TalkedToday=new(){"Abigail"}};
        Check(DreamPolicy.TakeAutomaticVisitor(legacy,"night",candidates)=="","A consumed old-save night was reopened.");
        night.OnDay(9);Check(!night.VisitorHandled,"Automatic visit did not reset next day.");
        var assets = Enumerable.Range(0,80).Select(i=>new DreamCollageAsset{Id="asset"+i,Category="category"+(i%20),Path="images/"+i}).ToArray();
        var collage = DreamCollage.Select(assets,"night:8:Abigail:player_visits");
        Check(collage.Length==16 && collage.Select(a=>a.Category).Distinct().Count()==16,"Collage repeats one asset category.");
        string[] ids = collage.Select(a=>a.Id).ToArray();
        Check(ids.SequenceEqual(DreamCollage.Select(assets.Reverse(),"night:8:Abigail:player_visits").Select(a=>a.Id)),"Collage changes during the same dream.");
        foreach(string seed in new[]{"night:9:Abigail:player_visits","night:8:Sam:player_visits","night:8:Abigail:npc_visits"})
            Check(!ids.SequenceEqual(DreamCollage.Select(assets,seed).Select(a=>a.Id)),"Different night/NPC/direction reused the same collage.");
        for(int seed=0;seed<100;seed++)
        {
            var positions=DreamCollage.Positions("layout"+seed,16);
            Check(positions.Length==16 && positions.All(p=>p.X>=.05f && p.X<=.95f && p.Y>=.12f && p.Y<=.48f
                && !(p.X>.36f && p.X<.65f && p.Y>.25f)),"Collage centre covers the speakers or leaves the viewport.");
            Check(positions.SequenceEqual(DreamCollage.Positions("layout"+seed,16)),"Collage position jumps between redraws.");
        }
        Check(DreamCollage.Select(Array.Empty<DreamCollageAsset>(),"empty").Length==0,"Missing assets broke the fallback.");
        string[] paletteNpcs={"Abigail","Sebastian","Haley","Emily","Leah","Penny","Maru","Harvey","Elliott","Shane","Sam","Alex","CustomNpc"};
        foreach(string npc in paletteNpcs)
        {
            var colours=Enumerable.Range(0,80).Select(i=>DreamPalettes.Select(npc,"night:"+i)).ToArray();
            Check(colours.Select(c=>c.Id).Distinct().Count()>=3,"NPC has only one nightly palette.");
            Check(DreamPalettes.Select(npc,"same")==DreamPalettes.Select(npc,"same"),"Palette changes on redraw.");
            Check(colours.All(c=>new[]{c.Top,c.Middle,c.Bottom,c.GlowA,c.GlowB,c.Tint,c.Stars}.All(rgb=>rgb>=0 && rgb<=0xFFFFFF)),"Invalid RGB palette.");
        }
        Check(paletteNpcs.Select(n=>DreamPalettes.Select(n,"same").Id).Distinct().Count()>=6,"NPC colours are not varied.");
        var spouse=DreamRelationship.FromGameState(14,true,false,false,true,false,false,true);
        var dating=DreamRelationship.FromGameState(10,true,true,false,false,false,false);
        var engaged=DreamRelationship.FromGameState(10,true,false,true,false,false,false);
        var roommate=DreamRelationship.FromGameState(14,true,false,false,true,true,false);
        var divorced=DreamRelationship.FromGameState(0,true,true,false,true,false,true);
        var friend=DreamRelationship.FromGameState(10,true,false,false,false,false,false,true);
        var young=DreamRelationship.FromGameState(10,false,true,false,false,false,false);
        Check(spouse is {Status:"married",RomanticPartner:true,Adult:true} && dating.Status=="dating" && engaged.Status=="engaged","Partner status lost.");
        Check(!roommate.RomanticPartner && !divorced.RomanticPartner && !friend.RomanticPartner && friend.Datable && !young.Adult,"Roommate, divorce, friendship or age misclassified.");
        foreach(string locale in new[]{"zh","en"}) using(L.BeginRequest(locale))
        {
            var episode=new DreamEpisode {Id="dream-id",Day=8,Npc="Abigail",Scene="collage",Direction="player_visits"};
            var client=new RecordingClient("{\"scene\":\"mine\",\"text\":\"I was wondering if you would find this place.\"}");
            var result=await new DreamService(client).ReplyAsync("key","model",episode,"CANON TONE_MARKER","LIVE_HEARTS","PRIVATE_MEMORY","TODAY",CancellationToken.None,spouse);
            var request=client.Requests.Single();
            Check(request.NpcName=="Abigail"&&request.TaskKind=="conversation","Dream lost hosted NPC routing.");
            Check(request.Messages[0].Content.Contains("TONE_MARKER")&&request.Messages[3].Content.Contains("LIVE_HEARTS"),"Identity or live relationship missing.");
            Check(request.Messages[1].Content.Contains("text")&&!request.Stream,"Dream protocol missing.");
            Check(!request.Messages.Last().Content.Contains("allowedScenes") && !request.Messages.Last().Content.Contains("\"Scene\""),"Preset map still constrains the dream prompt.");
            using(var payload=JsonDocument.Parse(request.Messages[3].Content))
            {
                var relation=payload.RootElement.GetProperty("relationship");
                Check(relation.GetProperty("status").GetString()=="married" && relation.GetProperty("romanticPartner").GetBoolean()
                    && relation.GetProperty("datable").GetBoolean() && relation.GetProperty("hearts").GetInt32()==14 && relation.GetProperty("adult").GetBoolean(),"Live relationship was lost in the request.");
            }
            Check(episode.Lines.Count==0,"Generated unseen reply was recorded.");
            episode.Lines.Add(new(){Role="user",Text="I will visit tomorrow, after fishing."});
            episode.Lines.Add(new(){Role="assistant",Text=result.Text});
            var moved=new RecordingClient("{\"scene\":\"lake\",\"text\":\"We can stay here.\"}");
            var later=await new DreamService(moved).ReplyAsync("key","model",episode,"identity","facts","memory","today",CancellationToken.None);
            using(var payload=JsonDocument.Parse(moved.Requests.Single().Messages[3].Content))
                Check(!payload.RootElement.GetProperty("relationship").GetProperty("romanticPartner").GetBoolean(),"Unknown relationship defaulted to romance.");
            Check(later.Scene=="collage","Legacy model scene selected a fixed map.");
            var state=new DreamPlayerState{Day=9,Episodes=new(){episode}};
            string recall=DreamPolicy.Recall(state,"Abigail",9,"Hello");
            Check(recall.Contains("DREAM ONLY")&&recall.Contains("tomorrow"),"Next morning lost dream provenance/quotation.");
            Check(DreamPolicy.Recall(state,"Sam",9,"dream")=="","Uninvolved NPC learned private dream.");
            episode.MorningMentionUsed=true;
            Check(DreamPolicy.Recall(state,"Abigail",9,"Hello")=="","Dream forced into every greeting.");
            Check(DreamPolicy.Recall(state,"Abigail",15,"fishing").Contains("DREAM ONLY"),"Relevant older dream inaccessible.");
            var npcMemory=new NpcConversationMemory{Messages=new(){new(){Role="system",Source=ConversationMemorySources.Dream,Content=DreamPolicy.Memory(episode),DedupeKey=episode.Id}}};
            string timeline=string.Join("\n",ConversationTimelinePolicy.BuildTimeline(npcMemory));
            Check(timeline.Contains("dream (not waking-world facts)")&&timeline.Contains("DREAM ONLY"),"Timeline presents a dream as a real action.");
            foreach(string text in new[]{"give_gift", "Here $h", "#split", "{\"action\":\"move_to\"}",""})
                Check(!DreamPolicy.IsValidReply(new(){Scene="farm",Text=text}),"Dialogue control syntax accepted.");
            Check(DreamPolicy.IsValidReply(new(){Text="Hi"}),"Text-only dream reply rejected.");
            var textOnly = new RecordingClient("{\"text\":\"You always have another question, don't you?\"}");
            Check((await new DreamService(textOnly).ReplyAsync("key","model",episode,"","","","",CancellationToken.None)).Scene=="collage", "New text-only protocol failed.");
            var broken=new RecordingClient("{\"scene\":\"farm\",\"text\":\"move_to\"}");
            bool rejected=false;
            try {await new DreamService(broken).ReplyAsync("key","model",episode,"","","","",CancellationToken.None);}
            catch(InvalidOperationException){rejected=true;}
            Check(rejected&&episode.Lines.Count==2,"Invalid output changed dream history.");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            var noRequest=new RecordingClient("{}");bool stopped=false;
            try {await new DreamService(noRequest).ReplyAsync("key","model",episode,"","","","",cancelled.Token);}
            catch(OperationCanceledException){stopped=true;}
            Check(stopped&&noRequest.Requests.Count==0,"Cancelled dream sent a request.");
            Check(L.T("dream.scene.collage")!="dream.scene.collage","Missing collage locale.");
            foreach(string key in new[]{"dream.visit","dream.skip","dream.wake","dream.failed"})Check(L.T(key)!=key,"Missing dream UI translation.");
            var saved=JsonSerializer.Deserialize<DreamPlayerState>(JsonSerializer.Serialize(state))!;
            Check(saved.Episodes.Single().Lines.Count==2&&saved.Episodes.Single().MorningMentionUsed,"Dream archive failed save roundtrip.");
        }
        Console.WriteLine("Dream probabilities, daily eligibility, deterministic rolls, isolated archive/recall, locale, routing, visitor-first night flow, varied collage, NPC palettes, live relationship context and cancellation passed.");
    }
}
