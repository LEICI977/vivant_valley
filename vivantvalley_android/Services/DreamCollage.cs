using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
namespace VivantValley.Services;

public sealed class DreamCollageAsset
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Path { get; set; } = "";
}
public static class DreamCollage
{
    // Stable throughout a dream, different for each save/player/night/direction/NPC.
    // Balance categories before sampling so numerous crop images don't crowd out everything else.
    private static uint Rank(string value)
        => BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static DreamCollageAsset[] Select(IEnumerable<DreamCollageAsset> assets, string seed, int count = 16)
        => assets.Where(a => a is not null && a.Id.Length > 0 && a.Path.Length > 0)
            .GroupBy(a => a.Id).Select(g => g.First())
            .GroupBy(a => a.Category)
            .Select(g => g.OrderBy(a => Rank(seed + ":asset:" + a.Id)).ThenBy(a => a.Id,StringComparer.Ordinal).First())
            .OrderBy(a => Rank(seed + ":category:" + a.Category)).ThenBy(a => a.Id,StringComparer.Ordinal)
            .Take(Math.Clamp(count,0,20)).ToArray();
    public static (float X, float Y)[] Positions(string seed, int count)
    {
        var points = new List<(float X, float Y)>();
        for (int i=0;i<Math.Clamp(count,0,20);i++)
        {
            (float X,float Y) point = default;
            for (int attempt=0;attempt<120;attempt++)
            {
                point = (.055f+(float)Unit(seed,i,"x"+attempt)*.89f,
                    .12f+(float)Unit(seed,i,"y"+attempt)*.35f);
                // Keep both speakers readable; use scattered positions, not sticker rows.
                if (point.X is > .36f and < .65f && point.Y > .25f) continue;
                if (points.All(p => Math.Pow((p.X-point.X)*1.5,2)+Math.Pow(p.Y-point.Y,2) > .009)) break;
            }
            // If rejection sampling exhausts its attempts, retain the speaker-safe band.
            if (point.X is > .36f and < .65f && point.Y > .25f) point.Y = .20f;
            points.Add(point);
        }
        return points.ToArray();
    }
    public static double Unit(string seed, int index, string property)
        => Rank($"{seed}:{index}:{property}") / 4294967296d;
}
