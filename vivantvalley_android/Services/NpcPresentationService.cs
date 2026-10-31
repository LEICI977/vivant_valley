using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace VivantValley.Services;

/// <summary>Plays short, cosmetic NPC animations without mutating schedules or route behaviors.</summary>
public sealed class NpcPresentationService
{
    private const double FrameDurationMilliseconds = 110d;
    private const int MaximumAnimationFrames = 24;

    private static readonly string[] HazardousAnimationMarkers =
    {
        "sleep",
        "fight",
        "transform",
        "beach_towel",
        "laying_down",
        "laying",
        "slash",
        "battle",
    };

    private readonly IMonitor monitor;
    private readonly Func<string, bool> isExternallyControlled;
    private readonly Dictionary<string, ActivePresentation> active = new(StringComparer.OrdinalIgnoreCase);

    public NpcPresentationService(IMonitor monitor, Func<string, bool> isExternallyControlled)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.isExternallyControlled = isExternallyControlled
            ?? throw new ArgumentNullException(nameof(isExternallyControlled));
    }

    public void Begin(string npcName, NpcPresentationPlan plan, IClickableMenu? ownerMenu)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return;

        End(npcName);
        NPC? npc = Game1.getCharacterFromName(npcName, mustBeVillager: false, includeEventActors: true);
        if (npc is null)
            return;

        if (plan.UseBodyAnimation
            && CanUseBodyAnimation(npc)
            && TryGetAnimationFrames(npc, plan.AnimationSuffixes, out int[] frames))
        {
            var presentation = new ActivePresentation(
                npc,
                npc.Sprite.CurrentFrame,
                frames,
                ownerMenu,
                Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0d);
            active[npc.Name] = presentation;
            ApplyFrame(presentation, frames[0]);
            return;
        }

        TryShowEmote(npc, plan.EmoteId);
    }

    public void Update()
    {
        if (active.Count == 0)
            return;

        foreach (string npcName in active.Keys.ToArray())
        {
            if (!active.TryGetValue(npcName, out ActivePresentation? presentation))
                continue;

            NPC npc = presentation.Npc;
            if (!Context.IsWorldReady
                || npc.currentLocation is null
                || !ReferenceEquals(npc.currentLocation, Game1.currentLocation)
                || presentation.OwnerMenu is not null
                   && !ReferenceEquals(presentation.OwnerMenu, Game1.activeClickableMenu)
                || !CanContinueBodyAnimation(npc))
            {
                End(npcName);
                continue;
            }

            double now = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds
                         ?? presentation.StartedAtMilliseconds;
            int index = (int)((now - presentation.StartedAtMilliseconds) / FrameDurationMilliseconds);
            if (index >= presentation.Frames.Length)
            {
                End(npcName);
                continue;
            }

            ApplyFrame(presentation, presentation.Frames[Math.Max(0, index)]);
        }
    }

    public void OnMenuChanged(IClickableMenu? oldMenu, IClickableMenu? newMenu)
    {
        if (oldMenu is null || ReferenceEquals(oldMenu, newMenu))
            return;

        foreach (string npcName in active
                     .Where(pair => ReferenceEquals(pair.Value.OwnerMenu, oldMenu))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            End(npcName);
        }
    }

    public void End(string? npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)
            || !active.Remove(npcName, out ActivePresentation? presentation))
        {
            return;
        }

        try
        {
            NPC npc = presentation.Npc;
            if (npc.Sprite.CurrentAnimation is null
                && npc.Sprite.CurrentFrame == presentation.LastAppliedFrame)
            {
                npc.Sprite.CurrentFrame = presentation.OriginalFrame;
                npc.Sprite.UpdateSourceRect();
            }
        }
        catch (Exception ex)
        {
            monitor.Log($"Failed to restore dialogue presentation for {npcName}: {ex.Message}", LogLevel.Trace);
        }
    }

    public void Clear()
    {
        foreach (string npcName in active.Keys.ToArray())
            End(npcName);
    }

    private bool CanUseBodyAnimation(NPC npc)
        => Context.IsWorldReady
           && Game1.currentMinigame is null
           && !Game1.eventUp
           && npc.currentLocation is not null
           && ReferenceEquals(npc.currentLocation, Game1.currentLocation)
           && !npc.IsInvisible
           && !npc.isSleeping.Value
           && !npc.swimming.Value
           && npc.yJumpOffset == 0
           && npc.controller is null
           && npc.temporaryController is null
           && npc.Sprite.CurrentAnimation is null
           && !npc.isMoving()
           && !npc.doingEndOfRouteAnimation.Value
           && !isExternallyControlled(npc.Name);

    private bool CanContinueBodyAnimation(NPC npc)
        => !Game1.eventUp
           && Game1.currentMinigame is null
           && !npc.IsInvisible
           && !npc.isSleeping.Value
           && !npc.swimming.Value
           && npc.yJumpOffset == 0
           && npc.controller is null
           && npc.temporaryController is null
           && npc.Sprite.CurrentAnimation is null
           && !npc.isMoving()
           && !npc.doingEndOfRouteAnimation.Value
           && !isExternallyControlled(npc.Name);

    private static void TryShowEmote(NPC npc, int? emoteId)
    {
        if (emoteId is null
            || !Context.IsWorldReady
            || Game1.eventUp
            || npc.IsInvisible
            || npc.IsEmoting
            || npc.currentLocation is null
            || !ReferenceEquals(npc.currentLocation, Game1.currentLocation))
        {
            return;
        }

        npc.doEmote(emoteId.Value, nextEventCommand: false);
    }

    private static bool TryGetAnimationFrames(
        NPC npc,
        IReadOnlyList<string> suffixes,
        out int[] frames)
    {
        frames = Array.Empty<int>();
        try
        {
            Dictionary<string, string> descriptions = DataLoader.AnimationDescriptions(Game1.content);
            foreach (string suffix in suffixes)
            {
                string animationName = npc.Name.ToLowerInvariant() + suffix;
                KeyValuePair<string, string> entry = descriptions.FirstOrDefault(pair =>
                    pair.Key.Equals(animationName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(entry.Key)
                    || IsHazardous(animationName, entry.Value)
                    || !TryParseFrames(npc, entry.Value, out frames))
                {
                    continue;
                }

                return true;
            }
        }
        catch
        {
            // Presentation is optional; malformed custom animation data falls back to an emote.
        }

        frames = Array.Empty<int>();
        return false;
    }

    private static bool IsHazardous(string animationName, string description)
    {
        if (HazardousAnimationMarkers.Any(marker =>
                animationName.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string[] sections = description.Split('/');
        return sections.Skip(4).Any(metadata =>
            metadata.Contains("offset", StringComparison.OrdinalIgnoreCase)
            || metadata.Contains("laying", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseFrames(NPC npc, string description, out int[] frames)
    {
        frames = Array.Empty<int>();
        string[] sections = description.Split('/');
        if (sections.Length < 3
            || npc.Sprite.SpriteWidth <= 0
            || npc.Sprite.SpriteHeight <= 0)
        {
            return false;
        }

        int maximumFrame = npc.Sprite.Texture.Width / npc.Sprite.SpriteWidth
                           * (npc.Sprite.Texture.Height / npc.Sprite.SpriteHeight);
        if (maximumFrame <= 0)
            return false;

        var parsed = new List<int>();
        for (int sectionIndex = 0; sectionIndex < 3; sectionIndex++)
        {
            if (string.IsNullOrWhiteSpace(sections[sectionIndex]))
                continue;
            parsed.AddRange(Utility.parseStringToIntArray(sections[sectionIndex]));
        }

        var compacted = new List<int>(Math.Min(parsed.Count, MaximumAnimationFrames));
        int repeated = 0;
        int previous = -1;
        foreach (int frame in parsed)
        {
            if (frame < 0 || frame >= maximumFrame)
                return false;

            repeated = frame == previous ? repeated + 1 : 1;
            previous = frame;
            if (repeated <= 2)
                compacted.Add(frame);
            if (compacted.Count >= MaximumAnimationFrames)
                break;
        }

        frames = compacted.ToArray();
        return frames.Length > 0;
    }

    private static void ApplyFrame(ActivePresentation presentation, int frame)
    {
        presentation.Npc.Sprite.CurrentFrame = frame;
        presentation.Npc.Sprite.UpdateSourceRect();
        presentation.LastAppliedFrame = frame;
    }

    private sealed class ActivePresentation
    {
        public ActivePresentation(
            NPC npc,
            int originalFrame,
            int[] frames,
            IClickableMenu? ownerMenu,
            double startedAtMilliseconds)
        {
            Npc = npc;
            OriginalFrame = originalFrame;
            LastAppliedFrame = originalFrame;
            Frames = frames;
            OwnerMenu = ownerMenu;
            StartedAtMilliseconds = startedAtMilliseconds;
        }

        public NPC Npc { get; }

        public int OriginalFrame { get; }

        public int LastAppliedFrame { get; set; }

        public int[] Frames { get; }

        public IClickableMenu? OwnerMenu { get; }

        public double StartedAtMilliseconds { get; }
    }
}
