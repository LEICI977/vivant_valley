using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VivantValley;

public enum WeeklyStoryStatus
{
    Generating,
    MailQueued,
    Ready,
    Running,
    Completed,
    Expired,
    Failed,
    Declined,
}

public static class WeeklyStoryBeatTypes
{
    public const string Enter = "enter";
    public const string Move = "move";
    public const string Face = "face";
    public const string Animation = "animation";
    public const string Emote = "emote";
    public const string Dialogue = "dialogue";
    public const string Pause = "pause";
    public const string Camera = "camera";
    public const string Sound = "sound";
    public const string Choice = "choice";
    public const string Exit = "exit";
    public const string End = "end";
}

public static class WeeklyStoryTimeSlots
{
    public const string Morning = "morning";
    public const string Afternoon = "afternoon";
    public const string Evening = "evening";
    public const string Night = "night";
}

public sealed class WeeklyStorySaveStore
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Dictionary<string, PlayerWeeklyStoryState> Players { get; set; }
        = new(StringComparer.Ordinal);

    public PlayerWeeklyStoryState GetOrCreate(string playerId)
    {
        string key = string.IsNullOrWhiteSpace(playerId) ? "local" : playerId.Trim();
        Players ??= new Dictionary<string, PlayerWeeklyStoryState>(StringComparer.Ordinal);
        if (!Players.TryGetValue(key, out PlayerWeeklyStoryState? state) || state is null)
        {
            state = new PlayerWeeklyStoryState { PlayerId = key };
            Players[key] = state;
        }

        state.Normalize(key);
        return state;
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        var normalized = new Dictionary<string, PlayerWeeklyStoryState>(StringComparer.Ordinal);
        foreach ((string key, PlayerWeeklyStoryState? value) in Players
                     ?? new Dictionary<string, PlayerWeeklyStoryState>())
        {
            if (value is null)
                continue;
            string playerId = string.IsNullOrWhiteSpace(key) ? value.PlayerId : key.Trim();
            if (string.IsNullOrWhiteSpace(playerId))
                continue;
            value.Normalize(playerId);
            normalized[playerId] = value;
        }
        Players = normalized;
    }
}

public sealed class PlayerWeeklyStoryState
{
    public string PlayerId { get; set; } = string.Empty;

    public int LastPlanningWeekKey { get; set; } = -1;

    public WeeklyStoryPlanningProgress Planning { get; set; } = new();

    public WeeklyStoryPlan? CurrentStory { get; set; }

    public List<WeeklyStoryHistoryEntry> History { get; set; } = new();

    public HashSet<string> VisitedLocationIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize(string playerId)
    {
        PlayerId = playerId;
        LastPlanningWeekKey = Math.Max(-1, LastPlanningWeekKey);
        Planning ??= new WeeklyStoryPlanningProgress();
        Planning.Normalize();
        CurrentStory?.Normalize();
        VisitedLocationIds = new HashSet<string>(
            (VisitedLocationIds ?? new HashSet<string>())
            .Select(location => WeeklyStoryPlan.LimitToken(location, 160))
            .Where(location => location.Length > 0)
            .Take(64),
            StringComparer.OrdinalIgnoreCase);
        History = (History ?? new List<WeeklyStoryHistoryEntry>())
            .Where(entry => entry is not null)
            .Select(entry =>
            {
                entry.Normalize();
                return entry;
            })
            .Where(entry => entry.StoryId.Length > 0)
            // Source transcripts and pending summaries must survive subsequent weeks.
            .OrderBy(entry => entry.CompletedDay)
            .ToList();
    }
}

public sealed class WeeklyStoryPlanningProgress
{
    public int WeekKey { get; set; } = -1;
    public int Attempts { get; set; }
    public string Status { get; set; } = "idle";
    public string FailureReason { get; set; } = string.Empty;
    public string FailureKind { get; set; } = string.Empty;
    public WeeklyStoryPlan? CachedPlan { get; set; }

    public void Normalize()
    {
        WeekKey = Math.Max(-1, WeekKey);
        Attempts = Math.Clamp(Attempts, 0, Services.WeeklyStoryPlanningPolicy.MaximumAttempts);
        FailureReason = WeeklyStoryPlan.LimitText(FailureReason, 400);
        FailureKind = WeeklyStoryPlan.LimitToken(FailureKind, 32);
        CachedPlan?.Normalize();
    }
}

