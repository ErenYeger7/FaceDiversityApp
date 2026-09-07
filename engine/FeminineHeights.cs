using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// Race-based height multipliers for feminized males, applied as a SkyPatcher `height=` op on the
// unified per-NPC config line. Config: config/feminine_heights.yaml — `default:` plus
// `heights: { <RaceEditorID>: <multiplier> }`. A vampire race uses its BASE race's value in all cases
// (NordRaceVampire -> NordRace); anything unlisted (incl. mod-added races, which resolve to a hex key)
// uses `default`. The numbers live in the file, not in code — edit the yaml, regenerate.
class FeminineHeights
{
    public float Default { get; private set; } = 0.87f;
    public Dictionary<string, float> Heights { get; } = new(StringComparer.OrdinalIgnoreCase);

    class Doc { public float? Default { get; set; } public Dictionary<string, float>? Heights { get; set; } }

    public static FeminineHeights? Load(string? yamlPath)
    {
        if (yamlPath is null || !File.Exists(yamlPath)) return null;
        try
        {
            var de = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
                                              .IgnoreUnmatchedProperties().Build();
            var doc = de.Deserialize<Doc>(File.ReadAllText(yamlPath));
            if (doc is null) return null;
            var fh = new FeminineHeights();
            if (doc.Default is { } d) fh.Default = d;
            if (doc.Heights is not null)
                foreach (var kv in doc.Heights) fh.Heights[kv.Key.Trim()] = kv.Value;
            return fh;
        }
        catch { return null; }
    }

    // Multiplier for a race EditorID: vampire counterpart -> its base race; exact match; else default.
    public float For(string? raceEid)
    {
        var r = (raceEid ?? "").Replace("Vampire", "", StringComparison.OrdinalIgnoreCase);
        return Heights.TryGetValue(r, out var v) ? v : Default;
    }
}
