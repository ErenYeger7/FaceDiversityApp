using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// The "Library" — personal, cross-modlist curation of which harvested faces you actually want.
//
// TWO scopes, deliberately different (not redundant):
//   • Per-mod WHITELIST  — library/mods/<plugin>.yaml — the faces you approved FROM one source. In Mod
//     Creator Mode: file ABSENT  => that source behaves exactly like today (pool everything); file
//     PRESENT => only the listed faces enter the pool (everything unlisted is blocked). We keep a
//     whitelist ONLY — "not selected" IS "blocked" — so there is never a whitelist+blacklist pair to
//     drift out of sync.
//   • Global BLACKLIST   — library/blacklisted_npcs.yaml — NPCs you never want from ANY mod, keyed by
//     ORIGIN FormKey (master+id). Because a replacer's override keeps the origin NPC's FormKey, one
//     entry vetoes every replacer's take on that NPC at once. This is a cross-mod veto, a different axis
//     from the per-mod whitelist, so it is not redundant with it.
//
// Effective "allowed" in Mod Creator Mode = (no whitelist for the source OR key ∈ whitelist) AND key ∉
// blacklist. The blacklist wins even over a whitelisted face.
//
// Face identity here is the FormKey string exactly as Faces emits it (e.g. "01326A:Skyrim.esm"): the
// origin key for a vanilla override (shared across replacers — that is what makes the blacklist global)
// and the mod's own key for a mod-added NPC (unique — blacklisting it affects only that mod). Compared
// case-insensitively so a hand-edited file still matches.
//
// Files are personal data (they encode taste + which third-party packs you use) and are gitignored.
static class Library
{
    // A face/NPC entry. `As` is reserved for the PHASE-2 race merger (treat this face as another race);
    // unused in phase 1 but already in the on-disk shape so enabling it later needs no format change.
    public class FaceEntry
    {
        public string Key { get; set; } = "";
        public string? As { get; set; }
        [YamlIgnore] public string? EditorID { get; set; }   // written as a trailing comment only
        [YamlIgnore] public string? Race { get; set; }       // used for race_summary + trailing comment
        [YamlIgnore] public string? Sex { get; set; }        // "F"/"M" — used for race_summary only
    }

    // Female/male face counts for one race (the per-mod coverage summary surfaced in Mod Creator).
    public class RaceCount { public int F { get; set; } public int M { get; set; } }

    class WhitelistDoc
    {
        public string? Plugin { get; set; }
        public Dictionary<string, RaceCount>? RaceSummary { get; set; }   // key = "raceSummary" via CamelCase convention
        public List<FaceEntry> Faces { get; set; } = new();
    }
    class BlacklistDoc { public List<FaceEntry> Npcs { get; set; } = new(); }

    static readonly IDeserializer De = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties().Build();

    public static string Root() => Path.Combine(Settings.AppHome(), "library");
    static string ModsDir() => Path.Combine(Root(), "mods");
    static string BlacklistPath() => Path.Combine(Root(), "blacklisted_npcs.yaml");
    // <plugin>.yaml — plugin filenames are already valid filenames, so just suffix .yaml.
    static string WhitelistPath(string plugin) => Path.Combine(ModsDir(), plugin + ".yaml");

    // ---- per-mod whitelist ----

    public static bool HasWhitelist(string plugin) => File.Exists(WhitelistPath(plugin));

