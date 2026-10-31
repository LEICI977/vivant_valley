using System.Text.RegularExpressions;

namespace VivantValley.Services;

/// <summary>Recall is optional evidence for a gap in the current conversation, never a per-turn quota.</summary>
public static class ConversationRecallPolicy
{
    public const int AutomaticRecallsPerSegment = 2;
    public static bool ExplicitHistory(string query) => Regex.IsMatch(query,
        @"昨晚|昨夜|上次|之前|以前|那次|去年|前年|记得|曾经|原话|当时|听说|谁.*告诉|梦|\b(last night|last time|previous|remember|used to|dream\w*|heard|exact words)\b", RegexOptions.IgnoreCase);
    public static bool WantsQuote(string query) => Regex.IsMatch(query,
        @"原话|原句|具体.*说|怎么说的|说了什么|\b(exact words|quote|what did .* say)\b", RegexOptions.IgnoreCase);
    public static bool NeedsOriginalEvidence(string query) => WantsQuote(query) || ExplicitHistory(query)
        && Regex.IsMatch(query, @"具体|细节|什么礼物|送.*什么|哪个地点|谁.*(?:说|告诉)|\b(details|which (?:gift|item|place)|what gift|who told)\b", RegexOptions.IgnoreCase);
    public static bool DreamQuery(string query) => Regex.IsMatch(query, @"梦|昨晚|昨夜|\b(dream\w*|last night)\b", RegexOptions.IgnoreCase);
    public static bool ShouldSearch(NpcConversationMemory memory, string query)
    {
        if (ConversationMemoryPolicy.IsTopicRefusal(query)) return false;
        if (ExplicitHistory(query)) return true;
        string plain = Regex.Replace(query, @"宝宝|宝贝|亲爱的|baby|darling|sweetheart", "", RegexOptions.IgnoreCase).Trim();
        if (plain.Length < 6 || Regex.IsMatch(plain, @"^(再|然后|那我|就要|多讲|继续|为什么|为什么呢|then|go on|tell me more|why\b)", RegexOptions.IgnoreCase)) return false;
        return memory.AutomaticRecallCount < AutomaticRecallsPerSegment
            && Regex.IsMatch(plain, @"[?？]|什么|哪|谁|是否|\b(what|where|who|which)\b", RegexOptions.IgnoreCase);
    }

    public static ContextMemoryResult Select(NpcConversationMemory memory, string query, IEnumerable<ContextMemoryCandidate> candidates)
    {
        var all = candidates.ToArray();
        ContextMemoryResult Empty(int duplicate = 0) => new("", Array.Empty<string>(), 0, all.Length, duplicate);
        if (!ShouldSearch(memory, query)) return Empty();
        bool dream = DreamQuery(query), explicitHistory = ExplicitHistory(query);
        var ranked = all.Where(c => !string.IsNullOrWhiteSpace(c.Text)
                && (!dream || c.Source is "dream" or "dream_only" or "shared_dream"))
            .Select(c => new { Candidate = c, Score = ContextMemoryCompiler.Relevance(query, Readable(c.Text)) })
            .Where(x => x.Score >= (explicitHistory ? 1 : 3)
                || dream && x.Candidate.AllowUnprompted)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Candidate.Priority).ThenBy(x => x.Candidate.Id, StringComparer.Ordinal);
        var best = ranked.FirstOrDefault()?.Candidate;
        if (best is null) return Empty();
        if (!NeedsOriginalEvidence(query) && best.EventIds is { Count: > 0 }
            && memory.Messages.Concat(memory.SegmentBridge).Any(m => best.EventIds.Contains(m.DedupeKey, StringComparer.Ordinal)
                || best.EventIds.Contains(MemoryEventArchive.EventId(m), StringComparer.Ordinal))) return Empty(1);
        // Do not replace an already supplied best match with successively weaker old fragments.
        var covered = memory.Messages.Select(m => m.Content).Concat(memory.SegmentBridge.Select(m => m.Content))
            .Append(memory.SegmentMemory).Append(memory.SegmentSummary);
        if (covered.Any(text => Covers(text, best.Text))) return Empty(1);
        string rendered = Readable("[" + best.Source + "] " + best.Text);
        // Explicit questions may need a full event/quotation; never shorten away a refusal or outcome.
        if (ContextMemoryCompiler.EstimateTokens(rendered) > (explicitHistory ? 2000 : 600)) return Empty();
        return new(rendered, new[] { best.Id }, ContextMemoryCompiler.EstimateTokens(rendered), all.Length, 0);
    }

    public static bool Covers(string existing, string candidate)
    {
        static string Normalize(string text) => Regex.Replace(Readable(text), @"\[[^\]\r\n]*\]|【[^】]*】|[\s\p{P}\p{S}]", "").ToLowerInvariant();
        string a = Normalize(existing), b = Normalize(candidate);
        return b.Length >= 8 && a.Contains(b, StringComparison.Ordinal);
    }

    /// <summary>Only memory presentation is cleaned; tool candidate keys and execution receipts are untouched.</summary>
    public static string Readable(string? text)
    {
        string clean = text ?? "";
        clean = Regex.Replace(clean, @"\[(?<kind>[a-zA-Z_]+);id=[^\]\r\n]+\]", m =>
        {
            string label = Label(m.Groups["kind"].Value);
            var status = Regex.Match(m.Value, @";status=([^;\]]+)");
            return status.Success ? label + (L.IsChinese ? "（状态：" : " (status: ") + status.Groups[1].Value + (L.IsChinese ? "）" : ")") : label;
        });
        clean = Regex.Replace(clean, @"\[recent_(?:known|evidence);(?<kind>[^;\]]+);[^\]\r\n]+\]", m => Label(m.Groups["kind"].Value));
        clean = Regex.Replace(clean, @"\[(?<kind>dream_only|derived_dialogue|historical_quote|historical_reference|dialogue|rumor|dream|shared_dream|heard|shared|observed_event)\]", m => Label(m.Groups["kind"].Value));
        return clean.Trim();
    }
    private static string Label(string source)
    {
        bool zh = L.IsChinese;
        return source switch
        {
            "dream_only" or "dream" or "shared_dream" => zh ? "【梦境经历】" : "[Dream experience]",
            "rumor" or "heard" => zh ? "【听说】" : "[Heard from others]",
            "shared" => zh ? "【已转述】" : "[Already shared]",
            "observed_event" => zh ? "【已发生事件】" : "[Observed event]",
            _ => zh ? "【历史资料】" : "[Past context]",
        };
    }
}
