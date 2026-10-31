namespace VivantValley.Services;

/// <summary>Forward-only choices, explicit conditions, and bounded equivalent-state validation.</summary>
public static class WeeklyStoryBranchPolicy
{
    public static bool Matches(IReadOnlyDictionary<string, string> when, IReadOnlyDictionary<string, string> selections)
        => when.All(condition => selections.TryGetValue(condition.Key, out string? value) && value == condition.Value);

    public static Dictionary<string, string> State(WeeklyStoryPlan plan)
        => plan.Selections.ToDictionary(selection => selection.ChoiceId, selection => selection.OptionId, StringComparer.Ordinal);

    public static WeeklyStoryChoiceOption[] AvailableOptions(WeeklyStoryBeat beat, IReadOnlyDictionary<string, string> state,
        IEnumerable<string> declinedActors) => AvailableOptions(beat, state); // Legacy caller compatibility.

    public static WeeklyStoryChoiceOption[] AvailableOptions(WeeklyStoryBeat beat, IReadOnlyDictionary<string, string> state)
        => beat.Choices.Where(option => Matches(option.When, state)).ToArray();

    public static WeeklyStoryChoiceOption[] AvailableOptions(WeeklyStoryPlan plan, WeeklyStoryBeat beat)
        => AvailableOptions(beat, State(plan));

    public static void RecordSelection(WeeklyStoryPlan plan, WeeklyStoryBeat beat, WeeklyStoryChoiceOption option)
    {
        plan.Selections.Add(new WeeklyStoryChoiceRecord
        {
            BeatIndex = plan.CurrentBeatIndex, ChoiceId = beat.Id, OptionId = option.Id,
            Text = option.Text, ResponseActor = option.ResponseActor, ResponseText = option.ResponseText,
            Outcome = option.Outcome, Ending = option.Ending, Boundary = option.Boundary,
        });
    }

    public static bool TryValidate(IReadOnlyList<WeeklyStoryBeat> beats, out string failure)
    {
        failure = string.Empty;
        if (beats.Count == 0 || beats.Count > WeeklyStoryPlan.MaximumBeatCount)
            return Fail("分支剧情节点数量无效。", out failure);
        WeeklyStoryBeat[] choices = beats.Where(beat => beat.Type == "choice").ToArray();
        if (choices.Length == 0)
            return Fail("互动剧情至少需要一个选择节点。", out failure);
        if (beats[^1].Type != "end" || beats.Take(beats.Count - 1).Any(beat => beat.Type == "end") || beats[^1].When.Count != 0)
            return Fail("end必须是最后一个无条件节点；结局由最后一次选择决定。", out failure);

        var known = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var paths = new List<PathState> { new() };
        var reachedOptions = new HashSet<string>(StringComparer.Ordinal);
        for (int beatIndex = 0; beatIndex < beats.Count; beatIndex++)
        {
            WeeklyStoryBeat beat = beats[beatIndex];
            if (!ValidConditions(beat.When, known)) return Fail("when引用了不存在、尚未出现的选择或选项。", out failure);
            if (!paths.Any(path => Matches(beat.When, path.Selections))) return Fail("剧情包含无法到达的条件节点。", out failure);
            if (beat.Type != "choice") continue;
            if (string.IsNullOrWhiteSpace(beat.Id) || known.ContainsKey(beat.Id) || beat.When.Count != 0)
                return Fail("choice需要唯一id，选择节点本身不能带when；在选项或普通节点上设置条件。", out failure);
            if (!WeeklyStoryPlaybackPolicy.HasSupportedOptionCount(beat.Choices.Count, 2) || beat.Choices.Any(option => string.IsNullOrWhiteSpace(option.Id)
                || string.IsNullOrWhiteSpace(option.Text) || string.IsNullOrWhiteSpace(option.ResponseText))
                || beat.Choices.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != beat.Choices.Count)
                return Fail("每个选择至少有两个完整且ID唯一的选项，且不超过技术容量。", out failure);
            bool final = ReferenceEquals(beat, choices[^1]);
            foreach (WeeklyStoryChoiceOption option in beat.Choices)
            {
                if (!ValidConditions(option.When, known)) return Fail("选项when只能引用更早的选择。", out failure);
                if (option.Intimacy is not ("none" or "affection" or "kiss" or "private")) return Fail("未知intimacy。", out failure);
                if (final ? option.Ending is not ("closer" or "unresolved" or "apart") : option.Ending.Length != 0)
                    return Fail("只有最后一次选择的每个选项必须填写ending：closer、unresolved或apart。", out failure);
            }
            // Drop decisions no future condition reads. Independent choices must not cause exponential work.
            var futureKeys = beats.Skip(beatIndex + 1)
                .SelectMany(item => item.When.Keys.Concat(item.Choices.SelectMany(option => option.When.Keys)))
                .ToHashSet(StringComparer.Ordinal);
            var next = new List<PathState>();
            var stateKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (PathState path in paths)
            {
                WeeklyStoryChoiceOption[] available = AvailableOptions(beat, path.Selections);
                if (available.Length < 2)
                    return Fail("条件组合导致选择节点不足两个可用选项。", out failure);
                foreach (WeeklyStoryChoiceOption option in available)
                {
                    reachedOptions.Add(beat.Id + ":" + option.Id);
                    var branch = new PathState(path);
                    branch.Selections[beat.Id] = option.Id;
                    foreach (string key in branch.Selections.Keys.Where(key => !futureKeys.Contains(key)).ToArray())
                        branch.Selections.Remove(key);
                    string stateKey = System.Text.Json.JsonSerializer.Serialize(branch.Selections.OrderBy(pair => pair.Key, StringComparer.Ordinal));
                    if (stateKeys.Add(stateKey))
                    {
                        if (next.Count >= 4096) return Fail("条件组合超过4096个有效状态，请简化交叉依赖。", out failure);
                        next.Add(branch);
                    }
                }
            }
            foreach (WeeklyStoryChoiceOption option in beat.Choices)
                if (!reachedOptions.Contains(beat.Id + ":" + option.Id)) return Fail("剧情包含无法选中的选项。", out failure);
            known.Add(beat.Id, beat.Choices.Select(option => option.Id).ToHashSet(StringComparer.Ordinal));
            paths = next;
        }
        return true;
    }

