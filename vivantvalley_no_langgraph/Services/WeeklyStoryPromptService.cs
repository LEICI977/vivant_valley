namespace VivantValley.Services;

/// <summary>Loads the selected language's creative and executable weekly prompts together.</summary>
public sealed class WeeklyStoryPromptService
{
    private readonly LocalizedPromptAssets assets;
    public WeeklyStoryPromptService(string? modDirectory = null, Action<string>? log = null)
        => assets = new LocalizedPromptAssets(modDirectory, log);

    public string LoadFreeScene() => assets.Read("free-scene-opening.txt");

    public string Load()
    {
        using var language = L.BeginRequest();
        return assets.Read("weekly-story.txt") + "\n\n" + assets.Read("weekly-story-format.txt");
    }
}
