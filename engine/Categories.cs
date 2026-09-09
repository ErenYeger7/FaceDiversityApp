using YamlDotNet.Serialization;

// Reads config/categories.yaml so target prefixes / labels aren't hardcoded. Falls back to the
// verified bandit=EncBandit mapping if the file is absent (keeps the CLI working with no config).
static class Categories
{
    public record Cat(string Key, string Label, bool Verified, string? TargetPrefix, string[] KnownReplacers, string? Scan = null);

    static List<Cat>? _cats;

    public static void Load(string? yamlPath)
    {
        _cats = null;
        if (yamlPath is null || !File.Exists(yamlPath)) return;
        try
        {
            var root = new Deserializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(yamlPath));
            if (!root.TryGetValue("categories", out var cv) || cv is not Dictionary<object, object> cats) return;
            var list = new List<Cat>();
            foreach (var kv in cats)
            {
                var key = kv.Key.ToString()!;
                if (kv.Value is not Dictionary<object, object> body) continue;
                string? S(string k) => body.TryGetValue(k, out var v) ? v?.ToString() : null;
                bool verified = string.Equals(S("verified"), "true", StringComparison.OrdinalIgnoreCase);
                var replacers = body.TryGetValue("known_replacers", out var r) && r is List<object> rl
                    ? rl.Select(x => x.ToString()!).ToArray() : Array.Empty<string>();
                list.Add(new Cat(key, S("label") ?? key, verified, S("target_editorid_prefix"), replacers, S("scan")));
            }
            _cats = list;
        }
        catch { _cats = null; }
    }

    public static IReadOnlyList<Cat> All => _cats ?? Fallback;

    static readonly List<Cat> Fallback = new()
        { new Cat("bandit", "Bandits", true, "EncBandit", new[] { "rsbotLiteralWhoBandits.esp" }) };

    // A "scan" category (e.g. all_males) resolves targets from the whole load order rather than an
    // EditorID prefix — it has no target_editorid_prefix and must not go through the prefix path.
    public static bool IsScan(string category) =>
        All.FirstOrDefault(x => string.Equals(x.Key, category, StringComparison.OrdinalIgnoreCase))?.Scan is { Length: > 0 };

    // The per-MOD category (`scan: mod`): targets are the NPCs one chosen plugin defines (own traits, both
    // sexes, unique or not) + Boost from whatever leveled lists reference them. Needs the load order (so it
    // is also a scan) plus a --target-mod.
    public static bool IsMod(string category) =>
        string.Equals(All.FirstOrDefault(x => string.Equals(x.Key, category, StringComparison.OrdinalIgnoreCase))?.Scan, "mod", StringComparison.OrdinalIgnoreCase);

    // A category's target_editorid_prefix may be a single prefix ("EncBandit") or a '|'-separated set
    // ("EncSoldier|EncSiege") when one logical group spans several vanilla EditorID prefixes. An EditorID
    // matches if it StartsWith ANY of them. Single-prefix specs (no '|') behave exactly as before.
    public static bool MatchesPrefix(string editorId, string prefixSpec)
    {
        foreach (var p in prefixSpec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (editorId.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string TargetPrefix(string category)
    {
        var c = All.FirstOrDefault(x => string.Equals(x.Key, category, StringComparison.OrdinalIgnoreCase));
        if (c?.TargetPrefix is { Length: > 0 } p) return p;
        if (string.Equals(category, "bandit", StringComparison.OrdinalIgnoreCase)) return "EncBandit";
        throw new InvalidOperationException($"category '{category}' has no verified target_editorid_prefix");
    }
}
