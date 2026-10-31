using System.Text;
using System.Text.Json;

namespace VivantValley.Services;

/// <summary>Selects zero to two next-morning surprise gifts from a persisted daily snapshot.</summary>
public sealed class OvernightMailPlannerService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly IDeepSeekClient client;

    public OvernightMailPlannerService(IDeepSeekClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public static bool IsEligibleConversation(
        IReadOnlyList<ConversationSignal> signals,
        double positiveThreshold)
    {
        if (signals is null || signals.Count == 0)
            return false;

        double threshold = Math.Clamp(positiveThreshold, 0d, 1d);
        foreach (ConversationSignal signal in signals)
        {
            // An unfinished/failed classifier must not erase an otherwise real chat;
            // the bounded transcript is passed to the final planner for judgment.
            if (signal.Confidence <= 0d)
                return true;
            if (signal.GetPositiveScore() >= threshold
                || signal.Valence >= 0.05d
                || signal.Warmth >= 0.4d
                || signal.Concern >= 0.6d)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<OvernightMailPlanDecision> PlanAsync(
        string apiKey,
        OvernightMailPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        using var language = L.BeginRequest();
        ArgumentNullException.ThrowIfNull(request);
        if (request.Npcs.Count == 0)
            return EmptyDecision();
        if (string.IsNullOrWhiteSpace(apiKey))
            return EmptyDecision("API Key 未设置");

        var apiRequest = new DeepSeekChatRequest
        {
            NpcName = null,
            TaskKind = "overnight_mail",
            Model = string.IsNullOrWhiteSpace(request.Model) ? "deepseek-v4-flash" : request.Model.Trim(),
            Thinking = new DeepSeekThinkingOptions
            {
                Type = string.IsNullOrWhiteSpace(request.ThinkingType) ? "disabled" : request.ThinkingType.Trim(),
            },
            ReasoningEffort = string.IsNullOrWhiteSpace(request.ReasoningEffort)
                ? "low"
                : request.ReasoningEffort.Trim(),
            MaxTokens = Math.Clamp(request.MaxOutputTokens, 256, 1600),
            Stream = false,
            Messages = new List<DeepSeekChatMessage>
            {
                new("system", BuildSystemPrompt(request.MaximumGiftCount)),
                new("user", BuildUserPrompt(request)),
            },
        };

        try
        {
            string raw = await client.CompleteChatAsync(apiKey, apiRequest, cancellationToken)
                .ConfigureAwait(false);
            return TryParse(raw, request, out OvernightMailPlanDecision? decision, out string failure)
                ? decision!
                : EmptyDecision(failure);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EmptyDecision(Clean(ex.Message, 240, "隔夜邮件规划失败"));
        }
    }

    public static bool TryParse(
        string raw,
        OvernightMailPlanRequest request,
        out OvernightMailPlanDecision? decision,
        out string failure)
    {
        decision = null;
        failure = string.Empty;
        OvernightMailWireResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OvernightMailWireResponse>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            failure = "隔夜邮件规划不是有效 JSON：" + Clean(ex.Message, 160, "解析失败");
            return false;
        }

        if (parsed?.Gifts is null)
        {
            failure = "隔夜邮件规划缺少 gifts 数组。";
            return false;
        }

        int maximumGiftCount = Math.Clamp(request.MaximumGiftCount, 0, 2);
        if (parsed.Gifts.Count > maximumGiftCount)
        {
            failure = $"隔夜邮件规划超过每日 {maximumGiftCount} 封上限。";
            return false;
        }

        var gifts = new List<OvernightMailGiftDecision>();
        var seenNpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OvernightMailWireGift wire in parsed.Gifts)
        {
            string npcName = Clean(wire?.NpcName, 80, string.Empty);
            string candidateId = Clean(wire?.GiftCandidateId, 64, string.Empty);
            var offered = request.Npcs.Take(2).ToArray();
            OvernightMailNpcSnapshot? npc = offered.FirstOrDefault(value =>
                value.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase));
            if (npc is null)
            {
                // Accept an unambiguous localized name only among NPCs actually present in this request.
                var aliases = offered.Where(value => value.NpcDisplayName.Equals(npcName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (aliases.Length == 1) npc = aliases[0];
            }
            OvernightMailGiftOption? selected = npc?.GiftCandidates.FirstOrDefault(value =>
                value.CandidateId.Equals(candidateId, StringComparison.Ordinal));
            if (npc is null)
            {
                failure = "隔夜邮件规划失败 [unknown_npc]：返回的人物不在本次候选中。";
                return false;
            }
            if (selected is null)
            {
                failure = $"隔夜邮件规划失败 [unknown_gift_candidate]：{npc.NpcName} 的礼物 ID 不在该人物候选中。";
                return false;
            }
            if (!seenNpcs.Add(npc.NpcName))
            {
                failure = $"隔夜邮件规划失败 [duplicate_npc]：同一人物 {npc.NpcName} 被选择多次。";
                return false;
            }

            string body = CleanLetterBody(wire?.LetterBody);
            if (body.Length == 0 || ContainsMailControlCode(wire?.LetterBody))
            {
                failure = $"{npc.NpcName} 的邮件正文为空或包含邮件控制码。";
                return false;
            }

            bool namesDifferentCandidate = npc.GiftCandidates.Any(candidate =>
                !candidate.CandidateId.Equals(selected.CandidateId, StringComparison.Ordinal)
                && ContainsNormalizedName(body, candidate.DisplayName));
            if (namesDifferentCandidate)
            {
                failure = $"{npc.NpcName} 的邮件正文描述了与选择不一致的候选。";
                return false;
            }

            gifts.Add(new OvernightMailGiftDecision
            {
                NpcName = npc.NpcName,
                GiftCandidateId = selected.CandidateId,
                ReasonTag = NormalizeTag(wire?.ReasonTag),
                LetterBody = body,
            });
        }

        decision = new OvernightMailPlanDecision { Gifts = gifts };
        return true;
    }

    private static string BuildSystemPrompt(int maximumGiftCount)
        => LocalizedPromptAssets.ForRequest("overnight-mail.txt").Replace("{{maximumGiftCount}}", Math.Clamp(maximumGiftCount, 0, 2).ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string BuildUserPrompt(OvernightMailPlanRequest request)
    {
        var payload = new
        {
            sourceDay = request.SourceDay,
            npcs = request.Npcs.Take(2).Select(npc =>
            {
                SocialPromptContext context = SocialPromptContext.Parse(npc.GameContext);
                return new
                {
                    npcName = npc.NpcName,
                    displayName = npc.NpcDisplayName,
                    relationship = context.Relationship,
                    identity = string.IsNullOrWhiteSpace(npc.Identity)
                        ? RidgesideVillageCompatibility.BuildConversationIdentityPrompt(npc.NpcDisplayName, npc.NpcName) : npc.Identity,
                    experiences = npc.Experiences,
                    todayConversation = CompactConversation(npc.ConversationExcerpt),
                    giftCandidates = npc.GiftCandidates.Select(candidate => new
                    {
                        giftCandidateId = candidate.CandidateId,
                        name = candidate.DisplayName,
                        hint = candidate.Hint,
                    }),
                };
            }),
        };
        return (L.IsChinese ? "按各人物当天对话判断是否寄信；experiences只是历史背景，不是今天的新事件。\n"
                : "Decide from each NPC's source-day dialogue; experiences are historical context, not new events today.\n")
               + JsonSerializer.Serialize(payload, ExperienceContextPolicy.ReadableJson);
    }

    private static IReadOnlyList<string> CompactConversation(string? transcript)
    {
        string[] lines = (transcript ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return new[] { "无" };

        return lines
            .TakeLast(6)
            .Select(line => Clean(line, 360, "无"))
            .ToArray();
    }

    private static OvernightMailPlanDecision EmptyDecision(string failure = "")
        => new()
        {
            Gifts = Array.Empty<OvernightMailGiftDecision>(),
            UsedFallback = failure.Length > 0,
            FailureReason = failure,
        };

    private static bool ContainsMailControlCode(string? value)
    {
        string text = value ?? string.Empty;
        return text.Contains('%')
               || text.Contains('^')
               || text.Contains("[#]", StringComparison.Ordinal)
               || text.Any(character => char.IsControl(character) && character is not '\n' and not '\t');
    }

    private static string CleanLetterBody(string? value)
    {
        string clean = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return clean.Length <= 700 ? clean : clean[..700];
    }

    private static bool ContainsNormalizedName(string value, string displayName)
    {
        string haystack = NormalizeName(value);
        string needle = NormalizeName(displayName);
        return needle.Length > 0 && haystack.Contains(needle, StringComparison.Ordinal);
    }

    private static string NormalizeName(string value)
        => new((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private static string NormalizeTag(string? value)
    {
        string tag = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (tag.Length > 64)
            tag = tag[..64];
        return tag.All(character => character is >= 'a' and <= 'z'
                                    or >= '0' and <= '9'
                                    or '_')
            ? tag
            : string.Empty;
    }

    private static string Clean(string? value, int maximumCharacters, string fallback)
    {
        string clean = (value ?? string.Empty).Replace('\r', ' ').Trim();
        if (clean.Length == 0)
            return fallback;
        return clean.Length <= maximumCharacters ? clean : clean[..maximumCharacters] + "…";
    }
}
