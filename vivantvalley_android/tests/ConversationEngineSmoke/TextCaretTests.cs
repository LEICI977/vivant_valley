using VivantValley.Services;

internal static class TextCaretTests
{
    public static void Run()
    {
        var buffer = new SingleLineTextBuffer(800) { Text = "你好AVe\u0301🌙再见" };
        int[] boundaries = buffer.GetBoundaries();
        // Model inter-character spacing: measuring separate characters loses this spacing.
        // Hit testing must use the same whole-prefix measurement as the drawn caret.
        float Measure(int start, int end)
        {
            int elements = boundaries.Count(b => b > start && b <= end);
            return elements == 0 ? 0 : elements * 16 + (elements - 1) * 4;
        }
        foreach (int start in boundaries)
        foreach (float scale in new[] { .75f, 1f, 1.5f })
        {
            float Scaled(int a, int b) => Measure(a, b) * scale;
            int end = TextCaretGeometry.VisibleEnd(boundaries, start, 96, Scaled);
            foreach (int expected in boundaries.Where(b => b >= start && b <= end))
            {
                int hit = TextCaretGeometry.HitTest(boundaries, start, end, Scaled(start, expected), Scaled);
                Check(hit == expected, "Mouse caret drifted after spacing, scaling or horizontal scrolling.");
                buffer.SetCaret(hit);
                Check(buffer.CaretIndex == expected, "Hit split a Unicode text element.");
            }
            Check(TextCaretGeometry.HitTest(boundaries, start, end, 1000, Scaled) == end,
                "Click after visible text should select its end.");
        }
        buffer.Text = "你好世界";
        boundaries = buffer.GetBoundaries();
        buffer.SetCaret(TextCaretGeometry.HitTest(boundaries, 0, 4, 36, (a, b) => (b-a)*18));
        buffer.Insert("，");
        Check(buffer.Text == "你好，世界", "Typing after a click appended instead of inserting.");
        Check(TextCaretGeometry.HitTest(new[] { 0 }, 0, 0, 50, (_, _) => 0) == 0, "Empty input caret failed.");
        Console.WriteLine("Mouse caret geometry, Unicode insertion and scrolling passed.");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
