using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VivantValley.Services;

/// <summary>
/// A bounded provider transcript. A completed HTTP response is only a candidate
/// continuation: the next request must prove that the game actually observed it.
/// No game state or credentials are persisted here.
/// </summary>
public sealed class AppendOnlyContextSession<TMessage>
{
    private readonly object gate = new();
    private readonly Func<TMessage, string> serialize;
    private readonly Func<string, TMessage> deserialize;
    private string fingerprint = string.Empty;
    private string expectedObservedState = string.Empty;
    private string[] transcript = Array.Empty<string>();
    private long revision;
    private long epoch;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public AppendOnlyContextSession(Func<TMessage, string> serialize, Func<string, TMessage>? deserialize = null)
    {
        this.serialize = serialize;
        this.deserialize = deserialize ?? (json => JsonSerializer.Deserialize<TMessage>(json)!);
    }

    public ContextTurn<TMessage> Begin(IReadOnlyList<TMessage> stableMessages,
        Func<IReadOnlyList<TMessage>> bootstrapFactory, IReadOnlyList<TMessage> currentMessages,
        string observedState, int maxEstimatedTokens = 6000, int reservedTokens = 0,
        IReadOnlyList<TMessage>? knownMessages = null)
    {
        if (maxEstimatedTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxEstimatedTokens));
        string[] stable = stableMessages.Select(serialize).ToArray();
        string[] current = currentMessages.Select(serialize).ToArray();
        string stableFingerprint = Hash(string.Join("\n", stable));
        lock (gate)
        {
            string[] known = (knownMessages ?? Array.Empty<TMessage>()).Select(serialize).Distinct(StringComparer.Ordinal).ToArray();
            string[] additions = known.Except(transcript, StringComparer.Ordinal).ToArray();
            bool reuse = transcript.Length > 0 && fingerprint == stableFingerprint
                && expectedObservedState == observedState
                && Estimate(transcript.Concat(additions).Concat(current)) + reservedTokens <= maxEstimatedTokens;
            var wire = reuse ? transcript.ToList() : stable.ToList();
            if (!reuse) additions = known;
            if (!reuse)
            {
                // Bootstrap entries are complete facts/turns. Drop whole older
                // entries if needed; never truncate the system or current input.
                var background = bootstrapFactory().Select(serialize).ToList();
                while (background.Count > 0
                    && Estimate(wire.Concat(background).Concat(additions).Concat(current)) + reservedTokens > maxEstimatedTokens)
                    background.RemoveAt(0);
                wire.AddRange(background);
            }
            wire.AddRange(additions);
            wire.AddRange(current);
            int estimated = Estimate(wire) + reservedTokens;
            bool exceeded = estimated > maxEstimatedTokens;
            // This is a working-context target, not a model-specific tokenizer
            // limit. Mandatory identity/current input are never silently cut.
            AiUsageDiagnostics.RecordContext(!reuse, exceeded);
            return new ContextTurn<TMessage>(this, revision, reuse ? epoch : epoch + 1,
                !reuse, SessionId, stableFingerprint, wire.Select(deserialize).ToList(),
                wire.ToArray(), estimated, exceeded);
        }
    }

    /// <summary>Retain an exact candidate transcript; observation is checked on the next Begin.</summary>
    public bool Complete(ContextTurn<TMessage> turn, IReadOnlyList<TMessage> fullTranscript,
        string expectedObservedState)
    {
        string[] wire = fullTranscript.Select(serialize).ToArray();
        if (!ReferenceEquals(turn.Owner, this) || wire.Length <= turn.Wire.Length
            || !wire.Take(turn.Wire.Length).SequenceEqual(turn.Wire, StringComparer.Ordinal))
            throw new InvalidOperationException("A context continuation must preserve the entire request prefix.");
        lock (gate)
        {
            if (turn.Revision != revision) return false; // Concurrent/stale result never replaces a newer branch.
            transcript = wire;
            fingerprint = turn.Fingerprint;
            this.expectedObservedState = expectedObservedState;
            epoch = turn.Epoch;
            revision++;
            return true;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            transcript = Array.Empty<string>();
            fingerprint = expectedObservedState = string.Empty;
            revision++;
        }
    }

    private static int Estimate(IEnumerable<string> messages)
        => messages.Sum(message => PromptTokenEstimate.Estimate(message) + 6);
    public static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class ContextTurn<TMessage>
{
    internal AppendOnlyContextSession<TMessage> Owner { get; }
    internal long Revision { get; }
    internal string Fingerprint { get; }
    internal string[] Wire { get; }
    public List<TMessage> Messages { get; }
    public long Epoch { get; }
    public bool IsColdStart { get; }
    public string SessionId { get; }
    public int EstimatedTokens { get; }
    public bool BudgetExceeded { get; }

    internal ContextTurn(AppendOnlyContextSession<TMessage> owner, long revision, long epoch,
        bool cold, string sessionId, string fingerprint, List<TMessage> messages, string[] wire, int tokens, bool budgetExceeded)
    {
        Owner = owner; Revision = revision; Epoch = epoch; IsColdStart = cold;
        SessionId = sessionId; Fingerprint = fingerprint; Messages = messages; Wire = wire; EstimatedTokens = tokens; BudgetExceeded = budgetExceeded;
    }
}

/// <summary>Conservative multilingual budgeting estimate, not a provider tokenizer or billing measurement.</summary>
public static class PromptTokenEstimate
{
    public static int Estimate(string? text) => ContextMemoryCompiler.EstimateTokens(text);
}
