using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VivantValley.Services;

/// <summary>Directed, editable game-play contacts. Lists do not claim canonical friendships.</summary>
public sealed class TownEchoNetwork
{
    public Dictionary<string, List<string>> Contacts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static TownEchoNetwork Defaults()
    {
        using var stream = typeof(TownEchoNetwork).Assembly.GetManifestResourceStream("VivantValley.TownEchoContacts.json")
            ?? throw new InvalidOperationException("Missing town-echo contact defaults.");
        var network = JsonSerializer.Deserialize<TownEchoNetwork>(stream)!;
        network.Validate();
        return network;
    }
    public void Validate()
    {
        if (Contacts is null || Contacts.Count == 0) throw new InvalidOperationException("Contacts cannot be empty.");
        Contacts = new Dictionary<string, List<string>>(Contacts, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Contacts)
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || pair.Value.Count != 5
                || pair.Value.Any(n => string.IsNullOrWhiteSpace(n) || n.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
                || pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 5)
                throw new InvalidOperationException($"Town echo contacts for {pair.Key} must have five distinct other NPCs.");
    }
    public IReadOnlyList<string> For(string npc)
        => Contacts.TryGetValue(npc, out var contacts) ? contacts : Array.Empty<string>();
    public void AddFallback(string npc)
    {
        if (string.IsNullOrWhiteSpace(npc) || Contacts.ContainsKey(npc)) return;
        Contacts[npc] = new[] { "Robin", "Gus", "Lewis", "Emily", "Willy", "Caroline" }
            .Where(n => !n.Equals(npc, StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
    }
}

/// <summary>Two-hop daily propagation of game-confirmed events. No simulated meetings or model calls.</summary>
public sealed class TownEchoStore
{
    public List<TownEchoEvent> Events { get; set; } = new();
    public void Normalize()
    {
        Events ??= new();
        Events.RemoveAll(e => e is null || string.IsNullOrWhiteSpace(e.PlayerId)
            || string.IsNullOrWhiteSpace(e.Source) || !TownEchoPolicy.Kinds.Contains(e.Kind));
        foreach (var e in Events)
        {
            if (string.IsNullOrWhiteSpace(e.Id)) e.Id = Guid.NewGuid().ToString("N");
            e.EventKey = string.IsNullOrWhiteSpace(e.EventKey) ? $"observed:{e.Source}:{e.Kind}:{e.Day}" : e.EventKey;
            e.Detail = TownEchoPolicy.Clean(e.Detail, 240);
            e.Place = TownEchoPolicy.Clean(e.Place, 100);
            e.Quantity = Math.Clamp(e.Quantity, 1, 999);
            e.Participants ??= new();
            e.Participants = new[] { e.Source }.Concat(e.Participants.Where(n => !string.IsNullOrWhiteSpace(n)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            e.Deliveries ??= new();
            e.WithheldRecipients ??= new();
            e.Deliveries.RemoveAll(d => d is null || string.IsNullOrWhiteSpace(d.Recipient)
                || e.Participants.Contains(d.Recipient, StringComparer.OrdinalIgnoreCase));
            foreach (var d in e.Deliveries)
            {
                // V1 transfers had no sender/depth. Preserve the existing source and receipt date.
                if (string.IsNullOrWhiteSpace(d.Sender)) { d.Sender = e.Source; d.Hop = 1; }
            }
            e.Deliveries = e.Deliveries.GroupBy(d => d.Recipient, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
            e.Deliveries.RemoveAll(d => d.Hop is not (1 or 2)
                || (d.Hop == 1 && d.Sender != e.Source)
                || (d.Hop == 2 && !e.Deliveries.Any(parent => parent.Hop == 1 && parent.Recipient == d.Sender)));
        }
        Events = Events.GroupBy(e => (e.PlayerId, e.EventKey)).Select(g => g.First())
            .OrderByDescending(e => e.Day).Take(128).ToList();
    }
    public bool Prune(int day) => Events.RemoveAll(e => e.Day > day || day - e.Day > TownEchoPolicy.RetentionDays) > 0;

    public bool Observe(string playerId, string source, string kind, int day)
        => Record(new TownEchoEvent { PlayerId = playerId, Source = source, Kind = kind, Day = day,
            EventKey = $"observed:{source}:{kind}:{day}" });

    public bool Record(TownEchoEvent item)
    {
        if (string.IsNullOrWhiteSpace(item.PlayerId) || string.IsNullOrWhiteSpace(item.Source)
            || string.IsNullOrWhiteSpace(item.EventKey) || item.Day < 0 || !TownEchoPolicy.Kinds.Contains(item.Kind)
            || Events.Any(e => e.PlayerId == item.PlayerId && e.EventKey == item.EventKey)) return false;
        Events.Add(item);
        Normalize();
        return true;
    }

    public bool Advance(int day, TownEchoNetwork network)
    {
        bool changed = Prune(day);
        foreach (var e in Events.OrderBy(e => e.Day).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            // Process existing first-hop receipts first. Newly received news cannot forward on this day.
            foreach (var parent in e.Deliveries.Where(d => d.Hop == 1 && !d.Forwarded && day > d.Day).ToArray())
            {
                Send(e, parent.Recipient, 2, 2, day, network);
                parent.Forwarded = true;
                changed = true;
            }
            if (!e.FirstWaveComplete && day > e.Day)
            {
                Send(e, e.Source, 1, 3, day, network);
                e.FirstWaveComplete = true;
                changed = true;
            }
        }
        return changed;
    }
    private static void Send(TownEchoEvent e, string sender, int hop, int limit, int day, TownEchoNetwork network)
    {
        int remaining = limit - e.Deliveries.Count(d => d.Sender == sender && d.Hop == hop);
        foreach (string recipient in network.For(sender)
                     .OrderBy(n => StableRank(e.PlayerId + ":" + e.Id + ":" + sender + ":" + n), StringComparer.Ordinal))
        {
            if (remaining <= 0) break;
            if (e.Participants.Contains(recipient, StringComparer.OrdinalIgnoreCase)
                || e.WithheldRecipients.Contains(recipient, StringComparer.OrdinalIgnoreCase)
                || e.Deliveries.Any(d => d.Recipient.Equals(recipient, StringComparison.OrdinalIgnoreCase))) continue;
            e.Deliveries.Add(new() { Sender = sender, Recipient = recipient, Hop = hop, Day = day });
            remaining--;
        }
    }
    private static string StableRank(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public TownEchoRecall? Recall(string playerId, string npc, string input, int day, bool chinese, Func<string, string> displayName)
    {
        var choices = Events.Where(e => e.PlayerId == playerId && e.Day <= day && day - e.Day <= TownEchoPolicy.RetentionDays)
            .Select(e => new { Event = e, Incoming = e.Deliveries.FirstOrDefault(d => d.Recipient == npc && d.Day <= day),
                Outgoing = e.Deliveries.Where(d => d.Sender == npc && d.Day <= day).ToArray() })
            .Where(c => c.Incoming is not null || c.Outgoing.Length > 0)
            .Select(c => new { c.Event, c.Incoming, c.Outgoing, Relevant = TownEchoPolicy.IsRelevant(input, c.Event.Kind,
                c.Event.Participants.Concat(c.Outgoing.Select(d => d.Recipient))
                    .Concat(c.Incoming is null ? Array.Empty<string>() : new[] { c.Incoming.Sender })
                    .SelectMany(n => new[] { n, displayName(n) })) })
            .Where(c => c.Relevant || c.Incoming is { Presented: false })
            .OrderByDescending(c => c.Relevant).ThenByDescending(c => c.Incoming?.Day ?? c.Event.Day)
            .ThenBy(c => c.Event.Id, StringComparer.Ordinal).FirstOrDefault();
        if (choices is null) return null;
        var item = choices.Event;
        string Name(string n) => TownEchoPolicy.Clean(displayName(n), 32);
        string fact = TownEchoPolicy.Describe(item, chinese, Name);
        string recipients = string.Join(chinese ? "、" : ", ", choices.Outgoing.Select(d => Name(d.Recipient)));
        string text;
        if (choices.Incoming is { } incoming)
        {
            string chain = incoming.Hop == 1 ? Name(item.Source) : Name(item.Source) + " → " + Name(incoming.Sender);
            text = chinese
                ? $"【听说，未亲历】第{incoming.Day}天从{Name(incoming.Sender)}听说；来源链：{chain} → 你。第{item.Day}天的事实：{fact}。"
                : $"[Heard, not witnessed] Day {incoming.Day}, heard from {Name(incoming.Sender)}; source: {chain} → you. Day {item.Day} fact: {fact}. ";
        }
        else text = chinese ? $"【亲历后转述】第{item.Day}天的事实：{fact}。" : $"[Experienced then shared] Day {item.Day}: {fact}. ";
        if (recipients.Length > 0) text += chinese ? $"你已转述给：{recipients}。" : $"You told: {recipients}. ";
        text += chinese ? "仅知上述事实，不编造其他结果或转述去向；历史不代表当前状态。先回应玩家，可自然提起。"
            : "Only these facts are known; invent no results or further recipients. History is not current status. Address the player first; mention naturally if appropriate.";
        return new TownEchoRecall(item.Id, choices.Incoming?.Recipient ?? "", text);
    }
    public bool MarkPresented(string playerId, string npc, string eventId)
    {
        var d = Events.FirstOrDefault(e => e.PlayerId == playerId && e.Id == eventId)?.Deliveries.FirstOrDefault(d => d.Recipient == npc);
        if (d is null || d.Presented) return false;
        d.Presented = true; return true;
    }
    public bool ContainsNpc(string playerId, string npc)
        => Events.Any(e => e.PlayerId == playerId && (e.Participants.Contains(npc, StringComparer.OrdinalIgnoreCase)
            || e.Deliveries.Any(d => d.Recipient.Equals(npc, StringComparison.OrdinalIgnoreCase))));
    public void Forget(string playerId, string? npc)
    {
        Events.RemoveAll(e => e.PlayerId == playerId && (npc is null || e.Participants.Contains(npc, StringComparer.OrdinalIgnoreCase)));
        if (npc is null) return;
        foreach (var e in Events.Where(e => e.PlayerId == playerId))
        {
            // Also remove descendants, so no remaining listener cites a source that no longer knows the event.
            var removed = e.Deliveries.Where(d => d.Recipient.Equals(npc, StringComparison.OrdinalIgnoreCase)
                || d.Sender.Equals(npc, StringComparison.OrdinalIgnoreCase)).Select(d => d.Recipient).Append(npc).ToArray();
            e.WithheldRecipients = e.WithheldRecipients.Concat(removed).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            e.Deliveries.RemoveAll(d => removed.Contains(d.Recipient, StringComparer.OrdinalIgnoreCase));
        }
    }
}
public sealed class TownEchoEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string EventKey { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Day { get; set; }
    public string Detail { get; set; } = "";
    public string Place { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public string Outcome { get; set; } = "";
    public List<string> Participants { get; set; } = new();
    public bool FirstWaveComplete { get; set; }
    public List<TownEchoDelivery> Deliveries { get; set; } = new();
    public List<string> WithheldRecipients { get; set; } = new();
}
public sealed class TownEchoDelivery
{
    public string Sender { get; set; } = "";
    public string Recipient { get; set; } = "";
    public int Hop { get; set; } = 1;
    public int Day { get; set; }
    public string Location { get; set; } = ""; // V1 save compatibility; new transfers do not fabricate a meeting location.
    public bool Presented { get; set; }
    public bool Forwarded { get; set; }
}
public sealed record TownEchoRecall(string EventId, string Recipient, string Text);
public static class TownEchoPolicy
{
    public const int RetentionDays = 14;
    public static readonly string[] Kinds = { "mine", "fishing", "gift_received", "gift_given", "gift_declined", "proactive", "story", "date", "date_interrupted" };
    public static string Clean(string? value, int max)
    {
        string text = (value ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
    public static bool IsRelevant(string input, string kind, IEnumerable<string> people)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        string[] words = kind switch
        {
            "mine" => new[] { "矿", "mine", "cave" }, "fishing" => new[] { "钓", "鱼", "fish" },
            "gift_given" or "gift_received" or "gift_declined" => new[] { "礼物", "送", "gift", "present" },
            "date" or "date_interrupted" => new[] { "约会", "date", "dating" }, _ => new[] { "活动", "剧情", "相遇", "见面", "event", "meet", "story" },
        };
        return words.Concat(new[] { "听说", "告诉", "转述", "heard", "told", "tell", "rumor" })
            .Any(w => input.Contains(w, StringComparison.OrdinalIgnoreCase))
            || people.Where(name => !string.IsNullOrWhiteSpace(name)).Any(name =>
                // Latin names must be mentioned, not embedded in an unrelated word (Sam/same, E/hello).
                name.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                    ? System.Text.RegularExpressions.Regex.IsMatch(input,
                        @"(?<![A-Za-z0-9_])" + System.Text.RegularExpressions.Regex.Escape(name) + @"(?![A-Za-z0-9_])",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                    : input.Contains(name, StringComparison.OrdinalIgnoreCase));
    }
    public static string Describe(TownEchoEvent e, bool zh, Func<string, string> name)
    {
        string source = name(e.Source), detail = Clean(e.Detail, 100), place = Clean(e.Place, 40);
        string cast = string.Join(zh ? "、" : ", ", e.Participants.Select(name));
        return e.Kind switch
        {
            "mine" => zh ? $"{source}与玩家一起进入过矿层" : $"{source} entered a mine floor with the player",
            "fishing" => zh ? $"钓鱼同行时，{source}与正在钓鱼的玩家同场" : $"{source} accompanied the player while the player fished",
            "gift_received" => zh ? $"玩家实际送给{source}：{detail} ×{e.Quantity}" : $"The player gave {source}: {detail} x{e.Quantity}",
            "gift_given" => zh ? $"{source}实际送给玩家：{detail} ×{e.Quantity}，玩家已收下" : $"{source} gave the player {detail} x{e.Quantity}; the player received it",
            "gift_declined" => zh ? $"{source}主动提出送{detail}给玩家，玩家婉拒，未交付" : $"{source} offered {detail}; the player declined and no gift was delivered",
            "date" => zh ? $"玩家与{source}在{place}的约会已结束；玩家实际选择：“{detail}”"
                : $"The player's date with {source} at {place} ended; actual player choice: ‘{detail}’",
            "date_interrupted" => zh ? $"玩家与{source}在{place}的约会已经开始，但中途结束，未完成；没有完成结果记录"
                : $"The player's date with {source} at {place} began but was interrupted before completion; no completed outcome is recorded",
            "story" => zh ? $"玩家与{cast}在{place}完成共同剧情活动；玩家实际选择：“{detail}”"
                : $"The player and {cast} completed a shared story event at {place}; actual player choice: ‘{detail}’",
            _ => zh ? $"{source}与玩家完成主动互动" + (detail.Length > 0 ? $"；玩家实际选择：“{detail}”" : "")
                : $"{source} completed a proactive interaction with the player" + (detail.Length > 0 ? $"; actual player choice: ‘{detail}’" : ""),
        };
    }
    public static TownEchoEvent? InterruptedDate(string playerId, WeeklyStoryPlan story, int day)
    {
        if (story.ActivityKind != "date" || story.Status != WeeklyStoryStatus.Running) return null;
        // Only already-read dialogue or delivered choices establish that the date actually took place.
        bool observed = story.Selections.Any(s => s.ResponseDelivered)
            || story.Beats.Take(story.CurrentBeatIndex).Any(b => b.Type == WeeklyStoryBeatTypes.Dialogue
                && (story.FormatVersion != 2 || WeeklyStoryBranchPolicy.Matches(b.When, WeeklyStoryBranchPolicy.State(story))));
        if (!observed) return null;
        return new TownEchoEvent { PlayerId = playerId, Source = story.Inviter, Kind = "date_interrupted", Day = day,
            EventKey = $"weekly-interrupted:{story.StoryId}:{story.AttemptCount}:{day}", Place = story.LocationDisplayName,
            Participants = story.Actors.Select(a => a.Name).ToList() };
    }
    public static TownEchoEvent? CompletedStory(string playerId, WeeklyStoryPlan story, WeeklyStoryChoiceOption? choice)
    {
        if (story.Status != WeeklyStoryStatus.Completed || choice is null || story.CompletedDay < 0
            || !WeeklyStoryPlaybackPolicy.CanComplete(story, choice, true)) return null;
        return new TownEchoEvent { PlayerId = playerId, Source = story.Inviter, Kind = story.ActivityKind == "date" ? "date" : "story",
            Day = story.CompletedDay, EventKey = "weekly:" + story.StoryId, Place = story.LocationDisplayName,
            Participants = story.Actors.Select(a => a.Name).ToList(), Detail = story.FormatVersion == 2
                ? string.Join(" / ", story.Selections.Where(s => s.ResponseDelivered).TakeLast(2).Select(s => Clean(s.Text, 45))) : choice.Text,
            Outcome = choice.Ending };
    }
}