    // Approved keys for a source, or null if the source has no whitelist file (=> "uncurated", pool all).
    public static HashSet<string>? LoadWhitelist(string plugin)
    {
        var p = WhitelistPath(plugin);
        if (!File.Exists(p)) return null;
        try
        {
            var doc = De.Deserialize<WhitelistDoc>(File.ReadAllText(p)) ?? new WhitelistDoc();
            return new HashSet<string>(doc.Faces.Where(f => !string.IsNullOrWhiteSpace(f.Key)).Select(f => f.Key.Trim()),
                                       StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }  // corrupt file => block all, never crash
    }

    // Full entries (key + optional `as` race override), or empty if no whitelist file.
    public static List<FaceEntry> LoadWhitelistEntries(string plugin)
    {
        var p = WhitelistPath(plugin);
        if (!File.Exists(p)) return new();
        try
        {
            var doc = De.Deserialize<WhitelistDoc>(File.ReadAllText(p)) ?? new WhitelistDoc();
            return doc.Faces.Where(f => !string.IsNullOrWhiteSpace(f.Key)).ToList();
        }
        catch { return new(); }
    }

    // faceKey -> race the face should be POOLED as (phase-2 merger). Only entries carrying a non-empty `as`.
    public static Dictionary<string, string> LoadWhitelistOverrides(string plugin) =>
        LoadWhitelistEntries(plugin)
            .Where(f => !string.IsNullOrWhiteSpace(f.As))
            .GroupBy(f => f.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().As!.Trim(), StringComparer.OrdinalIgnoreCase);

    // Per-race face counts saved with the whitelist, or null if the source has no whitelist (uncurated).
    public static Dictionary<string, RaceCount>? LoadWhitelistSummary(string plugin)
    {
        var p = WhitelistPath(plugin);
        if (!File.Exists(p)) return null;
        try
        {
            var doc = De.Deserialize<WhitelistDoc>(File.ReadAllText(p)) ?? new WhitelistDoc();
            return doc.RaceSummary ?? new Dictionary<string, RaceCount>(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, RaceCount>(StringComparer.OrdinalIgnoreCase); }
    }

    public static string SaveWhitelist(string plugin, IEnumerable<FaceEntry> faces)
    {
        Directory.CreateDirectory(ModsDir());
        var path = WhitelistPath(plugin);
        var sb = new System.Text.StringBuilder();
        sb.Append("# FaceDiversityApp — curated face whitelist for ").Append(plugin).Append('\n');
        sb.Append("# Faces NOT listed here are BLOCKED when this mod is used in Mod Creator Mode.\n");
        sb.Append("# `as: <Race>` (optional) pools the face AS that race — it fills that race's slots and the\n");
        sb.Append("# target then adopts this face's own race (a consistent NPC, not a face on a mismatched body).\n");
        sb.Append("plugin: ").Append(Quote(plugin)).Append('\n');

        var deduped = faces.Where(f => !string.IsNullOrWhiteSpace(f.Key))
                           .GroupBy(f => f.Key.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        // Per-race face counts (a face counts toward its own race AND any `as:` double-dip). Surfaced in
        // Mod Creator so the coverage of a curated mod is visible before it's added (e.g. "only 1 Orc face").
        var summary = new SortedDictionary<string, RaceCount>(StringComparer.OrdinalIgnoreCase);
        void Tally(string? race, string? sex)
        {
            if (string.IsNullOrWhiteSpace(race)) return;
            if (!summary.TryGetValue(race, out var c)) summary[race] = c = new RaceCount();
            if (string.Equals(sex, "M", StringComparison.OrdinalIgnoreCase)) c.M++; else c.F++;
        }
        foreach (var f in deduped) { Tally(f.Race, f.Sex); if (!string.IsNullOrWhiteSpace(f.As)) Tally(f.As, f.Sex); }
        if (summary.Count > 0)
        {
            sb.Append("# faces per race (incl. \"also serve\" double-dips) — surfaced in Mod Creator:\n");
            sb.Append("raceSummary:\n");
            foreach (var kv in summary) sb.Append("  ").Append(kv.Key).Append(": { f: ").Append(kv.Value.F).Append(", m: ").Append(kv.Value.M).Append(" }\n");
        }

        sb.Append("faces:\n");
        foreach (var f in deduped)
        {
            // flow mapping so key + optional `as` sit on one line and still parse (block `- key:, as:` does NOT).
            sb.Append("  - { key: ").Append(Quote(f.Key.Trim()));
            if (!string.IsNullOrWhiteSpace(f.As)) sb.Append(", as: ").Append(Quote(f.As!.Trim()));
            sb.Append(" }");
            var c = Comment(f.EditorID, f.Race);
            if (c is not null) sb.Append("   # ").Append(c);
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    // Delete a source's whitelist => it reverts to "uncurated" (pool everything) in Mod Creator Mode.
    public static bool RemoveWhitelist(string plugin)
    {
        var p = WhitelistPath(plugin);
        if (!File.Exists(p)) return false;
        try { File.Delete(p); return true; } catch { return false; }
    }

    // ---- global NPC blacklist ----

    public static HashSet<string> LoadBlacklist()
    {
        var p = BlacklistPath();
        if (!File.Exists(p)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = De.Deserialize<BlacklistDoc>(File.ReadAllText(p)) ?? new BlacklistDoc();
            return new HashSet<string>(doc.Npcs.Where(n => !string.IsNullOrWhiteSpace(n.Key)).Select(n => n.Key.Trim()),
                                       StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    public static string SaveBlacklist(IEnumerable<FaceEntry> npcs)
    {
        Directory.CreateDirectory(Root());
        var path = BlacklistPath();
        var sb = new System.Text.StringBuilder();
        sb.Append("# FaceDiversityApp — globally blacklisted NPCs (vetoed across ALL mods, in BOTH modes).\n");
        sb.Append("# Keyed by ORIGIN FormKey (master+id): one entry blocks every replacer's take on that NPC.\n");
        sb.Append("npcs:\n");
        foreach (var n in npcs.Where(n => !string.IsNullOrWhiteSpace(n.Key))
                              .GroupBy(n => n.Key.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                              .OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("  - key: ").Append(Quote(n.Key.Trim()));
            var c = Comment(n.EditorID, n.Race);
            if (c is not null) sb.Append("   # ").Append(c);
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    // ---- helpers ----

    // Double-quote a YAML scalar (FormKeys contain ':'). Escape backslash + quote per YAML double-quoted rules.
    static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // Trailing "# EditorID (Race)" so the file is human-scannable; never parsed back.
    static string? Comment(string? editorId, string? race)
    {
        var e = string.IsNullOrWhiteSpace(editorId) ? null : editorId!.Trim();
        var r = string.IsNullOrWhiteSpace(race) ? null : race!.Trim();
        if (e is null && r is null) return null;
        return r is null ? e : (e is null ? "(" + r + ")" : e + " (" + r + ")");
    }
}
