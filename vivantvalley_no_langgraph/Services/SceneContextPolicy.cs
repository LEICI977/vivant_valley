using System.Text.Json;

namespace VivantValley.Services;

/// <summary>Shared scene budgets. Select complete evidence records; never trim a fact mid-sentence.</summary>
public static class SceneContextPolicy
{
    public const int RecallTokens = 340;
    public const int HistoryTokens = 1400;

    public static T[] Recent<T>(IEnumerable<T> records, int tokenBudget = HistoryTokens)
    {
        var result = new List<T>();
        int remaining = Math.Max(0, tokenBudget);
        foreach (T record in records.Reverse())
        {
            int tokens = ContextMemoryCompiler.EstimateTokens(JsonSerializer.Serialize(record));
            if (tokens > remaining) continue;
            result.Add(record);
            remaining -= tokens;
        }
        result.Reverse();
        return result.ToArray();
    }

    public static string Fingerprint<T>(IEnumerable<T> records)
        => JsonSerializer.Serialize(records);

    public static IEnumerable<ContextMemoryCandidate> TextCandidates(string text, string source, bool allowUnprompted = false)
        => (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => new ContextMemoryCandidate(source + ":" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(line)))[..12], line,
                source, AllowUnprompted: allowUnprompted));
}
