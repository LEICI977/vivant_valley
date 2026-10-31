namespace VivantValley.Services;

/// <summary>Generates dialogue flavor for a deterministic proactive encounter.</summary>
public sealed class ProactiveSceneService
{
    private readonly IDeepSeekClient deepSeekClient;

    public ProactiveSceneService(IDeepSeekClient deepSeekClient)
    {
        this.deepSeekClient = deepSeekClient ?? throw new ArgumentNullException(nameof(deepSeekClient));
    }

    public async Task<string> GenerateAsync(
        string apiKey,
        string npcSystemPrompt,
        NpcConversationMemory memory,
        PlannedNpcEncounter encounter,
        string giftDisplayName,
        ConversationEngineOptions options,
        CancellationToken cancellationToken)
    {
        using var language = L.BeginRequest();
        string storyBrief = string.IsNullOrWhiteSpace(encounter.AiBrief)
            ? (L.IsChinese ? "你记得玩家最近说过的话，今天主动走近玩家，关心对方，并准备当面送出一份小礼物。" : "You remember the player's recent words and approach to check in and offer a small gift.")
            : encounter.AiBrief.Trim();
        string sceneContract = string.IsNullOrWhiteSpace(encounter.GiftItemId)
            ? (L.IsChinese ? "\n你在同一地点主动走近玩家。这次不交付物品，重点是把剧情中的问题或邀请自然地说出来。" : "\nYou approach in the same location. No item is delivered; naturally express the scene's question or invitation.")
            : (L.IsChinese ? "\n你在同一地点主动走近玩家，准备当面送出一份小礼物：" : "\nYou approach in the same location to hand over this gift: ") + giftDisplayName + ".";
        var messages = new List<DeepSeekChatMessage>
        {
            new(
                "system",
                LocalizedPromptAssets.ForRequest("proactive-scene.txt")
                + "\n\n" + npcSystemPrompt.Trim()
                + (L.IsChinese ? "\n【本次确定发生的剧情节点】" : "\nScheduled scene: ")
                 + storyBrief
                 + sceneContract),
        };

        string recalledMemory = ConversationMemoryPolicy.BuildRelevantRecall(
            memory.Summary,
            memory.PlayerId,
            memory.NpcName,
            encounter.ActionId,
            encounter.TriggerExcerpt,
            memory.TotalTurns);
        if (!string.IsNullOrWhiteSpace(recalledMemory))
        {
            messages.Add(new DeepSeekChatMessage(
                "system",
                (L.IsChinese ? "偶然想起的带日期旧事；只作为模糊背景，不得覆盖当前事实或角色性格：\n" : "Dated recollection, background only; never overrides current facts or personality:\n") + recalledMemory));
        }

        int first = Math.Max(0, memory.Messages.Count - Math.Min(8, options.MaxContextMessages));
        for (int index = first; index < memory.Messages.Count; index++)
        {
            ConversationMemoryMessage message = memory.Messages[index];
            if (message is null || string.IsNullOrWhiteSpace(message.Content))
                continue;

            string role = message.Role?.Trim().ToLowerInvariant() == "assistant" ? "assistant" : "user";
            messages.Add(new DeepSeekChatMessage(role, message.Content.Trim()));
        }

        messages.Add(new DeepSeekChatMessage(
            "user",
            (L.IsChinese ? "玩家最近一次提到的内容是：\n" : "The player last mentioned:\n")
            + (string.IsNullOrWhiteSpace(encounter.TriggerExcerpt) ? (L.IsChinese ? "（没有可引用的原文，请自然地表达关心。）" : "No exact quote; express concern naturally.") : encounter.TriggerExcerpt)
            + (L.IsChinese ? "\n现在直接说出本次主动来访的台词。" : "\nNow speak directly to the player for this encounter.")));

        var request = new DeepSeekChatRequest
        {
            NpcName = memory.NpcName,
            TaskKind = "proactive_scene",
            Model = options.Model,
            Messages = messages,
            Thinking = new DeepSeekThinkingOptions { Type = options.ThinkingType },
            ReasoningEffort = options.ReasoningEffort,
            MaxTokens = Math.Clamp(options.MaxOutputTokens, 128, 512),
            Stream = false,
        };

        return await deepSeekClient.CompleteChatAsync(apiKey, request, cancellationToken).ConfigureAwait(false);
    }
}
