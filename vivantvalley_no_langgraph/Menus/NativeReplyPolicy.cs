using Microsoft.Xna.Framework;

namespace VivantValley.Menus;

internal enum ReplyPhase { Generating, Reply, Confirmation, Error, Closing, Closed }
internal enum ReplyAction { Cancel, End, Continue, Return, Accept, Decline, Personality, Share, Replay }
internal enum ReplyReadAction { None, Reveal, NextPage }

internal static class NativeReplyPolicy
{
    public static ReplyAction[] Actions(ReplyPhase phase, bool read, bool personality, bool share, bool replay = false)
    {
        if (phase == ReplyPhase.Generating) return new[] { ReplyAction.Cancel };
        if (phase == ReplyPhase.Error) return new[] { ReplyAction.Return, ReplyAction.End };
        if (phase == ReplyPhase.Confirmation) return read
            ? new[] { ReplyAction.Accept, ReplyAction.Decline } : new[] { ReplyAction.Decline };
        if (phase != ReplyPhase.Reply) return Array.Empty<ReplyAction>();
        var actions = new List<ReplyAction>();
        if (replay) actions.Add(ReplyAction.Replay);
        if (read)
        {
            if (personality) actions.Add(ReplyAction.Personality);
            if (share) actions.Add(ReplyAction.Share);
            actions.Add(ReplyAction.Continue);
        }
        actions.Add(ReplyAction.End);
        return actions.ToArray();
    }

    // Scene replies have no external Continue button. After reading, let vanilla
    // advance the page or close the last page; retain its transition/debounce guards.
    public static ReplyReadAction ReadScene(bool transitioning, bool typed, bool safetyDelay)
        => transitioning ? ReplyReadAction.None : !typed ? ReplyReadAction.Reveal
            : safetyDelay ? ReplyReadAction.None : ReplyReadAction.NextPage;

    // Reading can never close the vanilla menu or accept an action.
    public static ReplyReadAction Read(bool transitioning, bool typed, bool lastPage, bool safetyDelay)
        => transitioning ? ReplyReadAction.None : !typed ? ReplyReadAction.Reveal
            : !lastPage && !safetyDelay ? ReplyReadAction.NextPage : ReplyReadAction.None;
}

/// <summary>Vanilla-speed visual outro with a single handoff, independent of NPC dialogue callbacks.</summary>
internal sealed class ReplyHandoffTransition
{
    private bool started;
    private bool stopped;
    private double durationMs;
    private double elapsedMs;
    public float Remaining => started ? (float)Math.Clamp(1 - elapsedMs / durationMs, 0, 1) : 1f;

    public bool Begin(int sourceWidth)
    {
        if (started || stopped) return false;
        started = true;
        // Vanilla moves both horizontal edges inward at 3 source pixels per millisecond.
        durationMs = Math.Max(1, sourceWidth / 6d);
        return true;
    }

    public bool Advance(TimeSpan elapsed, bool ownsMenu)
    {
        if (!started || stopped) return false;
        if (!ownsMenu) { Cancel(); return false; }
        elapsedMs = Math.Min(durationMs, elapsedMs + Math.Max(0, elapsed.TotalMilliseconds));
        if (elapsedMs < durationMs) return false;
        stopped = true;
        return true;
    }

    public Rectangle Bounds(Rectangle body)
    {
        int width = (int)(body.Width * Remaining), height = (int)(body.Height * Remaining);
        return new Rectangle(body.Center.X - width / 2, body.Center.Y - height / 2, width, height);
    }

    public void Cancel() => stopped = true;
}

/// <summary>Fits the complete vanilla frame, including its border, below one external action row.</summary>
internal sealed record NativeReplyLayout(Rectangle Frame, Rectangle Toolbar, float Scale)
{
    public static NativeReplyLayout Create(Rectangle safe, int sourceWidth, int sourceHeight, int actionHeight)
    {
        int barHeight = Math.Min(actionHeight, Math.Max(1, safe.Height / 4));
        const int gap = 12;
        float scale = Math.Min(1f,
            Math.Min(safe.Width / (float)Math.Max(1, sourceWidth),
                Math.Max(1, safe.Height - barHeight - gap) / (float)Math.Max(1, sourceHeight)));
        int w = Math.Max(1, (int)(sourceWidth * scale));
        int h = Math.Max(1, (int)(sourceHeight * scale));
        Rectangle frame = new(safe.Center.X - w / 2, safe.Bottom - h, w, h);
        return new(frame, new Rectangle(safe.Left, frame.Top - gap - barHeight, safe.Width, barHeight), scale);
    }

    public Rectangle[] Buttons(IReadOnlyList<int> preferredWidths)
    {
        if (preferredWidths.Count == 0) return Array.Empty<Rectangle>();
        const int gap = 8;
        int available = Math.Max(preferredWidths.Count, Toolbar.Width - gap * (preferredWidths.Count - 1));
        float ratio = Math.Min(1f, available / (float)Math.Max(1, preferredWidths.Sum()));
        int[] widths = preferredWidths.Select(w => Math.Max(1, (int)(w * ratio))).ToArray();
        int x = Math.Min(Toolbar.Right, Frame.Right) - widths.Sum() - gap * (widths.Length - 1);
        x = Math.Max(Toolbar.Left, x);
        var result = new Rectangle[widths.Length];
        for (int i = 0; i < widths.Length; i++)
        {
            result[i] = new Rectangle(x, Toolbar.Y, widths[i], Toolbar.Height);
            x += widths[i] + gap;
        }
        return result;
    }
}
