namespace VivantValley.Services;

public sealed class WeeklyStoryValidator
{
    private static readonly HashSet<string> AllowedBeatTypes = new(StringComparer.Ordinal)
    {
        WeeklyStoryBeatTypes.Enter,
        WeeklyStoryBeatTypes.Move,
        WeeklyStoryBeatTypes.Face,
        WeeklyStoryBeatTypes.Animation,
        WeeklyStoryBeatTypes.Emote,
        WeeklyStoryBeatTypes.Dialogue,
        WeeklyStoryBeatTypes.Pause,
        WeeklyStoryBeatTypes.Camera,
        WeeklyStoryBeatTypes.Sound,
        WeeklyStoryBeatTypes.Choice,
        WeeklyStoryBeatTypes.Exit,
        WeeklyStoryBeatTypes.End,
    };

    private static readonly HashSet<string> AllowedCues = new(StringComparer.Ordinal)
    {
        "", "wave", "sit", "look_away", "cheer", "shy", "surprised", "sad", "happy", "love",
    };

    private static readonly HashSet<string> AllowedSoundCues = new(StringComparer.Ordinal)
    {
        "", "soft", "surprise", "mystery", "storm", "footstep",
    };

    private static readonly HashSet<string> AllowedTargets = new(StringComparer.Ordinal)
    {
        "", "player", "player_left", "player_right", "center", "left", "right",
    };

    private static readonly HashSet<string> AllowedOutcomes = new(StringComparer.Ordinal)
    {
        "romantic", "warm", "playful", "honest", "shared", "neutral", "distant",
    };

    internal bool TryCreatePlan(
        WeeklyStoryWireResponse wire,
        WeeklyStoryPlanRequest request,
        out WeeklyStoryPlan? plan,
        out string failure)
    {
        plan = null;
        failure = string.Empty;
        if (wire is null)
            return Fail("AI 没有返回剧情对象。", out failure);
        if (wire.FormatVersion is not (1 or 2) || (request.MinimumChoices >= 2 && wire.FormatVersion != 2))
            return Fail("本次请求需要formatVersion=2的多选择剧情。", out failure);
        bool branching = wire.FormatVersion == 2;

        string activityKind = string.IsNullOrWhiteSpace(wire.ActivityKind) ? "story" : wire.ActivityKind.Trim().ToLowerInvariant();
        if (activityKind is not ("story" or "date")) return Fail("activityKind must be story or date.", out failure);
        string title = Clean(wire.Title, 80);
        string summary = Clean(wire.Summary, 600);
        string rawInviter = CleanSingleLine(wire.Inviter, 80);
        string locationId = CleanSingleLine(wire.LocationId, 160);
        string mailBody = CleanMail(wire.MailBody, 900);
        string timeSlot = NormalizeTimeSlot(CleanSingleLine(wire.TimeSlot, 24));
        if (title.Length == 0 || summary.Length == 0 || mailBody.Length == 0)
            return Fail("剧情缺少标题、概要、邀请人或信件正文。", out failure);

        WeeklyStoryLocationCandidate? location = ResolveLocation(locationId, request.Locations);
        if (location is null)
            return Fail("剧情选择了未授权或不存在的地点。", out failure);

        var participantNames = new List<string>();
        foreach (string rawName in wire.Participants ?? new List<string>())
        {
            string cleanName = CleanSingleLine(rawName, 80);
            if (cleanName.Length == 0)
                continue;
            WeeklyStoryNpcCandidate? candidate = ResolveNpc(cleanName, request.Npcs);
            if (candidate is null)
                return Fail($"剧情使用了未授权 NPC：{cleanName}。", out failure);
            if (!participantNames.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase))
                participantNames.Add(candidate.Name);
        }

        WeeklyStoryNpcCandidate? inviterCandidate = ResolveNpc(rawInviter, request.Npcs);
        if (inviterCandidate is null && rawInviter.Length == 0 && participantNames.Count > 0)
            inviterCandidate = ResolveNpc(participantNames[0], request.Npcs);
        if (inviterCandidate is null)
            return Fail("剧情缺少邀请人，或邀请人不是候选 NPC。", out failure);
        string inviter = inviterCandidate.Name;
        if (!participantNames.Contains(inviter, StringComparer.OrdinalIgnoreCase))
            participantNames.Insert(0, inviter);

