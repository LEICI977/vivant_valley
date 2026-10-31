using System.Text.Json;
using System.Text;

namespace VivantValley.Services;

public sealed class WeeklyStoryPlannerService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Provider output is still validated against the strict game contract
        // below. These options only tolerate harmless JSON presentation drift.
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IDeepSeekClient client;
    private readonly WeeklyStoryValidator validator;
    private readonly AiRequestDebugWriter? debugWriter;
    private readonly WeeklyStoryPromptService prompts;

    public WeeklyStoryPlannerService(
        IDeepSeekClient client,
        WeeklyStoryValidator validator,
        AiRequestDebugWriter? debugWriter = null,
        WeeklyStoryPromptService? prompts = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.debugWriter = debugWriter;
        this.prompts = prompts ?? new WeeklyStoryPromptService();
    }

    public async Task<WeeklyStoryPlanningResult> PlanAsync(
        string apiKey,
        WeeklyStoryPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        using var language = L.BeginRequest();
        if (request.Npcs.Count == 0 || request.Locations.Count == 0)
            return WeeklyStoryPlanningResult.Failed("没有可用的 NPC 或剧情地点。");
        if (string.IsNullOrWhiteSpace(apiKey))
            return WeeklyStoryPlanningResult.Failed("API Key 未设置。");

        var apiRequest = new DeepSeekChatRequest
        {
            NpcName = null,
            TaskKind = "weekly_story",
            Model = string.IsNullOrWhiteSpace(request.Model) ? "deepseek-v4-flash" : request.Model.Trim(),
            Thinking = new DeepSeekThinkingOptions
            {
                Type = string.IsNullOrWhiteSpace(request.ThinkingType) ? "disabled" : request.ThinkingType.Trim(),
            },
            ReasoningEffort = string.IsNullOrWhiteSpace(request.ReasoningEffort)
                ? "low"
                : request.ReasoningEffort.Trim(),
            MaxTokens = Math.Clamp(request.MaxOutputTokens, 900, 6000),
            Stream = false,
            Messages = new List<DeepSeekChatMessage>
            {
                new("system", L.WithOutputLanguage(request.FreeScene ? prompts.LoadFreeScene() : prompts.Load())),
                new("user", BuildUserPrompt(request)),
            },
        };

        try
        {
            string raw = await client.CompleteChatAsync(apiKey, apiRequest, cancellationToken)
                .ConfigureAwait(false);
            // Keep the exact assistant payload available when vivant_ai_debug
            // is enabled. The provider writer captures requests, while this
            // companion capture records the response which validator failures
            // otherwise hide.
            debugWriter?.TryWriteResponse("weekly-story-ai-response", raw);
            return TryParse(raw, request, out WeeklyStoryPlan? plan, out string failure, out string failureKind)
                ? WeeklyStoryPlanningResult.Succeeded(plan!)
                : WeeklyStoryPlanningResult.Failed(failure, failureKind);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WeeklyStoryPlanningResult.Failed(Clean(ex.Message, 240, "周剧情规划失败"), "request");
        }
    }

    public bool TryParse(
        string raw,
        WeeklyStoryPlanRequest request,
        out WeeklyStoryPlan? plan,
        out string failure)
        => TryParse(raw, request, out plan, out failure, out _);

    public bool TryParse(
        string raw,
        WeeklyStoryPlanRequest request,
        out WeeklyStoryPlan? plan,
        out string failure,
        out string failureKind)
    {
        plan = null;
        failure = string.Empty;
        failureKind = "validation";
        string json = ExtractJsonObject(raw);
        try
        {
            if (request.FreeScene)
            {
                bool valid = FreeStoryService.TryCreate(json, request, out plan, out failure);
                if (valid) failureKind = string.Empty;
                return valid;
            }
            WeeklyStoryWireResponse? wire = JsonSerializer.Deserialize<WeeklyStoryWireResponse>(json, JsonOptions);
            if (wire is null)
            {
                failure = "周剧情响应为空。";
                return false;
            }
            bool success = validator.TryCreatePlan(wire, request, out plan, out failure);
            if (success) failureKind = string.Empty;
            else if (failure.StartsWith("剧情演出节点不能超过", StringComparison.Ordinal)) failureKind = "too_many_beats";
            return success;
        }
        catch (JsonException ex)
        {
            bool incomplete = IsIncompleteJson(json);
            failureKind = incomplete ? "truncated_json" : "invalid_json";
            failure = (incomplete
                ? $"周剧情 JSON 未完整结束（疑似响应截断；输出上限 {Math.Clamp(request.MaxOutputTokens, 900, 6000)} tokens）："
                : "周剧情规划 JSON 或字段类型不符合协议：") + Clean(ex.Message, 180, "解析失败");
            return false;
        }
    }

    // Parse as a non-final block to distinguish an incomplete valid JSON prefix
    // from a complete object with wrong field types or broken syntax. Never repair content.
    private static bool IsIncompleteJson(string json)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), isFinalBlock: false,
                new JsonReaderState(new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }));
            int depth = 0;
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) depth--;
            }
            return depth > 0;
        }
        catch (JsonException) { return false; }
    }

    private static string BuildRetryInstruction(WeeklyStoryPlanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PreviousFailureReason)) return L.IsChinese ? "这是第一次生成。" : "This is the first attempt.";
        string reason = request.PreviousFailureReason;
        string kind = request.PreviousFailureKind;
        // Old saves only stored the reason. Use that evidence to target the retry too.
        if (kind.Length == 0 || kind == "validation")
        {
            if (reason.Contains("reached end of data", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("未完整结束", StringComparison.Ordinal)) kind = "truncated_json";
            else if (reason.Contains("剧情演出节点不能超过", StringComparison.Ordinal)) kind = "too_many_beats";
        }
        if (request.FreeScene) return "Regenerate one complete compact formatVersion 3 opening. No beats, choices or ending. Fix this diagnostic: " + Clean(reason, 400, "Invalid scene");
        if (!L.IsChinese)
        {
            string repair = kind switch
            {
                "truncated_json" => "The previous JSON was incomplete. Regenerate a COMPLETE compact object with 8–12 beats and two choices of two options. Keep each line/response under 100 characters and the invitation under 150. Do not continue the fragment.",
                "too_many_beats" => "Use 10–14 beats and two choices. Remove decorative actions from the design; 48 is a tolerance, not a target. Finish with end.",
                "invalid_json" => "Regenerate complete valid JSON with the documented types, double quotes and correct escaping. No Markdown or comments.",
                _ => "Correct the indicated actor, enum or branch reference. Prefer 10–14 beats rather than adding filler.",
            };
            return "Repair attempt. Previous diagnostic (not story content): " + Clean(reason, 400, "Validation failed")
                + "\n" + repair + " Use one or two candidate NPCs, distinct responseText and no dead ends. Ending labels may repeat.";
        }
        string instruction = kind switch
        {
            "truncated_json" => "上次JSON未写完：本次缩短为8至12个beats、两次choice、每次两个选项；每段台词和responseText不超过30字，邀请信不超过40字。省略镜头、声音和装饰动作；不要续写残片，重新输出完整紧凑JSON。",
            "too_many_beats" => "上次节点过多：本次只写10至14个beats、两次choice；只保留推动冲突和选择的台词，删除装饰性动作的设计。48是容错上限不是目标，end必须收尾。",
            "invalid_json" => "上次JSON语法或字段类型错误：按示例的字段类型重新生成完整JSON，使用双引号并正确转义，不要Markdown或注释。",
            _ => "按失败原因修正对应人物、枚举或分支引用；生成10至14个beats的短篇，不增加节点来补规则。",
        };
        return "这是修复重试。上次失败原因：" + Clean(reason, 400, "结构校验失败") + "。"
            + instruction
            + " 建议两次选择，使用1至2名候选NPC。优先只在两段反应台词上设置when，后续选项无条件；选择的专属回应写responseText。结局标签不必凑齐三类，条件分支不要产生无选项的死路。不要把控制台错误或本段指令写进剧情。";
    }

    private static string BuildUserPrompt(WeeklyStoryPlanRequest request)
    {
        var payload = new
        {
            weekKey = request.WeekKey,
            player = request.PlayerName,
            currentDate = new
            {
                totalDays = request.CurrentDate.TotalDays,
                year = request.CurrentDate.Year,
                season = request.CurrentDate.Season,
                day = request.CurrentDate.DayOfMonth,
            },
            storyDate = new
            {
                totalDays = request.StoryDate.TotalDays,
                year = request.StoryDate.Year,
                season = request.StoryDate.Season,
                day = request.StoryDate.DayOfMonth,
            },
            currentWeather = request.CurrentWeather,
            storyWeather = request.StoryWeather,
            festivalToday = request.FestivalToday,
            festivalName = request.FestivalName,
            scheduledDay = request.FreeScene ? request.CurrentDay : request.ScheduledDay,
            invitationWindowDays = request.InvitationWindowDays,
            invitationDelivery = "mail / face-to-face / home visit / telephone; chosen by the game",
            schedulingRule = request.FreeScene
                ? "Default flexible: start on the target map as soon as the player is free, from today. No invitation acceptance or chosen clock slot required. storyDate/storyWeather are planning context, not a promise or current fact. Only an actual daylight/sunset/night dependency with a concrete reason may constrain time. Never invent an appointment. Flexible openings and invitations must be time-neutral."
                : "Repeat the chosen time slot on each valid day. Weather and exact date may change. Do not require the generation-day weather, a particular weekday, or today-only dialogue. The game appends the actual date range, time and location to the invitation.",
            timeSlots = request.FreeScene ? Array.Empty<string>() : WeeklyStoryPlanningPolicy.AvailableTimeSlots(request),
            npcs = request.Npcs.Take(4).Select(npc => new
            {
                name = npc.Name,
                displayName = npc.DisplayName,
                hearts = npc.Hearts,
                relationship = npc.Relationship,
                datable = npc.IsDatable,
                spouse = npc.IsSpouse,
                adult = npc.IsAdult,
                dating = npc.IsDating,
                romanticInterest = npc.HasRomanticInterest,
                supporting = npc.IsSupporting,
                roleRule = npc.RoleRule,
                experiences = npc.Experiences,
                activeCommitments = npc.ActiveCommitments,
                recent = request.FreeScene ? Array.Empty<string>() : npc.RecentEvents.TakeLast(3).ToArray(),
            }),
            locations = request.Locations.Take(32).Select(location => new
            {
                locationId = location.Id,
                displayName = location.DisplayName,
                tags = location.Tags,
                maximumActors = location.MaximumActors,
                dangerous = location.Dangerous,
            }),
            recentStories = request.RecentHistory.TakeLast(6).Select(h => new
            {
                title = h.Title, location = h.LocationId, day = h.CompletedDay, participants = h.Participants,
                plannedTopic = h.PlannedPremise,
                status = "encounter_ended; not evidence of goal completion or a new agreement",
            }),
            legacyAvoidance = request.RecentHistory.Count > 0 ? Array.Empty<string>() : request.RecentStoryAnchors.TakeLast(2).ToArray(),
        };
        if (request.FreeScene)
            return "Create only a formatVersion 3 opening scene from these candidates. No future player choices, branches or predetermined outcomes.\n"
                + JsonSerializer.Serialize(payload, ExperienceContextPolicy.ReadableJson) + "\n" + BuildRetryInstruction(request);
        if (!L.IsChinese)
            return "Write a short playable relationship conflict, formatVersion=2. Prefer two choices and 10–18 beats. Choose the setting, cast and story from the supplied candidates; use storyDate and storyWeather exactly. "
                + $"Output budget: {Math.Clamp(request.MaxOutputTokens, 900, 6000)} tokens. Return compact JSON, no indentation or empty/default fields. "
                + (request.MaxOutputTokens <= 2400 ? "Prefer 10–12 beats, short lines and two options per choice. " : "")
                + BuildRetryInstruction(request) + "\n" + JsonSerializer.Serialize(payload);
        return "生成一段有关系冲突的短篇可执行剧情，formatVersion=2，建议两次choice，目标10至18个beats。地点、时间、人物组合和具体故事由你决定，但日期、季节和天气必须严格使用提供的 storyDate 与 storyWeather。"
               + $"本次输出上限为{Math.Clamp(request.MaxOutputTokens, 900, 6000)} tokens；输出紧凑JSON，不缩进、不逐字段换行，省略默认/空字段。"
               + (request.MaxOutputTokens <= 2400 ? "预算较小：优先10至12个beats，短台词，每个choice两个选项，不添加装饰动作。" : "")
               + BuildRetryInstruction(request)
               + "\n"
               + JsonSerializer.Serialize(payload);
    }

    internal static string ExtractJsonObject(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = text.IndexOf('\n');
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                text = text[(firstNewline + 1)..lastFence].Trim();
        }

        int start = text.IndexOf('{');
        if (start < 0)
            return text;

        int depth = 0;
        bool insideString = false;
        bool escaped = false;
        bool lineComment = false, blockComment = false;
        for (int index = start; index < text.Length; index++)
        {
            char character = text[index];
            if (lineComment)
            {
                if (character is '\r' or '\n') lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (character == '*' && index + 1 < text.Length && text[index + 1] == '/')
                { blockComment = false; index++; }
                continue;
            }
            if (insideString)
            {
                if (escaped)
                    escaped = false;
                else if (character == '\\')
                    escaped = true;
                else if (character == '"')
                    insideString = false;
                continue;
            }

            if (character == '/' && index + 1 < text.Length)
            {
                if (text[index + 1] == '/') { lineComment = true; index++; continue; }
                if (text[index + 1] == '*') { blockComment = true; index++; continue; }
            }
            if (character == '"')
            {
                insideString = true;
                continue;
            }
            if (character == '{')
                depth++;
            else if (character == '}' && --depth == 0)
                return text[start..(index + 1)];
        }

        // Let JsonSerializer return its precise truncation error when the root
        // object was never closed; we never invent missing story content.
        return text[start..];
    }

    private static string Clean(string? value, int maximum, string fallback)
    {
        string clean = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length == 0)
            return fallback;
        return clean.Length <= maximum ? clean : clean[..maximum];
    }
}

public sealed class WeeklyStoryPlanningResult
{
    public WeeklyStoryPlan? Plan { get; init; }

    public string FailureReason { get; init; } = string.Empty;

    public string FailureKind { get; init; } = string.Empty;

    public bool Success => Plan is not null;

    public static WeeklyStoryPlanningResult Succeeded(WeeklyStoryPlan plan)
        => new() { Plan = plan };

    public static WeeklyStoryPlanningResult Failed(string reason, string kind = "configuration")
        => new() { FailureReason = reason, FailureKind = kind };
}
