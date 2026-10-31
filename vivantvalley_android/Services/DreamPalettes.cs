namespace VivantValley.Services;

/// <summary>RGB palettes selected once per episode, independent of frame rate and language.</summary>
public sealed record DreamPalette(string Id, int Top, int Middle, int Bottom, int GlowA, int GlowB, int Tint, int Stars);
public static class DreamPalettes
{
    private static readonly DreamPalette[] palettes =
    {
        new("amethyst",  0x201432,0x513267,0x233653,0xC691F1,0x82B8E9,0xF0E4FF,0xF2DFFF),
        new("rose",      0x321B32,0x713B62,0x46334F,0xF5A6C8,0xD1A9F2,0xFFE5EE,0xFFD6E8),
        new("peach",     0x392738,0x865452,0x584B61,0xFFCA9D,0xE8A4CC,0xFFF0DD,0xFFE2BC),
        new("gold",      0x302633,0x705A35,0x3A4658,0xF9D983,0xAABDE9,0xFFF4D8,0xFFE9AA),
        new("forest",    0x102D2B,0x275B4C,0x344552,0x91DEB1,0xD0D9A0,0xE0FFE9,0xCEFFE7),
        new("turquoise", 0x102A3B,0x286477,0x344767,0x85E0DC,0xA4BFF4,0xDFFFF9,0xCEFFF4),
        new("midnight",  0x101E37,0x304676,0x3C355D,0x9CBEFA,0xC4A9ED,0xE1ECFF,0xD9E8FF),
        new("aurora",    0x17343B,0x316666,0x563F6C,0x94F0C4,0xEAB1E9,0xE6FFF0,0xD5FFE3),
        new("wine",      0x2C172D,0x65334D,0x514543,0xEAA5B7,0xE7BF91,0xFFE5E9,0xFFD7DC),
        new("sunset",    0x392134,0x824853,0x5A4168,0xFFB98A,0xD6A1E6,0xFFE8D8,0xFFD4B4),
        new("moonstone", 0x24313D,0x526774,0x4C526C,0xCDDFEE,0xC4C9E9,0xF0F7FF,0xF2F5FF),
        new("indigo",    0x201D3E,0x454279,0x3E536D,0xB5ADF6,0x8DD8DD,0xEEE9FF,0xE0DEFF),
    };
    public static DreamPalette Select(string npc, string seed)
    {
        // First colour is characteristic; alternatives retain a related but visibly different mood.
        int[] family = npc switch
        {
            "Abigail" => new[]{0,11,8}, "Sebastian" => new[]{6,11,5},
            "Haley" => new[]{2,1,3}, "Emily" => new[]{7,5,0},
            "Leah" => new[]{4,3,7}, "Penny" => new[]{1,2,3},
            "Maru" => new[]{11,5,10}, "Harvey" => new[]{5,4,10},
            "Elliott" => new[]{8,9,3}, "Shane" => new[]{6,4,10},
            "Sam" => new[]{3,9,2}, "Alex" => new[]{9,3,4},
            "Willy" or "Krobus" => new[]{5,6,10},
            "Linus" or "Caroline" or "Jas" or "Leo" => new[]{4,7,3},
            "Wizard" or "Dwarf" or "Marlon" => new[]{11,0,6},
            "Evelyn" or "Vincent" or "Jodi" => new[]{2,3,1},
            _ => Array.Empty<int>(),
        };
        double roll = DreamCollage.Unit(seed+":"+npc,0,"palette");
        int index = family.Length == 0 ? (int)(roll*palettes.Length)
            : family[roll < .5 ? 0 : roll < .75 ? 1 : 2];
        return palettes[index];
    }
}