        if (participantNames.Count is < 1 or > 3 || participantNames.Count > location.MaximumActors)
            return Fail("剧情参与人数无效。", out failure);

        var actors = new List<WeeklyStoryActor>();
        foreach (string name in participantNames)
        {
            WeeklyStoryNpcCandidate? candidate = request.Npcs.FirstOrDefault(npc =>
                npc.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (candidate is null)
                return Fail("剧情使用了未授权 NPC。", out failure);
            actors.Add(new WeeklyStoryActor { Name = candidate.Name, DisplayName = candidate.DisplayName });
        }

        (int startTime, int endTime) = timeSlot switch
        {
            WeeklyStoryTimeSlots.Morning => (700, 1150),
            WeeklyStoryTimeSlots.Afternoon => (1200, 1650),
            WeeklyStoryTimeSlots.Evening => (1700, 2150),
            WeeklyStoryTimeSlots.Night => (2200, 2500),
            _ => (0, 0),
        };
        if (startTime == 0)
            return Fail("剧情时间段无效。", out failure);
        if (!WeeklyStoryPlanningPolicy.AvailableTimeSlots(request).Contains(timeSlot))
            return Fail("剧情时间段已过或不足一小时，请使用提供的timeSlots。", out failure);

        // The game owns scheduling. Keep parsing dayOffset for old provider
        // responses, but never allow the model to move the event to another day.
        int scheduledDay = request.ScheduledDay >= 0
            ? request.ScheduledDay
            // Legacy callers created before the local scheduling contract may
            // still omit ScheduledDay; production requests always provide it.
            : request.CurrentDay + wire.DayOffset;
        if (scheduledDay < request.EarliestDay || scheduledDay > request.LatestDay)
            return Fail("剧情日期不在本周允许范围内。", out failure);

        List<WeeklyStoryBeat> beats = (wire.Beats ?? new List<WeeklyStoryBeat>())
            .Where(beat => beat is not null)
            .ToList();
        var repairs = new List<string>();
        if (!TryRepairHarmlessWireDrift(beats, actors, location, inviter, request.PlayerName, branching, repairs, out beats, out failure))
            return false;
        // Keep the event bounded before applying the participation and branch rules.
        if (beats.Count > WeeklyStoryPlan.MaximumBeatCount)
            return Fail($"剧情演出节点不能超过 {WeeklyStoryPlan.MaximumBeatCount} 个。", out failure);

        int choiceCount = 0;
        int preChoiceDialogueCount = 0;
        int preChoicePlayerDialogueCount = 0;
        int npcToNpcDialogueCount = 0;
        int postChoiceBeatCount = 0;
        bool choiceSeen = false;
        var actorNames = actors.Select(actor => actor.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (WeeklyStoryBeat beat in beats)
        {
            beat.Normalize();
            if (!AllowedBeatTypes.Contains(beat.Type))
                return Fail($"剧情包含未知节点：{beat.Type}。", out failure);
            if (RequiresActor(beat.Type) && !actorNames.Contains(beat.Actor))
                return Fail($"剧情节点引用了无效演员：{beat.Actor}。", out failure);
            if (!AllowedTargets.Contains(beat.Target)
                && !actorNames.Contains(beat.Target))
            {
                return Fail($"剧情节点引用了无效目标：{beat.Target}。", out failure);
            }
            if (beat.Type == WeeklyStoryBeatTypes.Sound
                ? !AllowedSoundCues.Contains(beat.Cue)
                : !AllowedCues.Contains(beat.Cue))
                return Fail($"剧情节点包含不安全动作：{beat.Cue}。", out failure);
            if (beat.Type == WeeklyStoryBeatTypes.Dialogue)
            {
                if (choiceSeen && !branching)
                    return Fail("选择之后不允许出现公共 dialogue。", out failure);

                preChoiceDialogueCount++;
                if (beat.Text.Length == 0 || HasControlCode(beat.Text))
                    return Fail("剧情台词为空或包含控制代码。", out failure);

                bool npcToNpc = actorNames.Contains(beat.Target)
                                && !beat.Actor.Equals(beat.Target, StringComparison.OrdinalIgnoreCase);
                if (IsPlayerTarget(beat.Target))
                    preChoicePlayerDialogueCount++;
                if (npcToNpc)
                    npcToNpcDialogueCount++;
            }
            if (beat.Type == WeeklyStoryBeatTypes.Choice)
            {
                if (choiceSeen && !branching)
                    return Fail("剧情只能包含一个 choice。", out failure);

                choiceCount++;
                if (!WeeklyStoryPlaybackPolicy.HasSupportedOptionCount(beat.Choices.Count, branching ? 2 : 1))
                    return Fail("选择节点选项数量无效或超过技术容量。", out failure);
                foreach (WeeklyStoryChoiceOption choice in beat.Choices)
                {
                    if (!actorNames.Contains(choice.ResponseActor)
                        || choice.ResponseText.Length == 0
                        || choice.MemoryText.Length == 0
                        || choice.Text.Length == 0
                        || !AllowedOutcomes.Contains(choice.Outcome)
                        || HasControlCode(choice.Text)
                        || HasControlCode(choice.ResponseText))
                    {
                        return Fail("剧情选择的回应、结果或演员无效。", out failure);
                    }
                    WeeklyStoryNpcCandidate responder = request.Npcs.First(npc =>
                        npc.Name.Equals(choice.ResponseActor, StringComparison.OrdinalIgnoreCase));
                    if (!branching && choice.Outcome == "romantic" && !responder.IsSpouse && responder.Hearts < 8)
                        return Fail("romantic结果需要至少8心或配偶关系；当前关系请使用warm、playful、honest、shared、neutral或distant。", out failure);
                }

                choiceSeen = true;
                continue;
            }

            if (choiceSeen && !branching)
            {
                postChoiceBeatCount++;
                if (postChoiceBeatCount > 6)
                    return Fail("选择后的收束节点过多。", out failure);
                if (beat.Type is not WeeklyStoryBeatTypes.Animation
                    and not WeeklyStoryBeatTypes.Emote
                    and not WeeklyStoryBeatTypes.Pause
                    and not WeeklyStoryBeatTypes.Face
                    and not WeeklyStoryBeatTypes.Move
                    and not WeeklyStoryBeatTypes.Camera
                    and not WeeklyStoryBeatTypes.Sound
                    and not WeeklyStoryBeatTypes.Exit
                    and not WeeklyStoryBeatTypes.End)
                {
                    return Fail("选择后只能保留少量动作、退场或结束节点。", out failure);
                }
            }
        }
        if (!WeeklyStoryPlaybackPolicy.TryValidateSequence(beats, out failure, wire.FormatVersion))
            return false;
        if (!branching && choiceCount != 1)
            return Fail("剧情必须恰好有一个选择节点。", out failure);
        if (!branching && preChoiceDialogueCount < 3)
            return Fail("主选择前至少需要三段有效 dialogue 来完成铺垫、发展和转折。", out failure);
        if (!branching && preChoicePlayerDialogueCount * 2 < preChoiceDialogueCount)
            return Fail("玩家参与度不足：至少一半的台词必须直接面向玩家。", out failure);
        if (!branching && npcToNpcDialogueCount > 2)
            return Fail("NPC 之间的 dialogue 最多只能有两段必要衔接。", out failure);
        if (!beats.Any(beat => beat.Type == WeeklyStoryBeatTypes.End)
            && beats.Count < WeeklyStoryPlan.MaximumBeatCount)
        {
            // The runner also completes naturally at the end of the list, so
            // an omitted terminal node is fine when the hard cap is reached.
            beats.Add(new WeeklyStoryBeat { Type = WeeklyStoryBeatTypes.End });
        }

        var result = new WeeklyStoryPlan
        {
            FormatVersion = wire.FormatVersion,
            StoryId = $"weekly-{request.WeekKey}-{request.Seed[..Math.Min(16, request.Seed.Length)]}",
            WeekKey = request.WeekKey,
            Seed = request.Seed,
            CreatedDay = request.CurrentDay,
            ScheduledDay = scheduledDay,
            ExpiryDay = scheduledDay + 2 + Math.Clamp(request.InvitationWindowDays, 1, 14),
            InvitationWindowDays = Math.Clamp(request.InvitationWindowDays, 1, 14),
            InvitationVersion = 1,
            StartTime = startTime,
            EndTime = endTime,
            TimeSlot = timeSlot,
            Title = title,
            ActivityKind = activityKind,
            Summary = summary,
            Inviter = actors.First(actor => actor.Name.Equals(inviter, StringComparison.OrdinalIgnoreCase)).Name,
            LocationId = location.Id,
            LocationDisplayName = location.DisplayName,
            MailBody = mailBody,
            Status = WeeklyStoryStatus.MailQueued,
            Actors = actors,
            Beats = beats,
            RepairNotes = repairs,
        };
        result.Normalize();
        plan = result;
        return true;
    }

    private static bool RequiresActor(string type)
        => type is WeeklyStoryBeatTypes.Enter
            or WeeklyStoryBeatTypes.Move
            or WeeklyStoryBeatTypes.Face
            or WeeklyStoryBeatTypes.Animation
            or WeeklyStoryBeatTypes.Emote
            or WeeklyStoryBeatTypes.Dialogue
            or WeeklyStoryBeatTypes.Exit;

    private static bool IsPlayerTarget(string target)
        => target.Equals("player", StringComparison.OrdinalIgnoreCase);

    private static bool TryRepairHarmlessWireDrift(
        IEnumerable<WeeklyStoryBeat> source,
        IReadOnlyList<WeeklyStoryActor> actors,
        WeeklyStoryLocationCandidate location,
        string inviter,
        string playerName,
        bool branching,
        List<string> repairs,
        out List<WeeklyStoryBeat> repaired,
        out string failure)
    {
        repaired = new List<WeeklyStoryBeat>();
        failure = string.Empty;
        bool choiceSeen = false;
        WeeklyStoryBeat? branch = null;
        foreach (WeeklyStoryBeat beat in source)
        {
            beat.Normalize();
            if (!AllowedBeatTypes.Contains(beat.Type))
                return Fail($"剧情包含未知节点：{beat.Type}。", out failure);
            if (!branching && choiceSeen && beat.Type == WeeklyStoryBeatTypes.Dialogue)
            {
                if (branch is not null && branch.Choices.Count >= 2
                    && branch.Choices.All(option => option.ResponseActor.Equals(beat.Actor, StringComparison.OrdinalIgnoreCase)
                        && option.ResponseText.Equals(beat.Text, StringComparison.Ordinal)))
                {
                    repairs.Add("移除与所有分支回应完全相同的重复台词。");
                    continue;
                }
                return Fail("选择之后的公共dialogue含独立正文，不能无损删除；请将分支结果放在各选项responseText。", out failure);
            }

            string actor = ResolveActorName(beat.Actor, actors);
            if (RequiresActor(beat.Type))
            {
                if (actor.Length == 0 && beat.Actor.Length == 0 && actors.Count == 1)
                {
                    actor = inviter;
                    repairs.Add("缺省演员补全为唯一参与NPC。");
                }
                else if (actor.Length == 0 && IsPlayerReference(beat.Actor, playerName))
                {
                    if (beat.Text.Length > 0 || beat.Type == WeeklyStoryBeatTypes.Dialogue)
                        return Fail("玩家不能作为NPC演员；含正文的玩家节点不能删除或转交NPC，请改成玩家choice。", out failure);
                    if (!AllowedCues.Contains(beat.Cue) || !TryNormalizeTarget(beat, actors, location, playerName, out _))
                        return Fail("玩家动作包含未知cue或target。", out failure);
                    repairs.Add("跳过运行器不支持的无正文玩家动作。");
                    continue;
                }
                else if (actor.Length == 0)
                    return Fail($"剧情节点引用了无效演员：{beat.Actor}。", out failure);
                beat.Actor = actor;
            }
            else if (beat.Type == WeeklyStoryBeatTypes.Choice)
            {
                if (actor.Length == 0 && beat.Actor.Length > 0 && !IsPlayerReference(beat.Actor, playerName))
                    return Fail($"剧情选择引用了无效演员：{beat.Actor}。", out failure);
                beat.Actor = actor.Length > 0 ? actor : inviter;
            }
            else
            {
                if (beat.Actor.Length > 0 && actor.Length == 0 && !IsPlayerReference(beat.Actor, playerName))
                    return Fail($"剧情节点引用了无效演员：{beat.Actor}。", out failure);
                beat.Actor = actor;
            }

            if (!TryNormalizeTarget(beat, actors, location, playerName, out string target))
                return Fail($"剧情节点引用了无效目标：{beat.Target}。", out failure);
            if (!beat.Target.Equals(target, StringComparison.OrdinalIgnoreCase))
                repairs.Add($"{beat.Type}目标归一化：{beat.Target}→{target}。");
            beat.Target = target;

            bool cueAllowed = beat.Type == WeeklyStoryBeatTypes.Sound
                ? AllowedSoundCues.Contains(beat.Cue)
                : AllowedCues.Contains(beat.Cue);
            if (!cueAllowed)
            {
                bool harmlessMisplacement = beat.Type == WeeklyStoryBeatTypes.Sound
                    ? AllowedCues.Contains(beat.Cue)
                    : AllowedSoundCues.Contains(beat.Cue);
                if (!harmlessMisplacement)
                    return Fail($"剧情节点包含不安全动作：{beat.Cue}。", out failure);
                // A legal cue from the adjacent cue vocabulary was placed on
                // the wrong beat kind. Empty cue is an explicit safe no-op.
                beat.Cue = string.Empty;
                repairs.Add($"{beat.Type}上错放的合法cue恢复为默认表现。");
            }

            if (beat.Type == WeeklyStoryBeatTypes.Choice)
            {
                if (!WeeklyStoryPlaybackPolicy.HasSupportedOptionCount(beat.Choices.Count, branching ? 2 : 1))
                    return Fail("选择节点选项数量无效或超过技术容量；不会静默删除选项。", out failure);
                var ids = new HashSet<string>(beat.Choices.Where(option => option.Id.Length > 0).Select(option => option.Id), StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < beat.Choices.Count; index++)
                {
                    WeeklyStoryChoiceOption choice = beat.Choices[index];
                    if (choice.Id.Length == 0 || !seen.Add(choice.Id))
                    {
                        if (branching)
                            return Fail("分支选项ID缺失或重复，不能自动改名破坏when引用。", out failure);
                        int suffix = index + 1;
                        string id;
                        do { id = $"option_{suffix++}"; } while (!ids.Add(id));
                        choice.Id = id;
                        seen.Add(id);
                        repairs.Add("补全空白或重复选项ID，保留全部选项正文。");
                    }
                    string responder = ResolveActorName(choice.ResponseActor, actors);
                    if (responder.Length == 0 && choice.ResponseActor.Length == 0 && actors.Count == 1)
                    {
                        responder = beat.Actor.Length > 0 ? beat.Actor : inviter;
                    }
                    if (responder.Length == 0)
                        return Fail($"剧情选择引用了无效回应演员：{choice.ResponseActor}。", out failure);
                    choice.ResponseActor = responder;
                    if (choice.MemoryText.Length == 0 && choice.Text.Length > 0)
                        choice.MemoryText = $"玩家选择了“{choice.Text}”。";
                }
            }

            repaired.Add(beat);
            if (beat.Type == WeeklyStoryBeatTypes.Choice)
            {
                choiceSeen = true;
                branch = beat;
            }
        }

        return true;
    }

    private static bool TryNormalizeTarget(
        WeeklyStoryBeat beat,
        IReadOnlyList<WeeklyStoryActor> actors,
        WeeklyStoryLocationCandidate location,
        string playerName,
        out string target)
    {
        target = beat.Target;
        if (AllowedTargets.Contains(target))
        {
            if (beat.Type is WeeklyStoryBeatTypes.Dialogue or WeeklyStoryBeatTypes.Choice)
            {
                // Spatial labels such as player_left are staging positions,
                // not dialogue recipients. A non-actor dialogue target is the
                // player by construction.
                target = "player";
            }
            else if (target.Length == 0 && beat.Type == WeeklyStoryBeatTypes.Camera)
            {
                target = "player";
            }
            return true;
        }

        if (IsPlayerReference(target, playerName))
        {
            target = "player";
            return true;
        }

        string actorTarget = ResolveActorName(target, actors);
        if (actorTarget.Length > 0)
        {
            target = actorTarget;
            return true;
        }

        if (target.Equals(location.Id, StringComparison.OrdinalIgnoreCase)
            || target.Equals(location.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            target = beat.Type switch
            {
                WeeklyStoryBeatTypes.Dialogue or WeeklyStoryBeatTypes.Choice
                    or WeeklyStoryBeatTypes.Face or WeeklyStoryBeatTypes.Camera => "player",
                WeeklyStoryBeatTypes.Enter or WeeklyStoryBeatTypes.Move
                    or WeeklyStoryBeatTypes.Animation or WeeklyStoryBeatTypes.Emote => "center",
                _ => string.Empty,
            };
            return true;
        }

        return false;
    }

    private static string ResolveActorName(string? value, IReadOnlyList<WeeklyStoryActor> actors)
    {
        string clean = CleanSingleLine(value, 80);
        WeeklyStoryActor? actor = actors.FirstOrDefault(candidate =>
            candidate.Name.Equals(clean, StringComparison.OrdinalIgnoreCase)
            || candidate.DisplayName.Equals(clean, StringComparison.OrdinalIgnoreCase));
        return actor?.Name ?? string.Empty;
    }

    private static WeeklyStoryNpcCandidate? ResolveNpc(
        string? value,
        IReadOnlyList<WeeklyStoryNpcCandidate> candidates)
    {
        string clean = CleanSingleLine(value, 80);
        return candidates.FirstOrDefault(candidate =>
            candidate.Name.Equals(clean, StringComparison.OrdinalIgnoreCase)
            || candidate.DisplayName.Equals(clean, StringComparison.OrdinalIgnoreCase));
    }

    private static WeeklyStoryLocationCandidate? ResolveLocation(
        string? value,
        IReadOnlyList<WeeklyStoryLocationCandidate> candidates)
    {
        string clean = CleanSingleLine(value, 160);
        return candidates.FirstOrDefault(candidate =>
            candidate.Id.Equals(clean, StringComparison.OrdinalIgnoreCase)
            || candidate.DisplayName.Equals(clean, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeTimeSlot(string? value)
        => CleanSingleLine(value, 24).ToLowerInvariant() switch
        {
            "morning" or "上午" or "早上" => WeeklyStoryTimeSlots.Morning,
            "afternoon" or "下午" => WeeklyStoryTimeSlots.Afternoon,
            "evening" or "傍晚" or "黄昏" or "dusk" => WeeklyStoryTimeSlots.Evening,
            "night" or "夜晚" or "晚上" => WeeklyStoryTimeSlots.Night,
            var other => other,
        };

    private static bool IsPlayerReference(string? value, string? playerName)
    {
        string clean = CleanSingleLine(value, 80);
        return clean.Equals("player", StringComparison.OrdinalIgnoreCase)
               || clean.Equals("farmer", StringComparison.OrdinalIgnoreCase)
               || clean.Equals("you", StringComparison.OrdinalIgnoreCase)
               || clean.Equals("玩家", StringComparison.Ordinal)
               || (!string.IsNullOrWhiteSpace(playerName)
                   && clean.Equals(playerName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasControlCode(string value)
        => value.Contains('%')
           || value.Contains('^')
           || value.Contains("[#]", StringComparison.Ordinal)
           || value.Any(character => char.IsControl(character) && character is not '\n' and not '\t');

    private static string Clean(string? value, int maximum)
    {
        string text = (value ?? string.Empty).Replace('\r', ' ').Trim();
        return text.Length <= maximum ? text : text[..maximum];
    }

    private static string CleanSingleLine(string? value, int maximum)
        => Clean(value, maximum).Replace('\n', ' ').Trim();

    private static string CleanMail(string? value, int maximum)
        => CleanSingleLine(value, maximum)
            .Replace("%", string.Empty, StringComparison.Ordinal)
            .Replace("^", " ", StringComparison.Ordinal)
            .Replace("[#]", string.Empty, StringComparison.Ordinal);

    private static bool Fail(string message, out string failure)
    {
        failure = message;
        return false;
    }
}
