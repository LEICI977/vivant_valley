using StardewModdingAPI;
using StardewValley;
using VivantValley.Services;

namespace VivantValley;

public sealed partial class ModEntry
{
    private IReadOnlyList<ExperienceFact> BuildNpcExperiences(string npc, int maximum = 4)
    {
        string player = GetPlayerId(); int day = Game1.Date.TotalDays;
        memoryStore.TryGet(player, npc, out var memory);
        var news = config.EnableTownEchoes
            ? RecentKnownEventPolicy.Echoes(memoryStore.TownEchoes, player, npc, day, L.IsChinese,
                name => Game1.getCharacterFromName(name)?.displayName ?? name)
            : Enumerable.Empty<RecentKnownEvent>();
        return ExperienceContextPolicy.Build(npc, day, memory,
            dreamStorageReady ? DreamPlayer().Episodes : Array.Empty<DreamEpisode>(),
            weeklyStoryStore.GetOrCreate(player).History, socialStore.GetOrCreatePlayer(player).MailGifts,
            news, maximum);
    }

    private sealed record StorySummaryWork(string Player, WeeklyStoryHistoryEntry Story, string Npc,
        string Fingerprint, Task<string> Task, CancellationTokenSource Cancellation);
    private StorySummaryWork? storySummaryWork;
    private DateTimeOffset nextStorySummaryScan;
    private readonly Dictionary<string, DateTimeOffset> storySummaryRetry = new();

    private void ResetStorySummaryWork()
    {
        if (storySummaryWork is { } work)
        {
            work.Cancellation.Cancel();
            _ = work.Task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            work.Cancellation.Dispose();
        }
        storySummaryWork = null; storySummaryRetry.Clear(); nextStorySummaryScan = default;
    }

    private void UpdateStorySummaryWork()
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || !memoryStorageReady) return;
        if (storySummaryWork is { Task.IsCompleted: true } work)
        {
            // Never overwrite memory while an ordinary request owns a clone of it.
            if (screenStates.GetActiveValues().Any(pair => pair.Value.HasPendingConversation)) return;
            storySummaryWork = null;
            try
            {
                string summary = work.Task.GetAwaiter().GetResult();
                if (GetPlayerId() != work.Player || !weeklyStoryStore.GetOrCreate(work.Player).History.Contains(work.Story)
                    || StoryExperiencePolicy.Fingerprint(work.Story, work.Npc) != work.Fingerprint)
                    throw new InvalidDataException("Story source changed; retaining original records.");
                // Identical witness sets can share a result; private/departed observers cannot.
                foreach (string npc in StoryExperiencePolicy.Commit(work.Story, work.Npc, work.Fingerprint, summary))
                {
                    var memory = memoryStore.GetOrCreate(work.Player, npc);
                    StoryExperiencePolicy.Publish(memory, work.Story, summary);
                }
                weeklyStoryDirty = true; memoryDirty = true;
                PersistWeeklyStories(force: true); PersistMemory(force: true);
            }
            catch (Exception ex)
            {
                storySummaryRetry[work.Story.StoryId + ":" + work.Fingerprint] = DateTimeOffset.UtcNow.AddMinutes(2);
                Monitor.Log($"Story summary deferred; observed records retained: {ex.GetType().Name}", LogLevel.Warn);
            }
            finally { work.Cancellation.Dispose(); }
        }
        if (storySummaryWork is not null || segmentWork is not null || dream is not null || activeFreeStory is not null
            || weeklyStoryDayEnding || string.IsNullOrWhiteSpace(runtimeApiKey)
            || DateTimeOffset.UtcNow < nextStorySummaryScan) return;
        nextStorySummaryScan = DateTimeOffset.UtcNow.AddSeconds(2);
        foreach (var story in weeklyStoryStore.GetOrCreate(GetPlayerId()).History)
        foreach (string npc in story.Participants)
        {
            if (story.WitnessSummaries.ContainsKey(npc) || StoryExperiencePolicy.Witnessed(story, npc).Length == 0
                || story.SummaryAttempts.GetValueOrDefault(npc) >= 2) continue;
            string fingerprint = StoryExperiencePolicy.Fingerprint(story, npc);
            if (storySummaryRetry.TryGetValue(story.StoryId + ":" + fingerprint, out var after) && after > DateTimeOffset.UtcNow) continue;
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 10, 600)));
            // Reserve the attempt in the save before generation; raw observed events remain intact.
            foreach (string observer in story.Participants.Where(n => StoryExperiencePolicy.Fingerprint(story, n) == fingerprint))
                story.SummaryAttempts[observer] = story.SummaryAttempts.GetValueOrDefault(observer) + 1;
            weeklyStoryDirty = true; PersistWeeklyStories(force: true);
            storySummaryWork = new(GetPlayerId(), story, npc, fingerprint,
                StoryExperiencePolicy.SummarizeAsync(deepSeekClient, runtimeApiKey, currentAiProfile.Model, story, npc, cancellation.Token), cancellation);
            return;
        }
    }
}
