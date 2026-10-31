using Microsoft.Xna.Framework;

namespace VivantValley.Services;

internal static class SceneStagingPolicy
{
    public static IEnumerable<Point> Candidates(Point player, int index)
    {
        Point preferred = player + ((index % 3) switch
        {
            0 => new Point(-2, 0),
            1 => new Point(2, 0),
            _ => new Point(0, -2),
        });
        var nearby = new List<Point>();
        for (int x = -4; x <= 4; x++)
        for (int y = -4; y <= 4; y++)
        {
            int distance = Math.Abs(x) + Math.Abs(y);
            if (distance is >= 1 and <= 4) nearby.Add(player + new Point(x, y));
        }
        return nearby.OrderBy(tile => Distance(tile, player) > 3)
            .ThenBy(tile => Distance(tile, preferred))
            .ThenBy(tile => tile.Y).ThenBy(tile => tile.X);
    }

    private static int Distance(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
}
