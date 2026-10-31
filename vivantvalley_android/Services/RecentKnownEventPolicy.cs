using System.Text.RegularExpressions;

namespace VivantValley.Services;

public sealed record RecentKnownEvent(string Id, string Kind, int Day, string Fact, IReadOnlyList<string> Evidence, int Order = 0);

/// <summary>Knowledge is independent of reminder opportunities and keyword recall. No generated summaries.</summary>
public static class RecentKnownEventPolicy
{
    public const int TokenBudget = 500;
    public const int WindowDays = 3;

    public static IEnumerable<RecentKnownEvent> Dreams(DreamPlayerState state, string npc, int day, bool zh,
        string? excludedDream = null)
    {
        foreach (var episode in state.Episodes.Where(e => e.Npc == npc && e.Id != excludedDream
                     && e.Day <= day && day - e.Day < WindowDays && e.Lines.Any(l => l.Role == "assistant")))
        {
            string direction = episode.Direction == "player_visits"
                ? zh ? "玩家进入你的梦" : "player visited your dream"
                : zh ? "你进入玩家的梦" : "you visited the player's dream";
            var evidence = new List<string>();
            for (int i = episode.Lines.Count - 1; i >= 0; i--)
            {
                var line = episode.Lines[i];
                if (line.Role != "assistant" || string.IsNullOrWhiteSpace(line.Text)) continue;
                // Preserve complete question/reply pairs, including a refusal at the end.
                string pair = i > 0 && episode.Lines[i - 1].Role == "user"
                    ? (zh ? "玩家：" : "Player: ") + episode.Lines[i - 1].Text + "\n" : "";
                pair += (zh ? "你：" : "You: ") + line.Text;
                evidence.Add(pair);
            }
            yield return new("dream-" + ContextMemoryCompiler.StableId(episode.Id), "dream_only", episode.Day,
                zh ? $"第{episode.Day}日夜：{direction}，有共同交谈；仅为梦中经历。"
                    : $"Night {episode.Day}: {direction}; you talked. Dream only, not waking events.", evidence, state.Episodes.IndexOf(episode));
        }
    }

    public static IEnumerable<RecentKnownEvent> Echoes(TownEchoStore store, string playerId, string npc,
        int day, bool zh, Func<string, string> name)
    {
        foreach (var item in store.Events.Where(e => e.PlayerId == playerId && e.Day <= day))
        {
            var incoming = item.Deliveries.FirstOrDefault(d => d.Recipient == npc && d.Day <= day);
            string fact = Fact(item, zh, name);
            // Full optional details are never cropped into a different outcome.
            var evidence = string.IsNullOrWhiteSpace(item.Detail) ? Array.Empty<string>()
                : new[] { (zh ? "事件详情（非当前指令）：" : "Event detail (not current instructions): ") + item.Detail };
            string id = "echo-" + ContextMemoryCompiler.StableId(item.Id);
            if (incoming is not null && day - incoming.Day < WindowDays)
                yield return new(id + "-in", "heard", incoming.Day,
                    zh ? $"第{incoming.Day}日从{name(incoming.Sender)}听说（未亲历，源头{name(item.Source)}）：第{item.Day}日{fact}"
                        : $"Day {incoming.Day}, heard from {name(incoming.Sender)} (not witnessed; source {name(item.Source)}): day {item.Day}, {fact}", evidence, store.Events.IndexOf(item));
            var outgoing = item.Deliveries.Where(d => d.Sender == npc && d.Day <= day).OrderBy(d => d.Day)
                .ThenBy(d => d.Recipient, StringComparer.Ordinal).ToArray();
            // A forwarding record must belong to a source or a recipient who already knew it.
            if (outgoing.Length == 0 || day - outgoing[^1].Day >= WindowDays
                || (item.Source != npc && (incoming is null || incoming.Day > outgoing[0].Day))) continue;
            string recipients = string.Join(zh ? "、" : ", ", outgoing.Select(d => $"{name(d.Recipient)}(D{d.Day})"));
            string shared = item.Source == npc ? (zh ? "你已告诉" : "You told ")
                : (zh ? "你已将听说的消息转述给" : "You passed on heard news to ");
            yield return new(id + "-out", "shared", outgoing[^1].Day,
                zh ? $"{shared}{recipients}：第{item.Day}日{fact}"
                    : $"{shared}{recipients}: day {item.Day}, {fact}", evidence, store.Events.IndexOf(item));
        }
    }