    public static bool CanComplete(WeeklyStoryPlan plan)
    {
        WeeklyStoryBeat[] choices = plan.Beats.Where(beat => beat.Type == "choice").ToArray();
        if (choices.Length == 0 || choices.Length > WeeklyStoryPlan.MaximumBeatCount || plan.Selections.Count != choices.Length) return false;
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < choices.Length; index++)
        {
            WeeklyStoryChoiceRecord selection = plan.Selections[index];
            WeeklyStoryBeat beat = choices[index];
            WeeklyStoryChoiceOption? option = AvailableOptions(beat, state).FirstOrDefault(value => value.Id == selection.OptionId);
            if (!selection.ResponseDelivered || selection.ChoiceId != beat.Id || selection.BeatIndex != plan.Beats.IndexOf(beat)
                || option is null || option.Text != selection.Text || option.ResponseActor != selection.ResponseActor
                || option.ResponseText != selection.ResponseText || option.Outcome != selection.Outcome
                || option.Ending != selection.Ending || option.Boundary != selection.Boundary) return false;
            state[beat.Id] = option.Id;
        }
        return true;
    }

    /// <summary>One final outcome per actor, not a reward for each button click.</summary>
    public static Dictionary<string, int> RelationshipChanges(WeeklyStoryPlan plan)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (WeeklyStoryChoiceRecord selection in plan.Selections.Where(selection => selection.ResponseDelivered))
        {
            int points = selection.Outcome switch
            { "romantic" => 50, "warm" => 35, "playful" => 30, "honest" or "shared" => 25, "distant" => -10, _ => 0 };
            IEnumerable<string> recipients = selection.Outcome == "shared"
                ? plan.Actors.Select(actor => actor.Name) : new[] { selection.ResponseActor };
            foreach (string actor in recipients) result[actor] = points;
        }
        return result;
    }

    private static bool ValidConditions(IReadOnlyDictionary<string, string> conditions, Dictionary<string, HashSet<string>> known)
        => conditions.All(pair => known.TryGetValue(pair.Key, out HashSet<string>? ids) && ids.Contains(pair.Value));

    private static bool Fail(string message, out string failure) { failure = message; return false; }

    private sealed class PathState
    {
        public Dictionary<string, string> Selections { get; } = new(StringComparer.Ordinal);
        public PathState() { }
        public PathState(PathState other)
        {
            foreach (var pair in other.Selections) Selections.Add(pair.Key, pair.Value);
        }
    }
}
