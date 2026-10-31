using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VivantValley.Services;

public sealed record AiRuntimeProfile(
    string Provider,
    string BaseUrl,
    Uri Endpoint,
    string Model,
    string ApiKey,
    string ApiKeySource,
    TimeSpan RequestTimeout,
    bool EnableThinking,
    string ReasoningEffort);

public sealed record AiProviderSettingsDraft(
    string Provider,
    string BaseUrl,
    string Model,
    string ReplacementApiKey,
    bool ClearSavedKey);

public static class AiEndpointResolver
{
    private const string ChatCompletionsPath = "/chat/completions";

    public static bool TryResolve(
        string? provider,
        string? input,
        out string normalizedBaseUrl,
        out Uri endpoint,
        out string error)
    {
        normalizedBaseUrl = string.Empty;
        endpoint = null!;
        error = string.Empty;

        string value = (input ?? string.Empty).Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed))
        {
            error = L.T("text.8a74fa28fb92");
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            error = L.T("text.b3d30bf04490");
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = L.T("text.4e841b40cd4b");
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = L.T("text.672238a408bd");
            return false;
        }
        if (parsed.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(parsed.Host))
        {
            error = L.T("text.c294b8b3c227");
            return false;
        }

        var baseBuilder = new UriBuilder(parsed);
        string basePath = NormalizePath(baseBuilder.Path);
        if (basePath.EndsWith(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase))
            basePath = basePath[..^ChatCompletionsPath.Length];
        basePath = basePath.TrimEnd('/');
        baseBuilder.Path = basePath.Length == 0 ? "/" : basePath;

        normalizedBaseUrl = baseBuilder.Uri.AbsoluteUri.TrimEnd('/');

        var endpointBuilder = new UriBuilder(baseBuilder.Uri)
        {
            Path = (basePath.Length == 0 ? string.Empty : basePath) + ChatCompletionsPath,
        };
        endpoint = endpointBuilder.Uri;
        return true;
    }

    public static string GetDefaultBaseUrl(string? provider)
        => AiProviderNames.Normalize(provider) switch
        {
            AiProviderNames.Hosted => "https://www.vivantvalley.com.cn/v1",
            AiProviderNames.OpenAI => "https://api.openai.com/v1",
            _ => "https://api.deepseek.com",
        };

    private static string NormalizePath(string value)
    {
        string path = (value ?? string.Empty).Trim();
        if (path.Length == 0 || path == "/")
            return string.Empty;
        return "/" + path.Trim('/');
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }
}

