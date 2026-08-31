using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using YamlDotNet.Serialization;

// Feminine display names for the all_males category. A best-effort auto-feminizer produces an editable
// YAML (keyed by the NPC's ORIGINAL full name); the user curates it. At generate time the chosen name is
// written as a SkyPatcher `fullName=` op on the same NPC config line (runtime, no ESP edit).
static class FeminineNames
{
    // Feminize a full name: transform the FIRST token (given name), keep the rest (surname/epithet).
    //   Ulfric Stormcloak -> Ulfrica Stormcloak   |   General Tullius -> General Tullia
    // Names we can't confidently gender (apostrophe/compound beast names, already-vowel endings) are
    // returned unchanged so they show up in the YAML as original->original for manual editing.
    // Honorifics that precede the actual given name — feminize the name AFTER the title, not the title
    // ("General Tullius" -> "General Tullia", not "Generala Tullius").
    static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        "General","Captain","Commander","Legate","Jarl","Thane","Master","Brother","Companion",
        "Housecarl","Vigilant","Sergeant","Lieutenant","Colonel","King","Emperor","Prince","Lord",
        "Count","Duke","Chief","Elder","Priest","Keeper","Warden","Aspirant","Initiate","High"
    };

    public static string Feminize(string full)
    {
        if (string.IsNullOrWhiteSpace(full)) return full;
        var toks = full.Split(' ');
        // pick the token to feminize: skip a leading title if a real name token follows
        int idx = (toks.Length > 1 && Titles.Contains(toks[0])) ? 1 : 0;
        toks[idx] = FeminizeToken(toks[idx]);
        return string.Join(' ', toks);
    }

    static readonly char[] Vowels = { 'a', 'e', 'i', 'o', 'u', 'A', 'E', 'I', 'O', 'U' };
    static string FeminizeToken(string t)
    {
        if (t.Length < 2) return t;
        if (t.Contains('\'') || t.Contains('-')) return t;         // Khajiit/Argonian compound — leave for editing
        // Latin -us / -ius endings -> -a / -ia  (Tullius->Tullia, Cassius->Cassia, Marcus->Marca)
        if (t.EndsWith("us", StringComparison.Ordinal)) return t[..^2] + "a";
        if (t.EndsWith("o", StringComparison.Ordinal)) return t[..^1] + "a"; // Marcurio->Marcuria
        char last = t[^1];
        if (Vowels.Contains(last)) return t;                        // already vowel-ending (-a/-i/-e) — leave
        return t + "a";                                             // consonant -> add 'a' (Ulfric->Ulfrica)
    }

    // Scan the load order for the same target set the generator uses, and write original->feminine YAML.
    // femnames --game <esm> --loadorder <paths-file> --out <yaml>
    public static int Run(string[] args)
    {
        string? game = null, loFile = null, outYaml = null;
        for (int i = 1; i < args.Length; i++)
            switch (args[i])
            {
                case "--game": game = args[++i]; break;
                case "--loadorder": loFile = args[++i]; break;
                case "--out": outYaml = args[++i]; break;
            }
        if (game is null || loFile is null || outYaml is null || !File.Exists(loFile))
        { Console.Error.WriteLine("femnames --game <esm> --loadorder <paths-file> --out <yaml>"); return 1; }

        using var esm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(game), GameCfg.Release);
        var raceName = esm.Races.ToDictionary(r => r.FormKey, r => r.EditorID ?? "");
        string RaceOf(FormKey fk) => raceName.TryGetValue(fk, out var s) && s != "" ? s : fk.ToString();

        var paths = File.ReadAllLines(loFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var lo = LoadOrderScan.Build(paths);
        int changed = 0, left = 0;
        var lines = new List<string>();
        try
        {
            // stable, de-duplicated by name (unique named males share few names); alphabetical for editing
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in LoadOrderScan.UniqueNamedMales(lo, RaceOf)
                         .Select(n => n.Name!.String!).Where(s => !string.IsNullOrWhiteSpace(s))
                         .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var fem = Feminize(n);
                if (fem == n) left++; else changed++;
                lines.Add($"  {Q(n)}: {Q(fem)}");
            }
        }
        finally { LoadOrderScan.DisposeLoadOrder(lo); }

        var header =
            "# feminine_names.yaml — feminine display names for the \"Apply feminine names\" option (all_males).\n" +
            "# Keyed by the NPC's ORIGINAL full name; value = the feminine name to display. Edit freely.\n" +
            "# A value equal to the original (or a missing key) leaves that NPC's name unchanged.\n" +
            "# Auto-generated best guesses (first name feminized, surname/epithet kept) — curate as you like.\n" +
            "# Regenerate:  facediv femnames --game <esm> --loadorder <paths-file> --out <this file>\n" +
            "feminine_names:\n";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outYaml))!);
        File.WriteAllText(outYaml, header + string.Join("\n", lines) + "\n");
        Console.WriteLine($"Wrote {outYaml}: {lines.Count} names ({changed} feminized, {left} left as-is for editing).");
        return 0;
    }

    // Load the name map for the engine. Returns original-name -> feminine-name (only entries that differ
    // and are non-empty; identical/blank values mean "leave unchanged" and are dropped).
    public static Dictionary<string, string> Load(string? yamlPath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (yamlPath is null || !File.Exists(yamlPath)) return map;
        try
        {
            var root = new Deserializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(yamlPath));
            if (root.TryGetValue("feminine_names", out var v) && v is Dictionary<object, object> d)
                foreach (var kv in d)
                {
                    var k = kv.Key?.ToString(); var val = kv.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(val) && k != val) map[k!] = val!;
                }
        }
        catch { }
        return map;
    }

    // quote a YAML scalar (names contain spaces, apostrophes, and occasional punctuation)
    static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
