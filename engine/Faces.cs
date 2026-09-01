using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using System.Text.Json;

// Shared face model for the GUI. Enumerates every female "face" a source offers (the pickable pool)
// with the SAME pool-race logic Generate uses, and computes the category's per-race target demand.
// Face identity = "<source-plugin-filename>#<FormKey>" — unique even when two sources override the
// same vanilla NPC (identical FormKey, different source).
static class Faces
{
    public record FaceInfo(
        string Id, string Source, string FormKey, string? EditorID,
        string PoolRace, string NpcRace, string? Voice, bool CustomRace, string Sex);

    // Race = the JOIN KEY, identical to FaceInfo.PoolRace (RaceOf: esm EditorID for vanilla, hex FormKey
    // for mod-added races) so demand rows line up with the harvested face pool. Name = pretty display
    // (mod-added races resolved to their EditorID via the full load order). Code = race FormKey (hex).
    public record RaceDemand(string Race, int TargetM, int TargetF, string Code = "", string Name = "");

    static bool Fem(INpcGetter n) => n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
    static bool OwnTraits(INpcGetter n) =>
        !(n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !n.Template.IsNull);

    public static string FaceId(string sourcePath, FormKey fk) => $"{Path.GetFileName(sourcePath)}#{fk}";

    // Every female NPC across the given sources, keyed by original-vanilla race where applicable.
    public static List<FaceInfo> Enumerate(string game, IEnumerable<string> sources)
    {
        using var esm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(game), GameCfg.Release);
        var raceName = esm.Races.ToDictionary(r => r.FormKey, r => r.EditorID ?? "");
        var esmNpcRace = esm.Npcs.ToDictionary(n => n.FormKey, n => n.Race.FormKey);
        string RaceOf(FormKey fk) => raceName.TryGetValue(fk, out var s) && s != "" ? s : fk.ToString();

