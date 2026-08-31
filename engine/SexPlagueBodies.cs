using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

// Builds the "SexPlague Bodies" mod: an OBody NG Preset Distribution Assistant INI that maps each
// SexPlague plague-stage faction to a pool of Bodies-of-Tamriel 3BA presets, filtered by the BOT
// classification chart baked into every preset name:  ((Cup-Shape[-Feature...]-Mileage) Name - Author
// Chains onto the SexPlague overlay (which puts feminized males into SexPlague_begin/mid/adv); OBody
// then rolls a random body from each faction's filtered pool.
static class SexPlagueBodies
{
    static readonly string[] Cups = { "AA", "BB", "CC", "DD", "EE", "FF", "HH", "OO" };
    // longer codes first so a plain Split match is unambiguous (HGX before HG, PetiteX before Petite)
    static readonly string[] Features = { "Nips", "Inv", "Sag", "Fat", "HGX", "HG" };
    static readonly string[] Mileage = { "VP", "CP", "AP", "OPX", "OP" };

    record Tier(string Faction, HashSet<string> Cups, HashSet<string> Mileage, HashSet<string> ExcludeFeatures);
    record Preset(string Name, string Cup, string Mile, HashSet<string> Feats);

    // sexplague-bodies --presets <SliderPresets dir> --config <sexplague_bodies.yaml> --out <mod folder>
    //                  [--name OBodyNG_PDA_SexPlague.ini]
    public static int Run(string[] args)
    {
        string? presetsDir = null, configPath = null, outFolder = null, iniName = "OBodyNG_PDA_SexPlague.ini";
        for (int i = 1; i < args.Length; i++)
            switch (args[i])
            {
                case "--presets": presetsDir = args[++i]; break;
                case "--config": configPath = args[++i]; break;
                case "--out": outFolder = args[++i]; break;
                case "--name": iniName = args[++i]; break;
                case "--game-version": i++; break; // handled in Program.cs
            }
        // default the presets dir + config to the per-PC settings / app config when not given, so the
        // regen shortcut needs no hardcoded paths and stays portable across PCs.
        if (string.IsNullOrWhiteSpace(presetsDir)) presetsDir = Settings.Current.BotPresets;
        if (string.IsNullOrWhiteSpace(configPath)) configPath = Path.Combine(Settings.ConfigDir(), "sexplague_bodies.yaml");
        if (string.IsNullOrWhiteSpace(outFolder)) outFolder = Path.Combine(Settings.AppHome(), "out", "SexPlague Bodies");
        if (string.IsNullOrWhiteSpace(presetsDir) || !Directory.Exists(presetsDir))
        { Console.Error.WriteLine("sexplague-bodies: no presets dir (pass --presets or set botPresets in config/settings.json)"); return 1; }
        if (!File.Exists(configPath))
        { Console.Error.WriteLine("sexplague-bodies: config not found: " + configPath); return 1; }

        var (plugin, mode, tiers) = LoadConfig(configPath);
        if (tiers.Count == 0) { Console.Error.WriteLine("no tiers in " + configPath); return 1; }

        // Read every BodySlide preset's authoritative <Preset name=> and parse its chart.
        var presets = new List<Preset>();
        int noChart = 0, badPipe = 0;
        var nameRx = new Regex("<Preset\\s+name=\"([^\"]*)\"", RegexOptions.IgnoreCase);
        foreach (var xml in Directory.EnumerateFiles(presetsDir, "*.xml", SearchOption.TopDirectoryOnly))
        {
            string text;
            try { text = File.ReadAllText(xml); } catch { continue; }
            var nm = nameRx.Match(text);
            if (!nm.Success) continue;
            var name = nm.Groups[1].Value;
            if (name.Contains('|')) { badPipe++; continue; }         // '|' is the INI delimiter — unusable
            var p = Parse(name);
            if (p is null) { noChart++; continue; }
            presets.Add(p);
        }

        // Classify into tiers: a preset joins a tier when cup ∈ tier.cups AND mileage ∈ tier.mileage AND
        // none of its features are excluded. (Cups are usually disjoint across tiers, so no double-count.)
        var iniLines = new List<string>
        {
            "; SexPlague Bodies — OBody NG preset distribution by plague stage (generated).",
            "; Regenerate: facediv sexplague-bodies --presets <dir> --config <sexplague_bodies.yaml> --out <mod>",
            ""
        };
        var report = new List<(string faction, int count)>();
        foreach (var t in tiers)
        {
            var picked = presets.Where(p => t.Cups.Contains(p.Cup) && t.Mileage.Contains(p.Mile)
                                            && !p.Feats.Overlaps(t.ExcludeFeatures))
                                .Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            report.Add((t.Faction, picked.Count));
            iniLines.Add($"; {t.Faction}: cups [{string.Join(",", t.Cups)}]  mileage [{string.Join(",", t.Mileage)}]" +
                         (t.ExcludeFeatures.Count > 0 ? $"  exclude [{string.Join(",", t.ExcludeFeatures)}]" : "") +
                         $"  -> {picked.Count} presets");
            iniLines.Add(picked.Count > 0
                ? $"factionFemale = {t.Faction}|{string.Join(",", picked)}|{mode}"
                : $"; (no presets matched {t.Faction})");
            iniLines.Add("");
        }

        Directory.CreateDirectory(outFolder);
        var iniPath = Path.Combine(outFolder, iniName);
        File.WriteAllLines(iniPath, iniLines);

        File.WriteAllText(Path.Combine(outFolder, "README.txt"),
            "SexPlague Bodies — generated by FaceDiversityApp (personal use only; do not redistribute)\n\n" +
            "Ships one OBody NG Preset Distribution Assistant INI that assigns Bodies-of-Tamriel 3BA body\n" +
            "presets to the SexPlague plague-stage factions, so feminized males get a body matching their\n" +
            "stage. REQUIRES: OBody NG, OBody NG Preset Distribution Assistant NG, 600+ 3BA Bodies of\n" +
            $"Tamriel, and {plugin} (for the factions) — all enabled.\n\n" +
            "Install like any mod (enable in your mod manager). The PDA merges this INI into OBody's\n" +
            "preset distribution config at launch.\n\n" +
            "Pools (regenerate after editing config/sexplague_bodies.yaml):\n" +
            string.Join("\n", report.Select(r => $"  {r.faction}: {r.count} presets")) + "\n");

        Console.WriteLine($"SexPlague Bodies: {presets.Count} classifiable presets" +
                          (noChart > 0 ? $" ({noChart} skipped: no cup chart)" : "") +
                          (badPipe > 0 ? $" ({badPipe} skipped: '|' in name)" : "") + ".");
        foreach (var (f, c) in report) Console.WriteLine($"  {f,-18} {c,4} presets" + (c == 0 ? "  (!)" : ""));
        Console.WriteLine($"Wrote {iniPath}");
        return 0;
    }

