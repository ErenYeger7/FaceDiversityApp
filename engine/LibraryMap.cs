using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// A LIBRARY BUILD map: what `build-library` put into FDA_Library_<yymmdd>.esp — one row per donor face.
// Keyed by the ORIGINAL face id ("<source plugin>#<origin FormKey>", = Faces.FaceId) so whitelists,
// blacklists, `as:` and `serve:` keep working, and carrying everything a SkyPatcher config needs to
// reference the donor WITHOUT the source mods installed: the library FormID, the (copied) race and skin
// keys, weight and sex. Written twice: next to the plugin (ships with the mod, human-readable) and under
// library/builds/ (what Mod Creator lists as a face source). A .csv twin sits next to each yaml.
class LibraryMap
{
    public class Row
    {
        public string Key { get; set; } = "";          // "<source plugin>#<origin FormKey>"
        public string Source { get; set; } = "";       // original plugin filename
        public string SourceMod { get; set; } = "";    // original mod folder name (mugshot pack match)
        public string Npc { get; set; } = "";          // original EditorID
        public string Name { get; set; } = "";
        public string Origin { get; set; } = "";       // origin FormKey string ("013265:Skyrim.esm")
        public string Library { get; set; } = "";      // FormID (hex, no plugin) in the library plugin
        public string Plugin { get; set; } = "";       // which plugin of a SET holds the donor ("" = the map's Plugin)
        public string EditorId { get; set; } = "";     // donor EditorID in the library plugin
        public string PoolRace { get; set; } = "";     // join key (esm race name, or hex) — identical to FaceInfo.PoolRace
        public string Race { get; set; } = "";         // FormKey the donor's race resolves to (copied into the library, or vanilla)
        public string Skin { get; set; } = "";         // FormKey of the donor's per-NPC skin (copied), or ""
        public float Weight { get; set; }
        public string Sex { get; set; } = "F";
    }

    public string Plugin { get; set; } = "";           // a single build's plugin, or the SET name (rows then carry their own Plugin)
    public string Built { get; set; } = "";
    public List<string> Plugins { get; set; } = new(); // set members (empty for a single build)
    public List<Row> Faces { get; set; } = new();
    public string PluginOf(Row r) => string.IsNullOrWhiteSpace(r.Plugin) ? Plugin : r.Plugin;

    static readonly IDeserializer De = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();

    public static string BuildsDir() => Path.Combine(Library.Root(), "builds");

