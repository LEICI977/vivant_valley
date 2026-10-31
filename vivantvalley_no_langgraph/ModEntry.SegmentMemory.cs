using StardewModdingAPI;
using StardewValley;
using VivantValley.Services;
using VivantValley.Menus;

namespace VivantValley;

public sealed partial class ModEntry
{
    private SegmentWork? segmentWork;
    private readonly Dictionary<string, DateTimeOffset> segmentRetryAfter = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SegmentSummaryResult> completedSegmentSummaries = new(StringComparer.Ordinal);
    private (int Screen, string Npc, string Name, string Text, string Player, int Day, string Location)? segmentDeferredInput;
    private AiStreamingDialogueMenu? segmentWaitMenu;

    private sealed record SegmentWork(string Player, string Npc, NpcConversationMemory Owner,
        ConversationSegmentBatch? Batch, DreamEpisode? Dream, string SourceFingerprint,
        Task<SegmentSummaryResult> Task, CancellationTokenSource Cancellation);

    private void ResetSegmentWork()
    {
        ResetStorySummaryWork();
        if (segmentWork is { } pending)
        {
            pending.Cancellation.Cancel();
            _ = pending.Task.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            pending.Cancellation.Dispose();
        }
        segmentWork = null;
        segmentDeferredInput = null;
        segmentWaitMenu = null;
        segmentRetryAfter.Clear();
        completedSegmentSummaries.Clear();
    }

    private bool WaitForSegmentSummary(NpcConversationMemory memory, int screen, string npc, string name, string text)
    {
        if (!Context.IsMainPlayer || !memoryStorageReady) return false;
        var batch = SegmentMemoryPolicy.NextBatch(memory);
        if (batch is null) return false;
        string key = memory.PlayerId + ":" + npc;
        if (segmentRetryAfter.TryGetValue(key, out var retry) && retry > DateTimeOffset.UtcNow) return false;
        if (segmentWork is null) StartSegmentWork(memory, batch, null);
        if (segmentWork?.Npc != npc || segmentWork.Player != memory.PlayerId) return false;
        segmentDeferredInput = (screen, npc, name, text, memory.PlayerId, Game1.Date.TotalDays, Game1.currentLocation.NameOrUniqueName);
        segmentWaitMenu = new AiStreamingDialogueMenu(npc, name, config.MaxReplyCharacters,
            onCancel: () => { segmentDeferredInput = null; }, onContinue: () => { }, onClosed: () => { segmentDeferredInput = null; });
        Game1.activeClickableMenu = segmentWaitMenu;
        ShowHud(L.IsChinese ? "正在整理前一段对话，完成后继续。原文已保留。" : "Summarizing the previous conversation segment; your dialogue is retained.", HUDMessage.newQuest_type);
        return true;
    }