public sealed class WeeklyStoryPlan
{
    /// <summary>1 = legacy single choice; 2 = bounded conditional multi-choice story.</summary>
    public int FormatVersion { get; set; } = 1;
    public FreeStoryScene? FreeScene { get; set; }
    public List<WeeklyStoryChoiceRecord> Selections { get; set; } = new();
    public string SelectedEnding { get; set; } = string.Empty;
    /// <summary>
    /// Safety bound for executable beats. The runner is sequential and supports
    /// longer scenes; this cap prevents malformed provider output from creating
    /// an effectively unbounded event while allowing stories to exceed the old
    /// 18-beat limit.
    /// </summary>
    public const int MaximumBeatCount = 48;

    public string StoryId { get; set; } = string.Empty;

    public int WeekKey { get; set; } = -1;

    public string Seed { get; set; } = string.Empty;

    public int CreatedDay { get; set; } = -1;

    public int ScheduledDay { get; set; } = -1;

    public int ExpiryDay { get; set; } = -1;

    public int StartTime { get; set; } = 1700;

    public int EndTime { get; set; } = 2300;

    public string TimeSlot { get; set; } = WeeklyStoryTimeSlots.Evening;
    public int TimingVersion { get; set; }
    public string TimingMode { get; set; } = "flexible";
    public string TimingReason { get; set; } = "";
    public string RequiredEnvironment { get; set; } = "";
    public bool StartedOnSite { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Inviter { get; set; } = string.Empty;

    public string ActivityKind { get; set; } = "story";

    public string LocationId { get; set; } = string.Empty;

    public string LocationDisplayName { get; set; } = string.Empty;

    public string MailId { get; set; } = string.Empty;

    public string MailBody { get; set; } = string.Empty;

    public bool MailRead { get; set; }

    // MailRead also means the player accepted an invitation through another channel.
    public int InvitationVersion { get; set; }
    public string InvitationKind { get; set; } = "mail";
    public int InvitationDeliveredDay { get; set; } = -1;
    public int LastInvitationOfferDay { get; set; } = -1;
    public int ArrivalDay { get; set; } = -1;
    public int RetryAfterMinutes { get; set; }
    public int InvitationWindowDays { get; set; } = 3;

    public WeeklyStoryStatus Status { get; set; } = WeeklyStoryStatus.Generating;

    public int AttemptCount { get; set; }

    public int CurrentBeatIndex { get; set; }

    public int CompletedDay { get; set; } = -1;

    public string SelectedChoiceId { get; set; } = string.Empty;

    public string SelectedChoiceMemory { get; set; } = string.Empty;

    public List<WeeklyStoryActor> Actors { get; set; } = new();

    public List<WeeklyStoryBeat> Beats { get; set; } = new();

    /// <summary>Local diagnostics; never sent back to the provider as story content.</summary>
    public List<string> RepairNotes { get; set; } = new();

    public void Normalize()
    {
        FreeScene?.Normalize();
        StoryId = LimitToken(StoryId, 96);
        WeekKey = Math.Max(-1, WeekKey);
        Seed = LimitToken(Seed, 96);
        CreatedDay = Math.Max(-1, CreatedDay);
        ScheduledDay = Math.Max(CreatedDay, ScheduledDay);
        ExpiryDay = Math.Max(ScheduledDay, ExpiryDay);
        StartTime = Math.Clamp(StartTime, 600, 2500);
        EndTime = Math.Clamp(EndTime, StartTime + 10, 2600);
        TimeSlot = LimitToken(TimeSlot, 24).ToLowerInvariant();
        Title = LimitText(Title, 80);
        Summary = LimitText(Summary, 600);
        Inviter = LimitToken(Inviter, 80);
        ActivityKind = ActivityKind == "date" ? "date" : "story";
        LocationId = LimitToken(LocationId, 160);
        LocationDisplayName = LimitText(LocationDisplayName, 100);
        InvitationKind = InvitationKind is "mail" or "direct" or "visit" or "phone" ? InvitationKind : "mail";
        InvitationWindowDays = Math.Clamp(InvitationWindowDays, 1, 14);
        InvitationDeliveredDay = Math.Max(-1, InvitationDeliveredDay);
        LastInvitationOfferDay = Math.Max(-1, LastInvitationOfferDay);
        ArrivalDay = Math.Max(-1, ArrivalDay);
        RetryAfterMinutes = Math.Max(0, RetryAfterMinutes);
        MailId = LimitToken(MailId, 160);
        MailBody = LimitText(MailBody, 900);
        AttemptCount = Math.Clamp(AttemptCount, 0, 3);
        CompletedDay = Math.Max(-1, CompletedDay);
        SelectedChoiceId = LimitToken(SelectedChoiceId, 64);
        SelectedChoiceMemory = LimitText(SelectedChoiceMemory, 1600);
        SelectedEnding = LimitToken(SelectedEnding, 24);
        Selections = (Selections ?? new()).Where(selection => selection is not null).Take(WeeklyStoryPlan.MaximumBeatCount + 1).ToList();
        RepairNotes = (RepairNotes ?? new List<string>()).Where(note => !string.IsNullOrWhiteSpace(note))
            .Select(note => LimitText(note, 160)).Distinct(StringComparer.Ordinal).Take(16).ToList();
        Actors = (Actors ?? new List<WeeklyStoryActor>())
            .Where(actor => actor is not null)
            .Select(actor =>
            {
                actor.Normalize();
                return actor;
            })
            .Where(actor => actor.Name.Length > 0)
            .GroupBy(actor => actor.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(3)
            .ToList();
        Beats = (Beats ?? new List<WeeklyStoryBeat>())
            .Where(beat => beat is not null)
            .Select(beat =>
            {
                beat.Normalize();
                return beat;
            })
            .Where(beat => beat.Type.Length > 0)
            // Retain one overflow beat so playback validation rejects corrupt
            // saved plans instead of converting them into truncated successes.
            .Take(MaximumBeatCount + 1)
            .ToList();
        CurrentBeatIndex = Math.Clamp(CurrentBeatIndex, 0, Beats.Count);
    }

    internal static string LimitToken(string? value, int maximum)
    {
        string clean = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= maximum ? clean : clean[..maximum];
    }

    internal static string LimitText(string? value, int maximum)
    {
        string clean = (value ?? string.Empty).Replace('\r', ' ').Trim();
        return clean.Length <= maximum ? clean : clean[..maximum];
    }
}

public sealed class WeeklyStoryActor
{
    /// <summary>Local eligibility captured at planning: 0 none, 1 affection, 2 kiss, 3 private fade-out.</summary>
    /// <summary>Legacy save metadata, no longer used to gate content or playback.</summary>
    public int RomanceLevel { get; set; }
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public void Normalize()
    {
        RomanceLevel = Math.Clamp(RomanceLevel, 0, 3);
        Name = WeeklyStoryPlan.LimitToken(Name, 80);
        DisplayName = WeeklyStoryPlan.LimitText(DisplayName, 80);
    }
}

public sealed class WeeklyStoryBeat
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>All entries must match earlier choice IDs and option IDs.</summary>
    [JsonPropertyName("when")]
    public Dictionary<string, string> When { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("actor")]
    public string Actor { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("cue")]
    public string Cue { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("durationMs")]
    public int DurationMs { get; set; } = 600;

    [JsonPropertyName("choices")]
    public List<WeeklyStoryChoiceOption> Choices { get; set; } = new();

    public void Normalize()
    {
        Id = WeeklyStoryPlan.LimitToken(Id, 64).ToLowerInvariant();
        When ??= new(StringComparer.Ordinal);
        Type = WeeklyStoryPlan.LimitToken(Type, 24).ToLowerInvariant();
        Actor = WeeklyStoryPlan.LimitToken(Actor, 80);
        Target = WeeklyStoryPlan.LimitToken(Target, 80).ToLowerInvariant();
        Cue = WeeklyStoryPlan.LimitToken(Cue, 40).ToLowerInvariant();
        Text = WeeklyStoryPlan.LimitText(Text, 240);
        DurationMs = Math.Clamp(DurationMs, 150, 4000);
        Choices = (Choices ?? new List<WeeklyStoryChoiceOption>())
            .Where(choice => choice is not null)
            .Select(choice =>
            {
                choice.Normalize();
                return choice;
            })
            .ToList();
    }
}

public sealed class WeeklyStoryChoiceOption
{
    [JsonPropertyName("when")]
    public Dictionary<string, string> When { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("ending")]
    public string Ending { get; set; } = string.Empty;

