using System.Text;

namespace VivantValley.Services;

/// <summary>Locale-specific editable prompts; legacy root overrides only apply to Chinese.</summary>
public sealed class LocalizedPromptAssets
{
    private readonly string? directory;
    private readonly Action<string>? log;
    public LocalizedPromptAssets(string? modDirectory = null, Action<string>? log = null)
    {
        directory = modDirectory;
        this.log = log;
    }

    public string Read(string name)
    {
        string locale = L.Locale;
        string? root = directory ?? L.ModDirectory;
        if (root is not null)
        {
            string folder = Path.Combine(root, "assets", "prompts");
            // Preserve pre-localization user edits without ever leaking them into English mode.
            string localized = Path.Combine(folder, locale, name);
            string legacy = Path.Combine(folder, name);
            bool supportsLegacy = name is "conversation.txt" or "conversation-final.txt" or "weekly-story.txt" or "weekly-story-format.txt";
            string path = locale == "zh" && supportsLegacy && !File.Exists(localized) && File.Exists(legacy) ? legacy : localized;
            try
            {
                string content = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (locale == "zh" && supportsLegacy && path == localized && File.Exists(legacy)
                    && content == Embedded(locale + "." + name))
                {
                    string old = File.ReadAllText(legacy, Encoding.UTF8).Trim();
                    if (old.Length > 0 && old != Embedded(name))
                    {
                        log?.Invoke($"Using customized legacy prompt {name}; move it to assets/prompts/zh/{name} to edit the new language version.");
                        return old;
                    }
                }
                if (content.Length > 0) return content;
                log?.Invoke($"Empty prompt {locale}/{name}; using embedded {locale} default.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { log?.Invoke($"Cannot load prompt {locale}/{name}; using embedded {locale} default: {ex.Message}"); }
        }
        return Embedded(locale + "." + name);
    }

    private static string Embedded(string name)
    {
        using Stream stream = typeof(LocalizedPromptAssets).Assembly.GetManifestResourceStream("VivantValley.Prompts." + name)
            ?? throw new InvalidOperationException($"Missing embedded prompt: {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }

    public static string ForRequest(string name) => L.WithOutputLanguage(new LocalizedPromptAssets().Read(name));
}