    private void StartSegmentWork(NpcConversationMemory memory, ConversationSegmentBatch? batch, DreamEpisode? episode)
    {
        if (segmentWork is not null || string.IsNullOrWhiteSpace(runtimeApiKey)) return;
        // Check local durability before paying to summarize. No dialogue is removed by this write.
        if (episode is null && memoryArchive?.TryAppend(memory) != true)
        {
            segmentRetryAfter[memory.PlayerId + ":" + memory.NpcName] = DateTimeOffset.UtcNow.AddMinutes(2);
            return;
        }
        if (episode is null) { memoryDirty = true; PersistMemory(force: true); }
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 10, 600)));
        string fingerprint = episode is null ? batch!.Fingerprint : MemoryEventArchive.Scope(System.Text.Json.JsonSerializer.Serialize(episode.Lines));
        object records = episode is null ? batch!.Records : episode.Lines.Select(l => new { l.Role, l.Text }).ToArray();
        string resultKey = memory.PlayerId + ":" + memory.NpcName + ":" + fingerprint;
        Task<SegmentSummaryResult> task = completedSegmentSummaries.TryGetValue(resultKey, out var cached)
            ? Task.FromResult(cached) : SegmentMemoryPolicy.SummarizeAsync(deepSeekClient, runtimeApiKey,
            currentAiProfile.Model, memory.NpcName, episode is null ? "completed_dialogue_and_code_results" : "dream_only",
            episode is null ? memory.SegmentSummary : "", records, cancellation.Token);
        segmentWork = new(memory.PlayerId, memory.NpcName, memory, batch, episode, fingerprint, task, cancellation);
    }

    private void UpdateSegmentWork()
    {
        UpdateStorySummaryWork();
        if (!Context.IsMainPlayer || !memoryStorageReady) return;
        if (segmentWork is { Task.IsCompleted: true } work)
        {
            segmentWork = null;
            try
            {
                var result = work.Task.GetAwaiter().GetResult();
                string resultKey = work.Player + ":" + work.Npc + ":" + work.SourceFingerprint;
                completedSegmentSummaries[resultKey] = result;
                if (!memoryStore.TryGet(work.Player, work.Npc, out var current) || current is null)
                    throw new InvalidDataException("Memory owner changed while summarizing.");
                // A pending dialogue owns a clone of this memory. Never replace it underneath that request.
                if (screenStates.GetActiveValues().Any(pair => pair.Value.HasPendingConversation
                    && pair.Value.PendingInfo?.NpcName == work.Npc))
                { segmentWork = work; return; }
                bool committed;
                if (work.Dream is { } episode)
                {
                    if (!dreamStorageReady || !dreamStore.For(work.Player).Episodes.Contains(episode)
                        || work.SourceFingerprint != MemoryEventArchive.Scope(System.Text.Json.JsonSerializer.Serialize(episode.Lines)))
                        throw new InvalidDataException("Dream source changed while summarizing.");
                    SegmentMemoryPolicy.Enable(current);
                    var entry = current.Messages.FirstOrDefault(m => m.DedupeKey == episode.Id);
                    // The complete episode remains in DreamSaveStore. Only its derived timeline entry is replaced.
                    if (entry is null)
                    {
                        // The pending marker may already belong to a completed twelve-turn archive.
                        // Publish the newly available summary without changing that archived source.
                        entry = new ConversationMemoryMessage { Role = "system", Source = ConversationMemorySources.Dream,
                            GameDate = "night " + episode.Day, LocationName = "dream:" + episode.Scene,
                            DedupeKey = episode.Id, EventId = episode.Id + ":summary" };
                        current.Messages.Add(entry);
                    }
                    episode.Summary = "[dream_only; night=" + episode.Day + "] " + result.Summary;
                    episode.SummaryPending = false;
                    entry.Content = episode.Summary;
                    // The complete dream remains durably stored by the existing save store.
                    string fact = "[dream_only;id=dream-" + ContextMemoryCompiler.StableId(episode.Id) + "] " + episode.Summary;
                    if (!current.Summary.Contains(fact, StringComparison.Ordinal)) current.Summary += "\n" + fact;
                    committed = true;
                    PersistDreams();
                }
                else committed = SegmentMemoryPolicy.TryCommit(current, work.Batch!, result, m => memoryArchive?.TryAppend(m) == true);
                if (!committed) throw new InvalidDataException("Segment not committed: archive unavailable or source changed; full dialogue retained.");
                completedSegmentSummaries.Remove(resultKey);
                memoryDirty = true;
                PersistMemory(force: true);
                Monitor.Log($"Segment memory committed: NPC={work.Npc}, source={(work.Dream is null ? "conversation" : "dream_only")}", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                segmentRetryAfter[work.Player + ":" + work.Npc] = DateTimeOffset.UtcNow.AddMinutes(2);
                Monitor.Log($"Segment summary/archival deferred for NPC {work.Npc}; original dialogue retained: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
            }
            finally
            {
                if (!ReferenceEquals(segmentWork, work)) work.Cancellation.Dispose();
            }
            if (segmentWork is null && segmentDeferredInput is { } input)
            {
                segmentDeferredInput = null;
                bool sameContext = GetPlayerId() == input.Player && Game1.Date.TotalDays == input.Day
                    && Game1.currentLocation.NameOrUniqueName == input.Location;
                if (ReferenceEquals(Game1.activeClickableMenu, segmentWaitMenu))
                {
                    Game1.activeClickableMenu = null;
                    segmentWaitMenu = null;
                    if (sameContext) BeginConversation(input.Screen, input.Npc, input.Name, input.Text);
                }
            }
        }
        if (segmentWork is not null || dream is not null || !dreamStorageReady || string.IsNullOrWhiteSpace(runtimeApiKey)) return;
        // Only newly ended episodes opt in. Loading an old save never queues every historical dream.
        foreach (var player in dreamStore.Players)
        {
            var episode = player.Value.Episodes.FirstOrDefault(e => e.SummaryPending && e.Status != "active"
                && (!segmentRetryAfter.TryGetValue(player.Key + ":" + e.Npc, out var retry) || retry <= DateTimeOffset.UtcNow));
            if (episode is null) continue;
            var memory = memoryStore.GetOrCreate(player.Key, episode.Npc);
            StartSegmentWork(memory, null, episode);
            break;
        }
    }
}
