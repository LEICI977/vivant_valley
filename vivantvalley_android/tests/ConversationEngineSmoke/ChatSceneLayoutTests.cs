using VivantValley;
using Microsoft.Xna.Framework;
using VivantValley.Menus;
using VivantValley.Services;

internal static class ChatSceneLayoutTests
{
    public static void Run()
    {
        // Includes keyboard-reduced landscapes, narrow screens and all input scale limits.
        foreach (var size in new[] { new Point(320, 180), new Point(480, 220), new Point(800, 280), new Point(1280, 720), new Point(1920, 1080) })
        foreach (float scale in new[] { .75f, 1f, 1.5f })
        foreach (int control in new[] { 44, 64, 96 })
        foreach (bool portrait in new[] { false, true })
        foreach (bool hideDuplicateName in new[] { false, true })
        {
            var safe = new Rectangle(17, 23, size.X, size.Y);
            var layout = ChatComposerLayout.Create(safe, scale, control, 120, 150, portrait, hideDuplicateName);
            Check(Math.Abs(layout.Outer.Width / 1296f - layout.Outer.Height / 456f) < .01f,
                "The native frame or portrait was stretched instead of uniformly scaled.");
            foreach (var rect in new[] { layout.Frame, layout.Header, layout.Caption, layout.Input, layout.Send, layout.End, layout.Close })
                Check(safe.Contains(rect), $"Composer escaped safe area: {size} {rect}");
            Check(layout.Input.Width >= 160 && layout.Input.Height >= 24, "Editor no longer usable.");
            Check(!layout.Input.Intersects(layout.Send) && !layout.Input.Intersects(layout.End)
                && !layout.Input.Intersects(layout.Caption) && !layout.Input.Intersects(layout.Header), "Editor overlaps another control.");
            Check(!layout.Send.Intersects(layout.End) && !layout.Header.Intersects(layout.Close), "Action hit targets overlap.");
            Check(layout.Header.Bottom < layout.Outer.Top && layout.Close.Bottom < layout.Outer.Top,
                "Toolbar must stay outside the native input frame.");
            Check(layout.Frame.Contains(layout.InputHitArea) && !layout.InputHitArea.Intersects(layout.Header),
                "Editor hit area escaped its body.");
            Check(layout.Caption.Height > 0 || hideDuplicateName && !layout.Portrait.IsEmpty,
                "NPC name was hidden without a visible portrait.");
            if (!layout.Portrait.IsEmpty)
            {
                Check(safe.Contains(layout.Portrait), "Portrait escaped visible area.");
                Check(!layout.Portrait.Intersects(layout.Input) && !layout.Portrait.Intersects(layout.Send)
                    && !layout.Portrait.Intersects(layout.Header), "Portrait obscures editing/actions.");
            }
            var buttons = ChatHeaderLayout.Arrange(layout.Header, new[] { 170, 82, 100 });
            foreach (var button in buttons) Check(layout.Header.Contains(button), "Header action escaped its row.");
            Check(buttons[0].Right < buttons[1].Left && buttons[1].Right < buttons[2].Left,
                "Personality, share and settings no longer occupy a single ordered row.");
            int[] widths = { 210, 82, 100, 150, 120 };
            if (!ChatHeaderLayout.Fits(layout.Header, widths))
            {
                var primary = ChatHeaderLayout.Arrange(layout.Header, new[] { 72, 150, 120 });
                foreach (var button in primary) Check(layout.Header.Contains(button), "Primary action clipped on narrow screen.");
                var overflow = ChatHeaderLayout.Dropdown(layout.Outer, widths.Take(3).ToArray(), layout.Header.Height);
                foreach (var button in overflow) Check(layout.Outer.Contains(button) && button.Height >= 24,
                    "Overflow action clipped or too small.");
                Check(overflow[0].Bottom < overflow[1].Top && overflow[1].Bottom < overflow[2].Top,
                    "Overflow actions overlap.");
            }
        }
        foreach (var size in new[] { new Point(800, 600), new Point(1280, 720), new Point(1920, 1080) })
        {
            Rectangle safe = new(20, 20, size.X - 40, size.Y - 40);
            var input = ChatComposerLayout.Create(safe, 1f, 44, 100, 140, true, true);
            var reply = NativeReplyLayout.Create(safe, 1296, 456, 44);
            Check(input.Outer == reply.Frame && input.Scale == reply.Scale,
                "At 100%, input and native reply must have identical frame geometry.");
            Check(Math.Abs(input.Portrait.Width / (float)input.Frame.Width - 484f / 1200f) < .01f,
                "Composer replaced the original full-height portrait area with a small custom portrait.");
            Check(input.Caption.Height == 0, "Duplicate NPC name escaped the native name plate.");
            var noPortrait = ChatComposerLayout.Create(safe, 1f, 44, 100, 140, false, true);
            Check(noPortrait.Portrait.IsEmpty && noPortrait.Caption.Height > 0 && noPortrait.Input.Width > input.Input.Width,
                "Portrait-disabled/no-portrait NPCs lost their name or usable editing space.");
            var movedSource = ChatComposerLayout.Create(safe, 1f, 44, 100, 140, true, true,
                new Rectangle(213, 107, 1200, 384));
            Check(movedSource.Outer == input.Outer && movedSource.Input == input.Input && movedSource.Portrait == input.Portrait,
                "Native source origin leaked into mouse/IME screen coordinates.");
        }
        var roomy = new Rectangle(20, 20, 2560, 1440);
        var small = ChatComposerLayout.Create(roomy, .75f, 44, 100, 140, true, true);
        var large = ChatComposerLayout.Create(roomy, 1.5f, 44, 100, 140, true, true);
        Check(large.Outer.Width == small.Outer.Width * 2 && large.Portrait.Width == small.Portrait.Width * 2,
            "Input size setting distorted portrait proportions.");
        var player = new Point(25, 30);
        var taken = new HashSet<Point>();
        for (int index = 0; index < 3; index++)
        {
            var candidates = SceneStagingPolicy.Candidates(player, index).ToArray();
            Check(candidates.Length == candidates.Distinct().Count(), "Duplicate scene tile.");
            Check(candidates.All(tile => Distance(tile, player) is >= 1 and <= 4), "Scene moved outside conversation range or onto player.");
            Check(candidates.TakeWhile(tile => Distance(tile, player) <= 3).Count() == candidates.Count(tile => Distance(tile, player) <= 3),
                "Distant fallback considered before nearby tiles.");
            Check(taken.Add(candidates[0]) && Distance(candidates[0], player) == 2, "Actors share a preferred tile or start too far away.");
        }
        foreach (string locale in new[] { "en", "zh" })
        using (L.BeginRequest(locale))
            Check(L.F("free-scene.addressing", $"对 {"Sam"} 说").Contains("Sam"), "Selected addressee lost in translation.");
        Console.WriteLine("Chat composer geometry and scene staging passed.");
    }
    private static int Distance(Point a, Point b) => Math.Abs(a.X-b.X)+Math.Abs(a.Y-b.Y);
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
