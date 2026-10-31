using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VivantValley.Services;

/// <summary>
/// Save/player/NPC-isolated, append-only event archive. A segment contains at most 64 complete records;
/// only its unfinished tail is replaced atomically, keeping the previous bytes as a backup.
/// Prompt compaction is allowed only after TryAppend has flushed successfully.
/// </summary>
public sealed class MemoryEventArchive
{
    private const int SegmentSize = 64;
    private readonly string root;
    private readonly Action<string>? failure;
    private readonly Dictionary<string, ArchiveState> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArchiveRecord[]> readOnlyRecords = new(StringComparer.Ordinal);
    private readonly HashSet<string> unavailableCheckpoints = new(StringComparer.Ordinal);
    private readonly HashSet<string> reportedFailures = new(StringComparer.Ordinal);
    public int UnavailableCheckpointCount => unavailableCheckpoints.Count;
    private readonly object gate = new();
    public string SaveScope { get; }
    public MemoryEventArchive(string directory, string saveIdentity, Action<string>? onFailure = null)
    {
        if (string.IsNullOrWhiteSpace(saveIdentity)) throw new ArgumentException("A save identity is required.", nameof(saveIdentity));
        SaveScope = Scope(saveIdentity);
        root = Path.Combine(directory, SaveScope);
        failure = onFailure;
    }