    [JsonPropertyName("intimacy")]
    public string Intimacy { get; set; } = "none";

    /// <summary>Declines further romantic escalation with the response actor during this scene.</summary>
    [JsonPropertyName("boundary")]
    public bool Boundary { get; set; }
    [JsonPropertyName("id")]
    [JsonConverter(typeof(WeeklyStoryChoiceIdConverter))]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("responseActor")]
    public string ResponseActor { get; set; } = string.Empty;

    [JsonPropertyName("responseText")]
    public string ResponseText { get; set; } = string.Empty;

    [JsonPropertyName("memoryText")]
    public string MemoryText { get; set; } = string.Empty;

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "warm";

    public void Normalize()
    {
        When ??= new(StringComparer.Ordinal);
        Ending = WeeklyStoryPlan.LimitToken(Ending, 24).ToLowerInvariant();
        Intimacy = WeeklyStoryPlan.LimitToken(Intimacy, 24).ToLowerInvariant();
        Id = WeeklyStoryPlan.LimitToken(Id, 64).ToLowerInvariant();
        Text = WeeklyStoryPlan.LimitText(Text, 100);
        ResponseActor = WeeklyStoryPlan.LimitToken(ResponseActor, 80);
        ResponseText = WeeklyStoryPlan.LimitText(ResponseText, 240);
        MemoryText = WeeklyStoryPlan.LimitText(MemoryText, 240);
        Outcome = WeeklyStoryPlan.LimitToken(Outcome, 24).ToLowerInvariant();
    }
}

