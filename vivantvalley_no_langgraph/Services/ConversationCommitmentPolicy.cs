using System.Text.RegularExpressions;

namespace VivantValley.Services;

/// <summary>Persisted local lifecycle of commitments; an empty AI delta is not cancellation.</summary>
public static class ConversationCommitmentPolicy
{
    public const int MaximumEntries = 12;
    private static readonly string[][] Subjects =
    {
        new[] { "mining", "矿洞", "矿井", "下矿", "mine", "mining" },
        new[] { "fishing", "钓鱼", "垂钓", "fishing" },
        new[] { "beach", "海滩", "海边", "beach" },
        new[] { "gift", "礼物", "送礼", "gift" },
        new[] { "farm", "农场", "farm" },
    };

    public static string? GetResolution(string? text)
    {
        string value = text ?? string.Empty;
        if (Regex.IsMatch(value, @"(?<!不要)(?<!没有)(?<!没)(?<!未)(?<!别)取消|(?<!没有)(?<!没)(?<!未)拒绝|不再需要|不用再(?:去|等|帮)|不去了|不去(?:了)?|作废|放弃|(?<!not )(?<!don't )\bcancel(?:led|ed)?\b|(?<!not )\bdeclin(?:e|ed)\b|(?<!not )\brefus(?:e|ed)\b|no longer|don't need", RegexOptions.IgnoreCase))
            return "cancelled";
        if (Regex.IsMatch(value, @"已(?:经)?完成|(?<!没)(?<!没有)(?<!未)完成了|履行了|已经解决|已兑现|已经去过|(?<!not[ _])\bcompleted\b|(?<!not[ _])\bresolved\b|(?<!not[ _])\bfulfilled\b", RegexOptions.IgnoreCase))
            return "resolved";
        return null;
    }