    // Mod Creator addresses a build as "<builds dir>\<plugin>.esp" (a pseudo-source path the mod picker
    // composes like any other); the map is the sibling yaml.
    public static bool IsLibraryPath(string path)
    {
        try
        {
            var dir = Path.GetFullPath(Path.GetDirectoryName(path) ?? "").TrimEnd('\\', '/');
            return string.Equals(dir, Path.GetFullPath(BuildsDir()).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                   && path.EndsWith(".esp", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    public static string MapPathFor(string espPath) => Path.Combine(BuildsDir(), Path.GetFileNameWithoutExtension(espPath) + ".yaml");

    public static LibraryMap? Load(string yamlPath)
    {
        if (!File.Exists(yamlPath)) return null;
        try
        {
            var m = De.Deserialize<LibraryMap>(File.ReadAllText(yamlPath));
            if (m is null || string.IsNullOrWhiteSpace(m.Plugin)) return null;
            m.Faces = m.Faces.Where(r => !string.IsNullOrWhiteSpace(r.Key) && !string.IsNullOrWhiteSpace(r.Library)).ToList();
            return m;
        }
        catch { return null; }
    }

    // Hand-emitted yaml (flow mappings — a block "- key:, source:" form is invalid YAML) + a csv twin.
    public static void Save(string yamlPath, LibraryMap m)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(yamlPath)!);
        var sb = new System.Text.StringBuilder();
        sb.Append("# FaceDiversityApp — library build map: which donor NPC in the plugin carries which harvested face.\n");
        sb.Append("# key = original face id (source plugin # origin FormKey); library = the donor's FormID in this plugin;\n");
        sb.Append("# race/skin = what the donor resolves to (copied into this plugin when the source's were custom).\n");
        sb.Append("plugin: ").Append(Library.Quote(m.Plugin)).Append('\n');
        sb.Append("built: ").Append(Library.Quote(m.Built)).Append('\n');
        if (m.Plugins.Count > 0) sb.Append("plugins: [").Append(string.Join(", ", m.Plugins.Select(Library.Quote))).Append("]\n");
        sb.Append("faces:\n");
        foreach (var r in m.Faces)
            sb.Append("  - { key: ").Append(Library.Quote(r.Key))
              .Append(", source: ").Append(Library.Quote(r.Source))
              .Append(", sourceMod: ").Append(Library.Quote(r.SourceMod))
              .Append(", npc: ").Append(Library.Quote(r.Npc))
              .Append(", name: ").Append(Library.Quote(r.Name))
              .Append(", origin: ").Append(Library.Quote(r.Origin))
              .Append(", library: ").Append(Library.Quote(r.Library))
              .Append(string.IsNullOrWhiteSpace(r.Plugin) ? "" : ", plugin: " + Library.Quote(r.Plugin))
              .Append(", editorId: ").Append(Library.Quote(r.EditorId))
              .Append(", poolRace: ").Append(Library.Quote(r.PoolRace))
              .Append(", race: ").Append(Library.Quote(r.Race))
              .Append(", skin: ").Append(Library.Quote(r.Skin))
              .Append(", weight: ").Append(r.Weight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
              .Append(", sex: ").Append(Library.Quote(r.Sex)).Append(" }\n");
        File.WriteAllText(yamlPath, sb.ToString());

        var csv = new System.Text.StringBuilder();
        csv.Append("key,source,sourceMod,npc,name,origin,plugin,library,editorId,poolRace,race,skin,weight,sex\n");
        static string C(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        foreach (var r in m.Faces)
            csv.Append(string.Join(",", new[] { C(r.Key), C(r.Source), C(r.SourceMod), C(r.Npc), C(r.Name), C(r.Origin), C(m.PluginOf(r)), C(r.Library), C(r.EditorId),
                                                C(r.PoolRace), C(r.Race), C(r.Skin), r.Weight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), C(r.Sex) })).Append('\n');
        File.WriteAllText(Path.ChangeExtension(yamlPath, ".csv"), csv.ToString());
    }

    // The build presented as Mod Creator faces: same ids/keys as the original harvest, so the library's
    // whitelists/blacklists/`as:`/`serve:` (keyed by ORIGINAL plugin) still apply, and mugshots resolve
    // by origin FormKey + original mod folder. No head parts (nothing to deep-copy — it's all in the build).
    public static List<Faces.FaceInfo> ToFaces(LibraryMap m) =>
        m.Faces.Select(r => new Faces.FaceInfo(
            r.Key, r.Source, r.Origin, r.Npc, r.PoolRace, r.Race, null,
            CustomRace: !r.Race.EndsWith(":Skyrim.esm", StringComparison.OrdinalIgnoreCase) && !Classify.BaseMasters.Contains(r.Race.Split(':').Last()),
            Sex: r.Sex, SourceMod: r.SourceMod, HeadPartKeys: Array.Empty<string>(), Library: true)).ToList();

    // Per-race {f,m} counts of a build (the "📚 Nord 12 · Orc 1" line in the mod picker).
    public static Dictionary<string, Library.RaceCount> Summary(LibraryMap m)
    {
        var d = new Dictionary<string, Library.RaceCount>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in m.Faces)
        {
            if (!d.TryGetValue(r.PoolRace, out var c)) d[r.PoolRace] = c = new Library.RaceCount();
            if (r.Sex.Equals("M", StringComparison.OrdinalIgnoreCase)) c.M++; else c.F++;
        }
        return d;
    }

    public record BuildInfo(string Name, string Plugin, int Faces, string Built, string Map, List<string> Plugins);
    public static List<BuildInfo> List()
    {
        var res = new List<BuildInfo>();
        if (!Directory.Exists(BuildsDir())) return res;
        foreach (var y in Directory.EnumerateFiles(BuildsDir(), "*.yaml").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
            if (Load(y) is { } m) res.Add(new BuildInfo(Path.GetFileNameWithoutExtension(y), m.Plugin, m.Faces.Count, m.Built, y, m.Plugins));
        return res;
    }
}