/// <summary>
/// Models occasionally emit a numeric choice id even though the wire contract
/// describes ids as strings. Preserve that harmless value as text so a retry
/// is not wasted on a type-only formatting mistake.
/// </summary>
internal sealed class WeeklyStoryChoiceIdConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number when reader.TryGetInt64(out long value)
                => value.ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Number when reader.TryGetDecimal(out decimal value)
                => value.ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Null => string.Empty,
            _ => throw new JsonException("Choice id must be a string or number."),
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

public sealed class WeeklyStoryHistoryEntry
{
    public string PlannedPremise { get; set; } = string.Empty;
    public List<string> OpeningTexts { get; set; } = new();
    public List<FreeStoryEvent> ObservedEvents { get; set; } = new();
    public Dictionary<string, string> WitnessSummaries { get; set; } = new();
    public Dictionary<string, int> SummaryAttempts { get; set; } = new();
    public List<WeeklyStoryChoiceRecord> Selections { get; set; } = new();
    public string Ending { get; set; } = string.Empty;
    public string StoryId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string LocationId { get; set; } = string.Empty;

    public int WeekKey { get; set; } = -1;

    public int CompletedDay { get; set; } = -1;

    public string Summary { get; set; } = string.Empty;

    public string ChoiceMemory { get; set; } = string.Empty;

    public List<string> Participants { get; set; } = new();

    public void Normalize()
    {
        PlannedPremise ??= "";
        OpeningTexts ??= new(); ObservedEvents ??= new(); WitnessSummaries ??= new(); SummaryAttempts ??= new();
        StoryId = WeeklyStoryPlan.LimitToken(StoryId, 96);
        Title = WeeklyStoryPlan.LimitText(Title, 80);
        LocationId = WeeklyStoryPlan.LimitToken(LocationId, 160);
        WeekKey = Math.Max(-1, WeekKey);
        CompletedDay = Math.Max(-1, CompletedDay);
        Summary = WeeklyStoryPlan.LimitText(Summary, 1600);
        ChoiceMemory = WeeklyStoryPlan.LimitText(ChoiceMemory, 1600);
        Selections = (Selections ?? new()).Where(selection => selection is not null).Take(WeeklyStoryPlan.MaximumBeatCount + 1).ToList();
        Ending = WeeklyStoryPlan.LimitToken(Ending, 24);
        Participants = (Participants ?? new List<string>())
            .Select(name => WeeklyStoryPlan.LimitToken(name, 80))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }
}

public sealed record WeeklyStoryNpcCandidate(
    string Name,
    string DisplayName,
    int Hearts,
    string Relationship,
    bool IsDatable,
    bool IsSpouse,
    string RoleRule,
    IReadOnlyList<string> RecentEvents)
{
    public IReadOnlyList<Services.ExperienceFact> Experiences { get; init; } = Array.Empty<Services.ExperienceFact>();
    public IReadOnlyList<string> ActiveCommitments { get; init; } = Array.Empty<string>();
    public bool IsAdult { get; init; }
    public bool IsDating { get; init; }
    public bool HasRomanticInterest { get; init; }
    public bool IsSupporting { get; init; }
}

