using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using System.Text.Json;

// analyze --game <Skyrim.esm> --category bandit --source <plugin> [--source ...] [--out <json>]
// Per-race coverage matrix: demand (vanilla own-traits targets) vs. female-face supply (sources).
static class Analyze
{
    public static int Run(string[] args)
    {
        string? game = null, category = "bandit", outJson = null;
        var sources = new List<string>();
        for (int i = 1; i < args.Length; i++)
            switch (args[i])
            {
                case "--game": game = args[++i]; break;
                case "--category": category = args[++i]; break;
                case "--source": sources.Add(args[++i]); break;
                case "--out": outJson = args[++i]; break;
            }
        if (game is null) { Console.Error.WriteLine("--game <Skyrim.esm> required"); return 1; }
        if (category != "bandit") { Console.Error.WriteLine($"category '{category}' not yet verified; only 'bandit' in v1."); return 1; }

        var esm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(game), GameCfg.Release);
        var raceName = esm.Races.ToDictionary(r => r.FormKey, r => r.EditorID ?? r.FormKey.ToString());
        string RaceOf(INpcGetter n) => raceName.TryGetValue(n.Race.FormKey, out var s) ? s : n.Race.FormKey.ToString();
        bool OwnTraits(INpcGetter n) => !(n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !n.Template.IsNull);
        bool Fem(INpcGetter n) => n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);

        var targets = esm.Npcs.Where(n => (n.EditorID ?? "").StartsWith("EncBandit", StringComparison.OrdinalIgnoreCase) && OwnTraits(n)).ToList();
        var demandM = new Dictionary<string, int>(); var demandF = new Dictionary<string, int>();
        foreach (var n in targets) { var r = RaceOf(n); var d = Fem(n) ? demandF : demandM; d[r] = d.GetValueOrDefault(r) + 1; }

        var supply = new Dictionary<string, int>();
        foreach (var sp in sources)
        {
            var sm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(sp), GameCfg.Release);
            foreach (var n in sm.Npcs.Where(Fem))
            {
                var r = raceName.TryGetValue(n.Race.FormKey, out var s) ? s : n.Race.FormKey.ToString();
                supply[r] = supply.GetValueOrDefault(r) + 1;
            }
        }

        var races = demandM.Keys.Union(demandF.Keys).Union(supply.Keys).OrderBy(x => x).ToList();
        int totM = 0, totF = 0, coveredF = 0, skipped = 0, sup = 0;
        var rows = new List<object>();
        Console.WriteLine($"Coverage — category '{category}'  (sources: {(sources.Count == 0 ? "(none)" : string.Join(", ", sources.Select(Path.GetFileName)))})");
        Console.WriteLine($"{"Race",-14}{"M tgt",7}{"F tgt",7}{"F faces",9}{"→ result",10}  status");
        Console.WriteLine(new string('-', 62));
        foreach (var r in races)
        {
            int m = demandM.GetValueOrDefault(r), f = demandF.GetValueOrDefault(r), s = supply.GetValueOrDefault(r);
            totM += m; totF += f; sup += s;
            bool covered = s > 0;
            if (covered) coveredF += (m + f); else skipped += m + f;
            string status = s == 0 ? "SKIP (no female faces)" : (s < (m + f) ? $"ok (faces cycle: {s} → {m + f})" : "ok");
            Console.WriteLine($"{r,-14}{m,7}{f,7}{s,9}{(covered ? (m + f) : 0),10}  {status}");
            rows.Add(new { race = r, targetM = m, targetF = f, femaleFaces = s, covered, resultFemale = covered ? m + f : 0, skipped = covered ? 0 : m + f });
        }
        Console.WriteLine(new string('-', 62));
        Console.WriteLine($"{"TOTAL",-14}{totM,7}{totF,7}{sup,9}{coveredF,10}");
        Console.WriteLine();
        Console.WriteLine($"Targets: {totM + totF} own-traits (M {totM} / F {totF}).  Female-face pool: {sup}.");
        Console.WriteLine($"All-female result with these sources: {coveredF} F.  Skipped (no race-matched female face): {skipped}.");

        if (outJson is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outJson))!);
            var payload = new { category, sources = sources.Select(Path.GetFileName), totals = new { targetsM = totM, targetsF = totF, femaleFacePool = sup, resultFemale = coveredF, skipped }, races = rows };
            File.WriteAllText(outJson, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"\nWrote coverage JSON: {outJson}");
        }
        return 0;
    }
}
