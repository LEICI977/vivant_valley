using System.Text.Json;

namespace VivantValley.Services;

/// <summary>Reads optional provider usage already present in complete responses.
/// Never changes a request, requests extra streaming metadata, or retains text.</summary>
public static class AiUsageDiagnostics
{
    private static readonly object Gate = new();
    private static long responses, withUsage, inputTokens, outputTokens, cacheReportedInput, cachedInput, cacheReports;
    private static long contextBuilds, newEpochs, overBudget;

    public static void RecordContext(bool cold, bool exceedsTarget)
    {
        lock (Gate) { contextBuilds++; if (cold) newEpochs++; if (exceedsTarget) overBudget++; }
    }

    public static void RecordResponse(string json)
    {
        AiReportedUsage? usage = ReadUsage(json);
        lock (Gate)
        {
            responses++;
            if (usage is null) return;
            withUsage++;
            inputTokens += usage.InputTokens;
            outputTokens += usage.OutputTokens;
            if (usage.CachedInputTokens is { } cached)
            { cacheReportedInput += usage.InputTokens; cachedInput += cached; cacheReports++; }
        }
    }

    public static string Describe()
    {
        lock (Gate)
            return $"本次启动已收到{responses}个非流式完整响应；其中{withUsage}个返回usage，"
                + $"已报告输入{inputTokens}、输出{outputTokens} token；{responses - withUsage}个未报告。"
                + $"缓存报告{cacheReports}个，已报告样本缓存{cachedInput}/{cacheReportedInput}输入 token。"
                + $"上下文构建{contextBuilds}次，新阶段{newEpochs}、续接{contextBuilds-newEpochs}、估算超目标{overBudget}次。"
                + "估算包含预留空间，不是供应商分词；新阶段/续接不等于缓存未命中/命中。"
                + "流式响应、未返回响应的请求及服务端计费未计入，不是完整账单。";
    }

    public static AiReportedUsage? ReadUsage(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("usage", out JsonElement usage)
                || usage.ValueKind != JsonValueKind.Object) return null;
            if (!ReadCount(usage, "prompt_tokens", out long input) || !ReadCount(usage, "completion_tokens", out long output)) return null;
            long? cached = null;
            if (ReadCount(usage, "prompt_cache_hit_tokens", out long hit) && hit <= input) cached = hit;
            else if (usage.TryGetProperty("prompt_tokens_details", out JsonElement details)
                && details.ValueKind == JsonValueKind.Object && ReadCount(details, "cached_tokens", out hit) && hit <= input) cached = hit;
            return new AiReportedUsage(input, output, cached);
        }
        catch (JsonException) { return null; }
    }

    private static bool ReadCount(JsonElement usage, string key, out long count)
    {
        count = 0;
        return usage.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out count) && count >= 0 && count <= 1_000_000_000;
    }
}

public sealed record AiReportedUsage(long InputTokens, long OutputTokens, long? CachedInputTokens = null);