    // Parse the leading (…) chart from a preset name: cup = first segment, mileage = last (if a known
    // code), features = middle segments that are feature codes. Returns null for non-BOT presets.
    static Preset? Parse(string name)
    {
        var m = Regex.Match(name, @"^\(+([^)]*)\)");
        if (!m.Success) return null;
        var segs = m.Groups[1].Value.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segs.Length < 2) return null;
        var cup = segs[0];
        if (!Cups.Contains(cup)) return null;
        var mile = Mileage.Contains(segs[^1]) ? segs[^1] : "?";
        var feats = segs.Skip(1).Take(Math.Max(0, segs.Length - 2))
                        .Where(s => Features.Contains(s)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new Preset(name, cup, mile, feats);
    }

    static (string plugin, string mode, List<Tier> tiers) LoadConfig(string path)
    {
        var tiers = new List<Tier>(); string plugin = "SexPlagueFactions.esp", mode = "*";
        var root = new Deserializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        if (!root.TryGetValue("sexplague_bodies", out var sv) || sv is not Dictionary<object, object> sb)
            return (plugin, mode, tiers);
        if (sb.TryGetValue("faction_plugin", out var fp) && fp is not null) plugin = fp.ToString()!;
        if (sb.TryGetValue("mode", out var md) && md is not null) mode = md.ToString()!;
        HashSet<string> ToSet(object? v) => v is List<object> l
            ? l.Select(x => x.ToString()!.Trim()).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
        if (sb.TryGetValue("tiers", out var tv) && tv is List<object> tl)
            foreach (var t in tl)
                if (t is Dictionary<object, object> td && td.TryGetValue("faction", out var fac) && fac is not null)
                    tiers.Add(new Tier(fac.ToString()!,
                        ToSet(td.GetValueOrDefault("cups")),
                        ToSet(td.GetValueOrDefault("mileage")),
                        ToSet(td.GetValueOrDefault("exclude_features"))));
        return (plugin, mode, tiers);
    }
}
