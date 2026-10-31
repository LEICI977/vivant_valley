using StardewValley;

namespace VivantValley.Services;

public sealed class WeeklyStoryLocationService
{
    private static readonly IReadOnlyDictionary<string, LocationProfile> KnownLocations
        = new Dictionary<string, LocationProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Farm"] = new("location.farm", new[] { "农场", "家门口", "日常", "拜访" }, 3, false, false),
            ["FarmHouse"] = new("location.farmhouse", new[] { "农舍", "家中", "私密", "拜访" }, 2, false, false),
            ["Saloon"] = new("location.saloon", new[] { "酒吧", "社交", "夜晚" }, 3, false, false),
            ["Beach"] = new("location.beach", new[] { "海岸", "码头", "夜景", "开阔" }, 3, false, false),
            ["Mountain"] = new("location.mountain", new[] { "山景", "湖边", "安静", "夜晚" }, 3, false, false),
            ["Forest"] = new("location.cindersap-forest", new[] { "树林", "湖边", "秘密会面", "雨天" }, 3, false, false),
            ["Town"] = new("location.pelican-town", new[] { "广场", "夜色", "偶遇", "热闹" }, 3, false, false),
            ["Railroad"] = new("location.railroad", new[] { "偏僻", "列车", "秘密", "夜晚" }, 3, false, true),
            ["Custom_Ridgeside_RidgesideVillage"] = new("location.ridgeside-village", new[] { "山村", "街道", "约会", "偶遇" }, 3, false, true),
            ["Custom_Ridgeside_Ridge"] = new("location.the-ridge", new[] { "山景", "观景", "浪漫", "安静" }, 3, false, true),
            ["Custom_Ridgeside_RidgeFalls"] = new("location.ridge-falls", new[] { "瀑布", "水边", "浪漫", "隐秘" }, 3, false, true),
            ["Custom_Ridgeside_RSVCliff"] = new("location.rsv-cliff", new[] { "高处", "观景", "刺激", "夜晚" }, 3, false, true),
            ["Custom_Ridgeside_RidgeForest"] = new("location.ridge-forest", new[] { "危险", "森林", "冒险", "秘密" }, 3, true, true),
            ["Custom_Ridgeside_RSVSpiritRealm"] = new("location.spirit-realm", new[] { "危险", "神秘", "超自然", "秘密" }, 3, true, true),
        };

    public IReadOnlyList<WeeklyStoryLocationCandidate> BuildCandidates()
    {
        var result = new List<WeeklyStoryLocationCandidate>();
        foreach ((string id, LocationProfile profile) in KnownLocations)
        {
            GameLocation? location = FindLocation(id);
            if (location is null || location.Map is null || location.currentEvent is not null)
                continue;
            result.Add(new WeeklyStoryLocationCandidate(
                id,
                string.IsNullOrWhiteSpace(location.DisplayName) ? L.T(profile.DisplayName) : location.DisplayName,
                profile.Tags,
                profile.MaximumActors,
                profile.Dangerous,
                profile.RequiresPriorVisit));
        }
        return result;
    }

    public bool TryResolve(string locationId, out GameLocation? location)
    {
        location = null;
        if (string.IsNullOrWhiteSpace(locationId) || !KnownLocations.ContainsKey(locationId.Trim()))
            return false;
        location = FindLocation(locationId.Trim());
        return location is not null && location.Map is not null;
    }

    public bool TryGetCandidate(string locationId, out WeeklyStoryLocationCandidate? candidate)
    {
        candidate = BuildCandidates().FirstOrDefault(location =>
            location.Id.Equals(locationId, StringComparison.OrdinalIgnoreCase));
        return candidate is not null;
    }

    private static GameLocation? FindLocation(string name)
        => Game1.getLocationFromName(name)
           ?? Game1.locations.FirstOrDefault(location =>
               location.NameOrUniqueName.Equals(name, StringComparison.OrdinalIgnoreCase)
               || location.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private sealed record LocationProfile(
        string DisplayName,
        IReadOnlyList<string> Tags,
        int MaximumActors,
        bool Dangerous,
        bool RequiresPriorVisit);
}
