using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace VivantValley.Services;

/// <summary>
/// Runs the manual-conversation state machine inside the SMAPI process. Model
/// calls happen on background tasks; game effects are delegated to the caller,
/// which marshals them onto the game thread.
/// </summary>
internal sealed class InProcessConversationEngine
{
    private const int PendingConfirmationTtlMinutes = 10;
    private const string FinalToolName = "submit_final_response";
    private const string TurnDecisionToolName = "decide_turn";
    private const int MaximumWorldFactCharacters = ConversationContextBudget.WorldCharacters;

    private static readonly HashSet<string> ActionToolNames = new(StringComparer.Ordinal)
    {
        NpcGiftToolNames.GiveGift,
        NpcMoveToolNames.MoveTo,
        NpcMineGuardToolNames.InviteMineGuard,
        NpcFishingToolNames.InviteFishingCompanion,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ConversationToolProviderClient providerClient;
    private readonly ConversationPromptService prompts;
    private readonly Func<GameBridgeToolRequest, Task<GameBridgeToolResult>> toolExecutor;
    private readonly ConcurrentDictionary<string, AppendOnlyContextSession<ConversationProviderMessage>> contextSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingConfirmation> pendingConfirmations = new(StringComparer.Ordinal);

    public InProcessConversationEngine(
        ConversationToolProviderClient providerClient,
        Func<GameBridgeToolRequest, Task<GameBridgeToolResult>> toolExecutor,
        ConversationPromptService? prompts = null)
    {
        this.providerClient = providerClient ?? throw new ArgumentNullException(nameof(providerClient));
        this.toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        this.prompts = prompts ?? new ConversationPromptService();
    }

    public async Task<LangGraphResponse> DecideAsync(
        NpcContextSnapshot snapshot,
        AiRuntimeProfile profile,
        string requestId,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("requestId cannot be empty.", nameof(requestId));
        if (!snapshot.Mode.Equals("conversation", StringComparison.OrdinalIgnoreCase))
            throw new LangGraphValidationException("Only conversation mode is supported.");

        using var language = L.BeginRequest();
        CleanupExpiredConfirmations();
        ConversationPromptSettings settings = prompts.Load();
        var run = new ConversationRun(
            requestId.Trim(),
            snapshot,
            profile,
            Math.Clamp(maxOutputTokens, 128, 2048),
            ConversationPromptService.BuildSystemPrompt(settings.SystemRules, snapshot.SystemPrompt, snapshot.PersonalitySettings),
            settings.FinalRules,
            ConversationContextBuilder.BuildWorldFacts(snapshot.WorldState, settings.WorldFactFields), L.Locale, snapshot.NpcName.Trim());
        PrepareContext(run);
        ActionChoice choice = await ChooseActionAsync(run, cancellationToken).ConfigureAwait(false);

        if (choice.FinalDecision is not null)
            return CompleteResponse(run, NormalizeDecision(choice.FinalDecision, toolCall: null, execution: null));

        if (choice.ToolCall is null)
        {
            LangGraphDecision decision = await FinalizeAsync(
                    run,
                    toolCall: null,
                    choice.Draft,
                    execution: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return CompleteResponse(run, decision);
        }

        ConversationProviderToolCall toolCall = choice.ToolCall;
        if (toolCall.Name.Equals(NpcGiftToolNames.GiveGift, StringComparison.Ordinal))
        {
            GameBridgeToolResult gameResult = await ExecuteToolAsync(run, toolCall).ConfigureAwait(false);
            LangGraphToolExecution execution = ToToolExecution(gameResult);
            LangGraphDecision decision = await FinalizeAsync(
                    run,
                    toolCall,
                    draft: string.Empty,
                    execution,
                    cancellationToken)
                .ConfigureAwait(false);
            return CompleteResponse(run, decision, execution);
        }

        string resumeToken = CreateResumeToken();
        pendingConfirmations[resumeToken] = new PendingConfirmation(
            DateTimeOffset.UtcNow,
            run,
            toolCall);
        return new LangGraphResponse
        {
            RequestId = run.RequestId,
            ContextVersion = snapshot.ContextVersion,
            Confirmation = BuildConfirmation(resumeToken, run, toolCall),
        };
    }

    public async Task<LangGraphResponse> ResumeAsync(
        string requestId,
        string resumeToken,
        bool approved,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("requestId cannot be empty.", nameof(requestId));
        if (string.IsNullOrWhiteSpace(resumeToken))
            throw new ArgumentException("resumeToken cannot be empty.", nameof(resumeToken));

        CleanupExpiredConfirmations();
        if (!pendingConfirmations.TryRemove(resumeToken.Trim(), out PendingConfirmation? pending)
            || !pending.Run.RequestId.Equals(requestId.Trim(), StringComparison.Ordinal))
        {
            throw new LangGraphValidationException(
                "Action confirmation is missing, expired, or already resolved.");
        }

        using var language = L.BeginRequest(pending.Run.Language);
        GameBridgeToolResult gameResult = approved
            ? await ExecuteToolAsync(pending.Run, pending.ToolCall).ConfigureAwait(false)
            : CreateDeclinedResult(pending.Run, pending.ToolCall);
        LangGraphToolExecution execution = ToToolExecution(gameResult);
        LangGraphDecision decision = await FinalizeAsync(
                pending.Run,
                pending.ToolCall,
                draft: string.Empty,
                execution,
                cancellationToken)
            .ConfigureAwait(false);
        return CompleteResponse(pending.Run, decision, execution);
    }

    public void ClearPending()
    {
        pendingConfirmations.Clear();
        contextSessions.Clear();
    }

    private async Task<ActionChoice> ChooseActionAsync(
        ConversationRun run,
        CancellationToken cancellationToken)
    {
        if (run.Snapshot.FixedConversationSegments) return await ChooseSegmentedActionAsync(run, cancellationToken).ConfigureAwait(false);
        List<ConversationProviderMessage> initialMessages = BuildInitialMessages(run);
        IReadOnlyList<JsonElement> tools = run.Tools;
        List<ConversationProviderMessage> messages = initialMessages;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                ConversationProviderResponse response = await providerClient.CompleteAsync(
                        run.Profile,
                        messages,
                        tools,
                        toolChoice: "auto",
                        run.MaxOutputTokens,
                        cancellationToken,
                        idempotencyKey: CreateIdempotencyKey(run.RequestId, "action", attempt),
                        npcName: run.NpcName,
                        taskKind: "conversation_action",
                        context: run.RequestContext)
                    .ConfigureAwait(false);
                if (response.ToolCalls.Count == 0)
                {
                    if (string.IsNullOrWhiteSpace(response.Content))
                        throw new ConversationProtocolException(
                            "Provider returned neither text nor a conversation tool call.");
                    run.ProviderTranscript = messages.Concat(new[] { ResponseMessage(response) }).ToList();
                    return new ActionChoice(null, response.Content.Trim(), null);
                }
                if (response.ToolCalls.Count != 1)
                    throw new ConversationProtocolException(
                        "Provider returned multiple conversation tool calls.");

                ConversationProviderToolCall call = response.ToolCalls[0] with
                {
                    Name = response.ToolCalls[0].Name.Trim().ToLowerInvariant(),
                };
                ValidateToolCall(call, run.Snapshot);
                run.ProviderTranscript = messages.Concat(new[] { ResponseMessage(response) }).ToList();
                if (call.Name.Equals(FinalToolName, StringComparison.Ordinal))
                    return new ActionChoice(null, string.Empty, ParseFinalDecision(call.Arguments));
                return new ActionChoice(call, string.Empty, null);
            }
            catch (ConversationProtocolException) when (attempt == 0)
            {
                messages = new List<ConversationProviderMessage>(initialMessages)
                {
                    ConversationProviderMessage.User(
                        "协议纠正：默认不执行任何游戏动作，可以直接输出自然对话或调用 "
                        + "明确下矿护卫请求时不要改用 move_to。只有角色在当前关系和处境下"
                        + "具有独立、明确的行动意愿时才能调用动作工具。不得同时调用多个函数，"
                        + "函数参数必须是有效 JSON。"),
                };
            }
            catch (DeepSeekApiException exception) when (
                attempt == 0
                && exception.Message.Contains(L.T("text.afdd093a2874"), StringComparison.Ordinal))
            {
                messages = new List<ConversationProviderMessage>(initialMessages)
                {
                    ConversationProviderMessage.User(
                        "上一次函数参数不是有效 JSON。本次只能调用一个已提供的函数，"
                        + "或者返回普通自然对话。"),
                };
            }
            catch (DeepSeekApiException exception) when (
                attempt == 1
                && exception.Message.Contains(L.T("text.afdd093a2874"), StringComparison.Ordinal))
            {
                return new ActionChoice(
                    null,
                    "工具选择参数连续无效，本轮不执行任何游戏副作用。",
                    null);
            }
            catch (ConversationProtocolException) when (attempt == 1)
            {
                return new ActionChoice(
                    null,
                    "工具选择协议连续无效，本轮不执行任何游戏副作用。",
                    null);
            }
        }

        throw new ConversationProtocolException("Provider action selection failed.");
    }

