using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VivantValley.Services;

public static class DreamPolicy
{
    public const int MaximumTurns = 24;
    public const string Scene = "collage";
    public static double Probability(int hearts) => Math.Clamp(hearts / 50d, 0d, 1d);
    private static double Roll(string key)
        => BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(key))) / 4294967296d;
    public static string RollVisitor(DreamPlayerState state, string seed, IEnumerable<DreamCandidate> candidates)
    {
        if (state.VisitorRolled) return state.Visitor;
        state.VisitorRolled = true;
        // Order-independent, one draw per NPC; a separate priority uniformly breaks ties.
        state.Visitor = candidates.GroupBy(n => n.Name).Select(g => g.First())
            .Where(n => state.TalkedToday.Contains(n.Name) && Roll($"{seed}:{state.Day}:chance:{n.Name}") < Probability(n.Hearts))
            .OrderBy(n => Roll($"{seed}:{state.Day}:winner:{n.Name}"))
            .ThenBy(n => n.Name, StringComparer.Ordinal).Select(n => n.Name).FirstOrDefault() ?? "";
        return state.Visitor;
    }
    // Claim before opening UI so cancellation/re-entry can never replay a visitor.
    public static string TakeAutomaticVisitor(DreamPlayerState state, string seed, IEnumerable<DreamCandidate> candidates)
    {
        if (state.NightConsumed || state.VisitorHandled) return "";
        string visitor = RollVisitor(state,seed,candidates);
        state.VisitorHandled = true;
        return visitor;
    }
    public static bool ReturnToChoice(DreamEpisode? episode)
        => episode?.Direction == "npc_visits";
    public static bool IsValidReply(DreamReply? reply)
        => reply is not null && !string.IsNullOrWhiteSpace(reply.Text)
           && reply.Text.Length <= 600 && !reply.Text.Any(c => c is '\0' or '$' or '#' or '{' or '}' or '[' or ']')
           && !Regex.IsMatch(reply.Text, @"\b(give_gift|move_to|invite_mine_guard|invite_fishing_companion)\b", RegexOptions.IgnoreCase);

    public static string Memory(DreamEpisode episode)
    {
        string prefix = L.IsChinese
            ? $"[梦境 / DREAM ONLY，第{episode.Day}日夜，{(episode.Direction == "player_visits" ? "玩家进入NPC梦境" : "NPC进入玩家梦境")}，{L.T("dream.scene." + episode.Scene)}；梦中内容不证明现实行动、物品或任务结果]"
            : $"[DREAM ONLY, night {episode.Day}, {episode.Direction}, {L.T("dream.scene." + episode.Scene)}; dream content is not evidence of real actions, items or outcomes]";
        return prefix + "\n" + string.Join("\n", episode.Lines.TakeLast(6).Select(line =>
            $"[DREAM quotation / 梦中原话] {(line.Role == "user" ? "player" : episode.Npc)}: {WeeklyStoryPlan.LimitText(line.Text,240)}"));
    }
    public static string Recall(DreamPlayerState state, string npc, int day, string query, string? excludeEpisodeId = null)
    {
        var candidates = state.Episodes.Where(e => e.Npc == npc && e.Id != excludeEpisodeId
            && e.Lines.Any(l => l.Role == "assistant") && e.Day <= day);
        // A dream is available on the first following conversation. Older dreams are
        // recalled only on a dream reference or overlapping topic, never every turn.
        var recent = candidates.Reverse().OrderByDescending(e => e.Day).FirstOrDefault();
        if (recent is null) return "";
        var selected = recent.Day == day - 1 && !recent.MorningMentionUsed ? recent
            : candidates.Reverse().OrderByDescending(e => e.Day).FirstOrDefault(e => Relevant(e, query));
        if (selected is null) return "";
        string cue = selected.Day == day - 1 && !selected.MorningMentionUsed
            ? (L.IsChinese ? "可以自然提及昨夜共同的梦，不要每轮重复。" : "You may naturally mention last night's shared dream, without repeating it each turn.") : "";
        return "\n" + cue + "\n" + Memory(selected);
    }

    public static IEnumerable<ContextMemoryCandidate> RecallCandidates(DreamPlayerState state, string npc, int day,
        string query, string? excludeEpisodeId = null)
    {
        // Keep each quotation attached to its provenance. In particular the active
        // episode must never be recalled as a second, supposedly older memory.
        foreach (var episode in state.Episodes.Where(e => e.Npc == npc && e.Id != excludeEpisodeId && e.Day <= day
                     && e.Lines.Any(l => l.Role == "assistant")
                     && Relevant(e, query))
                     .OrderByDescending(e => ContextMemoryCompiler.Relevance(query, string.Join(" ", e.Lines.Select(l => l.Text))))
                     .ThenByDescending(e => e.Day).Take(8))
        {
            foreach (var entry in episode.Lines.Select((line, index) => (line, index))
                         .OrderByDescending(entry => ContextMemoryCompiler.Relevance(query, entry.line.Text))
                         .ThenByDescending(entry => entry.index).Take(4))
                yield return new ContextMemoryCandidate($"{episode.Id}:{entry.index}",
                    $"DREAM ONLY, night {episode.Day}; {(entry.line.Role == "user" ? "player" : npc)}: {entry.line.Text}",
                    "dream");
        }
    }
    public static IEnumerable<ContextMemoryCandidate> RecallEpisodes(DreamPlayerState state, string npc, int day, string query)
    {
        bool lastNight = Regex.IsMatch(query, @"昨晚|昨夜|\blast night\b", RegexOptions.IgnoreCase);
        var eligible = state.Episodes.Where(e => e.Npc == npc && e.Day <= day && e.Status != "active"
            && e.Lines.Any(l => l.Role == "assistant") && (!lastNight || e.Day == day - 1));
        var episode = eligible.Select(e => new { Episode = e, Score = ContextMemoryCompiler.Relevance(query,
                e.Summary + " " + string.Join(" ", e.Lines.Select(l => l.Text))) })
            .OrderByDescending(e => e.Score).ThenByDescending(e => e.Episode.Day).FirstOrDefault()?.Episode;
        if (episode is null) yield break;
        string text = episode.Summary;
        if (ConversationRecallPolicy.NeedsOriginalEvidence(query) || string.IsNullOrWhiteSpace(text))
        {
            var pairs = episode.Lines.Select((line, i) => (line, i))
                .Where(x => x.line.Role == "user" && x.i + 1 < episode.Lines.Count && episode.Lines[x.i + 1].Role == "assistant")
                .Select(x => $"player: {x.line.Text} / {npc}: {episode.Lines[x.i + 1].Text}")
                .OrderByDescending(pair => ContextMemoryCompiler.Relevance(query, pair));
            text = pairs.FirstOrDefault() ?? "";
        }
        if (string.IsNullOrWhiteSpace(text)) yield break;
        yield return new("dream-episode-" + episode.Id, $"[Night {episode.Day}] {text}", "dream",
            Priority: episode.Day, AllowUnprompted: ConversationRecallPolicy.DreamQuery(query), EventIds: new[] { episode.Id });
    }

    private static bool Relevant(DreamEpisode e, string query)
    {
        if (Regex.IsMatch(query, @"梦|托梦|\bdream\w*\b", RegexOptions.IgnoreCase)) return true;
        string history = string.Join(" ", e.Lines.Select(l => l.Text));
        foreach (Match token in Regex.Matches(query, @"[a-zA-Z]{4,}|[\p{IsCJKUnifiedIdeographs}]{2,}"))
        {
            if (Regex.IsMatch(token.Value, @"^[a-zA-Z]"))
            { if (history.Contains(token.Value,StringComparison.OrdinalIgnoreCase)) return true; }
            else for (int i = 0; i + 3 <= token.Length; i++)
                if (history.Contains(token.Value.Substring(i,3),StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
