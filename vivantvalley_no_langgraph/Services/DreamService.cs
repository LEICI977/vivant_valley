using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
namespace VivantValley.Services;

/// <summary>Text-only dreams retain every observed line until the episode ends.</summary>
public sealed class DreamService(IDeepSeekClient client)
{
    // A conservative working guard, not a provider tokenizer or model context limit.
    // Normal episodes have at most 24 player turns (800 chars) and 25 replies (600 chars).
    public const int MaximumEstimatedTokens = 96000;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed record Reference(int Position, string Anchor, string Text, bool Automatic = false);
    private sealed record ObservedReply(int Position, string Anchor, string Raw);
    private sealed record Pending(string Anchor, int Count, string Raw, Reference[] References);
    private sealed class Session
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public long Epoch;
        public string Stable = "";
        public string? Background;
        public List<Reference> References { get; } = new();
        public List<ObservedReply> Replies { get; } = new();
        public Pending? Pending;
    }
    private static readonly ConditionalWeakTable<DreamEpisode, Session> Sessions = new();
    public static void Forget(DreamEpisode episode) => Sessions.Remove(episode);
    private static string Fingerprint(IEnumerable<DreamLine> lines)
        => AppendOnlyContextSession<DeepSeekChatMessage>.Hash(JsonSerializer.Serialize(lines.Select(line => new
        {
            line.Role, Text = new string(line.Text.Where(c => !char.IsWhiteSpace(c)).ToArray()),
        }), Json));
    private static object DecodeFacts(string facts)
    {
        try { using var parsed = JsonDocument.Parse(facts); return parsed.RootElement.Clone(); }
        catch (JsonException) { return facts; }
    }

    public static string BuildIdentity(string identity, NpcPersonalitySettings? personality)
        => identity.Trim() + "\n\n" + (personality?.BuildPrompt() ?? "");

    public async Task<DreamReply> ReplyAsync(string key, string model, DreamEpisode episode,
        string identity, string facts, string memory, string today, CancellationToken cancellation,
        DreamRelationship? relationship = null, IReadOnlyList<ContextMemoryCandidate>? memoryCandidates = null, string? saveId = null,
        IReadOnlyList<string>? recentKnownEvents = null, SpeechTurn? speech = null)
    {
        using var language = L.BeginRequest();
        var state = Sessions.GetValue(episode, _ => new Session());
        await state.Gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // Snapshot only game-observed lines. Generated but unread text never becomes history.
            var lines = episode.Lines.Select(l => new DreamLine { Role = l.Role, Text = l.Text }).ToArray();
            bool Matches(string anchor, int count) => lines.Length >= count && Fingerprint(lines.Take(count)) == anchor;
            bool rollback = false;
            if (state.Pending is { } pending)
            {
                if (Matches(pending.Anchor, pending.Count))
                {
                    state.Replies.Add(new(pending.Count - 1, pending.Anchor, pending.Raw));
                    state.References.AddRange(pending.References);
                }
                else rollback = true;
                state.Pending = null;
            }
            rollback |= state.Replies.RemoveAll(r => !Matches(r.Anchor, r.Position + 1)) > 0;
            rollback |= state.References.RemoveAll(r => !Matches(r.Anchor, r.Position)) > 0;
            // Opening daytime context is optional, whole, and frozen for this dream.
            state.Background ??= ContextMemoryCompiler.EstimateTokens(today) <= 1800
                ? ConversationRecallPolicy.Readable(today) : "";
            var live = relationship ?? new DreamRelationship();
            var messages = new List<DeepSeekChatMessage>
            {
                new("system", identity),
                new("system", LocalizedPromptAssets.ForRequest("dream-dialogue.txt")),
                new("system", new LocalizedPromptAssets().Read("dream-context.txt")),
                new("user", JsonSerializer.Serialize(new
                {
                    dream = new { episode.Direction, visualStyle = "floating dream collage, no fixed real-world location" },
                    relationship = new { status = live.Status, hearts = live.Hearts, adult = live.Adult,
                        datable = live.Datable, romanticPartner = live.RomanticPartner },
                    wakingWorldFacts = DecodeFacts(facts), daytimeContext = state.Background,
                }, Json)),
            };
            string stable = JsonSerializer.Serialize(messages, Json);
            bool cold = rollback || state.Stable != stable;
            if (cold) state.Epoch++;
            state.Stable = stable;
            int position = lines.Length - (lines.LastOrDefault()?.Role == "user" ? 1 : 0);
            string anchor = Fingerprint(lines.Take(position));
            var additions = new List<Reference>();
            foreach (string known in (recentKnownEvents ?? Array.Empty<string>()).Select(ConversationRecallPolicy.Readable).Distinct())
                if (known.Length > 0 && !state.References.Any(r => r.Text == known))
                    additions.Add(new(position, anchor, known));
            string query = lines.LastOrDefault()?.Role == "user" ? lines[^1].Text : "";
            var recallState = new NpcConversationMemory
            {
                AutomaticRecallCount = state.References.Count(r => r.Automatic),
                Messages = lines.Select(l => new ConversationMemoryMessage { Role = l.Role, Content = l.Text }).ToList(),
                SegmentMemory = state.Background + "\n" + string.Join("\n", state.References.Concat(additions).Select(r => r.Text)),
            };
            var recall = ConversationRecallPolicy.Select(recallState, query,
                memoryCandidates ?? ContextMemoryCompiler.FromSummary(memory).ToArray());
            if (recall.Text.Length > 0)
                additions.Add(new(position, anchor, recall.Text, !ConversationRecallPolicy.ExplicitHistory(query)));
            var references = state.References.Concat(additions).ToArray();
            for (int i = 0; i <= lines.Length; i++)
            {
                messages.AddRange(references.Where(r => r.Position == i).Select(r => new DeepSeekChatMessage("user", r.Text)));
                if (i == lines.Length) break;
                var line = lines[i];
                string content = line.Role == "assistant"
                    ? state.Replies.LastOrDefault(r => r.Position == i)?.Raw ?? JsonSerializer.Serialize(new { text = line.Text }, Json)
                    : line.Text;
                messages.Add(new(line.Role == "assistant" ? "assistant" : "user", content));
            }
            int estimate = messages.Sum(m => PromptTokenEstimate.Estimate(m.Content) + 6) + 900;
            AiUsageDiagnostics.RecordContext(cold, estimate > MaximumEstimatedTokens);
            if (estimate > MaximumEstimatedTokens)
                throw new InvalidOperationException("Dream context exceeds its working limit; all observed dialogue was retained. End this dream or shorten the input.");
            string raw = await client.CompleteChatAsync(key, new DeepSeekChatRequest
            {
                NpcName = episode.Npc, TaskKind = "conversation", Model = model, Stream = false,
                MaxTokens = 900, Thinking = new DeepSeekThinkingOptions { Type = "disabled" }, ReasoningEffort = "low", Messages = messages,
                Context = new ConversationRequestContext { SessionId = state.Id, ContextEpoch = state.Epoch, Mode = "dream", SaveId = saveId,
                    SpeechLanguage = speech?.Language, TurnId = speech?.Id, VoiceEnabled = speech?.Enabled, VoiceOptionId = speech?.VoiceOptionId },
            }, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var reply = JsonSerializer.Deserialize<DreamReply>(WeeklyStoryPlannerService.ExtractJsonObject(raw),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (!DreamPolicy.IsValidReply(reply)) throw new InvalidOperationException("Invalid dream reply.");
            reply!.Scene = DreamPolicy.Scene;
            var expected = lines.Concat(new[] { new DreamLine { Role = "assistant", Text = reply.Text } }).ToArray();
            state.Pending = new(Fingerprint(expected), expected.Length, raw, additions.ToArray());
            return reply;
        }
        finally { state.Gate.Release(); }
    }
}