    public bool TryAppend(NpcConversationMemory memory)
    {
        lock (gate)
        {
            try
            {
                var state = Load(memory);
                var additions = new List<ArchiveRecord>();
                foreach (var message in memory.Messages.Where(m => m is not null && !string.IsNullOrWhiteSpace(m.Content)))
                {
                    var copy = message.Clone();
                    string id = EventId(copy);
                    if (!state.Records.ContainsKey(id)) additions.Add(new ArchiveRecord { Id = id, Message = copy });
                }
                // Retain old summaries verbatim as unverified derived records, never promote them to game facts.
                if (!string.IsNullOrWhiteSpace(memory.Summary))
                {
                    string id = "summary-" + Scope(memory.Summary);
                    if (!state.Records.ContainsKey(id)) additions.Add(new ArchiveRecord { Id = id, Summary = memory.Summary });
                }
                foreach (var batch in additions.DistinctBy(a => a.Id).Chunk(SegmentSize))
                {
                    foreach (var record in batch)
                    {
                        if (state.Tail.Records.Count >= SegmentSize)
                            state.Tail = NextSegment(state, memory.PlayerId, memory.NpcName);
                        state.Tail.Records.Add(record);
                        // Flush complete pages now; flush the last partial page below.
                        if (state.Tail.Records.Count == SegmentSize) WriteSegment(state, state.Tail);
                    }
                    if (state.Tail.Records.Count < SegmentSize) WriteSegment(state, state.Tail);
                    // Publish receipt only after durable writes. Failed writes invalidate state and are retried from disk.
                    foreach (var record in batch) state.Records.TryAdd(record.Id, record);
                }
                memory.ArchiveBranchId = state.BranchId;
                memory.ArchivedRecordCount = state.Records.Count;
                memory.ArchiveRecoveryCheckpoints = state.Tail.RecoveryCheckpoints.ToList();
                unavailableCheckpoints.Remove(Key(memory.PlayerId, memory.NpcName));
                if (state.RecoveryNotice is { } notice)
                {
                    ReportOnce(notice);
                    state.RecoveryNotice = null;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
            {
                states.Remove(Key(memory.PlayerId,memory.NpcName));
                ReportOnce($"Memory archive failed for NPC {memory.NpcName}; originals retained. {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Load only the saved checkpoint for recall. Startup never appends or forks archive files.</summary>
    public void PrimeReadOnly(NpcConversationMemory memory)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(memory.ArchiveBranchId)) return;
            try
            {
                if (!Guid.TryParseExact(memory.ArchiveBranchId, "N", out _) || memory.ArchivedRecordCount < 0)
                    throw new InvalidDataException("Invalid archive checkpoint.");
                string directory = Path.Combine(BucketPath(memory.PlayerId, memory.NpcName), memory.ArchiveBranchId);
                var records = new Dictionary<string, ArchiveRecord>(StringComparer.Ordinal);
                int expectedPage = 0;
                if (Directory.Exists(directory))
                    foreach (string path in Directory.EnumerateFiles(directory, "segment-*.json").OrderBy(p => p, StringComparer.Ordinal))
                    {
                        var page = ReadSegment(path, memory.PlayerId, memory.NpcName, out _);
                        if (page.Number != expectedPage++) break; // Never import a later page across a missing prefix.
                        foreach (var record in page.Records)
                            records.TryAdd(record.Id, record);
                    }
                if (records.Count < memory.ArchivedRecordCount)
                    unavailableCheckpoints.Add(Key(memory.PlayerId, memory.NpcName));
                readOnlyRecords[Key(memory.PlayerId, memory.NpcName)] = records.Values.Take(memory.ArchivedRecordCount).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
            { ReportOnce($"Memory archive read unavailable for NPC {memory.NpcName}; originals retained. {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    /// <summary>Returns attributed quotations; archived speech is never treated as current world state.</summary>
    public IReadOnlyList<ContextMemoryCandidate> RecallCandidates(string playerId, string npcName, string query,
        IEnumerable<string>? excludedEventIds = null, int maximum = 8)
    {
        if (string.IsNullOrWhiteSpace(ConversationMemoryPolicy.GetCurrentTopicText(query))) return Array.Empty<ContextMemoryCandidate>();
        lock (gate)
        {
            try
            {
                var excluded = (excludedEventIds ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
                IEnumerable<ArchiveRecord> available = states.TryGetValue(Key(playerId,npcName),out var state)
                    ? state.Records.Values : readOnlyRecords.GetValueOrDefault(Key(playerId,npcName)) ?? Array.Empty<ArchiveRecord>();
                var records = available.Where(r => r.Message is not null
                    && !excluded.Contains(r.Id) && ContextMemoryCompiler.Relevance(query,r.Message.Content) > 0)
                    .OrderByDescending(r=>r.Message!.TimelineSequence).Select(r=>Candidate(r,npcName));
                var summaries = available.Where(r=>r.Summary is not null && !excluded.Contains(r.Id))
                    .SelectMany(r=>ContextMemoryCompiler.FromSummary(r.Summary,"historical_summary")
                        .Select(c=>c with { Priority=-30, EventIds=new[]{r.Id} }));
                return records.Concat(summaries).Where(c=>!excluded.Contains(c.Id) && ContextMemoryCompiler.Relevance(query,c.Text)>0)
                    .DistinctBy(c=>c.Id).OrderByDescending(c=>ContextMemoryCompiler.Relevance(query,c.Text))
                    .ThenByDescending(c=>c.Priority).Take(Math.Clamp(maximum,0,32)).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
            { failure?.Invoke("Memory archive recall unavailable: " + ex.GetType().Name); return Array.Empty<ContextMemoryCandidate>(); }
        }
    }

    /// <summary>Ordinary conversation recalls summaries first, or one complete exchange, never isolated quotes.</summary>
    public IReadOnlyList<ContextMemoryCandidate> RecallEpisodes(string playerId, string npcName, string query)
    {
        lock (gate)
        {
            var records = (states.TryGetValue(Key(playerId, npcName), out var state)
                ? state.Records.Values.ToArray() : readOnlyRecords.GetValueOrDefault(Key(playerId, npcName)) ?? Array.Empty<ArchiveRecord>());
            var summaries = records.Where(r => r.Summary is not null)
                .SelectMany(r => ContextMemoryCompiler.FromSummary(r.Summary, "historical_summary"))
                .Where(c => ContextMemoryCompiler.Relevance(query, c.Text) > 0)
                .OrderByDescending(c => ContextMemoryCompiler.Relevance(query, c.Text)).Take(8).ToArray();
            if (!ConversationRecallPolicy.NeedsOriginalEvidence(query) && summaries.Length > 0) return summaries;
            var messages = records.Where(r => r.Message is not null && r.Message.Source != SegmentMemoryPolicy.RecalledContextSource)
                .Select(r => r.Message!).ToArray();
            return ConversationTimelinePolicy.GroupMessages(messages).Where(g => g.IsTurn)
                .Select(g => new ContextMemoryCandidate("turn-" + EventId(g.Messages[0]),
                    $"[{g.Messages[0].GameDate}] " + string.Join(" / ", g.Messages.Select(m => $"{m.Role}: {m.Content}")),
                    "historical_quote", EventIds: g.Messages.Select(EventId).ToArray()))
                .Where(c => ContextMemoryCompiler.Relevance(query, c.Text) > 0)
                .OrderByDescending(c => ContextMemoryCompiler.Relevance(query, c.Text)).Take(8).ToArray();
        }
    }

    public bool TryForget(string playerId, string? npcName)
    {
        lock(gate)
        {
            try
            {
                string path = npcName is null ? Path.Combine(root,Scope(playerId)) : BucketPath(playerId,npcName);
                if (Directory.Exists(path)) Directory.Delete(path,recursive:true);
                foreach (string key in states.Keys.Where(k => k.StartsWith(Scope(playerId) + "/",StringComparison.Ordinal)
                    && (npcName is null || k == Key(playerId,npcName))).ToArray()) states.Remove(key);
                foreach (string key in readOnlyRecords.Keys.Where(k => k.StartsWith(Scope(playerId) + "/",StringComparison.Ordinal)
                    && (npcName is null || k == Key(playerId,npcName))).ToArray()) readOnlyRecords.Remove(key);
                return true;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            { failure?.Invoke("Memory archive forget failed: " + ex.GetType().Name); return false; }
        }
    }

    public static string EventId(ConversationMemoryMessage message)
    {
        // Include text + provenance + creation identity, so loading an earlier save and branching can
        // reuse a timeline sequence without overwriting the previously archived branch.
        return "event-" + Scope(JsonSerializer.Serialize(new { message.Role,message.Content,message.GameDate,
            message.CreatedAtUtc,message.Source,message.EventId,message.EpisodeId,message.DedupeKey,message.LocationName }));
    }
    public static string Scope(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private ContextMemoryCandidate Candidate(ArchiveRecord record, string npc)
    {
        var message = record.Message!;
        string source = message.Source switch
        {
            ConversationMemorySources.Dream => "dream",
            ConversationMemorySources.ModAction or ConversationMemorySources.ModGift or ConversationMemorySources.VanillaGift => "observed_event",
            ConversationMemorySources.VanillaChoice => "player_choice",
            SegmentMemoryPolicy.RecalledContextSource => "historical_reference",
            _ => "historical_quote",
        };
        string actor = message.Role == "user" ? "player" : message.Role == "assistant" ? npc : "record";
        string text = $"[{message.GameDate}] {actor}: {message.Content}";
        return new(record.Id,text,source,Priority:-20,Status:"historical",EventIds:new[]{record.Id});
    }

    private ArchiveState Load(NpcConversationMemory memory)
    {
        string playerId=memory.PlayerId,npcName=memory.NpcName;
        if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(npcName)) throw new InvalidDataException("Missing memory owner.");
        string key = Key(playerId,npcName);
        if (states.TryGetValue(key,out var existing)) return existing;
        bool legacy=string.IsNullOrEmpty(memory.ArchiveBranchId);
        string branch=legacy ? Guid.NewGuid().ToString("N") : memory.ArchiveBranchId;
        if(!Guid.TryParseExact(branch,"N",out _) || memory.ArchivedRecordCount<0) throw new InvalidDataException("Invalid archive checkpoint.");
        string directory = Path.Combine(BucketPath(playerId,npcName),branch);
        var state = new ArchiveState(directory,branch,NewSegment(0,playerId,npcName));
        state.Tail.RecoveryCheckpoints = (memory.ArchiveRecoveryCheckpoints ?? new()).ToList();
        int expectedPage = 0;
        if (Directory.Exists(directory))
            foreach (string file in Directory.EnumerateFiles(directory,"segment-*.json").OrderBy(x=>x,StringComparer.Ordinal))
            {
                ArchiveSegment segment = ReadSegment(file,playerId,npcName,out string fingerprint);
                if (segment.Number != expectedPage++) break;
                state.PersistedHashes[segment.Number]=fingerprint;
                foreach (var record in segment.Records) state.Records.TryAdd(record.Id,record);
                if(segment.Number >= state.Tail.Number) state.Tail=segment;
            }
        bool missing = !legacy && state.Records.Count < memory.ArchivedRecordCount;
        if(!legacy && state.Records.Count != memory.ArchivedRecordCount)
        {
            // A reloaded/rolled-back save must never remember the future. Fork at the exact saved
            // record receipt. Keep the old branch intact and copy only its committed prefix.
            string fork=Guid.NewGuid().ToString("N");
            var restored=new ArchiveState(Path.Combine(BucketPath(playerId,npcName),fork),fork,NewSegment(0,playerId,npcName));
            restored.Tail.RecoveryCheckpoints = (memory.ArchiveRecoveryCheckpoints ?? new())
                .Concat(state.Tail.RecoveryCheckpoints).Distinct(StringComparer.Ordinal).ToList();
            if (missing)
            {
                restored.Tail.RecoveryCheckpoints.Add($"{SaveScope}/{branch};expected={memory.ArchivedRecordCount};available={state.Records.Count}");
                restored.RecoveryNotice = $"Memory archive continued for NPC {npcName} in a new branch using retained records. "
                    + $"Old checkpoint expected {memory.ArchivedRecordCount}, found {state.Records.Count}. Missing history was NOT recovered; old checkpoint and files preserved.";
            }
            foreach(var record in state.Records.Values.Take(memory.ArchivedRecordCount))
            {
                if(restored.Tail.Records.Count==SegmentSize)
                { WriteSegment(restored,restored.Tail); restored.Tail=NextSegment(restored,playerId,npcName); }
                restored.Tail.Records.Add(record); restored.Records.Add(record.Id,record);
            }
            if(restored.Tail.Records.Count>0 || missing) WriteSegment(restored,restored.Tail);
            state=restored;
        }
        // Only TryAppend publishes the new receipt after all writes have succeeded.
        states.Add(key,state);
        return state;
    }
    private ArchiveSegment ReadSegment(string path,string player,string npc,out string fingerprint)
    {
        byte[] bytes=File.ReadAllBytes(path);
        fingerprint=Convert.ToHexString(SHA256.HashData(bytes));
        var segment = JsonSerializer.Deserialize<ArchiveSegment>(bytes) ?? throw new InvalidDataException("Empty segment.");
        if(segment.Version!=1 || segment.SaveScope!=SaveScope || segment.PlayerId!=player || segment.NpcName!=npc)
            throw new InvalidDataException($"Archive segment {segment.Number}: format or save/player/NPC ownership mismatch; preserving file.");
        segment.RecoveryCheckpoints ??= new();
        if (segment.Records is null || segment.Records.Count > SegmentSize)
            throw new InvalidDataException($"Archive segment {segment.Number}: invalid record collection; preserving file.");
        for (int index = 0; index < segment.Records.Count; index++)
        {
            var record = segment.Records[index];
            if (record is null || string.IsNullOrWhiteSpace(record.Id) || record.Message is null && string.IsNullOrWhiteSpace(record.Summary))
                throw new InvalidDataException($"Archive segment {segment.Number}, record {index}: missing record data; preserving file.");
            if (record.Message is not null && !MatchesStoredEventId(record.Message, record.Id))
                throw new InvalidDataException($"Archive segment {segment.Number}, record {index}: message checksum mismatch; preserving file.");
        }
        return segment;
    }
    private static bool MatchesStoredEventId(ConversationMemoryMessage message, string id)
    {
        if (EventId(message) == id) return true;
        // Earlier versions hashed the source, then cloned null optional strings to "".
        // Accept only an exact checksum of that known old representation. Never bypass ownership/content validation.
        var fields = new[] { nameof(message.Role), nameof(message.GameDate), nameof(message.Source),
            nameof(message.EventId), nameof(message.EpisodeId), nameof(message.DedupeKey), nameof(message.LocationName) }
            .Select(name => typeof(ConversationMemoryMessage).GetProperty(name)!)
            .Where(property => property.GetValue(message) is string text && text.Length == 0).ToArray();
        for (int mask = 1; mask < (1 << fields.Length); mask++)
        {
            var original = message.Clone();
            for (int index = 0; index < fields.Length; index++)
                if ((mask & (1 << index)) != 0) fields[index].SetValue(original, null);
            if (EventId(original) == id) return true;
        }
        return false;
    }
    private void WriteSegment(ArchiveState state,ArchiveSegment segment)
    {
        Directory.CreateDirectory(state.Directory);
        string path=Path.Combine(state.Directory,$"segment-{segment.Number:D8}.json");
        string temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
        // Cooperating game instances serialize tail replacement. A stale in-memory page
        // must fork/reload instead of overwriting records another instance appended.
        using var writeLock=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        if(File.Exists(path))
        {
            string current=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if(!state.PersistedHashes.TryGetValue(segment.Number,out string? expected) || current!=expected)
                throw new IOException("Archive segment changed after it was read.");
        }
        else if(state.PersistedHashes.ContainsKey(segment.Number)) throw new IOException("Archive segment disappeared.");
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { JsonSerializer.Serialize(stream,segment); stream.Flush(flushToDisk:true); }
            if(File.Exists(path)) File.Replace(temporary,path,path+".bak",ignoreMetadataErrors:true);
            else File.Move(temporary,path);
            state.PersistedHashes[segment.Number]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
    private ArchiveSegment NewSegment(int number,string player,string npc)
        => new(){Number=number,SaveScope=SaveScope,PlayerId=player,NpcName=npc};
    private ArchiveSegment NextSegment(ArchiveState state, string player, string npc)
    {
        var next = NewSegment(state.Tail.Number + 1, player, npc);
        next.RecoveryCheckpoints = state.Tail.RecoveryCheckpoints.ToList();
        return next;
    }
    private void ReportOnce(string message)
    {
        if (reportedFailures.Add(message)) failure?.Invoke(message);
    }
    private string BucketPath(string player,string npc) => Path.Combine(root,Scope(player),Scope(npc));
    private static string Key(string player,string npc) => Scope(player)+"/"+Scope(npc);
    private sealed class ArchiveState(string directory,string branchId,ArchiveSegment tail)
    {
        public string Directory {get;}=directory;
        public string BranchId {get;}=branchId;
        public ArchiveSegment Tail {get;set;}=tail;
        public string? RecoveryNotice {get;set;}
        public Dictionary<string,ArchiveRecord> Records {get;}=new(StringComparer.Ordinal);
        public Dictionary<int,string> PersistedHashes {get;}=new();
    }
    public sealed class ArchiveSegment
    {
        public int Version {get;set;}=1;
        public int Number {get;set;}
        public string SaveScope {get;set;}="";
        public string PlayerId {get;set;}="";
        public string NpcName {get;set;}="";
        public List<ArchiveRecord> Records {get;set;}=new();
        public List<string> RecoveryCheckpoints {get;set;}=new();
    }
    public sealed class ArchiveRecord
    {
        public string Id {get;set;}="";
        public ConversationMemoryMessage? Message {get;set;}
        public string? Summary {get;set;}
    }
}