/// <summary>Routes the existing chat contract through the currently selected provider.</summary>
public sealed class AiProviderClient : IDeepSeekClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient httpClient;
    private readonly Func<AiRuntimeProfile> profileAccessor;
    private readonly AiRequestDebugWriter? debugWriter;

    public AiProviderClient(
        HttpClient httpClient,
        Func<AiRuntimeProfile> profileAccessor,
        AiRequestDebugWriter? debugWriter = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.profileAccessor = profileAccessor ?? throw new ArgumentNullException(nameof(profileAccessor));
        this.debugWriter = debugWriter;
    }

    public async Task<string> CompleteChatAsync(
        string apiKey,
        DeepSeekChatRequest request,
        CancellationToken cancellationToken = default)
    {
        AiRuntimeProfile profile = profileAccessor();
        Validate(profile, request, expectedStream: false);
        using HttpResponseMessage response = await SendAsync(profile, request, cancellationToken)
            .ConfigureAwait(false);
        string body = await ReadBodyAsync(response, profile, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body, profile);
        AiUsageDiagnostics.RecordResponse(body);
        return ParseAssistantContent(body, response.StatusCode, profile);
    }

    public async Task<string> StreamChatAsync(
        string apiKey,
        DeepSeekChatRequest request,
        Action<DeepSeekStreamChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChunk);
        AiRuntimeProfile profile = profileAccessor();
        Validate(profile, request, expectedStream: true);

        using HttpResponseMessage response = await SendAsync(profile, request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body = await ReadBodyAsync(response, profile, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, body, profile);
        }

        using var timeout = CreateTimeoutSource(profile, cancellationToken);
        var content = new StringBuilder();
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
            while (true)
            {
                string? line = await reader.ReadLineAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
                if (line is null)
                    break;
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;

                string data = line[5..].TrimStart();
                if (data.Equals("[DONE]", StringComparison.Ordinal))
                    break;
                if (data.Length == 0)
                    continue;

                DeepSeekStreamChunk chunk = ParseStreamChunk(data, response.StatusCode, profile);
                if (chunk.ContentDelta.Length > 0)
                    content.Append(chunk.ContentDelta);
                if (chunk.ContentDelta.Length > 0 || chunk.ReasoningDelta.Length > 0)
                    onChunk(chunk);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeepSeekTimeoutException(
                L.F("text.3218375fce8d", $"读取 {profile.Provider} 流式响应在 {profile.RequestTimeout.TotalSeconds:0.#} 秒后超时。"),
                exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or ObjectDisposedException)
        {
            throw new DeepSeekApiException(
                L.F("text.c73e4a6d8c0e", $"读取 {profile.Provider} 流式响应失败：{Sanitize(exception.Message, profile.ApiKey)}"),
                response.StatusCode,
                exception);
        }

        string reply = content.ToString().Trim();
        if (reply.Length == 0)
            throw new DeepSeekApiException(L.F("text.ab767619eb07", $"{profile.Provider} 返回了空的流式回复。"), response.StatusCode);
        return reply;
    }

    private async Task<HttpResponseMessage> SendAsync(
        AiRuntimeProfile profile,
        DeepSeekChatRequest request,
        CancellationToken cancellationToken)
    {
        string json = SerializeRequest(profile, request);
        debugWriter?.TryWrite("ai-provider", profile, json);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        if (profile.Provider == AiProviderNames.Hosted)
            httpRequest.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            request.Stream ? "text/event-stream" : "application/json"));
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var timeout = CreateTimeoutSource(profile, cancellationToken);
        try
        {
            return await httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeepSeekTimeoutException(
                L.F("text.1fadc661fde7", $"{profile.Provider} API 请求在 {profile.RequestTimeout.TotalSeconds:0.#} 秒后超时。"),
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new DeepSeekApiException(
                L.F("text.ed585a06bc6a", $"无法连接 {profile.Provider} API：{Sanitize(exception.Message, profile.ApiKey)}"),
                statusCode: null,
                exception);
        }
    }

    private static string SerializeRequest(AiRuntimeProfile profile, DeepSeekChatRequest request)
    {
        object payload = profile.Provider == AiProviderNames.Hosted
            ? new HostedChatRequest
            {
                Model = profile.Model,
                Messages = request.Messages,
                MaxTokens = request.MaxTokens,
                Stream = request.Stream,
                Context = request.Context?.ToHostedPayload(request.NpcName, request.TaskKind)
                    ?? (string.IsNullOrWhiteSpace(request.TaskKind) ? null : new
                {
                    npc_name = string.IsNullOrWhiteSpace(request.NpcName) ? null : request.NpcName.Trim(),
                    task = request.TaskKind,
                }),
            }
            : profile.Provider == AiProviderNames.OpenAI
            ? new OpenAiChatRequest
            {
                Model = profile.Model,
                Messages = request.Messages,
                MaxCompletionTokens = request.MaxTokens,
                Stream = request.Stream,
            }
            : new DeepSeekProviderRequest
            {
                Model = profile.Model,
                Messages = request.Messages,
                Thinking = new DeepSeekThinkingOptions
                {
                    Type = "disabled",
                },
                MaxTokens = request.MaxTokens,
                Stream = request.Stream,
            };
        return JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions);
    }

    private sealed class HostedChatRequest
    {
        [JsonPropertyName("vivant_context")] public object? Context { get; init; }
        [JsonPropertyName("model")] public string Model { get; init; } = string.Empty;
        [JsonPropertyName("messages")] public IReadOnlyList<DeepSeekChatMessage> Messages { get; init; } = Array.Empty<DeepSeekChatMessage>();
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
    }

    private static async Task<string> ReadBodyAsync(
        HttpResponseMessage response,
        AiRuntimeProfile profile,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeoutSource(profile, cancellationToken);
        try
        {
            return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeepSeekTimeoutException(
                L.F("text.582209801e43", $"读取 {profile.Provider} 响应在 {profile.RequestTimeout.TotalSeconds:0.#} 秒后超时。"),
                exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or ObjectDisposedException)
        {
            throw new DeepSeekApiException(
                L.F("text.b8c59b2fc9de", $"读取 {profile.Provider} 响应失败：{Sanitize(exception.Message, profile.ApiKey)}"),
                response.StatusCode,
                exception);
        }
    }

    private static void Validate(AiRuntimeProfile profile, DeepSeekChatRequest request, bool expectedStream)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiKey))
            throw new DeepSeekConfigurationException(L.F("text.17cc2abbc068", $"{profile.Provider} API Key 为空，请先打开 AI 设置。"));
        if (string.IsNullOrWhiteSpace(profile.Model))
            throw new DeepSeekConfigurationException(L.F("text.54493e24626d", $"{profile.Provider} 模型不能为空。"));
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages is null || request.Messages.Count == 0)
            throw new DeepSeekConfigurationException(L.T("text.11c9ebc861fa"));
        if (request.MaxTokens < 1)
            throw new DeepSeekConfigurationException(L.T("text.714a6500d6c3"));
        if (profile.Provider == AiProviderNames.Hosted
            && (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128))
            throw new DeepSeekConfigurationException(L.T("text.b4654ed5447c"));
        if (request.Stream != expectedStream)
            throw new DeepSeekConfigurationException(expectedStream ? L.T("text.8079ca2ffe5d") : L.T("text.b51a219ba458"));
        if (request.Messages.Any(message =>
                message is null
                || string.IsNullOrWhiteSpace(message.Role)
                || string.IsNullOrWhiteSpace(message.Content)))
        {
            throw new DeepSeekConfigurationException(L.T("text.5e6229d2bfb4"));
        }
    }

    private static CancellationTokenSource CreateTimeoutSource(
        AiRuntimeProfile profile,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (profile.RequestTimeout != Timeout.InfiniteTimeSpan)
            source.CancelAfter(profile.RequestTimeout);
        return source;
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string responseBody,
        AiRuntimeProfile profile)
    {
        if (response.IsSuccessStatusCode)
            return;

        string providerMessage = ExtractProviderError(responseBody, profile.ApiKey);
        string prefix = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => profile.Provider == AiProviderNames.Hosted
                    ? L.T("text.54686fc95aed")
                    : L.F("text.4abe6af6ce52", $"{profile.Provider} API 鉴权失败，请检查 API Key。"),
            (HttpStatusCode)429
                => L.F("text.b027f34547de", $"{profile.Provider} API 请求过于频繁或额度不足（HTTP 429）。"),
            _ => L.F("text.6daebcb81a2e", $"{profile.Provider} API 请求失败（HTTP {(int)response.StatusCode} {response.StatusCode}）。"),
        };
        throw new DeepSeekApiException(
            providerMessage.Length == 0 ? prefix : prefix + L.T("text.cb95484caa8c") + providerMessage,
            response.StatusCode);
    }

    private static string ParseAssistantContent(
        string json,
        HttpStatusCode statusCode,
        AiRuntimeProfile profile)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out JsonElement message)
                || !message.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.String)
            {
                throw new DeepSeekApiException(
                    L.F("text.69a5fcd53948", $"{profile.Provider} 响应缺少 choices[0].message.content。"),
                    statusCode);
            }

            string value = content.GetString()?.Trim() ?? string.Empty;
            if (value.Length == 0)
                throw new DeepSeekApiException(L.F("text.f770dbd81de2", $"{profile.Provider} 返回了空回复。"), statusCode);
            return value;
        }
        catch (JsonException exception)
        {
            throw new DeepSeekApiException(
                L.F("text.53430f7410c8", $"{profile.Provider} 返回了无法解析的 JSON：{Sanitize(exception.Message, profile.ApiKey)}"),
                statusCode,
                exception);
        }
    }

    private static DeepSeekStreamChunk ParseStreamChunk(
        string json,
        HttpStatusCode statusCode,
        AiRuntimeProfile profile)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error))
            {
                string providerMessage = error.ValueKind == JsonValueKind.Object
                                         && error.TryGetProperty("message", out JsonElement message)
                                         && message.ValueKind == JsonValueKind.String
                    ? message.GetString() ?? string.Empty
                    : error.ToString();
                throw new DeepSeekApiException(
                    L.F("text.139564d22074", $"{profile.Provider} 流式响应返回错误：{Sanitize(providerMessage, profile.ApiKey)}"),
                    statusCode);
            }

            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("delta", out JsonElement delta)
                || delta.ValueKind != JsonValueKind.Object)
            {
                return new DeepSeekStreamChunk(string.Empty, string.Empty);
            }

            string content = TryGetString(delta, "content");
            string reasoning = profile.Provider == AiProviderNames.DeepSeek
                ? TryGetString(delta, "reasoning_content")
                : string.Empty;
            return new DeepSeekStreamChunk(content, reasoning);
        }
        catch (JsonException exception)
        {
            throw new DeepSeekApiException(
                L.F("text.278df0bad3a0", $"{profile.Provider} 返回了无法解析的流式 JSON：{Sanitize(exception.Message, profile.ApiKey)}"),
                statusCode,
                exception);
        }
    }

    private static string ExtractProviderError(string responseBody, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return Sanitize(message.GetString() ?? string.Empty, apiKey);
                }
                if (error.ValueKind == JsonValueKind.String)
                    return Sanitize(error.GetString() ?? string.Empty, apiKey);
            }
        }
        catch (JsonException)
        {
        }
        return string.Empty;
    }

    private static string TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Sanitize(string value, string apiKey)
    {
        string sanitized = value ?? string.Empty;
        if (!string.IsNullOrEmpty(apiKey))
            sanitized = sanitized.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal);
        sanitized = sanitized.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 500 ? sanitized : sanitized[..500] + "…";
    }

    private sealed class DeepSeekProviderRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<DeepSeekChatMessage> Messages { get; init; } = new();

        [JsonPropertyName("thinking")]
        public DeepSeekThinkingOptions Thinking { get; init; } = new();

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }
    }

    private sealed class OpenAiChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<DeepSeekChatMessage> Messages { get; init; } = new();

        [JsonPropertyName("max_completion_tokens")]
        public int MaxCompletionTokens { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }
    }
}
