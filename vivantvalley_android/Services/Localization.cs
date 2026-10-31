using System.Globalization;
using System.Text.Json;

namespace VivantValley;

/// <summary>Game-selected locale, with an isolated snapshot for each asynchronous generation.</summary>
public static class L
{
    private static string gameLocale = "zh";
    private static readonly AsyncLocal<string?> requestLocale = new();
    private static IReadOnlyDictionary<string, string> english = Load("default", null);
    private static IReadOnlyDictionary<string, string> chinese = Load("zh", null);
    public static string? ModDirectory { get; private set; }
    public static string Locale => requestLocale.Value ?? Volatile.Read(ref gameLocale);
    public static bool IsChinese => Locale == "zh";
    public static string Normalize(string? locale)
        => locale?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true ? "zh" : "en";

    public static void Initialize(string directory, string locale)
    {
        ModDirectory = directory;
        english = Load("default", directory);
        chinese = Load("zh", directory);
        SetLocale(locale);
    }

    public static void SetLocale(string locale) => Volatile.Write(ref gameLocale, Normalize(locale));
    public static IDisposable BeginRequest(string? locale = null)
    {
        string? previous = requestLocale.Value;
        requestLocale.Value = locale is null ? Locale : Normalize(locale);
        return new Scope(previous);
    }

    public static string T(string key)
        => (IsChinese ? chinese : english).TryGetValue(key, out string? value) ? value
            : english.TryGetValue(key, out value) ? value : key;

    // Only translate bundled authored text that still matches its source; preserve user-authored replacements.
    public static string Authored(string key, string source)
        => !IsChinese && chinese.TryGetValue(key, out string? original) && source == original ? T(key) : source;

    public static string F(string key, FormattableString source)
        => string.Format(IsChinese ? CultureInfo.GetCultureInfo("zh-CN") : CultureInfo.GetCultureInfo("en-US"),
            T(key), source.GetArguments());

    public static string OutputInstruction => IsChinese
        ? "当前游戏语言为简体中文。所有玩家可见台词、选项、标题、信件及自然语言记忆使用简体中文，即使历史或输入使用其他语言。JSON字段名、枚举、工具名、内部ID保持协议原值；不要翻译ID。"
        : "The selected game language is English. Write all player-visible dialogue, choices, titles, letters and natural-language memory in English, even if input, context or history uses another language. Keep JSON keys, enum values, tool names and internal IDs exactly as specified; never translate identifiers. For concise dialogue use short English sentences, not a Chinese-character word target.";

    public static string WithOutputLanguage(string prompt) => prompt.Trim() + "\n\n" + OutputInstruction;

    private static IReadOnlyDictionary<string, string> Load(string locale, string? directory)
    {
        using Stream? stream = typeof(L).Assembly.GetManifestResourceStream("VivantValley.I18n." + locale + ".json");
        var result = stream is null ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        if (directory is not null)
        {
            try
            {
                var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(Path.Combine(directory, "i18n", locale + ".json")));
                foreach (var pair in overrides ?? new()) if (!string.IsNullOrWhiteSpace(pair.Value)) result[pair.Key] = pair.Value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return result;
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => requestLocale.Value = previous;
    }
}
