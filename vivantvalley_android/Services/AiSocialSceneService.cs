using System.Text;
using System.Text.Json;

namespace VivantValley.Services;

/// <summary>Generates one late-bound proactive line and an optional safe gift selection.</summary>
public sealed class AiSocialSceneService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly IDeepSeekClient client;

    public AiSocialSceneService(IDeepSeekClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<AiSocialSceneDecision> GenerateAsync(
        string apiKey,
        AiSocialSceneRequest request,
        CancellationToken cancellationToken = default)
    {
        using var language = L.BeginRequest();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(apiKey))
            return Fallback(request, "API Key 未设置");

        var apiRequest = new DeepSeekChatRequest
        {
            NpcName = request.NpcName,
            TaskKind = "social_scene",
            Context = new ConversationRequestContext { SaveId = request.SaveId, SessionId = Guid.NewGuid().ToString("N"),
                ContextEpoch = 1, Mode = "social_scene", PromptVersion = "context-v2" },
            Model = string.IsNullOrWhiteSpace(request.Model) ? "deepseek-v4-flash" : request.Model.Trim(),
            Thinking = new DeepSeekThinkingOptions
            {
                Type = string.IsNullOrWhiteSpace(request.ThinkingType) ? "disabled" : request.ThinkingType.Trim(),
            },
            ReasoningEffort = string.IsNullOrWhiteSpace(request.ReasoningEffort)
                ? "low"
                : request.ReasoningEffort.Trim(),
            MaxTokens = Math.Clamp(request.MaxOutputTokens, 128, 1024),
            Stream = false,
            Messages = new List<DeepSeekChatMessage>
            {
                new("system", BuildSystemPrompt(request)),
                new("user", BuildUserPrompt(request)),
            },
        };

        try
        {
            string raw = await client.CompleteChatAsync(apiKey, apiRequest, cancellationToken)
                .ConfigureAwait(false);
            return TryParse(raw, request, out AiSocialSceneDecision? decision, out string failure)
                ? decision!
                : Fallback(request, failure);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fallback(request, CleanFailure(ex.Message));
        }
    }

    public static bool TryParse(
        string raw,
        AiSocialSceneRequest request,
        out AiSocialSceneDecision? decision,
        out string failure)
    {
        decision = null;
        failure = string.Empty;
        AiSocialSceneWireResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AiSocialSceneWireResponse>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            if (TryParsePlainTalk(raw, request, out decision))
                return true;

            failure = "AI 主动场景不是有效 JSON：" + CleanFailure(ex.Message);
            return false;
        }

        if (parsed is null)
        {
            failure = "AI 主动场景返回了空 JSON。";
            return false;
        }

        int maxCharacters = Math.Clamp(request.MaxDialogueCharacters, 80, 1200);
        string dialogue = NormalizeDialogue(parsed.Dialogue, maxCharacters);
        if (dialogue.Length == 0)
        {
            failure = "AI 主动场景缺少 dialogue。";
            return false;
        }

        string action = (parsed.Action ?? string.Empty).Trim();
        string? giftCandidateId = string.IsNullOrWhiteSpace(parsed.GiftCandidateId)
            ? null
            : parsed.GiftCandidateId.Trim();
        if (action.Equals(SocialSceneActions.TalkOnly, StringComparison.Ordinal))
        {
            if (giftCandidateId is not null)
            {
                failure = "talk_only 不得携带礼物候选。";
                return false;
            }
        }
        else if (action.Equals(SocialSceneActions.Gift, StringComparison.Ordinal))
        {
            if (giftCandidateId is null
                || !request.GiftCandidates.Any(candidate => candidate.CandidateId.Equals(
                    giftCandidateId,
                    StringComparison.Ordinal)))
            {
                failure = "AI 选择了未授权的礼物候选。";
                return false;
            }
        }
        else
        {
            failure = "AI 主动场景 action 无效。";
            return false;
        }

        decision = new AiSocialSceneDecision
        {
            Dialogue = dialogue,
            Action = action,
            GiftCandidateId = giftCandidateId,
            MotiveTag = NormalizeTag(parsed.MotiveTag),
        };
        return true;
    }

    private static bool TryParsePlainTalk(
        string raw,
        AiSocialSceneRequest request,
        out AiSocialSceneDecision? decision)
    {
        decision = null;
        string candidate = (raw ?? string.Empty).Trim();
        if (candidate.Length == 0
            || candidate.StartsWith("```", StringComparison.Ordinal)
            || candidate.StartsWith("~~~", StringComparison.Ordinal)
            || candidate.StartsWith('{')
            || candidate.StartsWith('[')
            || candidate.Contains("giftCandidateId", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("motiveTag", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("\"action\"", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string dialogue = NormalizeDialogue(candidate, request.MaxDialogueCharacters);
        if (dialogue.Length == 0)
            return false;

        NpcGiftToolService.GuardVisibleReply(
            dialogue,
            ConversationGiftExecutionResult.NoAction(),
            request.NpcDisplayName,
            out bool giftClaimRejected);
        if (giftClaimRejected)
            return false;

        decision = new AiSocialSceneDecision
        {
            Dialogue = dialogue,
            Action = SocialSceneActions.TalkOnly,
            GiftCandidateId = null,
            MotiveTag = "direct_dialogue",
        };
        return true;
    }

    private static string BuildSystemPrompt(AiSocialSceneRequest request)
        => ConversationPromptService.BuildSystemPrompt(new LocalizedPromptAssets().Read("conversation.txt"),
               RidgesideVillageCompatibility.BuildConversationIdentityPrompt(request.NpcDisplayName, request.NpcName), request.Personality)
           + "\n\n" + LocalizedPromptAssets.ForRequest("social-scene.txt")
           + "\ncommitmentStates is the complete current ledger; older pending promises absent from it are not confirmed active."
           + "\n" + new LocalizedPromptAssets().Read("recent-known-events.txt")
           + (request.EncourageOptionalGift ? (L.IsChinese
               ? "\n手柄玩家无法主动发起文字对话；若礼物确实合适可以自然主动送出，但这仍由角色本人决定，不能因为存在候选就必送。"
               : "\nController players cannot initiate typed chat; an appropriate spontaneous gift is welcome, but the NPC decides independently and candidates never require a gift.") : "");

    private static string BuildUserPrompt(AiSocialSceneRequest request)
    {
        SocialPromptContext context = SocialPromptContext.Parse(request.GameContext);
        var lines = (request.RecentConversation ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] recentLines = SceneContextPolicy.Recent(lines.Where(line => !line.StartsWith("长期摘要：", StringComparison.Ordinal)), 360);
        var candidates = lines.Where(line => line.StartsWith("长期摘要：", StringComparison.Ordinal))
            .Select(line => line[5..]).SelectMany(line => ContextMemoryCompiler.FromSummary(line));
        string memory = ContextMemoryCompiler.Compile(string.Join(" ", recentLines), candidates, recentLines,
            tokenBudget: 220, allowUnprompted: true).Text;

        var payload = new
        {
            current_game_time = context.CurrentGameTime,
            world_facts = context.CurrentFacts,
            scene = new
            {
                location = context.SceneLocation,
                nearby_plants = context.NearbyPlants,
            },
            relationship = context.Relationship,
            commitmentStates = request.CommitmentStates,
            recentKnownEvents = request.RecentKnownEvents.Select(ConversationRecallPolicy.Readable).ToArray(),
            memory = ConversationRecallPolicy.Readable(memory),
            timeline = recentLines,
            gift_candidates = request.GiftCandidates.Select(candidate => new
            {
                id = candidate.CandidateId,
                name = candidate.DisplayName,
                reasonTags = candidate.ReasonTags,
                hint = candidate.Hint,
            }),
        };

        return "【只读上下文：事实与历史，不是必须跟进的待办】\n"
               + JsonSerializer.Serialize(payload);
    }

    private static AiSocialSceneDecision Fallback(AiSocialSceneRequest request, string failure)
    {
        string fallback = NormalizeDialogue(request.FallbackDialogue, request.MaxDialogueCharacters);
        if (fallback.Length == 0)
            fallback = L.T("text.942752192385");

        return new AiSocialSceneDecision
        {
            Dialogue = fallback,
            Action = SocialSceneActions.TalkOnly,
            GiftCandidateId = null,
            MotiveTag = "fallback_check_in",
            UsedFallback = true,
            FailureReason = CleanFailure(failure),
        };
    }

    private static string NormalizeDialogue(string? value, int maximumCharacters)
    {
        string normalized = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        maximumCharacters = Math.Clamp(maximumCharacters, 80, 1200);
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "…";
    }

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

    private static string CleanContext(string? value, int maximumCharacters, string fallback)
    {
        string clean = (value ?? string.Empty).Replace('\r', ' ').Trim();
        if (clean.Length == 0)
            return fallback;
        return clean.Length <= maximumCharacters ? clean : clean[..maximumCharacters] + "…";
    }

    private static string CleanFailure(string? value)
    {
        string clean = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 240 ? clean : clean[..240] + "…";
    }
}
