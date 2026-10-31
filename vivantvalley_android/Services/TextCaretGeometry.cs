namespace VivantValley.Services;

/// <summary>Hit testing uses the same measured prefixes as rendering, including font spacing/kerning.</summary>
public static class TextCaretGeometry
{
    public static int VisibleEnd(int[] boundaries, int start, float width, Func<int, int, float> measure)
    {
        int end = start;
        foreach (int boundary in boundaries.Where(b => b > start))
        {
            if (measure(start, boundary) > width) break;
            end = boundary;
        }
        return end;
    }

    public static int HitTest(int[] boundaries, int start, int end, float target, Func<int, int, float> measure)
    {
        int previous = start;
        float previousX = 0;
        foreach (int boundary in boundaries.Where(b => b > start && b <= end))
        {
            float x = measure(start, boundary);
            if (target < (previousX + x) / 2f) return previous;
            previous = boundary; previousX = x;
        }
        return previous;
    }
}