        var list = new List<FaceInfo>();
        foreach (var sp in sources)
        {
            ISkyrimModDisposableGetter sm;
            try { sm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(sp), GameCfg.Release); }
            catch { continue; }
            using (sm)
                foreach (var n in sm.Npcs)
                {
                    bool custom = !Classify.BaseMasters.Contains(n.Race.FormKey.ModKey.FileName);
                    // pool by ORIGINAL vanilla race for overrides (see Generate), else the record's own race
                    var poolRace = Classify.BaseMasters.Contains(n.FormKey.ModKey.FileName)
                                   && esmNpcRace.TryGetValue(n.FormKey, out var vr)
                        ? RaceOf(vr) : RaceOf(n.Race.FormKey);
                    list.Add(new FaceInfo(
                        FaceId(sp, n.FormKey), Path.GetFileName(sp), n.FormKey.ToString(), n.EditorID,
                        poolRace, RaceOf(n.Race.FormKey), n.Voice.FormKey.IsNull ? null : n.Voice.FormKey.ToString(),
                        custom, Fem(n) ? "F" : "M"));
                }
        }
        return list;
    }

    // Per-race target demand for a category (own-traits leaves only, matching Generate's target set).
    public static List<RaceDemand> Demand(string game, string category)
    {
        // Scan the vanilla base masters (Skyrim + DLC) as a mini load order so DLC-defined generic groups
        // are reachable. Winning overrides = the record the game actually uses. Race names resolve from the
        // full cache (DLC races too). Disposed at the end (each master is memory-mapped).
        var lo = LoadOrderScan.Build(LoadOrderScan.BaseMasterPaths(game));
        try
        {
            var cache = lo.ToImmutableLinkCache();
            string RaceOf(FormKey fk) => cache.TryResolve<IRaceGetter>(fk, out var r) && !string.IsNullOrEmpty(r.EditorID) ? r.EditorID! : fk.ToString();
            var prefix = Categories.TargetPrefix(category);

            var m = new Dictionary<string, int>(); var f = new Dictionary<string, int>();
            var code = new Dictionary<string, string>();
            foreach (var n in lo.PriorityOrder.Npc().WinningOverrides().Where(n =>
                Categories.MatchesPrefix(n.EditorID ?? "", prefix) && OwnTraits(n)
                && !RaceOf(n.Race.FormKey).Contains("Child", StringComparison.OrdinalIgnoreCase)))
            {
                var r = RaceOf(n.Race.FormKey);
                var d = Fem(n) ? f : m; d[r] = d.GetValueOrDefault(r) + 1;
                code[r] = n.Race.FormKey.ToString();
            }
            return m.Keys.Union(f.Keys).OrderBy(x => x)
                .Select(r => new RaceDemand(r, m.GetValueOrDefault(r), f.GetValueOrDefault(r), code.GetValueOrDefault(r, ""), r)).ToList();
        }
        finally { LoadOrderScan.DisposeLoadOrder(lo); }
    }

    // Per-race target demand for the load-order "all_males" scan: every winning UNIQUE named male, keyed
    // by (esm) race name so it lines up with the harvested face pool. All targets are male (TargetM);
    // races with no matching harvested face simply skip at generate time (shown as 0 faces in coverage).
    public static List<RaceDemand> DemandLoadOrder(string game, IEnumerable<string> orderedPaths)
    {
        using var esm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(game), GameCfg.Release);
        var raceName = esm.Races.ToDictionary(r => r.FormKey, r => r.EditorID ?? "");
        string RaceOf(FormKey fk) => raceName.TryGetValue(fk, out var s) && s != "" ? s : fk.ToString();

        var lo = LoadOrderScan.Build(orderedPaths);
        try
        {
            // JOIN on RaceOf (esm EditorID / hex) so demand keys match the harvested face pool exactly.
            // NameOf is DISPLAY ONLY: resolve mod-added race EditorIDs from the FULL load order so the
            // coverage table shows text instead of a hex FormKey. Keying on NameOf here would silently
            // break the join for every mod-added race (name on this side, hex on the face side).
            var cache = lo.ToImmutableLinkCache();
            string NameOf(FormKey fk) => cache.TryResolve<IRaceGetter>(fk, out var r) && !string.IsNullOrEmpty(r.EditorID)
                ? r.EditorID! : RaceOf(fk);
            var m = new Dictionary<string, int>();
            var code = new Dictionary<string, string>(); var name = new Dictionary<string, string>();
            foreach (var n in LoadOrderScan.UniqueNamedMales(lo, RaceOf))
            {
                var key = RaceOf(n.Race.FormKey);            // JOIN KEY (matches FaceInfo.PoolRace)
                m[key] = m.GetValueOrDefault(key) + 1;
                code[key] = n.Race.FormKey.ToString();
                name[key] = NameOf(n.Race.FormKey);          // pretty display
            }
            return m.OrderByDescending(k => k.Value).ThenBy(k => k.Key)
                    .Select(kv => new RaceDemand(kv.Key, kv.Value, 0,
                        code.GetValueOrDefault(kv.Key, ""), name.GetValueOrDefault(kv.Key, kv.Key))).ToList();
        }
        finally { LoadOrderScan.DisposeLoadOrder(lo); }
    }

    // How many vanilla LeveledNpc lists directly reference this category's NPCs — i.e. whether the
    // Leveled-List Boost has anywhere to inject. 0 => the category can't be boosted.
    public static int BoostListCount(string game, string category)
    {
        using var esm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(game), GameCfg.Release);
        var raceName = esm.Races.ToDictionary(r => r.FormKey, r => r.EditorID ?? "");
        string RaceOf(FormKey fk) => raceName.TryGetValue(fk, out var s) && s != "" ? s : fk.ToString();
        var prefix = Categories.TargetPrefix(category);
        var keys = new HashSet<FormKey>(esm.Npcs.Where(n =>
            Categories.MatchesPrefix(n.EditorID ?? "", prefix) && OwnTraits(n)
            && !RaceOf(n.Race.FormKey).Contains("Child", StringComparison.OrdinalIgnoreCase)).Select(n => n.FormKey));
        int count = 0;
        foreach (var ll in esm.LeveledNpcs)
            if (ll.Entries is not null && ll.Entries.Any(e => e.Data is not null && keys.Contains(e.Data.Reference.FormKey)))
                count++;
        return count;
    }

    // CLI: list-faces --game <esm> --source <p> [--source ...]   -> JSON array of FaceInfo
    public static int Run(string[] args)
    {
        string? game = null; var srcs = new List<string>();
        for (int i = 1; i < args.Length; i++)
            switch (args[i]) { case "--game": game = args[++i]; break; case "--source": srcs.Add(args[++i]); break; }
        if (game is null) { Console.Error.WriteLine("list-faces --game <Skyrim.esm> --source <plugin> [...]"); return 1; }
        Console.WriteLine(JsonSerializer.Serialize(Enumerate(game, srcs), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
