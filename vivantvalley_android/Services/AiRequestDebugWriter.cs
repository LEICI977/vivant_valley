using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text;

namespace VivantValley.Services;

/// <summary>Persists complete request bodies for local inspection without logging them to SMAPI.</summary>
public sealed class AiRequestDebugWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string directoryPath;
    private readonly Func<bool> enabledAccessor;
    private readonly Action<string>? warningLogger;
    private int sequence;

    public AiRequestDebugWriter(
        string modDirectoryPath,
        Func<bool> enabledAccessor,
        Action<string>? warningLogger = null)
    {
        if (string.IsNullOrWhiteSpace(modDirectoryPath))
            throw new ArgumentException("A mod directory is required.", nameof(modDirectoryPath));

        directoryPath = Path.Combine(modDirectoryPath, "debug-ai-requests");
        this.enabledAccessor = enabledAccessor ?? throw new ArgumentNullException(nameof(enabledAccessor));
        this.warningLogger = warningLogger;
    }

    public string DirectoryPath => directoryPath;

    public void TryWrite(string source, AiRuntimeProfile profile, string requestJson)
    {
        if (!enabledAccessor())
            return;

        try
        {
            using JsonDocument request = JsonDocument.Parse(requestJson);
            string safeSource = SanitizeFilePart(source);
            int number = Interlocked.Increment(ref sequence);
            string timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HHmmss_fff");
            string fileName = $"{timestamp}_{number:000}_{safeSource}.json";
            Directory.CreateDirectory(directoryPath);

            var capture = new
            {
                captured_at_utc = DateTimeOffset.UtcNow,
                source = source,
                provider = profile.Provider,
                model = profile.Model,
                endpoint = profile.Endpoint.AbsoluteUri,
                // JsonElement.WriteTo preserves the source token spelling, so
                // escaped strings such as \u4F60\u597D would remain escaped.
                // Materialize the tree first to decode them before writing.
                request = Materialize(request.RootElement),
            };
            string output = JsonSerializer.Serialize(capture, JsonOptions);
            File.WriteAllText(
                Path.Combine(directoryPath, fileName),
                output,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception exception)
        {
            warningLogger?.Invoke($"AI request debug capture failed: {exception.Message}");
        }
    }

    /// <summary>Capture a raw AI response alongside the request for diagnostics.</summary>
    public void TryWriteResponse(string source, string responseText)
    {
        if (!enabledAccessor())
            return;

        try
        {
            string safeSource = SanitizeFilePart(source);
            int number = Interlocked.Increment(ref sequence);
            string timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HHmmss_fff");
            string fileName = $"{timestamp}_{number:000}_{safeSource}.json";
            Directory.CreateDirectory(directoryPath);
            var capture = new
            {
                captured_at_utc = DateTimeOffset.UtcNow,
                source,
                response = responseText ?? string.Empty,
            };
            string output = JsonSerializer.Serialize(capture, JsonOptions);
            File.WriteAllText(
                Path.Combine(directoryPath, fileName),
                output,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception exception)
        {
            warningLogger?.Invoke($"AI response debug capture failed: {exception.Message}");
        }
    }

    private static string SanitizeFilePart(string value)
    {
        string input = string.IsNullOrWhiteSpace(value) ? "request" : value.Trim();
        char[] chars = input
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')
            .ToArray();
        string result = new string(chars).Trim('-');
        return result.Length == 0 ? "request" : result;
    }

    private static object? Materialize(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(property => property.Name, property => Materialize(property.Value), StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(Materialize).ToArray(),
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.TryGetInt64(out long integer)
                ? integer
                : element.TryGetDecimal(out decimal decimalValue)
                    ? decimalValue
                    : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null,
        };
}
