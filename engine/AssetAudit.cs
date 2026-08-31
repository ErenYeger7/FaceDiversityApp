using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Audits a GENERATED mod's face assets against the real load order: every texture a FaceGen/mesh NIF
// references is resolved (loose AND BSA) across the mod itself, every enabled mod, and the stock game.
// Reports textures that resolve NOWHERE (the purple/missing brows the user saw) with the offending face,
// and the hidden texture-dependency mods a face needs that are NOT plugin masters (so invisible in the
// README's master list). Read-only.
static class AssetAudit
{
    // Require a real "textures\" anchor (optionally "Data\Textures\") so we don't match binary noise; the
    // old optional-prefix regex extracted junk fragments and mangled "Data\Textures\" paths.
    static readonly Regex DdsRx = new(@"(?:data[\\/])?textures[\\/][\w \-\\/().]+?\.dds",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static IEnumerable<string> DdsPaths(byte[] nif)
    {
        foreach (Match m in DdsRx.Matches(Encoding.Latin1.GetString(nif)))
        {
            var p = m.Value.Replace('/', '\\').TrimStart('\\');
            if (p.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) p = p[5..];   // strip game-root prefix
            if (p.Contains("facegendata\\", StringComparison.OrdinalIgnoreCase)) continue; // tint/geom re-keyed, not a dep
            if (p.IndexOf('\\', "textures\\".Length) < 0) continue;                        // need a real subfolder (drops noise)
            yield return p;
        }
    }

    // Face = "plugin|formid" (the replaced NPC, parsed from the NIF path). InDisabled = a DISABLED mod that
    // actually contains this texture (→ just enable it), or null. Source = the pack / NPC-face folder the
    // path lives under (the thing to install if it's genuinely absent).
    public record Finding(string Texture, string? Face, string? InDisabled = null, string Source = "");
    public record Result(int nifs, int distinct, int inMod, int deps, int missing, int missingSecondary,
                         List<Finding> missingList, Dictionary<string, int> byMod,
                         bool stockOk, string stockFolder, int modFolders, string profileDir,
                         Dictionary<string, int> inDisabled, Dictionary<string, int> missingBySource);

    // The texture path identifies its PROVIDER far better than the replaced NPC's plugin. Group key:
    // the distinctive folder — `textures\<X>\…` -> X (ARIS, Pandorable, sghairs…); for the vanilla-style
    // `textures\actors\character\<Y>\…` -> Y (the per-NPC / overhaul folder like "aela textures", "Serana").
    static string SourceKey(string tex)
    {
        var s = tex.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (s.Length < 2) return tex;
        if (s.Length > 3 && s[0].Equals("textures", StringComparison.OrdinalIgnoreCase)
            && s[1].Equals("actors", StringComparison.OrdinalIgnoreCase)
            && s[2].Equals("character", StringComparison.OrdinalIgnoreCase))
            return "actors\\character\\" + s[3];
        return s[1];
    }

    // Canary: a texture that only exists in the base-game BSAs. If it doesn't resolve, the base-game
    // Data folder is wrong/missing — vanilla textures will flood the report as false "missing".
    const string VanillaCanary = @"textures\actors\character\male\malehead.dds";

    // Secondary map suffixes (normal/specular/subsurface/glow/…). A missing DIFFUSE renders purple; a
    // missing secondary map is usually harmless (the game falls back), so we don't count those as breakage.
    static readonly HashSet<string> MapSuffix = new(StringComparer.OrdinalIgnoreCase)
        { "n", "s", "sk", "msn", "em", "e", "sss", "m", "p", "d", "g", "b", "h", "rgb" };
    static bool IsSecondaryMap(string tex)
    {
        var name = Path.GetFileNameWithoutExtension(tex);
        int u = name.LastIndexOf('_');
        return u > 0 && MapSuffix.Contains(name[(u + 1)..]);
    }

    // Core audit. `dirs` is the full search order (audited mod first, then enabled mods, then stock).
    // `disabledDirs` (optional) are installed-but-disabled mods — searched only to explain missing textures.
    public static Result Audit(string modFolder, List<(string name, string folder)> dirs, string profileDir = "",
                               List<(string name, string folder)>? disabledDirs = null)
    {
        var resolver = new LoadOrderAssets(dirs);
        var selfName = dirs.Count > 0 ? dirs[0].name : "(this mod)";
        var nifs = Directory.Exists(modFolder)
            ? Directory.EnumerateFiles(modFolder, "*.nif", SearchOption.AllDirectories).ToList()
            : new List<string>();

        // distinct texture -> a sample referencing face (plugin|formid parsed from the facegeom path)
        var refToFace = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var nif in nifs)
        {
            byte[] data; try { data = File.ReadAllBytes(nif); } catch { continue; }
            var face = FaceOf(nif);
            foreach (var tex in DdsPaths(data))
                if (!refToFace.ContainsKey(tex)) refToFace[tex] = face;
        }

        int inMod = 0, deps = 0, missingSecondary = 0;
        var missing = new List<Finding>(); var byMod = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tex, face) in refToFace)
        {
            var prov = resolver.ResolveName(tex);
            if (prov is null) { if (IsSecondaryMap(tex)) missingSecondary++; else missing.Add(new Finding(tex, face)); }
            else if (string.Equals(prov, selfName, StringComparison.OrdinalIgnoreCase)) inMod++;
            else { deps++; byMod[prov] = byMod.GetValueOrDefault(prov) + 1; }
        }
        bool stockOk = resolver.ResolveName(VanillaCanary) != null;
        var stockFolder = dirs.LastOrDefault(d => d.name == "(stock game)").folder ?? "";
        int modFolders = dirs.Count(d => d.name != "(this mod)" && d.name != "(stock game)");

        // Explain each missing texture PER ENTRY: is it actually present in a DISABLED mod (just enable it)?
        // and what pack/NPC-face folder does its path belong to (what to install if truly absent)? We keep
        // the aggregate dictionaries for the summary AND fold the same facts back onto every Finding.
        var dr = (disabledDirs is { Count: > 0 }) ? new LoadOrderAssets(disabledDirs) : null;
        var inDisabled = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var enriched = new List<Finding>(missing.Count);
        foreach (var f in missing)
        {
            var disab = dr?.ResolveName(f.Texture);
            if (disab != null) inDisabled[disab] = inDisabled.GetValueOrDefault(disab) + 1;
            var src = SourceKey(f.Texture);
            bySource[src] = bySource.GetValueOrDefault(src) + 1;
            enriched.Add(f with { InDisabled = disab, Source = src });
        }
        return new Result(nifs.Count, refToFace.Count, inMod, deps, missing.Count, missingSecondary,
            enriched.OrderBy(f => f.Texture).ToList(),
            byMod.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value),
            stockOk, stockFolder, modFolders, profileDir,
            inDisabled.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value),
            bySource.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value));
    }

    // facegeom NIF path -> "plugin|formid" (…/facegeom/<Plugin.esp>/00xxxxxx.nif)
    static string? FaceOf(string nifPath)
    {
        var parts = nifPath.Replace('/', '\\').Split('\\');
        int i = Array.FindLastIndex(parts, p => p.Equals("facegeom", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 2 >= parts.Length) return null;
        var id = Path.GetFileNameWithoutExtension(parts[i + 2]);
        if (id.Length == 8) id = id[2..];                         // 00xxxxxx -> xxxxxx
        return $"{parts[i + 1]}|{id.TrimStart('0').PadLeft(6, '0')}";
    }

    // Build the search order from resolved paths and audit (used by the server + CLI).
    public static Result AuditWith(string modFolder, string modsRoot, string profilesDir, string profile, string stock)
    {
        var profileDir = Path.Combine(profilesDir, profile ?? "");
        var dirs = new List<(string name, string folder)> { ("(this mod)", modFolder) };
        dirs.AddRange(LoadOrderScan.EnabledMods(profileDir, modsRoot));
        if (Directory.Exists(stock)) dirs.Add(("(stock game)", stock));
        return Audit(modFolder, dirs, profileDir, LoadOrderScan.DisabledMods(profileDir, modsRoot));
    }

    // audit-assets --mod <generated mod folder> [--mods <dir>] [--profiles <dir>] [--profile <name>] [--stock <dir>]
    public static int Run(string[] args)
    {
        string? mod = null, mods = null, profiles = null, profile = null, stock = null; bool json = false;
        for (int i = 1; i < args.Length; i++)
            switch (args[i])
            {
                case "--mod": mod = args[++i]; break;
                case "--mods": mods = args[++i]; break;
                case "--profiles": profiles = args[++i]; break;
                case "--profile": profile = args[++i]; break;
                case "--stock": stock = args[++i]; break;
                case "--json": json = true; break;
                case "--game-version": i++; break;
            }
        var s = Settings.Current;
        mod ??= "";
        mods ??= s.Mods;
        profiles ??= (!string.IsNullOrWhiteSpace(s.Profiles) ? s.Profiles
                      : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(mods.TrimEnd('/', '\\'))) ?? mods, "profiles"));
        profile ??= s.Profile;
        stock ??= (!string.IsNullOrWhiteSpace(s.Game) ? Path.GetDirectoryName(Path.GetFullPath(s.Game)) : null) ?? "";
        if (string.IsNullOrWhiteSpace(mod) || !Directory.Exists(mod))
        { Console.Error.WriteLine("audit-assets --mod <generated mod folder> [--mods --profiles --profile --stock]"); return 1; }

        var r = AuditWith(mod, mods, profiles, profile, stock);
        if (json) { Console.WriteLine(JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true })); return r.missing > 0 ? 2 : 0; }

        Console.WriteLine($"Asset audit of {Path.GetFileName(mod)}:");
        Console.WriteLine($"  searched {r.modFolders} enabled mod folders (from {r.profileDir}\\modlist.txt)" +
                          (r.modFolders == 0 ? "  ⚠ ZERO — check the mods/profile path!" : ""));
        if (!r.stockOk)
            Console.WriteLine($"  ⚠ BASE-GAME TEXTURES NOT FOUND at '{r.stockFolder}' — vanilla textures will show as false 'missing'.\n" +
                              $"    Point the base-game Data folder at the install that has Skyrim - Textures*.bsa (e.g. Steam\\...\\Skyrim VR\\Data).");
        Console.WriteLine($"  {r.nifs} NIFs, {r.distinct} distinct textures referenced.");
        Console.WriteLine($"  baked into the mod:            {r.inMod}");
        Console.WriteLine($"  supplied by other mods:        {r.deps}");
        Console.WriteLine($"  MISSING diffuse (render PURPLE): {r.missing}");
        Console.WriteLine($"  missing secondary maps (usually harmless): {r.missingSecondary}");
        if (r.byMod.Count > 0)
        {
            Console.WriteLine("\nTexture-dependency mods (KEEP THESE ENABLED — most are NOT plugin masters):");
            foreach (var kv in r.byMod) Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
        }
        if (r.inDisabled.Count > 0)
        {
            Console.WriteLine("\n✔ FIXABLE — these DISABLED mods contain missing textures; ENABLE them:");
            foreach (var kv in r.inDisabled) Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
        }
        if (r.missingBySource.Count > 0)
        {
            Console.WriteLine("\nMISSING textures grouped by their source folder (the pack/NPC that provides them):");
            foreach (var kv in r.missingBySource.Take(40)) Console.WriteLine($"  {kv.Value,5}  textures\\{kv.Key}\\…");
        }
        if (r.missing > 0)
        {
            Console.WriteLine($"\nMISSING textures ({r.missing}) — [replaced NPC] path (render purple):");
            foreach (var f in r.missingList.Take(40)) Console.WriteLine($"  [{f.Face}]  {f.Texture}");
            if (r.missing > 40) Console.WriteLine($"  … and {r.missing - 40} more (use Copy for the full list).");
        }
        else Console.WriteLine("\nAll referenced textures resolve on this load order. ✔");
        return r.missing > 0 ? 2 : 0;
    }
}
