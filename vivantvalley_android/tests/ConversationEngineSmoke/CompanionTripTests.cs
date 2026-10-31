using Microsoft.Xna.Framework;
using VivantValley;
using VivantValley.Menus;
using VivantValley.Services;

internal static class CompanionTripTests
{
    public static void Run()
    {
        var events = new List<string>();
        var own = new Session(1, events);
        var other = new Session(2, events);
        var sessions = new Dictionary<string, Session> { ["Abigail"] = own, ["Leah"] = other };
        Action arrive = () => events.Add("npc_at_entrance");
        Check(!CompanionTripPolicy.End(sessions, "Abigail", 1, false, arrive), "Farmhand ended authoritative NPC session.");
        Check(!CompanionTripPolicy.End(sessions, "Abigail", 2, true, arrive), "Another farmer can dismiss this NPC.");
        Check(!CompanionTripPolicy.End(sessions, "Unknown", 1, true, arrive) && events.Count == 0, "Invalid dismissal moved an NPC.");
        bool failed = false;
        try { CompanionTripPolicy.End(sessions, "Abigail", 1, true, () => throw new InvalidOperationException("blocked arrival")); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && CompanionTripPolicy.IsOwned(sessions,"Abigail",1) && events.Count==0,
            "Unavailable return point cancelled the session instead of allowing a retry.");
        Check(CompanionTripPolicy.End(sessions,"Abigail",1,true,arrive), "Owner cannot end an active trip.");
        Check(events.SequenceEqual(new[] {"npc_at_entrance", CompanionTripPolicy.PlayerEnded}),
            "NPC schedule resumed before moving out of the mine.");
        Check(!CompanionTripPolicy.End(sessions,"Abigail",1,true,arrive) && events.Count==2,
            "Repeated click moved or released the NPC twice.");
        Check(CompanionTripPolicy.IsOwned(sessions,"Leah",2), "Ending a trip cancelled another player's NPC.");
        other.IsComplete=true;
        Check(!CompanionTripPolicy.IsOwned(sessions,"Leah",2), "Completed session exposed End trip.");

        var memory = new ConversationSessionMemoryStore();
        var destination = new ConversationMoveDestination { Key="beach", DisplayName="海滩", StartLocationName="Town" };
        memory.StartMove("1","Abigail","day 1",destination);
        memory.RecordFishingCatch("1","Abigail","day 1","River","鱼");
        memory.RecordTripEnded("1","Abigail","day 1",true);
        var facts = memory.BuildPromptFacts("1","Abigail",10);
        Check(facts.Any(f=>f.Contains("已主动结束")) && facts.Any(f=>f.Contains("矿洞入口"))
            && !facts.Any(f=>f.Contains("正在和玩家一起前往")), "Ended trip retained a live traveling fact.");
        Check(facts.Any(f=>f.Contains("钓到了")), "Ending a trip discarded an actual catch.");
        Check(memory.BuildPromptFacts("2","Abigail",10).Count==0 && memory.BuildPromptFacts("1","Leah",10).Count==0,
            "Trip end leaked to another farmer or NPC.");
        memory.RecordTripEnded("1","Abigail","day 1",true);
        Check(memory.BuildPromptFacts("1","Abigail",10).Count(f=>f.Contains("已主动结束"))==1,
            "Repeated end facts accumulated.");
        Check(memory.BuildPromptFacts("1","Abigail",18).Count==0, "Trip end facts never expire.");
        memory.StartMove("1","Abigail","day 2",destination);
        Check(memory.BuildPromptFacts("1","Abigail",19).Any(f=>f.Contains("正在和玩家一起前往")),
            "An earlier end prevented the next trip from becoming active.");
        foreach (int w in new[] {180,240,320,480,900})
        {
            Rectangle area = new(50,20,w,40);
            var buttons = ChatHeaderLayout.Arrange(area,new[] {100,170,82,80});
            for(int i=0;i<buttons.Length;i++)
            {
                Check(area.Contains(buttons[i]),"Trip toolbar escaped header.");
                if(i>0) Check(!buttons[i].Intersects(buttons[i-1]) && buttons[i].Y==buttons[0].Y,
                    "Trip toolbar overlaps or adds a row.");
            }
        }
        Console.WriteLine("Companion ownership, one-shot end, arrival failure, session memory and header tests passed.");
    }
    private sealed class Session(long leader, List<string> events) : IPlayerCompanionSession
    {
        public long LeaderId => leader;
        public bool IsComplete { get; set; }
        public void Cancel(string reason) {events.Add(reason);IsComplete=true;}
    }
    private static void Check(bool value,string message)
    {if(!value)throw new InvalidOperationException(message);}
}
