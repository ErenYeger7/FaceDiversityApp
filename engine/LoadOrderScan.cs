using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Cache;

// Load-order-wide NPC scanning for the "all loaded males" category. Unlike the Enc* categories (which
// filter Skyrim.esm by EditorID prefix), this resolves the WHOLE active load order's winning overrides.
//
// MO2 has no unified Data folder — each plugin lives in its own mods/<mod>/ folder — so we resolve every
// active plugin to its real path and hand Mutagen explicit ModPaths (never the game path). This sidesteps
// the xEdit/registry MO2 hazard entirely: Mutagen reads exactly the files we point it at, in order.
static class LoadOrderScan
{
    // Base masters are always active even if plugins.txt doesn't star them.
    static readonly string[] BaseMasters =
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    // The VANILLA base masters (Skyrim + the three DLC + Update) that physically sit next to the game
    // esm. Prefix categories scan these as a mini load order so DLC-defined generic groups (Solstheim
    // reavers/cultists in Dragonborn.esm, Dawnguard hunters/Volkihar vampires in Dawnguard.esm) are
    // reachable — not just Skyrim.esm. Missing masters (e.g. a base-game-only install) are skipped.
    public static List<string> BaseMasterPaths(string gameEsmPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(gameEsmPath)) ?? "";
        var paths = new List<string>();
        foreach (var m in BaseMasters)
        {
            var p = Path.Combine(dir, m);
            if (File.Exists(p)) paths.Add(p);
        }
        return paths;
    }

    // Resolve the profile's active plugins to absolute paths, in true load order.
    //   order  = loadorder.txt (authoritative plugin order)
    //   active = plugins.txt lines starting with '*' (+ base masters)
    //   path   = mods/<mod>/ in MO2 priority (modlist.txt, top wins), then the stock Data folder
    public static List<string> ResolveActivePaths(string profileDir, string modsRoot, string stockDataDir)
    {
        var loFile = Path.Combine(profileDir, "loadorder.txt");
        var pxFile = Path.Combine(profileDir, "plugins.txt");
        var mlFile = Path.Combine(profileDir, "modlist.txt");
        if (!File.Exists(loFile)) return new();

        var order = File.ReadAllLines(loFile).Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] != '#').ToList();

        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(pxFile))
            foreach (var l in File.ReadAllLines(pxFile))
            {
                var s = l.Trim();
                if (s.StartsWith("*")) active.Add(s[1..]);
            }
        foreach (var m in BaseMasters) active.Add(m);

        // filename -> path (highest-priority mod wins, then stock Data)
        var pathOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Consider(string dir)
        {
            if (!Directory.Exists(dir)) return;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.es*", SearchOption.TopDirectoryOnly); }
            catch { return; }
            foreach (var f in files)
            {
                var n = Path.GetFileName(f);
                if (!(n.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)
                   || n.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)
                   || n.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))) continue;
                if (!pathOf.ContainsKey(n)) pathOf[n] = f;
            }
        }
        if (File.Exists(mlFile))
            foreach (var raw in File.ReadAllLines(mlFile))
            {
                var s = raw.Trim();
                if (!s.StartsWith("+")) continue;          // enabled mods only, top = highest priority
                Consider(Path.Combine(modsRoot, s[1..]));
            }
        Consider(stockDataDir);                            // base game / CC content fallback

        var paths = new List<string>();
        foreach (var name in order)
            if (active.Contains(name) && pathOf.TryGetValue(name, out var p))
                paths.Add(p);
        return paths;
    }

    // Enabled MOD folders in MO2 priority (top of modlist.txt = highest). Includes asset-only mods that
    // ship no plugin (e.g. a shared brow/hair texture pack) — exactly the ones the plugin load order
    // misses but which faces depend on for textures.
    public static List<(string name, string folder)> EnabledMods(string profileDir, string modsRoot)
    {
        var res = new List<(string, string)>();
        var ml = Path.Combine(profileDir, "modlist.txt");
        if (!File.Exists(ml)) return res;
        foreach (var raw in File.ReadAllLines(ml))
        {
            var sline = raw.Trim();
            if (!sline.StartsWith("+")) continue;                 // enabled only; '-' disabled, '*'/others skipped
            var name = sline[1..];
            if (name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            var folder = Path.Combine(modsRoot, name);
            if (Directory.Exists(folder)) res.Add((name, folder));
        }
        return res;
    }

    // DISABLED mod folders (modlist.txt lines starting with '-'). Used by the audit to tell whether a
    // "missing" texture is actually installed but just turned off (enable it) vs genuinely absent.
    public static List<(string name, string folder)> DisabledMods(string profileDir, string modsRoot)
    {
        var res = new List<(string, string)>();
        var ml = Path.Combine(profileDir, "modlist.txt");
        if (!File.Exists(ml)) return res;
        foreach (var raw in File.ReadAllLines(ml))
        {
            var sline = raw.Trim();
            if (!sline.StartsWith("-")) continue;                 // disabled only
            var name = sline[1..];
            if (name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            var folder = Path.Combine(modsRoot, name);
            if (Directory.Exists(folder)) res.Add((name, folder));
        }
        return res;
    }

    // Build a Mutagen load order from explicit plugin paths (already in load order). Caller MUST dispose
    // (each listing memory-maps its file). Plugins that fail to load are skipped.
    public static LoadOrder<IModListingGetter<ISkyrimModGetter>> Build(IEnumerable<string> orderedPaths)
    {
        var listings = new List<IModListingGetter<ISkyrimModGetter>>();
        foreach (var p in orderedPaths)
        {
            try
            {
                var mk = ModKey.FromFileName(Path.GetFileName(p));
                var mod = SkyrimMod.CreateFromBinaryOverlay(new ModPath(mk, p), GameCfg.Release);
                listings.Add(new ModListing<ISkyrimModGetter>(mod, enabled: true));
            }
            catch { /* unreadable/duplicate plugin — skip, matches game tolerance */ }
        }
        return new LoadOrder<IModListingGetter<ISkyrimModGetter>>(listings);
    }

    // Winning "named male" NPC overrides across the load order: male, Unique-flagged, own-traits (its own
    // face, not template-inherited), a non-empty display name, an adult (non-child) race, and a HUMANOID
    // race — one carrying the ActorTypeNPC keyword. That keyword is the standard humanoid-NPC classifier:
    // it cleanly keeps humans, beast races, and Dremora/Elder/vampire variants (all ActorTypeNPC) while
    // excluding every creature (spider/skeever/dog/dragon/horse/hagraven/werewolf/... = ActorTypeCreature)
    // that would otherwise pollute the list and only ever skip for lack of a matching face.
    public static List<INpcGetter> UniqueNamedMales(
        LoadOrder<IModListingGetter<ISkyrimModGetter>> lo, Func<FormKey, string> raceOf)
    {
        // Resolve the ActorTypeNPC keyword's FormKey from the load order (no hardcoded FormID).
        FormKey npcKw = default; bool haveKw = false;
        foreach (var kw in lo.PriorityOrder.Keyword().WinningOverrides())
            if (kw.EditorID == "ActorTypeNPC") { npcKw = kw.FormKey; haveKw = true; break; }

        var cache = lo.ToImmutableLinkCache();
        var isNpcRace = new Dictionary<FormKey, bool>();           // memoise per race
        bool HumanoidRace(FormKey rk)
        {
            if (!haveKw) return true;                              // keyword missing (unexpected) -> don't over-filter
            if (isNpcRace.TryGetValue(rk, out var v)) return v;
            v = cache.TryResolve<IRaceGetter>(rk, out var r) && r.Keywords is not null
                && r.Keywords.Any(k => k.FormKey == npcKw);
            return isNpcRace[rk] = v;
        }

        var res = new List<INpcGetter>();
        foreach (var n in lo.PriorityOrder.Npc().WinningOverrides())
        {
            // The PLAYER's base record (Skyrim.esm 0x7 "Player"/"Prisoner") is a unique, named, own-traits
            // male and passed every filter below — a build then flipped the player female, pinned a fixed
            // face on them, tagged them with SexPlague and renamed them "Prisonera". Likewise the 100
            // character-generation PRESET NPCs (NordMalePreset01…, ACBS "Is CharGen Face Preset") are
            // Unique+named, so they were feminized too — which is why RaceMenu's male presets "did nothing".
            // Neither is ever a target.
            if (IsPlayerRecord(n) || IsChargenPreset(n)) continue;
            if (n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female)) continue;
            if (!n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique)) continue;
            if (n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !n.Template.IsNull) continue;
            if (string.IsNullOrWhiteSpace(n.Name?.String)) continue;
            if (raceOf(n.Race.FormKey).Contains("Child", StringComparison.OrdinalIgnoreCase)) continue;
            if (!HumanoidRace(n.Race.FormKey)) continue;           // creatures -> out
            res.Add(n);
        }
        return res;
    }

    // ---- per-MOD category: every own-traits adult humanoid NPC a plugin DEFINES (male and female, unique or
    // not — a civil-war mod's leveled-list soldiers are deliberately not unique, so "all named males" never
    // sees them), as the load order's winning overrides. Plus a summary of the mod's TEMPLATED NPCs: those
    // have no face of their own; the face seen in game belongs to whatever their template chain resolves to
    // (a single NPC, or the leaves of a leveled character list) — classified by where those leaves live.
    public record TemplateSummary(int Templated, int LeavesInMod, int LeavesVanilla, int LeavesOtherMods, int Unresolved, List<string> OtherMods);

    // For a BASE MASTER target, NPCs that already have a dedicated path are set aside: every EditorID matching
    // a prefix category (EncBandit, EncForsworn, ...) and the unique named males (the all_males scan) — so
    // "Skyrim.esm" means the vanilla population those categories do NOT cover (hold guards, city folk, ...).
    public class Excluded { public int ByCategoryCount; public int UniqueCount; }
    // the dedicated-path exclusions apply to a base-master target only
    public static bool IsBaseMaster(string plugin) => BaseMasters.Contains(plugin, StringComparer.OrdinalIgnoreCase);
    public static IEnumerable<string> CategoryPrefixes() =>
        Categories.All.Where(c => !string.IsNullOrWhiteSpace(c.TargetPrefix)).Select(c => c.TargetPrefix!);
    public static List<INpcGetter> ModNpcs(LoadOrder<IModListingGetter<ISkyrimModGetter>> lo, string modFile, Func<FormKey, string> raceOf,
                                           IEnumerable<string>? excludePrefixes = null, bool excludeUniqueNamedMales = false, Excluded? excluded = null)
    {
        var prefixes = excludePrefixes?.ToList() ?? new List<string>();
        int exCat = 0, exUnique = 0;
        FormKey npcKw = default; bool haveKw = false;
        foreach (var kw in lo.PriorityOrder.Keyword().WinningOverrides())
            if (kw.EditorID == "ActorTypeNPC") { npcKw = kw.FormKey; haveKw = true; break; }
        var cache = lo.ToImmutableLinkCache();
        var isNpcRace = new Dictionary<FormKey, bool>();
        bool HumanoidRace(FormKey rk)
        {
            if (!haveKw) return true;
            if (isNpcRace.TryGetValue(rk, out var v)) return v;
            v = cache.TryResolve<IRaceGetter>(rk, out var r) && r.Keywords is not null && r.Keywords.Any(k => k.FormKey == npcKw);
            return isNpcRace[rk] = v;
        }
        var res = new List<INpcGetter>();
        foreach (var n in lo.PriorityOrder.Npc().WinningOverrides())
        {
            if (!string.Equals(n.FormKey.ModKey.FileName, modFile, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsPlayerOrPreset(n)) continue;
            if (n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !n.Template.IsNull) continue;
            if (raceOf(n.Race.FormKey).Contains("Child", StringComparison.OrdinalIgnoreCase)) continue;
            if (!HumanoidRace(n.Race.FormKey)) continue;
            if (prefixes.Count > 0 && prefixes.Any(p => Categories.MatchesPrefix(n.EditorID ?? "", p))) { exCat++; continue; }
            // named characters belong to the all_males path whatever their CURRENT sex (an installed all-males
            // build already made the winning override female — a base-master run must not reface them again)
            if (excludeUniqueNamedMales && n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique)
                && !string.IsNullOrWhiteSpace(n.Name?.String)) { exUnique++; continue; }
            res.Add(n);
        }
        if (excluded is not null) { excluded.ByCategoryCount = exCat; excluded.UniqueCount = exUnique; }
        return res;
    }

    public static TemplateSummary TemplatesOf(LoadOrder<IModListingGetter<ISkyrimModGetter>> lo, string modFile)
    {
        var cache = lo.ToImmutableLinkCache();
        var baseSet = new HashSet<string>(BaseMasters, StringComparer.OrdinalIgnoreCase);
        int templated = 0, inMod = 0, vanilla = 0, other = 0, unresolved = 0;
        var otherMods = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        // leaves of a template chain: an NPC that owns its traits, or every NPC entry of a leveled list (one
        // level of nesting, which is what vanilla uses)
        void Classify(FormKey leaf)
        {
            var mk = leaf.ModKey.FileName.ToString();
            if (string.Equals(mk, modFile, StringComparison.OrdinalIgnoreCase)) inMod++;
            else if (baseSet.Contains(mk)) vanilla++;
            else { other++; otherMods.Add(mk); }
        }
        foreach (var n in lo.PriorityOrder.Npc().WinningOverrides())
        {
            if (!string.Equals(n.FormKey.ModKey.FileName, modFile, StringComparison.OrdinalIgnoreCase)) continue;
            if (!(n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !n.Template.IsNull)) continue;
            templated++;
            var t = n.Template.FormKey;
            if (cache.TryResolve<INpcGetter>(t, out var tn))
            {
                // follow a chain of templated NPCs to the one that owns its traits (bounded)
                int guard = 0;
                while (tn.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !tn.Template.IsNull && guard++ < 8
                       && cache.TryResolve<INpcGetter>(tn.Template.FormKey, out var next)) tn = next;
                Classify(tn.FormKey);
            }
            else if (cache.TryResolve<ILeveledNpcGetter>(t, out var ll))
            {
                var seen = false;
                foreach (var e in ll.Entries ?? new List<ILeveledNpcEntryGetter>())
                    if (e.Data is not null && !e.Data.Reference.IsNull) { Classify(e.Data.Reference.FormKey); seen = true; }
                if (!seen) unresolved++;
            }
            else unresolved++;
        }
        return new TemplateSummary(templated, inMod, vanilla, other, unresolved, otherMods.ToList());
    }

    // Skyrim.esm 000007 is the player's own NPC record. Checked by FormKey (not EditorID) so an override
    // that renames it still matches.
    public static readonly FormKey PlayerFormKey = new(ModKey.FromNameAndExtension("Skyrim.esm"), 0x7);
    public static bool IsPlayerRecord(INpcGetter n) => n.FormKey == PlayerFormKey;
    // Character-creation face presets carry the ACBS "Is CharGen Face Preset" flag; a refaced preset shows
    // up in RaceMenu's preset slider as the transplanted face.
    public static bool IsChargenPreset(INpcGetter n) => n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.IsCharGenFacePreset);
    public static bool IsPlayerOrPreset(INpcGetter n) => IsPlayerRecord(n) || IsChargenPreset(n);

    public static void DisposeLoadOrder(LoadOrder<IModListingGetter<ISkyrimModGetter>> lo)
    {
        foreach (var l in lo)
            if (l.Mod is IDisposable d) { try { d.Dispose(); } catch { } }
    }
}
