using System.Security.Cryptography;
using System.Text;

namespace VivantValley.Services;

/// <summary>Selects cosmetic dialogue presentation without another AI request.</summary>
public static class NpcPresentationPolicy
{
    private const int BodyAnimationThreshold = 256; // Always try; unavailable animations fall back to an emote.

    private static readonly string[] HappyTerms =
    {
        "\u54c8\u54c8",
        "\u592a\u597d\u4e86",
        "\u5f00\u5fc3",
        "\u8c22\u8c22",
        "\u8c22\u5566",
        "\u9ad8\u5174",
        "haha",
        "thanks",
        "thank you",
    };

    private static readonly string[] SadTerms =
    {
        "\u62b1\u6b49",
        "\u96be\u8fc7",
        "\u9057\u61be",
        "\u5bf9\u4e0d\u8d77",
        "\u4f24\u5fc3",
        "sorry",
        "sad",
    };

    private static readonly string[] LoveTerms =
    {
        "\u7231\u4f60",
        "\u559c\u6b22\u4f60",
        "\u4eb2\u7231\u7684",
        "\u60f3\u4f60",
        "love you",
        "dear",
    };

    private static readonly string[] SurpriseTerms =
    {
        "\u4ec0\u4e48",
        "\u5c45\u7136",
        "\u771f\u7684",
        "\uff1f\uff01",
        "?!",
        "what",
        "really",
    };

    private static readonly IReadOnlyDictionary<NpcPresentationMood, string[]> AnimationSuffixes
        = new Dictionary<NpcPresentationMood, string[]>
        {
            [NpcPresentationMood.Neutral] = new[]
            {
                "_read", "_drink", "_meditate", "_photo", "_gameboy", "_game", "_relax", "_wave",
            },
            [NpcPresentationMood.Happy] = new[]
            {
                "_laugh", "_dance", "_wave", "_sing", "_guitar", "_football", "_exercise", "_gym", "_gameboy", "_game",
            },
            [NpcPresentationMood.Sad] = new[]
            {
                "_read", "_meditate", "_drink", "_relax",
            },
            [NpcPresentationMood.Angry] = new[]
            {
                "_read", "_drink", "_meditate", "_photo", "_gameboy", "_game", "_relax", "_wave",
            },
            [NpcPresentationMood.Love] = new[]
            {
                "_wave", "_laugh", "_dance", "_sing", "_guitar", "_ballet",
            },
            [NpcPresentationMood.Surprised] = new[]
            {
                "_wave", "_photo",
            },
        };

    public static NpcPresentationPlan Create(
        string? npcName,
        string? dialogueText,
        NpcPresentationCue cue = NpcPresentationCue.None)
    {
        string text = dialogueText?.Trim() ?? string.Empty;
        NpcPresentationMood mood = ResolveMood(text, cue);
        return Create(npcName, text, mood);
    }

    public static NpcPresentationPlan CreateForStoryCue(
        string? npcName,
        string? dialogueText,
        string? storyCue)
    {
        string text = dialogueText?.Trim() ?? string.Empty;
        string cue = storyCue?.Trim().ToLowerInvariant() ?? string.Empty;
        NpcPresentationMood mood = cue switch
        {
            "happy" or "cheer" or "wave" => NpcPresentationMood.Happy,
            "sad" => NpcPresentationMood.Sad,
            "love" or "shy" => NpcPresentationMood.Love,
            "surprised" => NpcPresentationMood.Surprised,
            _ => ResolveMood(text),
        };
        string[] suffixes = cue switch
        {
            "wave" => new[] { "_wave" },
            "sit" => new[] { "_sit", "_sitting", "_bench", "_relax" },
            "look_away" => Array.Empty<string>(),
            "cheer" => new[] { "_cheer", "_dance", "_laugh" },
            "shy" => new[] { "_shy", "_blush", "_read" },
            "surprised" => new[] { "_surprised", "_surprise", "_wave" },
            "sad" => new[] { "_sad", "_read", "_meditate" },
            "happy" => AnimationSuffixes[NpcPresentationMood.Happy],
            "love" => AnimationSuffixes[NpcPresentationMood.Love],
            _ => AnimationSuffixes[mood],
        };
        NpcPresentationPlan basePlan = Create(npcName, text, mood);
        return basePlan with
        {
            UseBodyAnimation = suffixes.Length > 0,
            AnimationSuffixes = suffixes,
        };
    }

    private static NpcPresentationPlan Create(
        string? npcName,
        string text,
        NpcPresentationMood mood)
    {
        string seed = $"{npcName?.Trim().ToLowerInvariant()}\u001f{text}\u001f{mood}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        string[] orderedSuffixes = Rotate(AnimationSuffixes[mood], hash[1]);

        return new NpcPresentationPlan(
            mood,
            GetPortraitIndex(mood),
            GetDialogueEmotionToken(mood),
            GetEmoteId(mood),
            (int)hash[0] < BodyAnimationThreshold,
            orderedSuffixes);
    }

    public static NpcPresentationMood ResolveMood(
        string? dialogueText,
        NpcPresentationCue cue = NpcPresentationCue.None)
    {
        NpcPresentationMood? authoritativeMood = cue switch
        {
            NpcPresentationCue.GiftOffered or NpcPresentationCue.GiftDelivered => NpcPresentationMood.Love,
            NpcPresentationCue.GiftDeclined or NpcPresentationCue.ActivityDeclined => NpcPresentationMood.Sad,
            NpcPresentationCue.ActivityAccepted => NpcPresentationMood.Happy,
            _ => null,
        };
        if (authoritativeMood is not null)
            return authoritativeMood.Value;

        string text = dialogueText ?? string.Empty;
        if (ContainsAny(text, SadTerms))
            return NpcPresentationMood.Sad;
        if (ContainsAny(text, LoveTerms))
            return NpcPresentationMood.Love;
        if (ContainsAny(text, HappyTerms))
            return NpcPresentationMood.Happy;
        if (ContainsAny(text, SurpriseTerms))
            return NpcPresentationMood.Surprised;
        return NpcPresentationMood.Neutral;
    }

    public static int GetPortraitIndex(NpcPresentationMood mood)
        => mood switch
        {
            NpcPresentationMood.Happy => 1,
            NpcPresentationMood.Sad => 2,
            NpcPresentationMood.Love => 4,
            _ => 0,
        };

    private static string GetDialogueEmotionToken(NpcPresentationMood mood)
        => mood switch
        {
            NpcPresentationMood.Happy => "$h",
            NpcPresentationMood.Sad => "$s",
            NpcPresentationMood.Love => "$l",
            _ => string.Empty,
        };

    private static int? GetEmoteId(NpcPresentationMood mood)
        => mood switch
        {
            NpcPresentationMood.Happy => 32,
            NpcPresentationMood.Sad => 28,
            NpcPresentationMood.Love => 20,
            NpcPresentationMood.Surprised => 16,
            _ => null,
        };

    private static bool ContainsAny(string text, IEnumerable<string> terms)
        => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string[] Rotate(string[] values, byte offsetSeed)
    {
        if (values.Length <= 1)
            return values.ToArray();

        int offset = offsetSeed % values.Length;
        return values.Skip(offset).Concat(values.Take(offset)).ToArray();
    }
}
