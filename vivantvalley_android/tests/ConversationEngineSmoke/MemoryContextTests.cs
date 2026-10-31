using System.Text.Json;
using VivantValley;
using VivantValley.Services;

internal static class MemoryContextTests
{
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
    public static void Run()
    {
        var candidates=new[]
        {
            new ContextMemoryCandidate("novel","玩家喜欢听艾利欧特读小说。","dialogue"),
            new ContextMemoryCandidate("cancel","玩家已取消海边约会。","commitment",Status:"cancelled"),
            new ContextMemoryCandidate("dream","梦中说过想听小说，没有约定现实时间。","dream"),
        };
        Check(ContextMemoryCompiler.Compile("今天好累",candidates).Text=="","Unrelated memory must stay absent.");
        Check(ContextMemoryCompiler.Compile("还记得之前那件事吗",candidates).Text=="","Vague recollection must not pick arbitrary old facts.");
        var seaside=new[]{new ContextMemoryCandidate("beach","约好有空再看海。","commitment")};
        Check(ContextMemoryCompiler.Compile("一起去海边",seaside).SelectedIds.Contains("beach")
            && ContextMemoryCompiler.Compile("the seaside",seaside).SelectedIds.Contains("beach"),"Equivalent game locations did not match.");
        Check(ContextMemoryCompiler.Compile("天气不错",seaside).Text=="","Game-location alias matched unrelated weather.");
        Check(ContextMemoryCompiler.Compile("海边",new[]{new ContextMemoryCandidate("two-events","海边的两件事。",EventIds:new[]{"one","two"})},
            coveredEventIds:new[]{"one"}).SelectedIds.Count==1,"Partial evidence coverage discarded a whole multi-event memory.");
        Check(ContextMemoryCompiler.Compile("别提小说",new[]{candidates[0] with { AllowUnprompted=true }}).Text=="",
            "Unprompted memory ignored topic refusal.");
        var recall=ContextMemoryCompiler.Compile("小说",candidates,tokenBudget:300);
        Check(recall.SelectedIds.Contains("novel") && recall.SelectedIds.Contains("dream") && recall.Text.Contains("dream_only"),"Relevant dream provenance lost.");
        var roundTrip=ContextMemoryCompiler.Compile("小说",ContextMemoryCompiler.FromSummary(recall.Text),tokenBudget:300);
        Check(roundTrip.Text==recall.Text && roundTrip.SelectedIds.SequenceEqual(recall.SelectedIds),"Compiler output wrapped/renamed on second pass.");
        var deduplicated=ContextMemoryCompiler.Compile("小说",candidates.Concat(candidates),
            recentTexts:new[]{"NPC：玩家喜欢听艾利欧特读小说。"},coveredEventIds:new[]{"dream"},tokenBudget:300);
        Check(deduplicated.Text=="" && deduplicated.DroppedDuplicates>0,"Recent/id duplicates returned.");
        var budget=ContextMemoryCompiler.Compile("海边",new[]
        {
            new ContextMemoryCandidate("too-long",new string('海',600)+"海边约会没有答应。"),candidates[1]
        },tokenBudget:90);
        Check(budget.SelectedIds.SequenceEqual(new[]{"cancel"}) && budget.Text.EndsWith("玩家已取消海边约会。"),"Budget cut negation or lost whole short item.");
        Check(ContextMemoryCompiler.Compile("别再提小说了，聊钓鱼吧",candidates).Text=="","Rejected topic was recalled.");
        Check(ContextMemoryCompiler.Compile("novel",new[]{new ContextMemoryCandidate("quote","The player did not promise to read the novel.")},tokenBudget:100)
            .Text.Contains("did not promise"),"English negation lost.");
        Check(ContextMemoryCompiler.EstimateTokens("你好😀")>=6,"Unicode token estimate unsafe.");
        Check(!ConversationMemoryPolicy.IsExplicitOpenLoop("约好明天去海边"+new string('海',100)+"但是后来取消了"),
            "Long commitment lost trailing cancellation during normalization.");
        string overlong=new string('海',330)+"；玩家没有答应。";
        Check(!ConversationMemoryPolicy.UpdateLongTermMemory("[旧] 玩家喜欢小说。",overlong,"Y1 spring 3","p","n",1).Contains(new string('海',20)),
            "Oversized derived patch was truncated into an unsupported assertion.");
        Check(ConversationMemoryPolicy.UpdateLongTermMemory(new string('海',2100)+"；玩家没有答应。","","Y1 spring 3","p","n",1)=="",
            "Oversized single legacy fact was clipped instead of omitted after archival.");

        string directory=Path.Combine(Path.GetTempPath(),"vv-memory-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var savedArchive=ConversationTimelinePolicy.ArchiveBeforePrune;
        var savedSummary=ConversationMemoryPolicy.ArchiveBeforeSummaryCompaction;
        try
        {
            var errors=new List<string>();
            var archive=new MemoryEventArchive(directory,"save-A",errors.Add);
            var memory=new NpcConversationMemory {PlayerId="player-A",NpcName="Elliott",Summary="旧摘要：喜欢小说。"};
            var baseTime=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero);
            for(int i=0;i<70;i++) memory.Messages.Add(new ConversationMemoryMessage
            {
                Role=i%2==0?"user":"assistant",Content=$"小说第{i}页的往事。",Source=ConversationMemorySources.AiChat,
                GameDate="Y1 spring 1",TimelineSequence=i+1,CreatedAtUtc=baseTime.AddSeconds(i)
            });
            Check(archive.TryAppend(memory),"First archival failed.");
            Check(memory.ArchivedRecordCount==71 && memory.ArchiveBranchId.Length==32,"Archive receipts missing.");
            Check(Directory.GetFiles(directory,"segment-*.json",SearchOption.AllDirectories).Length==2,"Archive not chunked at 64 records.");
            int count=memory.ArchivedRecordCount;
            Check(archive.TryAppend(memory) && memory.ArchivedRecordCount==count,"Archive retry duplicated records.");
            string serialized=JsonSerializer.Serialize(memory);
            var savedCheckpoint=JsonSerializer.Deserialize<NpcConversationMemory>(serialized)!;
            memory.Messages.Add(new(){Role="user",Content="未来分支的月光戒指。",Source=ConversationMemorySources.AiChat,
                GameDate="Y1 spring 2",TimelineSequence=71,CreatedAtUtc=baseTime.AddDays(1)});
            Check(archive.TryAppend(memory),"Append to partial segment failed.");
            Check(Directory.GetFiles(directory,"*.bak",SearchOption.AllDirectories).Length>0,"Atomic replacement did not preserve previous segment.");
            Check(archive.RecallCandidates("player-A","Elliott","月光戒指").Count==1,"Archived quotation not retrievable.");
            Check(archive.RecallCandidates("player-B","Elliott","小说").Count==0 && archive.RecallCandidates("player-A","Haley","小说").Count==0,"Archive leaked owners.");
            var otherSave=new MemoryEventArchive(directory,"save-B");
            var isolated=new NpcConversationMemory{PlayerId="player-A",NpcName="Elliott"};
            Check(otherSave.TryAppend(isolated) && otherSave.RecallCandidates("player-A","Elliott","小说").Count==0,"Archive leaked saves.");
            var reload=new MemoryEventArchive(directory,"save-A");
            string oldBranch=savedCheckpoint.ArchiveBranchId;
            Check(reload.TryAppend(savedCheckpoint) && savedCheckpoint.ArchiveBranchId!=oldBranch,"Rollback did not fork archive at saved receipt.");
            Check(reload.RecallCandidates("player-A","Elliott","月光戒指").Count==0
                && reload.RecallCandidates("player-A","Elliott","小说").Count>0,"Rollback leaked future or lost earlier history.");
            Check(Directory.GetFiles(directory,"segment-*.json",SearchOption.AllDirectories).Length==4,"Rollback modified/deleted original branch.");
            var roundTripped=JsonSerializer.Deserialize<NpcConversationMemory>(JsonSerializer.Serialize(savedCheckpoint))!;
            var ordinaryReload=new MemoryEventArchive(directory,"save-A");
            Check(ordinaryReload.TryAppend(roundTripped) && roundTripped.ArchiveBranchId==savedCheckpoint.ArchiveBranchId,"Normal reload unnecessarily changed scope.");
            Check(ordinaryReload.TryForget("player-A","Elliott") && ordinaryReload.RecallCandidates("player-A","Elliott","小说").Count==0,"Forget left archive recall accessible.");

            var live=new NpcConversationMemory{PlayerId="parallel",NpcName="Sam",Messages=new(){new(){Role="user",Content="原本的小说。",CreatedAtUtc=baseTime}}};
            var writerA=new MemoryEventArchive(directory,"parallel-save");
            Check(writerA.TryAppend(live),"Concurrent archive setup failed.");
            var stale=live.Clone(); var writerB=new MemoryEventArchive(directory,"parallel-save");
            Check(writerB.TryAppend(stale),"Concurrent archive read failed.");
            live.Messages.Add(new(){Role="user",Content="第一个分支的矿井。",CreatedAtUtc=baseTime.AddMinutes(1)});
            Check(writerA.TryAppend(live),"First concurrent writer failed.");
            stale.Messages.Add(new(){Role="user",Content="第二个分支的海边。",CreatedAtUtc=baseTime.AddMinutes(2)});
            Check(!writerB.TryAppend(stale),"Stale archive page overwrote another writer's committed records.");
            Check(writerB.TryAppend(stale) && stale.ArchiveBranchId!=live.ArchiveBranchId
                && writerB.RecallCandidates("parallel","Sam","矿井").Count==0
                && writerA.RecallCandidates("parallel","Sam","矿井").Count==1,"Archive conflict did not preserve isolated branches.");

            // A real filesystem failure, not a fake successful callback, must block pruning.
            string blockedRoot=Path.Combine(directory,"blocked"); File.WriteAllText(blockedRoot,"not a directory");
            var failed=new MemoryEventArchive(blockedRoot,"failed-save",errors.Add);
            ConversationTimelinePolicy.ArchiveBeforePrune=failed.TryAppend;
            var unarchived=new NpcConversationMemory{PlayerId="p",NpcName="n",Messages=memory.Messages.Select(m=>m.Clone()).ToList()};
            int before=unarchived.Messages.Count;
            Check(ConversationTimelinePolicy.RetainRecentMessages(unarchived).Count==before && errors.Count>0,"Disk failure silently pruned original messages.");
            ConversationMemoryPolicy.ArchiveBeforeSummaryCompaction=(_,_,_)=>false;
            string legacy=string.Join('\n',Enumerable.Range(0,60).Select(i=>$"[旧{i}] 这是不能丢失的旧资料{i}。"));
            string retained=ConversationMemoryPolicy.UpdateLongTermMemory(legacy,"新的小说喜好。","Y1 spring 3","p","n",1);
            Check(retained.StartsWith(legacy,StringComparison.Ordinal) && retained.EndsWith("新的小说喜好。"),"Disk failure discarded legacy summary.");
            unarchived.Summary=legacy;
            var legacyClient=new RecordingClient("新的回复。");
            var legacyResult=new ConversationEngine(legacyClient).GenerateReplyAsync("test-key","identity",unarchived,"小说", "Y1 spring 3",
                new ConversationEngineOptions{MaxContextMessages=4,SummaryTriggerMessageCount=4,RecentMessagesToKeep=2}).GetAwaiter().GetResult();
            Check(legacyClient.Requests.Count==1 && legacyResult.UpdatedMemory.Messages.Count==before+2
                && legacyResult.UpdatedMemory.Summary==legacy && !legacyResult.Compaction.SummaryAttempted
                && legacyResult.Compaction.ContinuedAfterSummaryFailure,"Legacy fallback discarded data or requested compaction after archive failure.");
        }
        finally
        {
            ConversationTimelinePolicy.ArchiveBeforePrune=savedArchive;
            ConversationMemoryPolicy.ArchiveBeforeSummaryCompaction=savedSummary;
            Directory.Delete(directory,recursive:true);
        }
        Console.WriteLine("Memory context/archive tests passed.");
    }
}
