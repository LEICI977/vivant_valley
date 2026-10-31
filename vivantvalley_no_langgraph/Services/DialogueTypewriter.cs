using System.Text;

namespace VivantValley.Services;

/// <summary>Reveals a complete dialogue string locally at a stable, readable pace.</summary>
public sealed class DialogueTypewriter
{
    private const double DefaultCharacterDelayMs = 30d;
    private const double PunctuationDelayMs = 110d;
    private readonly StringBuilder fullText = new();
    private double elapsedMs;
    private int visibleCharacters;

    public string VisibleText => fullText.Length == 0
        ? string.Empty
        : fullText.ToString(0, Math.Min(visibleCharacters, fullText.Length));

    public string FullText => fullText.ToString();

    public int VisibleCharacters => visibleCharacters;

    public bool IsComplete => visibleCharacters >= fullText.Length;

    public void SetText(string? text)
    {
        fullText.Clear();
        elapsedMs = 0;
        visibleCharacters = 0;
        if (!string.IsNullOrEmpty(text))
            fullText.Append(text);
    }

    /// <summary>Append text for callers which already have a complete local buffer.</summary>
    public void Append(string? text)
    {
        if (!string.IsNullOrEmpty(text))
            fullText.Append(text);
    }

    public void Update(double elapsedMilliseconds)
    {
        if (IsComplete || elapsedMilliseconds <= 0)
            return;

        elapsedMs += Math.Min(elapsedMilliseconds, 250d);
        while (!IsComplete)
        {
            double required = GetDelayForNextCharacter();
            if (elapsedMs < required)
                break;
            elapsedMs -= required;
            visibleCharacters++;
        }
    }

    public void RevealAll()
    {
        visibleCharacters = fullText.Length;
        elapsedMs = 0;
    }

    private double GetDelayForNextCharacter()
    {
        if (visibleCharacters < 0 || visibleCharacters >= fullText.Length)
            return DefaultCharacterDelayMs;

        char character = fullText[visibleCharacters];
        return character switch
        {
            '\r' or '\n' => PunctuationDelayMs,
            '，' or '。' or '！' or '？' or '；' or '：' or ',' or '.' or '!' or '?' or ';' or ':' or '…'
                => PunctuationDelayMs,
            _ => DefaultCharacterDelayMs,
        };
    }
}