public sealed record WeeklyStoryLocationCandidate(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Tags,
    int MaximumActors,
    bool Dangerous,
    bool RequiresPriorVisit = false);

public sealed class WeeklyStoryPlanRequest
{
    /// <summary>Production requires multi-choice; old integrations may still validate legacy plans.</summary>
    public bool FreeScene { get; init; }
    public int MinimumChoices { get; init; } = 1;
    public int InvitationWindowDays { get; init; } = 3;
    public int WeekKey { get; init; }

    /// <summary>Total in-game day used for local scheduling and stale-request checks.</summary>
    public int CurrentDay { get; init; }

    public int CurrentTimeOfDay { get; init; } = 600;

    /// <summary>The only day the planner is allowed to schedule.</summary>
    public int ScheduledDay { get; init; } = -1;

    public int EarliestDay { get; init; }

    public int LatestDay { get; init; }

    public WeeklyStoryDateContext CurrentDate { get; init; } = new();

    public WeeklyStoryDateContext StoryDate { get; init; } = new();

    public string CurrentWeather { get; init; } = "clear";

    public string StoryWeather { get; init; } = "clear";

    public bool FestivalToday { get; init; }

    public string FestivalName { get; init; } = string.Empty;

    public string PlayerName { get; init; } = string.Empty;

    public string Seed { get; init; } = string.Empty;

    public IReadOnlyList<WeeklyStoryNpcCandidate> Npcs { get; init; }
        = Array.Empty<WeeklyStoryNpcCandidate>();

    public IReadOnlyList<WeeklyStoryLocationCandidate> Locations { get; init; }
        = Array.Empty<WeeklyStoryLocationCandidate>();

    public IReadOnlyList<string> RecentStoryAnchors { get; init; }
        = Array.Empty<string>();

    public IReadOnlyList<WeeklyStoryHistoryEntry> RecentHistory { get; init; } = Array.Empty<WeeklyStoryHistoryEntry>();

    public string Model { get; init; } = "deepseek-v4-flash";

    public string ThinkingType { get; init; } = "disabled";

    public string ReasoningEffort { get; init; } = "low";

    public int MaxOutputTokens { get; init; } = 4000;

    /// <summary>Optional validator feedback from the previous generation attempt.</summary>
    public string PreviousFailureReason { get; init; } = string.Empty;
    public string PreviousFailureKind { get; init; } = string.Empty;
}

public sealed class WeeklyStoryDateContext
{
    public int TotalDays { get; init; }

    public int Year { get; init; }

    public string Season { get; init; } = string.Empty;

    public int DayOfMonth { get; init; }

    public string Label => $"第 {Year} 年 {Season} {DayOfMonth} 日";

    public static WeeklyStoryDateContext FromTotalDays(int totalDays)
    {
        int day = Math.Max(0, totalDays);
        int seasonIndex = day / 28 % 4;
        return new WeeklyStoryDateContext
        {
            TotalDays = day,
            Year = day / 112 + 1,
            Season = seasonIndex switch
            {
                0 => "spring",
                1 => "summer",
                2 => "fall",
                _ => "winter",
            },
            DayOfMonth = day % 28 + 1,
        };
    }
}

internal sealed class WeeklyStoryWireResponse
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = 1;
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("activityKind")]
    public string? ActivityKind { get; set; }

    [JsonPropertyName("inviter")]
    public string? Inviter { get; set; }

    [JsonPropertyName("participants")]
    public List<string>? Participants { get; set; }

    [JsonPropertyName("locationId")]
    public string? LocationId { get; set; }

    [JsonPropertyName("dayOffset")]
    public int DayOffset { get; set; }

    [JsonPropertyName("timeSlot")]
    public string? TimeSlot { get; set; }

    [JsonPropertyName("mailBody")]
    public string? MailBody { get; set; }

    [JsonPropertyName("beats")]
    public List<WeeklyStoryBeat>? Beats { get; set; }
}

/// <summary>Observed runtime selections, never populated from the planner response.</summary>
public sealed class WeeklyStoryChoiceRecord
{
    public int BeatIndex { get; set; }
    public string ChoiceId { get; set; } = string.Empty;
    public string OptionId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string ResponseActor { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string Ending { get; set; } = string.Empty;
    public bool Boundary { get; set; }
    public bool ResponseDelivered { get; set; }
}