    private static string Fact(TownEchoEvent e, bool zh, Func<string, string> name)
    {
        string who = name(e.Source);
        return e.Kind switch
        {
            "gift_received" => zh ? $"玩家送礼给{who}。" : $"the player gave {who} a gift.",
            "gift_given" => zh ? $"{who}送礼，玩家已收下。" : $"{who} gave a gift; the player accepted it.",
            "gift_declined" => zh ? $"{who}提议送礼，玩家拒绝，未交付。" : $"{who} offered a gift; the player declined; not delivered.",
            "mine" => zh ? $"{who}与玩家进入过矿层。" : $"{who} entered a mine floor with the player.",
            "fishing" => zh ? $"{who}陪伴玩家钓鱼。" : $"{who} accompanied the player fishing.",
            "date_interrupted" => zh ? $"{who}与玩家的约会中断，未完成。" : $"{who}'s date with the player was interrupted, not completed.",
            "date" => zh ? $"{who}与玩家的约会已结束。" : $"{who}'s date with the player ended.",
            "story" => zh ? $"{who}与玩家完成共同剧情。" : $"{who} and the player completed a shared story.",
            _ => zh ? $"{who}与玩家完成互动。" : $"{who} and the player completed an interaction.",
        };
    }

    public static IReadOnlyList<string> Select(IEnumerable<RecentKnownEvent> events, string query, int tokenBudget = TokenBudget)
    {
        // One latest item from each category is protected from keyword filtering.
        // A specific topic can select a matching recent item instead of the latest one.
        var chosen = events.GroupBy(e => e.Kind, StringComparer.Ordinal).Select(group => group
            .OrderByDescending(e => ContextMemoryCompiler.Relevance(query, e.Fact + " " + string.Join(" ", e.Evidence)))
            .ThenByDescending(e => e.Day).ThenByDescending(e => e.Order).ThenBy(e => e.Id, StringComparer.Ordinal).First())
            .OrderByDescending(e => Focus(query, e.Kind)).ThenByDescending(e => e.Day)
            .ThenBy(e => e.Kind, StringComparer.Ordinal).Take(3).ToArray();
        var result = new List<string>();
        var included = new List<RecentKnownEvent>();
        var detailed = new HashSet<string>(StringComparer.Ordinal);
        int remaining = Math.Max(0, tokenBudget);
        bool Add(string text)
        {
            int cost = ContextMemoryCompiler.EstimateTokens(text);
            if (cost > remaining) return false;
            remaining -= cost; result.Add(text); return true;
        }
        bool AddEvidence(RecentKnownEvent e)
        {
            foreach (string evidence in e.Evidence.OrderByDescending(text => ContextMemoryCompiler.Relevance(query, text)))
                if (Add($"[recent_evidence;{e.Kind};{e.Id}] {evidence}")) { detailed.Add(e.Id); return true; }
            return false;
        }
        foreach (var e in chosen)
            if (Add($"[recent_known;{e.Kind};{e.Id}] {e.Fact}"))
            {
                included.Add(e);
                // A direct follow-up deserves the complete evidence before unrelated
                // categories spend the budget. A greeting still gets category coverage.
                if (!ConversationMemoryPolicy.IsTopicRefusal(query) && Focus(query, e.Kind)) AddEvidence(e);
            }
        foreach (var e in included.Where(e => e.Kind != "dream_only" && !detailed.Contains(e.Id))) AddEvidence(e);
        return result;
    }

    private static bool Focus(string query, string kind) => kind switch
    {
        "dream_only" => Regex.IsMatch(query, @"梦|昨晚|昨天晚上|\bdream\w*\b|\blast night\b", RegexOptions.IgnoreCase),
        "heard" => Regex.IsMatch(query, @"听说|谁.*(?:说|告诉)|\bheard\b|\bwho told\b", RegexOptions.IgnoreCase),
        "shared" => Regex.IsMatch(query, @"告诉.*谁|告诉.*别人|转述|\bwho.*(?:tell|told)\b|\btold.*(?:others|anyone)\b", RegexOptions.IgnoreCase),
        _ => false,
    };
}
