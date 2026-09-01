using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// Head/skin-compatibility groups for the Library face merger ("Also serve" / double-dip).
//
// A face may ALSO be generated for another race ONLY if that race sits in the SAME group here. The extra
// assignment is an OVERLAY: the target keeps ITS own race (a vampire stays a vampire) and merely wears the
// head-compatible face, so a group must only ever contain races that share the head mesh + skin family.
// Vanilla base<->vampire pairs qualify (verified in Skyrim.esm: identical MaleHead*/skin ARMO — only the
// eyes + skin normal differ). Everything else is left isolated so the UI can't offer a fidelity-breaking
// mapping (e.g. a human face onto an elf/beast). Config is `config/race_compat.yaml`; user-editable.
static class RaceCompat
{
    static Dictionary<string, HashSet<string>> _members = new(StringComparer.OrdinalIgnoreCase);

    class Doc { public List<List<string>> Groups { get; set; } = new(); }

    public static void Load(string? yamlPath)
    {
        _members = new(StringComparer.OrdinalIgnoreCase);
        if (yamlPath is null || !File.Exists(yamlPath)) return;
        try
        {
            var de = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
                                              .IgnoreUnmatchedProperties().Build();
            var doc = de.Deserialize<Doc>(File.ReadAllText(yamlPath)) ?? new Doc();
            foreach (var g in doc.Groups)
            {
                var set = new HashSet<string>(g.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
                                              StringComparer.OrdinalIgnoreCase);
                if (set.Count < 2) continue;                 // a lone race has nobody to share with
                foreach (var r in set) _members[r] = set;    // each race -> its full group set (shared ref)
            }
        }
        catch { _members = new(StringComparer.OrdinalIgnoreCase); }
    }

    // Other races a face of `race` may ALSO serve (its group minus itself).
    public static List<string> Others(string race) =>
        _members.TryGetValue(race, out var set)
            ? set.Where(r => !string.Equals(r, race, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
            : new();

    // Same race, or same group. Guards the engine even if a stale `as:` slips through the UI.
    public static bool AreCompatible(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || (_members.TryGetValue(a, out var set) && set.Contains(b));

    // race -> the others it may serve, for every race in the config (UI dropdown source).
    public static Dictionary<string, List<string>> Map() =>
        _members.Keys.ToDictionary(r => r, Others, StringComparer.OrdinalIgnoreCase);
}