    private async Task<LangGraphDecision> FinalizeAsync(
        ConversationRun run,
        ConversationProviderToolCall? toolCall,
        string draft,
        LangGraphToolExecution? execution,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonElement> finalTool = run.Tools;
        object forcedChoice = new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?> { ["name"] = FinalToolName },
        };
        List<ConversationProviderMessage> messages = BuildFinalMessages(run, toolCall, draft, execution);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                ConversationProviderResponse response = await providerClient.CompleteAsync(
                        run.Profile,
                        messages,
                        finalTool,
                        forcedChoice,
                        run.MaxOutputTokens,
                        cancellationToken,
                        idempotencyKey: CreateIdempotencyKey(run.RequestId, "final", attempt),
                        npcName: run.NpcName,
                        taskKind: "conversation_final",
                        context: run.RequestContext)
                    .ConfigureAwait(false);
                if (response.ToolCalls.Count != 1
                    || !response.ToolCalls[0].Name.Equals(FinalToolName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConversationProtocolException(
                        "Final provider response must contain one submit_final_response call.");
                }

                LangGraphDecision rawDecision = ParseFinalDecision(response.ToolCalls[0].Arguments);
                run.ProviderTranscript = messages.Concat(new[] { ResponseMessage(response) }).ToList();
                return NormalizeDecision(rawDecision, toolCall, execution);
            }
            catch (ConversationProtocolException) when (attempt == 0)
            {
                messages = BuildFinalMessages(run, toolCall, draft, execution);
                messages.Add(ConversationProviderMessage.User(
                    "协议纠正：现在必须调用 submit_final_response。所有字段都是必需的，"
                    + "schema_version 必须是整数 1，decision 必须是 reply，travel_barks 必须是"
                    + "字符串数组，所有 signal 值必须是数字。"));
            }
            catch (DeepSeekApiException exception) when (
                attempt == 0
                && exception.Message.Contains(L.T("text.afdd093a2874"), StringComparison.Ordinal))
            {
                messages = BuildFinalMessages(run, toolCall, draft, execution);
                messages.Add(ConversationProviderMessage.User(
                    "submit_final_response 参数必须是符合定义的有效 JSON。"));
            }
        }

        throw new ConversationProtocolException("Provider final response failed.");
    }

    private static string CreateIdempotencyKey(string requestId, string phase, int attempt)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"vivant-valley:{requestId}:{phase}:{attempt}"));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private async Task<GameBridgeToolResult> ExecuteToolAsync(
        ConversationRun run,
        ConversationProviderToolCall toolCall)
    {
        JsonElement arguments = toolCall.Arguments;
        var request = new GameBridgeToolRequest
        {
            RequestId = run.RequestId,
            ToolCallId = toolCall.Id,
            PlayerId = run.Snapshot.PlayerId,
            NpcName = run.Snapshot.NpcName,
            ActionId = run.Snapshot.ActionId,
            ContextVersion = run.Snapshot.ContextVersion,
            Tool = toolCall.Name,
            CandidateKey = GetOptionalString(arguments, "candidate_key"),
            DestinationKey = GetOptionalString(arguments, "destination_key"),
            ReasonTag = GetOptionalString(arguments, "reason_tag"),
        };
        return await toolExecutor(request).ConfigureAwait(false);
    }

    private static GameBridgeToolResult CreateDeclinedResult(
        ConversationRun run,
        ConversationProviderToolCall toolCall)
    {
        string destinationKey = GetOptionalString(toolCall.Arguments, "destination_key");
        string? displayName = run.Snapshot.AllowedMoveDestinations.FirstOrDefault(value =>
            value.DestinationKey.Equals(destinationKey, StringComparison.Ordinal))?.DisplayName;
        string message = toolCall.Name switch
        {
            NpcMineGuardToolNames.InviteMineGuard => L.T("text.14d2bb1a1de8"),
            NpcFishingToolNames.InviteFishingCompanion => L.T("text.3ae594fff45e"),
            _ => "The player chose not to start this journey.",
        };
        return new GameBridgeToolResult
        {
            RequestId = run.RequestId,
            ToolCallId = toolCall.Id,
            ContextVersion = run.Snapshot.ContextVersion,
            Tool = toolCall.Name,
            Status = "rejected",
            Ok = false,
            DestinationKey = string.IsNullOrWhiteSpace(destinationKey) ? null : destinationKey,
            DisplayName = displayName,
            ReasonCode = "player_declined",
            Message = message,
            ReceiptId = $"{run.RequestId}:{toolCall.Id}:declined",
        };
    }

    private static LangGraphMoveConfirmation BuildConfirmation(
        string resumeToken,
        ConversationRun run,
        ConversationProviderToolCall toolCall)
    {
        string destinationKey = GetOptionalString(toolCall.Arguments, "destination_key");
        string kind;
        string displayName;
        if (toolCall.Name.Equals(NpcMineGuardToolNames.InviteMineGuard, StringComparison.Ordinal))
        {
            kind = "mine_guard_confirmation";
            displayName = "矿井";
        }
        else if (toolCall.Name.Equals(NpcFishingToolNames.InviteFishingCompanion, StringComparison.Ordinal))
        {
            kind = "fishing_confirmation";
            displayName = "钓鱼地点";
        }
        else
        {
            kind = "move_confirmation";
            displayName = run.Snapshot.AllowedMoveDestinations.First(value =>
                value.DestinationKey.Equals(destinationKey, StringComparison.Ordinal)).DisplayName;
        }

        return new LangGraphMoveConfirmation
        {
            Kind = kind,
            ResumeToken = resumeToken,
            ToolCallId = toolCall.Id,
            DestinationKey = destinationKey,
            DisplayName = displayName,
            NpcDisplayName = run.Snapshot.NpcDisplayName,
        };
    }

    private void PrepareSegmentedContext(ConversationRun run, string scope)
    {
        var snapshot = run.Snapshot;
        run.Tools = new[] { BuildTurnDecisionTool(allowAction: true) };
        var live = BuildCurrentPayload(run);
        foreach (string field in new[] { "allowed_gift_candidates", "allowed_move_destinations", "memory", "story_facts" }) live.Remove(field);
        var messages = new List<ConversationProviderMessage>
        {
            ConversationProviderMessage.System(run.SystemPrompt + "\n\n" + new LocalizedPromptAssets().Read("segmented-conversation.txt")),
            ConversationProviderMessage.User("[Current game facts; authoritative]\n" + JsonSerializer.Serialize(live, JsonOptions)),
        };
        messages.Add(ConversationProviderMessage.User("[Memory; stable segment background, not tasks]\n" + ConversationRecallPolicy.Readable(snapshot.SegmentMemory)));
        messages.Add(ConversationProviderMessage.User("[Timeline begins; previous segment summary, not current facts]\n" + ConversationRecallPolicy.Readable(snapshot.SegmentSummary)));
        if (snapshot.SegmentBridge.Count > 0)
            messages.Add(ConversationProviderMessage.User(L.IsChinese ? "【上一段最后一轮，已归档，仅用于承接】" : "[Last archived turn, for continuity only]"));
        var references = SegmentMemoryPolicy.NewContextReferences(snapshot.SegmentMessages.Concat(snapshot.SegmentBridge), snapshot.MemorySummary,
            snapshot.RecentKnownEvents, snapshot.RequestMetadata.GetValueOrDefault("game_date", ""), snapshot.SegmentMemory + "\n" + snapshot.SegmentSummary);
        foreach (var message in snapshot.SegmentBridge.Concat(snapshot.SegmentMessages).Concat(references))
        {
            if (string.IsNullOrWhiteSpace(message.Content)) continue;
            bool chat = string.IsNullOrWhiteSpace(message.Source) || message.Source == ConversationMemorySources.AiChat;
            messages.Add(chat && message.Role == "user" ? ConversationProviderMessage.User(message.Content.Trim())
                : chat && message.Role == "assistant" ? ConversationProviderMessage.Assistant(message.Content.Trim())
                : ConversationProviderMessage.User(SegmentMemoryPolicy.RenderContextRecord(message)));
        }
        messages.Add(ConversationProviderMessage.User(snapshot.PlayerInput.Trim()));
        int estimated = messages.Sum(m => PromptTokenEstimate.Estimate(JsonSerializer.Serialize(m, JsonOptions)) + 6)
            + PromptTokenEstimate.Estimate(JsonSerializer.Serialize(run.Tools, JsonOptions)) + run.MaxOutputTokens;
        AiUsageDiagnostics.RecordContext(snapshot.SegmentMessages.Count == 0, estimated > 32000);
        // Never silently delete dialogue. Extremely long custom prompts or accumulated failed summaries need an explicit error.
        if (estimated > 32000) throw new DeepSeekConfigurationException(L.IsChinese
            ? "本段上下文过长；完整对话已保留，请先完成分段总结或缩短自定义提示词。"
            : "This segment exceeds the working context limit. Dialogue is retained; finish its summary or shorten custom instructions.");
        run.SegmentedMessages = messages;
        run.RequestContext = new ConversationRequestContext { SaveId = snapshot.RequestMetadata.GetValueOrDefault("save_id"),
            SessionId = scope[..32], ContextEpoch = snapshot.ConversationSegment + 1, Mode = "conversation", PromptVersion = "segments-action-v4",
            TurnId = run.RequestId, VoiceEnabled = snapshot.RequestMetadata.GetValueOrDefault("speech_enabled") == "true",
            SpeechLanguage = snapshot.RequestMetadata.GetValueOrDefault("speech_language"), VoiceOptionId = snapshot.RequestMetadata.GetValueOrDefault("speech_voice") };
    }

    private async Task<ActionChoice> ChooseSegmentedActionAsync(ConversationRun run, CancellationToken cancellation)
    {
        var messages = BuildInitialMessages(run);
        string? selectedKind = null;
        // Intent and legal-option selection each permit one bounded protocol correction.
        // No free-text response may bypass the explicit decision, and nothing here executes an action.
        for (int stage = 0; stage < 2; stage++)
        {
            var stageMessages = messages.ToList();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    object choice = stage == 0
                        ? new { type = "function", function = new { name = TurnDecisionToolName } }
                        : "required";
                    var response = await providerClient.CompleteAsync(run.Profile, messages, run.Tools, choice, run.MaxOutputTokens,
                        cancellation, CreateIdempotencyKey(run.RequestId, "segment-decision-" + stage, attempt), run.NpcName,
                        "conversation_action", run.RequestContext).ConfigureAwait(false);
                    if (response.ToolCalls.Count != 1 || string.IsNullOrWhiteSpace(response.ToolCalls[0].Id))
                        throw new ConversationProtocolException("Exactly one structured turn decision or offered action is required.");
                    var call = response.ToolCalls[0];
                    if (!run.Tools.Any(t => t.GetProperty("function").GetProperty("name").GetString() == call.Name))
                        throw new ConversationProtocolException("This tool is not offered at the current decision stage.");
                    if (call.Name == TurnDecisionToolName)
                    {
                        var decision = ParseTurnDecision(call.Arguments, allowAction: stage == 0, out string kind);
                        if (decision is not null)
                        {
                            run.ProviderTranscript = messages.Append(ResponseMessage(response)).ToList();
                            return new ActionChoice(null, "", decision);
                        }
                        selectedKind = kind;
                        object options = kind switch
                        {
                            "gift" => run.Snapshot.AllowedTools,
                            "move" => HasMineGuardIntent(run.Snapshot.PlayerInput) || HasFishingIntent(run.Snapshot.PlayerInput)
                                ? Array.Empty<LangGraphMoveDestination>() : run.Snapshot.AllowedMoveDestinations,
                            "mine" => new { available = run.Snapshot.MineGuardAvailable },
                            "fishing" => new { available = run.Snapshot.FishingCompanionAvailable },
                            _ => throw new ConversationProtocolException("Unknown action category."),
                        };
                        // The code reads options only after NPC intent. The receipt is not an execution result.
                        messages.Add(ResponseMessage(response));
                        messages.Add(ConversationProviderMessage.Tool(call.Id, JsonSerializer.Serialize(new
                        {
                            kind, options, executed = false,
                            next_step = L.IsChinese
                                ? "检查当前选项后调用对应动作，或用 decide_turn 拒绝、澄清、改日、聊天；尚未执行，不得声称已经行动。"
                                : "Check these options, then call the offered action or decide_turn to decline, clarify, defer or chat. Nothing has executed.",
                        }, JsonOptions)));
                        string actionTool = kind switch { "gift" => NpcGiftToolNames.GiveGift, "move" => NpcMoveToolNames.MoveTo,
                            "mine" => NpcMineGuardToolNames.InviteMineGuard, _ => NpcFishingToolNames.InviteFishingCompanion };
                        run.Tools = BuildProviderTools(run.Snapshot)
                            .Where(t => t.GetProperty("function").GetProperty("name").GetString() == actionTool)
                            .Append(BuildTurnDecisionTool(allowAction: false)).ToArray();
                        break;
                    }
                    if (stage != 1 || selectedKind is null)
                        throw new ConversationProtocolException("Actions require a current options receipt after an explicit intent decision.");
                    ValidateToolCall(call, run.Snapshot);
                    run.ProviderTranscript = messages.Append(ResponseMessage(response)).ToList();
                    // Existing confirmation/execution/finalization owns all side effects and visible action dialogue.
                    run.Tools = new[] { BuildFinalResponseTool() };
                    return new ActionChoice(call, "", null);
                }
                catch (ConversationProtocolException ex) when (attempt == 0)
                {
                    // Discard malformed drafts/calls instead of creating orphan tool receipts or showing them.
                    messages = stageMessages.ToList();
                    messages.Add(ConversationProviderMessage.User((L.IsChinese
                        ? "协议纠正：必须且只能调用一个本阶段提供的工具，并完整填写参数。聊天、拒绝、澄清、改日用 decide_turn；愿意立即行动必须选择 consider_action（如提供），action_kind 指定类别，intent 简述意图，reply 留空。读过选项后只能调用当前合法动作或返回非行动决定，不能只用台词答应。"
                        : "Protocol correction: call exactly one tool offered at this stage with all required fields. Use decide_turn for chat/decline/clarify/defer. Willingness to act now requires consider_action (if offered), action_kind and a short intent, with an empty reply. After reading options, call a legal offered action or return a non-action decision; do not merely promise to act in dialogue.")
                        + (ex.RetryGuidance is null ? "" : "\n" + ex.RetryGuidance)));
                }
                catch (ConversationProtocolException ex)
                {
                    // Selection never executes game actions. Preserve the original failure for diagnostics.
                    throw new ConversationProtocolException(ex.Message, beforeExecution: true, innerException: ex);
                }
            }
        }
        throw new ConversationProtocolException("Action selection did not finish.", beforeExecution: true);
    }

    private static JsonElement BuildTurnDecisionTool(bool allowAction)
    {
        // Reuse the established dialogue/memory contract without changing what reaches the memory pipeline.
        var schema = JsonNode.Parse(BuildFinalResponseTool().GetProperty("function").GetProperty("parameters").GetRawText())!;
        var properties = schema["properties"]!;
        properties["decision"] = JsonSerializer.SerializeToNode(new { type = "string", @enum = allowAction
            ? new[] { "chat", "decline", "clarify", "defer", "consider_action" } : new[] { "chat", "decline", "clarify", "defer" } });
        properties["reply"] = JsonSerializer.SerializeToNode(new { type = "string", description = L.IsChinese
            ? "非行动决定填写口头台词；consider_action 必须为空，执行后才生成台词。"
            : "Spoken dialogue for a non-action decision; must be empty for consider_action until execution." });
        properties["travel_barks"] = JsonSerializer.SerializeToNode(new
        {
            type = "array", items = new { type = "string", minLength = 1 }, maxItems = 0,
            description = L.IsChinese
                ? "当前只是决策，尚未执行行动；travel_barks 必须填写空数组 []。同行台词只能在真实移动成功后生成。"
                : "This is a decision before execution; travel_barks must be the empty array []. Travel dialogue is only generated after a successful move receipt.",
        });
        properties["action_kind"] = JsonSerializer.SerializeToNode(new { type = "string", @enum = allowAction
            ? new[] { "none", "gift", "move", "mine", "fishing" } : new[] { "none" } });
        properties["intent"] = JsonSerializer.SerializeToNode(new { type = "string", maxLength = 240, description = L.IsChinese
            ? "consider_action 的简短行动意图，不是台词；非行动决定必须为空。"
            : "Short action intent for consider_action, never spoken; must be empty for non-action decisions." });
        schema["required"]!.AsArray().Add("action_kind");
        schema["required"]!.AsArray().Add("intent");
        return BuildTool(TurnDecisionToolName, L.IsChinese
            ? "按人格、实际关系和当前处境明确决定本轮回应：聊天、拒绝、澄清、改日，或愿意现在行动。不是执行工具。"
            : "Explicitly decide this turn according to personality, real relationship and circumstances: chat, decline, clarify, defer, or willingness to act now. This does not execute anything.", schema);
    }

    private static LangGraphDecision? ParseTurnDecision(JsonElement args, bool allowAction, out string kind)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw new ConversationProtocolException("Turn decision must be an object.");
        var fields = new[] { "schema_version", "decision", "reply", "travel_barks", "memory_update", "action_kind", "intent" };
        // Speech is optional hosted metadata; it must never change action validation or visible dialogue.
        var businessFields = args.EnumerateObject().Where(p => p.Name != "speech").ToArray();
        if (args.EnumerateObject().Count(p => p.Name == "speech") > 1
            || businessFields.Length != fields.Length || businessFields.Any(p => !fields.Contains(p.Name))
            || !args.TryGetProperty("intent", out var intent) || intent.ValueKind != JsonValueKind.String
            || intent.GetString()!.Length > 240 || !args.TryGetProperty("reply", out var reply) || reply.ValueKind != JsonValueKind.String)
            throw new ConversationProtocolException("Turn decision fields are invalid.");
        kind = GetOptionalString(args, "action_kind");
        string decision = GetOptionalString(args, "decision");
        bool wantsAction = decision == "consider_action";
        if (wantsAction)
        {
            if (!allowAction || !new[] { "gift", "move", "mine", "fishing" }.Contains(kind)
                || string.IsNullOrWhiteSpace(intent.GetString()) || !string.IsNullOrWhiteSpace(reply.GetString()))
                throw new ConversationProtocolException("Action intent requires a known category and an empty visible reply.");
        }
        else if (!new[] { "chat", "decline", "clarify", "defer" }.Contains(decision)
            || kind != "none" || !string.IsNullOrWhiteSpace(intent.GetString()))
            throw new ConversationProtocolException("Non-action decisions cannot contain an action intent.");
        var normalized = JsonNode.Parse(args.GetRawText())!.AsObject();
        normalized.Remove("action_kind");
        normalized.Remove("intent");
        normalized.Remove("speech");
        normalized["decision"] = "reply";
        if (wantsAction) normalized["reply"] = "internal action intent";
        var result = ParseFinalDecision(JsonSerializer.SerializeToElement(normalized, JsonOptions));
        if (result.TravelBarks.Count != 0)
            throw new ConversationProtocolException("Travel barks require an executed move receipt.", retryGuidance: L.IsChinese
                ? "本次错误：你在执行前填写了同行台词。请将 decide_turn 的 travel_barks 改为 []，重新提交本阶段决定；行动意图或候选选项不代表已经执行。"
                : "Specific error: you supplied travel dialogue before execution. Set decide_turn.travel_barks to [] and resubmit this stage's decision; intent or offered options do not mean an action has executed.");
        return wantsAction ? null : result;
    }

    private void PrepareContext(ConversationRun run)
    {
        NpcContextSnapshot snapshot = run.Snapshot;
        string save = snapshot.RequestMetadata.GetValueOrDefault("save_id", "legacy");
        // Never store a credential in a scope ID or send it as routing metadata.
        string key = AppendOnlyContextSession<ConversationProviderMessage>.Hash(string.Join("\n",
            save, snapshot.PlayerId, snapshot.NpcName, snapshot.Mode, run.Profile.Provider,
            run.Profile.Endpoint.ToString(), run.Profile.Model,
            AppendOnlyContextSession<ConversationProviderMessage>.Hash(run.Profile.ApiKey)));
        if (contextSessions.Count >= 64 && !contextSessions.ContainsKey(key))
        {
            // Contexts are only working sets; evicting one cannot remove game memories.
            foreach (string old in contextSessions.Keys.Take(contextSessions.Count - 63))
                contextSessions.TryRemove(old, out _);
        }
        var session = contextSessions.GetOrAdd(key, _ => new AppendOnlyContextSession<ConversationProviderMessage>(
            message => JsonSerializer.Serialize(message, JsonOptions)));
        if (snapshot.FixedConversationSegments)
        {
            PrepareSegmentedContext(run, key);
            return;
        }
        run.Tools = BuildProviderTools(snapshot);
        var stable = new[] { ConversationProviderMessage.System(run.SystemPrompt + "\n\n"
            + new LocalizedPromptAssets().Read("context-continuity.txt") + "\n\n"
            + new LocalizedPromptAssets().Read("recent-known-events.txt")) };
        var current = new[]
        {
            ConversationProviderMessage.User(L.T("text.22a0586c4ab8")
                + JsonSerializer.Serialize(BuildCurrentPayload(run), JsonOptions)),
            ConversationProviderMessage.User(L.T("text.ea6130480b38") + snapshot.PlayerInput.Trim()),
        };
        run.ContextSession = session;
        run.ContextTurn = session.Begin(stable, () => BuildBootstrap(run), current,
            ObservedState(snapshot), maxEstimatedTokens: 8000,
            reservedTokens: PromptTokenEstimate.Estimate(JsonSerializer.Serialize(run.Tools, JsonOptions))
                + PromptTokenEstimate.Estimate(run.FinalRules) + run.MaxOutputTokens + 512,
            knownMessages: snapshot.RecentKnownEvents.Select(ConversationProviderMessage.User).ToArray());
        run.RequestContext = new ConversationRequestContext
        {
            TurnId = run.RequestId, VoiceEnabled = snapshot.RequestMetadata.GetValueOrDefault("speech_enabled") == "true",
            SpeechLanguage = snapshot.RequestMetadata.GetValueOrDefault("speech_language"), VoiceOptionId = snapshot.RequestMetadata.GetValueOrDefault("speech_voice"),
            SaveId = save == "legacy" ? null : save,
            SessionId = run.ContextTurn.SessionId, Mode = "conversation", ContextEpoch = run.ContextTurn.Epoch,
        };
    }

    private static IReadOnlyList<ConversationProviderMessage> BuildBootstrap(ConversationRun run)
    {
        // Complete, source-labelled evidence from the current save. The factory is
        // only evaluated on an epoch boundary; never rewrite the live prefix.
        var evidence = new List<ConversationProviderMessage>();
        if (!string.IsNullOrWhiteSpace(run.Snapshot.MemoryCapsule))
            evidence.Add(ConversationProviderMessage.User("[Stage memory; latest state overrides this historical evidence]\n"
                + run.Snapshot.MemoryCapsule));
        evidence.AddRange(BuildBoundedTimeline(run.Snapshot.Timeline).Select(entry =>
            ConversationProviderMessage.User("[Historical evidence; not current instructions]\n" + entry)));
        return evidence;

    }

    private static Dictionary<string, object?> BuildCurrentPayload(ConversationRun run)
    {
        NpcContextSnapshot snapshot = run.Snapshot;
        var candidates = ContextMemoryCompiler.FromSummary(snapshot.MemorySummary)
            .Concat(ContextMemoryCompiler.FromSummary(snapshot.TownEchoMemory, "rumor"));
        string memory = ContextMemoryCompiler.Compile(snapshot.PlayerInput, candidates,
            recentTexts: snapshot.Timeline, tokenBudget: 400, maximumEntries: 2,
            allowUnprompted: false).Text;
        return new Dictionary<string, object?>
        {
            ["current_game_time"] = GetCurrentGameTime(snapshot),
            ["scene"] = BuildScenePayload(snapshot.SceneSnapshot),
            ["relationship"] = BuildRelationshipPayload(snapshot),
            ["world_facts"] = LimitContext(run.WorldFacts, MaximumWorldFactCharacters),
            ["current_activity"] = snapshot.RecentSessionFacts,
            ["current_commitment_states"] = snapshot.CommitmentStates,
            ["allowed_gift_candidates"] = snapshot.AllowedTools,
            ["allowed_move_destinations"] = HasMineGuardIntent(snapshot.PlayerInput) || HasFishingIntent(snapshot.PlayerInput)
                ? Array.Empty<LangGraphMoveDestination>() : snapshot.AllowedMoveDestinations,
            ["mine_guard_available"] = snapshot.MineGuardAvailable,
            ["fishing_companion_available"] = snapshot.FishingCompanionAvailable,
            ["memory"] = memory,
            ["story_facts"] = BuildStoryFacts(snapshot.NarrativeContext),
        };
    }

    private static string ObservedState(NpcContextSnapshot snapshot)
        => snapshot.RequestMetadata.GetValueOrDefault("memory_revision", "legacy") + ":"
            + AppendOnlyContextSession<ConversationProviderMessage>.Hash(snapshot.RecentMessages
                .LastOrDefault(message => message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))?.Content.Trim() ?? "");

    private static string ExpectedState(ConversationRun run, string reply)
    {
        string revision = run.Snapshot.RequestMetadata.GetValueOrDefault("memory_revision", "legacy");
        if (long.TryParse(revision, out long value)) revision = (value + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return revision + ":" + AppendOnlyContextSession<ConversationProviderMessage>.Hash(reply.Trim());
    }

    private static ConversationProviderMessage ResponseMessage(ConversationProviderResponse response)
        => response.ToolCalls.Count == 0 ? ConversationProviderMessage.Assistant(response.Content)
            : ConversationProviderMessage.AssistantToolCall(response.ToolCalls[0], response.Content);

    private static List<ConversationProviderMessage> BuildInitialMessages(ConversationRun run)
        => run.SegmentedMessages?.ToList() ?? run.ContextTurn!.Messages.ToList();

    private static List<ConversationProviderMessage> BuildFinalMessages(
        ConversationRun run, ConversationProviderToolCall? toolCall, string draft,
        LangGraphToolExecution? execution)
    {
        var messages = run.ProviderTranscript?.ToList() ?? BuildInitialMessages(run);
        if (toolCall is not null)
        {
            messages.Add(ConversationProviderMessage.Tool(toolCall.Id, JsonSerializer.Serialize(execution, JsonOptions)));
        }
        else if (run.ProviderTranscript is null && !string.IsNullOrWhiteSpace(draft))
        {
            messages.Add(ConversationProviderMessage.User(L.T("text.92d1c257851e") + draft.Trim()));
        }
        messages.Add(ConversationProviderMessage.User(run.FinalRules));
        messages.Add(ConversationProviderMessage.User(L.T("text.7271913b3bb0")));
        return messages;
    }

    private static string GetCurrentGameTime(NpcContextSnapshot snapshot)
    {
        if (snapshot.RequestMetadata is not null
            && snapshot.RequestMetadata.TryGetValue("game_date", out string? gameDate)
            && !string.IsNullOrWhiteSpace(gameDate))
        {
            return gameDate.Trim();
        }

        return string.Empty;
    }

    private static Dictionary<string, object?> BuildScenePayload(string? sceneSnapshot)
    {
        string location = "未知";
        var objects = new List<string>();
        foreach (string line in (sceneSnapshot ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            const string locationPrefix = "- 地点：";
            const string plantsPrefix = "- 附近植物：";
            if (trimmed.StartsWith(locationPrefix, StringComparison.Ordinal))
                location = trimmed[locationPrefix.Length..].Trim();
            else if (trimmed.StartsWith(plantsPrefix, StringComparison.Ordinal))
            {
                string value = trimmed[plantsPrefix.Length..].Trim();
                if (!value.Equals("无", StringComparison.Ordinal))
                    objects.AddRange(value.Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        return new Dictionary<string, object?>
        {
            ["location"] = location,
            ["nearby_plants"] = objects.Distinct(StringComparer.Ordinal).Take(8).ToArray(),
        };
    }

    private static Dictionary<string, object?> BuildRelationshipPayload(NpcContextSnapshot snapshot)
        => new()
        {
            ["points"] = snapshot.RelationshipPoints,
            ["hearts"] = snapshot.RelationshipHearts,
            ["status"] = snapshot.RelationshipStatus,
            ["talked_today"] = snapshot.RelationshipTalkedToday,
            ["gifts_this_week"] = snapshot.RelationshipGiftsThisWeek,
        };

    private static string BuildStoryFacts(string? narrativeContext)
        => ConversationContextBudget.StoryFacts(narrativeContext);

    private static string[] BuildBoundedTimeline(IEnumerable<string>? timeline)
        => ConversationTimelinePolicy.BoundEntries(timeline);

    private static string LimitContext(string? value, int maximumCharacters)
    {
        string clean = (value ?? string.Empty).Replace('\r', ' ').Trim();
        return clean.Length <= maximumCharacters ? clean : clean[..maximumCharacters];
    }

    private static IReadOnlyList<JsonElement> BuildProviderTools(NpcContextSnapshot snapshot)
    {
        var definitions = new List<JsonElement>();
        {
            definitions.Add(BuildTool(
                NpcGiftToolNames.GiveGift,
                L.T("text.d1ea6cb2ab6e"),
                new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["candidate_key"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = L.T("text.6987cdc282c6"),
                        },
                        ["reason_tag"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = L.T("text.fcb05109ab6f"),
                        },
                    },
                    ["required"] = new[] { "candidate_key" },
                    ["additionalProperties"] = false,
                }));
        }

        {
            definitions.Add(BuildTool(
                NpcMoveToolNames.MoveTo,
                L.T("text.3f3a2fdcf91f")
                + L.T("text.55e11a112c75"),
                new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["destination_key"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = L.T("text.a193ca43255e"),
                        },
                    },
                    ["required"] = new[] { "destination_key" },
                    ["additionalProperties"] = false,
                }));
        }

        {
            definitions.Add(BuildTool(
                NpcMineGuardToolNames.InviteMineGuard,
                L.T("text.e8709a24e62b")
                + L.T("text.810749d389d7"),
                EmptyObjectSchema()));
        }
        {
            definitions.Add(BuildTool(
                NpcFishingToolNames.InviteFishingCompanion,
                L.T("text.13e6a80aeaae")
                + L.T("text.52342392f7c5"),
                EmptyObjectSchema()));
        }
        definitions.Add(BuildFinalResponseTool());
        return definitions;
    }

    private static JsonElement BuildFinalResponseTool()
        => BuildTool(
            FinalToolName,
            L.T("text.03c8d6e68cff"),
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["schema_version"] = new { type = "integer", @enum = new[] { 1 } },
                    ["decision"] = new { type = "string", @enum = new[] { "reply" } },
                    ["reply"] = new { type = "string", minLength = 1 },
                    ["travel_barks"] = new
                    {
                        type = "array",
                        items = new { type = "string", minLength = 1 },
                        maxItems = 3,
                    },
                    ["memory_update"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["summary_patch"] = new
                            {
                                type = "string",
                                maxLength = 320,
                                description = L.T("text.170d74e8b997"),
                            },
                            ["signal"] = new Dictionary<string, object?>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object?>
                                {
                                    ["valence"] = new { type = "number" },
                                    ["warmth"] = new { type = "number" },
                                    ["concern"] = new { type = "number" },
                                    ["confidence"] = new { type = "number" },
                                },
                                ["required"] = new[] { "valence", "warmth", "concern", "confidence" },
                                ["additionalProperties"] = false,
                            },
                            ["topics"] = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = L.T("text.d91e852cafd8"),
                            },
                            ["open_loops"] = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = L.T("text.c52f5b3959bd"),
                            },
                        },
                        ["required"] = new[] { "summary_patch", "signal", "topics", "open_loops" },
                        ["additionalProperties"] = false,
                    },
                },
                ["required"] = new[] { "schema_version", "decision", "reply", "travel_barks", "memory_update" },
                ["additionalProperties"] = false,
            });

    private static JsonElement BuildTool(string name, string description, object parameters)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = parameters,
                },
            },
            JsonOptions);

    private static Dictionary<string, object?> EmptyObjectSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(),
            ["additionalProperties"] = false,
        };

    private static void ValidateToolCall(
        ConversationProviderToolCall call,
        NpcContextSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(call.Id))
            throw new ConversationProtocolException("Provider tool call is missing an ID.");
        if (!ActionToolNames.Contains(call.Name) && !call.Name.Equals(FinalToolName, StringComparison.Ordinal))
            throw new ConversationProtocolException("Provider returned an unknown conversation tool.");
        if (call.Arguments.ValueKind != JsonValueKind.Object)
            throw new ConversationProtocolException("Provider tool arguments must be an object.");

        switch (call.Name)
        {
            case FinalToolName:
                ValidateFinalDecision(call.Arguments);
                return;
            case NpcGiftToolNames.GiveGift:
            {
                string candidateKey = GetOptionalString(call.Arguments, "candidate_key");
                if (candidateKey.Length == 0
                    || !snapshot.AllowedTools.Any(value => value.CandidateKey.Equals(candidateKey, StringComparison.Ordinal)))
                {
                    throw new ConversationProtocolException("Gift candidate is outside the current allowlist.");
                }
                if (call.Arguments.TryGetProperty("reason_tag", out JsonElement reason)
                    && reason.ValueKind != JsonValueKind.String)
                {
                    throw new ConversationProtocolException("Gift reason_tag must be a string.");
                }
                return;
            }
            case NpcMoveToolNames.MoveTo:
            {
                JsonProperty[] properties = call.Arguments.EnumerateObject().ToArray();
                string destinationKey = GetOptionalString(call.Arguments, "destination_key");
                if (properties.Length != 1
                    || !properties[0].NameEquals("destination_key")
                    || destinationKey.Length == 0
                    || !snapshot.AllowedMoveDestinations.Any(value =>
                        value.DestinationKey.Equals(destinationKey, StringComparison.Ordinal))
                    || HasMineGuardIntent(snapshot.PlayerInput)
                    || HasFishingIntent(snapshot.PlayerInput))
                {
                    throw new ConversationProtocolException("Move destination is outside the current allowlist.");
                }
                return;
            }
            case NpcMineGuardToolNames.InviteMineGuard:
                if (!snapshot.MineGuardAvailable || call.Arguments.EnumerateObject().Any())
                    throw new ConversationProtocolException("Mine guard is outside the current allowlist.");
                return;
            case NpcFishingToolNames.InviteFishingCompanion:
                if (!snapshot.FishingCompanionAvailable || call.Arguments.EnumerateObject().Any())
                    throw new ConversationProtocolException("Fishing companion is outside the current allowlist.");
                return;
        }
    }

    private static LangGraphDecision ParseFinalDecision(JsonElement arguments)
    {
        ValidateFinalDecision(arguments);
        LangGraphDecision? decision = arguments.Deserialize<LangGraphDecision>(JsonOptions);
        return decision ?? throw new ConversationProtocolException("Final decision is empty.");
    }

    private static void ValidateFinalDecision(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("schema_version", out JsonElement schemaVersion)
            || !schemaVersion.TryGetInt32(out int version)
            || version != 1
            || GetOptionalString(value, "decision") != "reply"
            || string.IsNullOrWhiteSpace(GetOptionalString(value, "reply")))
        {
            throw new ConversationProtocolException("submit_final_response has invalid required fields.");
        }
        if (!value.TryGetProperty("travel_barks", out JsonElement travelBarks)
            || travelBarks.ValueKind != JsonValueKind.Array
            || travelBarks.GetArrayLength() > 3
            || travelBarks.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
        {
            throw new ConversationProtocolException("travel_barks must contain at most three strings.");
        }
        if (!value.TryGetProperty("memory_update", out JsonElement memory)
            || memory.ValueKind != JsonValueKind.Object
            || !memory.TryGetProperty("summary_patch", out JsonElement summary)
            || summary.ValueKind != JsonValueKind.String
            || !memory.TryGetProperty("signal", out JsonElement signal)
            || signal.ValueKind != JsonValueKind.Object)
        {
            throw new ConversationProtocolException("memory_update is invalid.");
        }
        foreach (string name in new[] { "valence", "warmth", "concern", "confidence" })
        {
            if (!signal.TryGetProperty(name, out JsonElement number)
                || number.ValueKind != JsonValueKind.Number
                || !number.TryGetDouble(out _))
            {
                throw new ConversationProtocolException($"signal.{name} must be numeric.");
            }
        }
        foreach (string name in new[] { "topics", "open_loops" })
        {
            if (!memory.TryGetProperty(name, out JsonElement values)
                || values.ValueKind != JsonValueKind.Array
                || values.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            {
                throw new ConversationProtocolException($"memory_update.{name} must be a string array.");
            }
        }
    }

    private static LangGraphDecision NormalizeDecision(
        LangGraphDecision decision,
        ConversationProviderToolCall? toolCall,
        LangGraphToolExecution? execution)
    {
        string toolName = toolCall?.Name ?? NpcGiftToolNames.None;
        bool actionExecuted = ActionToolNames.Contains(toolName);
        decision.SchemaVersion = 1;
        decision.Decision = "reply";
        decision.Action = new LangGraphAction
        {
            Name = actionExecuted ? toolName : NpcGiftToolNames.None,
            CandidateKey = toolName == NpcGiftToolNames.GiveGift
                ? GetOptionalString(toolCall!.Arguments, "candidate_key")
                : null,
            DestinationKey = toolName == NpcMoveToolNames.MoveTo
                ? GetOptionalString(toolCall!.Arguments, "destination_key")
                : null,
            Delivery = SocialGiftDeliveryModes.Immediate,
            ReasonTag = toolName == NpcGiftToolNames.GiveGift
                ? GetOptionalString(toolCall!.Arguments, "reason_tag")
                : string.Empty,
        };
        decision.Reply = (decision.Reply ?? string.Empty).Trim();
        decision.TravelBarks ??= new List<string>();
        if (toolName != NpcMoveToolNames.MoveTo || execution?.Ok != true)
            decision.TravelBarks.Clear();
        decision.MemoryUpdate ??= new LangGraphMemoryUpdate();
        return decision;
    }

    private static LangGraphResponse CompleteResponse(
        ConversationRun run, LangGraphDecision decision, LangGraphToolExecution? execution = null)
    {
        if (run.ProviderTranscript is { Count: > 0 } transcript && run.ContextTurn is not null)
        {
            var complete = transcript.ToList();
            // The final tool is a presentation result, not a game action. Close
            // its protocol pair before a subsequent user turn can be appended.
            var finalCall = complete[^1].ToolCalls?.FirstOrDefault();
            if (finalCall is not null)
                complete.Add(ConversationProviderMessage.Tool(finalCall.Id, "{\"accepted\":true}"));
            run.ContextSession!.Complete(run.ContextTurn, complete, ExpectedState(run, decision.Reply));
        }
        return new LangGraphResponse
        {
            RequestId = run.RequestId,
            ContextVersion = run.Snapshot.ContextVersion,
            Decision = decision,
            ToolExecution = execution,
        };
    }

    private static LangGraphToolExecution ToToolExecution(GameBridgeToolResult result)
        => new()
        {
            RequestId = result.RequestId,
            ToolCallId = result.ToolCallId,
            ContextVersion = result.ContextVersion,
            Tool = result.Tool,
            Status = result.Status,
            Ok = result.Ok,
            CandidateKey = result.CandidateKey,
            DestinationKey = result.DestinationKey,
            DisplayName = result.DisplayName,
            Quantity = result.Quantity,
            ReasonCode = result.ReasonCode,
            Message = result.Message,
            ReceiptId = result.ReceiptId,
        };

    private static string GetOptionalString(JsonElement value, string propertyName)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(propertyName, out JsonElement property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string CreateResumeToken()
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return token.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private void CleanupExpiredConfirmations()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-PendingConfirmationTtlMinutes);
        foreach ((string token, PendingConfirmation pending) in pendingConfirmations)
        {
            if (pending.CreatedAtUtc < cutoff)
                pendingConfirmations.TryRemove(token, out _);
        }
    }

    private static bool HasMineGuardIntent(string input)
    {
        string text = string.Concat((input ?? string.Empty).Where(value => !char.IsWhiteSpace(value)))
            .ToLowerInvariant();
        if (text.Length == 0)
            return false;
        string[] historical = { "去过", "下过矿", "进过矿洞", "去过矿井", "以前下矿" };
        string[] companion = { "一起", "陪我", "跟我", "和我", "保护", "打怪", "保安", "护卫" };
        if (historical.Any(text.Contains) && !companion.Any(text.Contains))
            return false;
        string[] mineTerms = { "矿洞", "矿井", "矿坑", "矿里", "下矿", "矿山" };
        string[] moveTerms =
        {
            "去", "到", "进", "进入", "下", "前往", "一起", "陪我", "跟我", "和我", "随我", "带我", "带你", "走", "出发", "保护", "打怪", "保安", "护卫",
        };
        if (mineTerms.Any(text.Contains) && moveTerms.Any(text.Contains))
            return true;
        string[] english =
        {
            "mineguard", "guardmeinthemine", "guardmeinmines", "accompanymeintothemine",
            "accompanymeintothemines", "gointotheminewithme", "gominingwithme",
            "gotothemine", "gotothemines", "gointothemine", "gointothemines",
            "enterthemine", "enterthemines",
        };
        return english.Any(text.Contains);
    }

    private static bool HasFishingIntent(string input)
    {
        string text = string.Concat((input ?? string.Empty).Where(value => !char.IsWhiteSpace(value)))
            .ToLowerInvariant();
        if (text.Length == 0)
            return false;
        string[] fishingTerms = { "钓鱼", "钓竿", "鱼竿", "抛竿", "甩竿", "鱼塘", "海钓", "钓鱼点" };
        string[] moveTerms = { "一起", "陪我", "跟我", "和我", "带你", "带我", "去", "来", "陪" };
        if (fishingTerms.Any(text.Contains) && moveTerms.Any(text.Contains))
            return true;
        string[] english =
        {
            "invite_fishing_companion", "fishwithme", "gofishingwithme", "accompanymefishing",
        };
        return english.Any(text.Contains);
    }

    private sealed record ConversationRun(
        string RequestId,
        NpcContextSnapshot Snapshot,
        AiRuntimeProfile Profile,
        int MaxOutputTokens,
        string SystemPrompt,
        string FinalRules,
        string WorldFacts,
        string Language,
        string NpcName)
    {
        public AppendOnlyContextSession<ConversationProviderMessage>? ContextSession { get; set; }
        public ContextTurn<ConversationProviderMessage>? ContextTurn { get; set; }
        public List<ConversationProviderMessage>? ProviderTranscript { get; set; }
        public List<ConversationProviderMessage>? SegmentedMessages { get; set; }
        public IReadOnlyList<JsonElement> Tools { get; set; } = Array.Empty<JsonElement>();
        public ConversationRequestContext? RequestContext { get; set; }
    }

    private sealed record ActionChoice(
        ConversationProviderToolCall? ToolCall,
        string Draft,
        LangGraphDecision? FinalDecision);

    private sealed record PendingConfirmation(
        DateTimeOffset CreatedAtUtc,
        ConversationRun Run,
        ConversationProviderToolCall ToolCall);

    internal sealed class ConversationProtocolException : Exception
    {
        public string? RetryGuidance { get; }
        public bool BeforeExecution { get; }
        public string? PlayerMessage => BeforeExecution
            ? (L.IsChinese
                ? "AI 返回的行动格式不符合要求，本轮未执行行动，请重试。"
                : "The AI returned an invalid action format. No action was executed this turn; please try again.")
            : null;

        public ConversationProtocolException(string message, string? retryGuidance = null,
            bool beforeExecution = false, Exception? innerException = null) : base(message, innerException)
        {
            RetryGuidance = retryGuidance;
            BeforeExecution = beforeExecution;
        }
    }
}