    public static void Apply(NpcSocialState state, IEnumerable<string>? proposed,
        string playerText, string summaryPatch, int day, long turn)
    {
        if (day < state.CommitmentsUpdatedDay || (day == state.CommitmentsUpdatedDay && turn < state.CommitmentsUpdatedTurn))
            return;
        state.Commitments ??= new List<ConversationCommitment>();
        if (state.CommitmentsUpdatedDay < 0 && state.Commitments.Count == 0)
        {
            ConversationSignal? latest = state.GetLatestSignal();
            if (latest is not null)
                foreach (string text in latest.OpenLoops.Where(ConversationMemoryPolicy.IsExplicitOpenLoop))
                    state.Commitments.Add(new ConversationCommitment { Key = Key(text), Text = text, DueDay = DueDay(text, latest.Day),
                        CreatedDay = latest.Day, UpdatedDay = latest.Day, UpdatedTurn = latest.ConversationTurn });
        }
        string[] deltas = (proposed ?? Array.Empty<string>()).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        foreach (ConversationCommitment item in state.Commitments.Where(item => item.Status == "active" && IsExpired(item, day)))
            item.Status = "expired";
        string changes = playerText + "\n" + summaryPatch;
        // Handle each clause independently so cancelling mining doesn't also
        // cancel a new fishing invitation in the next clause.
        foreach (string clause in changes.Split(new[] { '\n', '。', '；', ';', '，', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string? resolution = GetResolution(clause);
            bool suppress = ConversationMemoryPolicy.IsTopicRefusal(clause);
            if (resolution is null && !suppress)
                continue;
            ConversationCommitment[] active = state.Commitments.Where(item => item.Status == "active").ToArray();
            foreach (ConversationCommitment item in active)
            {
                bool explicitAll = clause.Contains("全部", StringComparison.Ordinal) || clause.Contains("所有约定", StringComparison.Ordinal)
                                   || clause.Contains("all plans", StringComparison.OrdinalIgnoreCase);
                bool unambiguousReference = active.Length == 1 && Regex.IsMatch(clause,
                    @"(?:取消|作废|别再提|不要再提)(?:了|这个|那个|上次的|之前的|我们的|\s)*约定|cancel (?:that|the) promise", RegexOptions.IgnoreCase);
                bool genericShift = IsGenericShift(clause);
                if (!explicitAll && !unambiguousReference && !genericShift && !SharesSubject(clause, item.Text))
                    continue;
                if (resolution is not null) item.Status = resolution;
                if (suppress) item.SuppressFollowUp = true;
                item.UpdatedDay = day;
                item.UpdatedTurn = turn;
            }
        }

        foreach (string text in deltas.Where(ConversationMemoryPolicy.IsExplicitOpenLoop))
        {
            // A model cannot reopen a topic the player just cancelled or asked
            // us to stop pursuing. A later explicit new promise can reopen it.
            bool blockedNow = playerText.Split(new[] { '\n', '。', '；', ';', '，', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(clause => (ConversationMemoryPolicy.IsTopicRefusal(clause) || GetResolution(clause) is not null)
                    && (SharesSubject(clause, text) || IsGenericShift(clause) || clause.Contains("全部", StringComparison.Ordinal)
                        || clause.Contains("所有约定", StringComparison.Ordinal)));
            if (blockedNow) continue;
            string key = Key(text);
            ConversationCommitment? existing = state.Commitments.LastOrDefault(item => item.Key == key);
            bool explicitRenewal = playerText.Split(new[] { '\n', '。', '；', ';', '，', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(clause => ConversationMemoryPolicy.IsExplicitOpenLoop(clause) && !ConversationMemoryPolicy.IsTopicRefusal(clause)
                    && Regex.IsMatch(clause, @"约好|我(?:们)?(?:再)?(?:答应|承诺|计划)|重新约|(?:明天|后天|下次|下周).*(?:一起|我们|去)|let's|i promise|we agreed", RegexOptions.IgnoreCase)
                    && SharesSubject(clause, text));
            // Also respect closed/suppressed entries whose wording the model
            // paraphrased. Subject similarity is only a veto, never a merge.
            if (!explicitRenewal && state.Commitments.Any(item => (item.Status != "active" || item.SuppressFollowUp)
                && SharesSubject(item.Text, text))) continue;
            if (existing is not null && existing.Status != "active" && !explicitRenewal)
                continue;
            if (existing is null)
            {
                existing = new ConversationCommitment { Key = key, CreatedDay = day, DueDay = DueDay(text, day) };
                state.Commitments.Add(existing);
            }
            existing.Text = Limit(text, 96);
            existing.Status = "active";
            existing.UpdatedDay = day;
            existing.UpdatedTurn = turn;
            if (explicitRenewal)
            {
                existing.SuppressFollowUp = false;
                existing.CreatedDay = day;
                existing.DueDay = DueDay(text, day);
            }
        }
        state.CommitmentsUpdatedDay = day;
        state.CommitmentsUpdatedTurn = turn;
        Normalize(state);
    }

    public static void Normalize(NpcSocialState state)
    {
        state.Commitments = (state.Commitments ?? new List<ConversationCommitment>())
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Text))
            .Select(item =>
            {
                item.Text = Limit(item.Text, 96);
                item.Key = Key(item.Text);
                if (item.Status is not ("active" or "resolved" or "cancelled" or "expired")) item.Status = "expired";
                return item;
            })
            .OrderByDescending(item => item.UpdatedDay).ThenByDescending(item => item.UpdatedTurn)
            .GroupBy(item => item.Key, StringComparer.Ordinal).Select(group => group.First())
            .Take(MaximumEntries).OrderBy(item => item.UpdatedDay).ThenBy(item => item.UpdatedTurn).ToList();
    }

    public static IReadOnlyList<string> Active(NpcSocialState state, int day, bool forFollowUp = true)
        => (state.Commitments ?? new List<ConversationCommitment>())
            .Where(item => item.Status == "active" && (!forFollowUp || !item.SuppressFollowUp)
                           && day >= item.CreatedDay && !IsExpired(item, day))
            .OrderByDescending(item => item.UpdatedDay).ThenByDescending(item => item.UpdatedTurn)
            .Take(ConversationSignal.MaxOpenLoops).Select(item => item.Text).ToArray();

    public static bool SharesSubject(string first, string second)
    {
        foreach (string[] group in Subjects)
            if (group.Skip(1).Any(word => first.Contains(word, StringComparison.OrdinalIgnoreCase))
                && group.Skip(1).Any(word => second.Contains(word, StringComparison.OrdinalIgnoreCase))) return true;
        string a = Key(first), b = Key(second);
        return a.Length >= 4 && b.Length >= 4 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }

    public static string PrepareRecall(string summary, NpcSocialState state, int day)
    {
        ConversationCommitment[] entries = (state.Commitments ?? new List<ConversationCommitment>()).ToArray();
        var lines = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !ConversationMemoryPolicy.IsExplicitOpenLoop(line)
                || !entries.Any(item => SharesSubject(item.Text, line))).ToList();
        foreach (ConversationCommitment item in entries.Where(item => !item.SuppressFollowUp))
        {
            string status = item.Status switch { "active" => IsExpired(item, day) ? "已过期" : "待履行", "resolved" => "已完成", "cancelled" => "已取消", _ => "已过期" };
            lines.Add($"[第{item.CreatedDay}天记录] 约定{status}：{item.Text}");
        }
        return string.Join('\n', lines);
    }

    private static string Key(string value)
        => Regex.Replace(value.ToLowerInvariant(), @"玩家|npc|约好|约定|承诺|答应|已经|[\s\p{P}\p{S}]", "");
    private static bool IsGenericShift(string value)
        => Regex.IsMatch(value.Trim(), @"^(?:换个话题|换一个话题|说点别的|change the subject|new topic|move on)[吧了。.!！\s]*$", RegexOptions.IgnoreCase);
    private static bool IsExpired(ConversationCommitment item, int day)
        => day > (item.DueDay.HasValue ? item.DueDay.Value + 1 : item.CreatedDay + 14);
    private static int? DueDay(string text, int day)
    {
        var explicitDay = Regex.Match(text, @"第\s*(\d+)\s*天");
        if (explicitDay.Success && int.TryParse(explicitDay.Groups[1].Value, out int target) && target >= day) return target;
        if (text.Contains("后天", StringComparison.Ordinal)) return day + 2;
        if (text.Contains("明天", StringComparison.Ordinal) || text.Contains("tomorrow", StringComparison.OrdinalIgnoreCase)) return day + 1;
        if (text.Contains("下周", StringComparison.Ordinal) || text.Contains("next week", StringComparison.OrdinalIgnoreCase)) return day + 7;
        return null;
    }
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
