namespace VivantValley.Services;

/// <summary>Pure playback rules shared by the weekly-story runner and its regression tests.</summary>
public static class WeeklyStoryPlaybackPolicy
{
    public static bool TryValidateSequence(IReadOnlyList<WeeklyStoryBeat> beats, out string failure, int formatVersion = 1)
    {
        if (formatVersion == 2) return WeeklyStoryBranchPolicy.TryValidate(beats, out failure);
        if (formatVersion != 1) { failure = "不支持的周剧情版本。"; return false; }
        failure = string.Empty;
        if (beats.Count == 0 || beats.Count > WeeklyStoryPlan.MaximumBeatCount)
        {
            failure = $"剧情必须有1至{WeeklyStoryPlan.MaximumBeatCount}个节点。";
            return false;
        }
        int choices = 0;
        for (int index = 0; index < beats.Count; index++)
        {
            WeeklyStoryBeat beat = beats[index];
            if (choices > 0 && beat.Type == WeeklyStoryBeatTypes.Dialogue)
            {
                failure = "choice后的公共台词未归属分支，不能跳过正文播放。";
                return false;
            }
            if (beat.Type == WeeklyStoryBeatTypes.Choice)
            {
                choices++;
                if (beat.Choices.Count is < 2 or > 3
                    || beat.Choices.Any(choice => string.IsNullOrWhiteSpace(choice.Id)
                        || string.IsNullOrWhiteSpace(choice.Text) || string.IsNullOrWhiteSpace(choice.ResponseText))
                    || beat.Choices.Select(choice => choice.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != beat.Choices.Count)
                {
                    failure = "选择必须有2至3个完整、ID唯一的选项。";
                    return false;
                }
            }
            if (beat.Type == WeeklyStoryBeatTypes.End && (choices != 1 || index != beats.Count - 1))
            {
                failure = "end只能位于唯一choice之后的最后一个节点，不能提前结束或隐藏后续内容。";
                return false;
            }
        }
        if (choices != 1)
        {
            failure = "剧情必须恰好有一个可执行的choice。";
            return false;
        }
        return true;
    }

    public static bool TryValidatePlan(WeeklyStoryPlan plan, out string failure)
    {
        if (plan.FormatVersion == 3) return FreeStoryService.ValidatePlan(plan, out failure);
        if (!TryValidateSequence(plan.Beats, out failure, plan.FormatVersion)) return false;
        var actors = plan.Actors.Select(actor => actor.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (actors.Count is < 1 or > 3)
        { failure = "存档剧情演员数量无效。"; return false; }
        foreach (WeeklyStoryBeat beat in plan.Beats)
        {
            if (beat.Type is not ("enter" or "move" or "face" or "animation" or "emote" or "dialogue"
                or "pause" or "camera" or "sound" or "choice" or "exit" or "end"))
            { failure = "存档剧情包含未知节点。"; return false; }
            if ((beat.Type is "enter" or "move" or "face" or "animation" or "emote" or "dialogue" or "choice" or "exit")
                && !actors.Contains(beat.Actor))
            { failure = "存档剧情引用了缺失的演员。"; return false; }
            if (beat.Choices.Any(option => !actors.Contains(option.ResponseActor)))
            { failure = "存档剧情引用了缺失的回应演员。"; return false; }
        }
        return true;
    }

    public static bool CanComplete(WeeklyStoryPlan plan, WeeklyStoryChoiceOption? selected, bool responseDelivered)
        => responseDelivered && selected is not null
           && (plan.FormatVersion != 2 || (WeeklyStoryBranchPolicy.CanComplete(plan)
               && plan.Beats.Last(beat => beat.Type == WeeklyStoryBeatTypes.Choice).Choices
                   .Any(option => ReferenceEquals(option, selected) && option.Id == plan.Selections[^1].OptionId)))
           && plan.Beats.Where(beat => beat.Type == WeeklyStoryBeatTypes.Choice)
               .SelectMany(beat => beat.Choices).Any(option => ReferenceEquals(option, selected));

    /// <summary>Only observed choice and spoken response, never the planner's prospective summary.</summary>
    public static string BuildCompletionMemory(WeeklyStoryPlan plan, WeeklyStoryChoiceOption selected)
        => plan.FormatVersion == 2
            ? $"每周剧情“{plan.Title}”已完成；" + string.Join("；", plan.Selections.Where(selection => selection.ResponseDelivered)
                .Select((selection, index) => $"选择{index + 1}“{selection.Text}”，{selection.ResponseActor}回应“{selection.ResponseText}”"))
                + $"；结局：{EndingLabel(selected.Ending)}。"
            : $"每周剧情“{plan.Title}”已完成；玩家选择“{selected.Text}”；{selected.ResponseActor}回应“{selected.ResponseText}”。";

    public static string EndingLabel(string ending) => ending switch
    { "closer" => L.T("text.2680844ff3cf"), "unresolved" => L.T("text.6459e9e4da7f"), "apart" => L.T("text.5134ac4515b3"), _ => L.T("text.c7b24e7997e9") };

    // Capacity guards, not creative targets. V1 retains its historical protocol.
    public const int MaximumOptionsPerChoice = 48;
    public const int ChoicePageSize = 3;

    public static bool HasSupportedOptionCount(int count, int formatVersion)
        => count >= 2 && count <= (formatVersion == 2 ? MaximumOptionsPerChoice : 3);

    public static WeeklyStoryChoiceOption[] ChoicePage(IReadOnlyList<WeeklyStoryChoiceOption> choices, int page)
        => choices.Skip(Math.Clamp(page, 0, Math.Max(0, (choices.Count - 1) / ChoicePageSize)) * ChoicePageSize)
            .Take(ChoicePageSize).ToArray();

    public static WeeklyStoryChoiceOption? ResolveChoicePageAnswer(IReadOnlyList<WeeklyStoryChoiceOption> choices, int page, string answer)
        => ChoicePage(choices, page).Where((option, index) => answer == "vv_option_" + index).FirstOrDefault();

    public static bool ShouldAdvanceAfterDialogue(bool isChoiceResponse)
        => !isChoiceResponse;

    /// <summary>V1 ends its dialogue at the sole choice; V2 continues through validated branch beats.</summary>
    public static bool IsUnexpectedPostChoiceDialogue(int formatVersion, bool hasSelectedChoice, bool isChoiceResponse)
        => formatVersion != 2 && hasSelectedChoice && !isChoiceResponse;

    public static bool CanVerifyPlayerRelease(bool hasActiveMenu, bool hasActiveEvent)
        => !hasActiveMenu && !hasActiveEvent;
}
