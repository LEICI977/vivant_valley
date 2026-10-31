using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VivantValley.Services;

/// <summary>A complete, attributable memory item. Source and status are data, never inferred world outcomes.</summary>
public sealed record ContextMemoryCandidate(string Id, string Text, string Source = "dialogue", int Priority = 0,
    string? Status = null, bool AllowUnprompted = false, IReadOnlyList<string>? EventIds = null);

public sealed record ContextMemoryResult(string Text, IReadOnlyList<string> SelectedIds, int EstimatedTokens,
    int CandidateCount, int DroppedDuplicates);

/// <summary>One budget for all recall sources. No truncation of negation, promises, citations or JSON.</summary>
public static class ContextMemoryCompiler
{
    private static readonly HashSet<string> StopTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "我们","你们","他们","玩家","村民","这个","那个","今天","已经","以后","还是","只是","什么",
        "怎么","可以","一起","最近","现在","真的","觉得","知道","一下","好的","谢谢","你好","喜欢",
        "之前","以前","上次","记得","记忆","事情","那件","件事","还记","不知","好像","就是","不是",
        "的时","时候","了吗","了吗","the","and","that","this","with","player","today","thing","before",
        "remember","last","time","like","likes","want","really","have","what","about","your","you","was","were"
    };

    public static ContextMemoryResult Compile(string query, IEnumerable<ContextMemoryCandidate> candidates,
        IEnumerable<string>? recentTexts = null, IEnumerable<string>? coveredEventIds = null,
        int tokenBudget = 220, int maximumEntries = 2, bool allowUnprompted = false)
    {
        var source = candidates.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Text)).ToArray();
        var covered = (coveredEventIds ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        var recent = (recentTexts ?? Array.Empty<string>()).Select(Normalize).Where(t => t.Length > 0).ToArray();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenContent = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<string>();
        var ids = new List<string>();
        int used = 0, duplicates = 0;
        // Stable tie order makes unchanged candidates byte-for-byte repeatable across requests.
        foreach (var item in source.Select(c => new { Candidate = c, Score = Relevance(query, c.Text) })
                     .Where(x => x.Score > 0 || (!ConversationMemoryPolicy.IsTopicRefusal(query)
                && (allowUnprompted || x.Candidate.AllowUnprompted)))
                     .OrderByDescending(x => x.Candidate.Priority).ThenByDescending(x => x.Score)
                     .ThenBy(x => x.Candidate.Id, StringComparer.Ordinal))
        {
            var candidate = item.Candidate;
            string normalized = Normalize(candidate.Text);
            string id = string.IsNullOrWhiteSpace(candidate.Id) ? StableId(candidate.Source + "\n" + candidate.Text) : candidate.Id;
            // Never treat a different source (especially dream vs real action) as equivalent.
            string sourceName = Source(candidate.Source, candidate.Text);
            string contentKey = sourceName + "\n" + normalized;
            if (covered.Contains(id) || candidate.EventIds is { Count: > 0 } eventIds && eventIds.All(covered.Contains)
                || seenIds.Contains(id) || seenContent.Contains(contentKey)
                || recent.Any(text => normalized.Length >= 8 && text.Contains(normalized, StringComparison.Ordinal)))
            { duplicates++; continue; }
            seenIds.Add(id); seenContent.Add(contentKey);
            string status = string.IsNullOrWhiteSpace(candidate.Status) ? "" : ";status=" + Label(candidate.Status);
            string line = "[" + sourceName + ";id=" + Label(id) + status + "] " + candidate.Text.Trim();
            int cost = EstimateTokens(line) + (selected.Count > 0 ? 1 : 0);
            if (selected.Count >= Math.Max(0, maximumEntries) || used + cost > Math.Max(0, tokenBudget)) continue;
            selected.Add(line); ids.Add(id); used += cost;
        }
        return new(string.Join('\n', selected), ids, used, source.Length, duplicates);
    }

    public static IReadOnlyList<ContextMemoryCandidate> FromSummary(string? summary, string source = "dialogue")
    {
        var result = new List<ContextMemoryCandidate>();
        foreach (string line in (summary ?? "").Split(new[] { '\r','\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var encoded = Regex.Match(line,@"^\[(?<source>[a-zA-Z0-9_:.\-]+);id=(?<id>[a-zA-Z0-9_:.\-]+)(?:;status=(?<status>[a-zA-Z0-9_:.\-]+))?\]\s*(?<text>.*)$");
            if(encoded.Success)
            {
                result.Add(new(encoded.Groups["id"].Value,encoded.Groups["text"].Value,encoded.Groups["source"].Value,
                    Status:encoded.Groups["status"].Success ? encoded.Groups["status"].Value : null));
                continue;
            }
            result.Add(new("fact-" + StableId(line),line,Source(source,line),Status:Status(line)));
        }
        return result;
    }

    /// <summary>Frozen only at an epoch boundary; current code-owned states still travel in the live tail.</summary>
    public static string BuildCapsule(string? summary, IEnumerable<string>? currentCommitments = null,
        IEnumerable<string>? recentTexts = null, int tokenBudget = 420)
    {
        var facts=FromSummary(summary).Where(item=>item.Status is null)
            .Select((item,index)=>item with { Priority=index }).ToList();
        foreach(string commitment in currentCommitments ?? Array.Empty<string>())
            facts.Add(new("commitment-"+StableId(commitment),commitment,"commitment",Priority:10000,Status:"pending"));
        return Compile("",facts,recentTexts,tokenBudget:tokenBudget,maximumEntries:4,allowUnprompted:true).Text;
    }
    private static string? Status(string text)
    {
        if(!text.Contains("约定",StringComparison.Ordinal) && !Regex.IsMatch(text,@"\bpromise\b",RegexOptions.IgnoreCase)) return null;
        if(text.Contains("已过期",StringComparison.Ordinal)) return "expired";
        if(text.Contains("待履行",StringComparison.Ordinal)) return "pending";
        return ConversationCommitmentPolicy.GetResolution(text) ?? "recorded";
    }

    /// <summary>A conservative, provider-independent estimate; actual tokenizer usage remains authoritative.</summary>
    public static int EstimateTokens(string? text)
    {
        int ascii = 0, other = 0;
        foreach (Rune rune in (text ?? "").EnumerateRunes())
        {
            if (rune.IsAscii) ascii++;
            else other += rune.Value > 0xffff ? 3 : 2;
        }
        return (ascii + 2) / 3 + other;
    }

    public static int Relevance(string? query, string? text)
    {
        string current = ConversationMemoryPolicy.GetCurrentTopicText(query);
        if (current.Length == 0) return 0;
        var terms = Terms(current); var haystack = Terms(text ?? "");
        return terms.Count(haystack.Contains);
    }

    public static string StableId(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];

    private static string Label(string text)
        => Regex.Replace(text, @"[^a-zA-Z0-9_:.\-]", "_")[..Math.Min(80, Regex.Replace(text, @"[^a-zA-Z0-9_:.\-]", "_").Length)];
    private static string Source(string source, string text)
        => source is "dream" or "shared_dream" || text.Contains("DREAM",StringComparison.OrdinalIgnoreCase)
            || text.Contains("梦境",StringComparison.Ordinal) || text.Contains("梦中",StringComparison.Ordinal)
            ? "dream_only" : Label(string.IsNullOrWhiteSpace(source) ? "dialogue" : source);
    private static string Normalize(string text)
    {
        string withoutDate = Regex.Replace(text.Trim(), @"^\[[^\]\r\n]{1,100}\]\s*", "");
        return Regex.Replace(withoutDate.Normalize().ToLowerInvariant(), @"[\s\p{P}\p{S}]", "");
    }
    private static HashSet<string> Terms(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, @"[a-zA-Z0-9_]{3,}|[\p{IsCJKUnifiedIdeographs}]{2,}"))
        {
            if (match.Value[0] is < '\u3400' or > '\u9fff') result.Add(match.Value);
            else for (int i = 0; i + 1 < match.Length; i++) result.Add(match.Value.Substring(i, 2));
        }
        // Small deterministic game-domain aliases, not generic semantic guessing.
        if (Regex.IsMatch(text,@"海边|海滩|沙滩|看海|\b(?:beach|seaside)\b",RegexOptions.IgnoreCase)) result.Add("place:beach");
        if (Regex.IsMatch(text,@"矿井|矿洞|下矿|\b(?:mines|mining)\b|\bthe mine\b",RegexOptions.IgnoreCase)) result.Add("place:mines");
        result.ExceptWith(StopTerms);
        return result;
    }
}
