using YamlDotNet.Serialization;

// Optional SexPlague overlay: assigns each feminized male a tier faction (weighted), plus a seed faction
// and a controller ability, emitted as a SkyPatcher NPC config (runtime, no master dependency).
static class SexPlague
{
    public record Tier(string Faction, string Label, int Percent);
    public record Config(string Plugin, string SeedFaction, string ControllerSpell, List<Tier> Tiers);

    public static Config? Load(string? yamlPath)
    {
        if (yamlPath is null || !File.Exists(yamlPath)) return null;
        try
        {
            var root = new Deserializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(yamlPath));
            if (!root.TryGetValue("sexplague", out var sv) || sv is not Dictionary<object, object> sp) return null;
            string S(string k, string def) => sp.TryGetValue(k, out var v) ? v?.ToString() ?? def : def;
            var tiers = new List<Tier>();
            if (sp.TryGetValue("tiers", out var tv) && tv is List<object> tl)
                foreach (var t in tl)
                    if (t is Dictionary<object, object> td)
                        tiers.Add(new Tier(
                            td.TryGetValue("faction", out var f) ? f.ToString()! : "0x800",
                            td.TryGetValue("label", out var l) ? l.ToString()! : "tier",
                            td.TryGetValue("percent", out var p) && int.TryParse(p.ToString(), out var pi) ? pi : 0));
            if (tiers.Count == 0) return null;
            return new Config(S("plugin", "SexPlagueFactions.esp"), S("seed_faction", "0x803"),
                              S("controller_spell", "0x80A"), tiers);
        }
        catch { return null; }
    }

    // Split n items across tiers by percentage (largest-remainder, so the counts sum to exactly n).
    public static int[] Distribute(int n, IReadOnlyList<int> pct)
    {
        int k = pct.Count; var res = new int[k];
        int total = pct.Sum(); if (total <= 0) total = 1;
        var frac = new double[k]; int sum = 0;
        for (int i = 0; i < k; i++) { double q = (double)n * pct[i] / total; res[i] = (int)Math.Floor(q); frac[i] = q - res[i]; sum += res[i]; }
        foreach (var i in Enumerable.Range(0, k).OrderByDescending(i => frac[i]).Take(Math.Max(0, n - sum))) res[i]++;
        return res;
    }
}
