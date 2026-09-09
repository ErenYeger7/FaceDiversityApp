using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// The LIBRARY SET: one "Build library" click packs every curated face into as many ESL-flagged plugins as the
// 2048-record limit needs (FDA_Library_1.esp, _2, ...), each with its own BSAs, in ONE output mod folder.
//
// Stable IDs — the registry (library/registry.yaml) remembers which plugin and FormID every face (and every
// copied record) got. A rebuild keeps them and only appends, so SkyPatcher configs made against an older
// build keep working; a face you un-whitelist leaves a tombstone (its ID stays reserved) rather than shifting
// anything. "Re-pack" throws the registry away and packs from scratch (then regenerate configs — recipes).
//
// Packing — first-fit-decreasing BY MOD: a mod's faces share its head parts/texture sets/skins, so keeping
// them in one plugin keeps those records shared; a mod that can't fit whole is split (its shared records are
// then duplicated in both plugins — harmless). Every fit test is the engine's own dry run with the plugin's
// pinned IDs, so the verdict is exact, never an estimate.
class LibraryRegistry
{
    public string Set { get; set; } = "FDA_Library";
    public int Plugins { get; set; }
    // face key -> (plugin index 1-based, FormID); record key "<plugin>|<source FormKey>" -> FormID
    public Dictionary<string, (int plugin, uint id)> Faces { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, uint> Records { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static string PathOf() => Path.Combine(Library.Root(), "registry.yaml");

    class Entry { public string Key { get; set; } = ""; public int Plugin { get; set; } public string Id { get; set; } = ""; }
    class Doc { public string? Set { get; set; } public int Plugins { get; set; } public List<Entry> Faces { get; set; } = new(); public List<Entry> Records { get; set; } = new(); }

    public static LibraryRegistry Load()
    {
        var r = new LibraryRegistry();
        var p = PathOf();
        if (!File.Exists(p)) return r;
        try
        {
            var de = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
            var doc = de.Deserialize<Doc>(File.ReadAllText(p)) ?? new Doc();
            r.Set = string.IsNullOrWhiteSpace(doc.Set) ? "FDA_Library" : doc.Set!; r.Plugins = doc.Plugins;
            foreach (var e in doc.Faces) if (e.Key.Length > 0) r.Faces[e.Key] = (e.Plugin, Convert.ToUInt32(e.Id, 16));
            foreach (var e in doc.Records) if (e.Key.Length > 0) r.Records[$"{e.Plugin}|{e.Key}"] = Convert.ToUInt32(e.Id, 16);
        }
        catch { return new LibraryRegistry(); }
        return r;
    }

    public void Save()
    {
        Directory.CreateDirectory(Library.Root());
        var sb = new System.Text.StringBuilder();
        sb.Append("# FaceDiversityApp — library set registry: which plugin + FormID every face and copied record has.\n");
        sb.Append("# Append-only: rebuilding keeps these so existing SkyPatcher configs stay valid. Delete this file (or\n");
        sb.Append("# use Re-pack) to start over — then regenerate every config that references the library.\n");
        sb.Append("set: ").Append(Library.Quote(Set)).Append('\n');
        sb.Append("plugins: ").Append(Plugins).Append('\n');
        sb.Append("faces:\n");
        foreach (var kv in Faces.OrderBy(k => k.Value.plugin).ThenBy(k => k.Value.id))
            sb.Append("  - { key: ").Append(Library.Quote(kv.Key)).Append(", plugin: ").Append(kv.Value.plugin).Append(", id: \"").Append(kv.Value.id.ToString("X6")).Append("\" }\n");
        sb.Append("records:\n");
        foreach (var kv in Records.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var bar = kv.Key.IndexOf('|');
            sb.Append("  - { key: ").Append(Library.Quote(kv.Key[(bar + 1)..])).Append(", plugin: ").Append(kv.Key[..bar]).Append(", id: \"").Append(kv.Value.ToString("X6")).Append("\" }\n");
        }
        File.WriteAllText(PathOf(), sb.ToString());
    }

    public Dictionary<string, uint> PinnedFaces(int plugin) =>
        Faces.Where(f => f.Value.plugin == plugin).ToDictionary(f => f.Key, f => f.Value.id, StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, uint> PinnedRecords(int plugin) =>
        Records.Where(r => r.Key.StartsWith(plugin + "|")).ToDictionary(r => r.Key[(r.Key.IndexOf('|') + 1)..], r => r.Value, StringComparer.OrdinalIgnoreCase);
}

static class LibrarySet
{
    public record ModInput(string Plugin, string Path, List<string> FaceIds);
    public record SetOptions(string Game, string OutRoot, string SetName, List<ModInput> Mods, List<string> AssetDirs, bool Bsa, bool CrossMod, bool Repack);
    public record PluginPlan(int Index, string Name, List<string> FaceIds, int Records, int Reserved, Dictionary<string, int> ModFaces, int NewFaces);
    public record SetPlan(List<PluginPlan> Plugins, List<LibraryBuild.Skipped> Skipped, Dictionary<string, string> FaceGenFrom,
                          Dictionary<string, Library.RaceCount> PerRace, Dictionary<string, int> ModRecords, int PinnedFaces, int NewFaces,
                          int Tombstones, string? Error);

    static string PluginName(string set, int i) => $"{set}_{i}.esp";

    // Sources a plugin needs = the mods that own its faces.
    static (List<string> sources, HashSet<string> include) SourcesFor(IEnumerable<string> faceIds, List<ModInput> mods)
    {
        var inc = new HashSet<string>(faceIds, StringComparer.OrdinalIgnoreCase);
        var srcs = mods.Where(m => m.FaceIds.Any(inc.Contains)).Select(m => m.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (srcs, inc);
    }

    // Exact fit test: the engine's dry run over the plugin's faces with its pinned IDs. Null = does not fit.
    static LibraryBuild.Result? TryFit(SetOptions o, LibraryRegistry reg, int plugin, IEnumerable<string> faceIds)
    {
        var (srcs, inc) = SourcesFor(faceIds, o.Mods);
        if (srcs.Count == 0) return null;
        try
        {
            var r = LibraryBuild.Execute(new LibraryBuild.Options(o.Game, Path.GetTempPath(), PluginName(o.SetName, plugin), srcs, inc, o.AssetDirs, null,
                BakeCrossMod: false, Bsa: false, PinnedFaces: reg.PinnedFaces(plugin), PinnedRecords: reg.PinnedRecords(plugin)), dryRun: true);
            return r.Overflow ? null : r;
        }
        catch (InvalidOperationException) { return null; }
    }

    public static SetPlan Plan(SetOptions o) => Plan(o, out _);

    static SetPlan Plan(SetOptions o, out LibraryRegistry regOut)
    {
        var reg = o.Repack ? new LibraryRegistry { Set = o.SetName } : LibraryRegistry.Load();
        if (!string.Equals(reg.Set, o.SetName, StringComparison.OrdinalIgnoreCase) && reg.Faces.Count > 0)
            reg = new LibraryRegistry { Set = o.SetName };   // a different set name starts its own registry
        reg.Set = o.SetName;
        regOut = reg;

        // 1. per-mod QA + record cost (dry run of the mod alone)
        var skipped = new List<LibraryBuild.Skipped>(); var faceGenFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var perRace = new Dictionary<string, Library.RaceCount>(StringComparer.OrdinalIgnoreCase);
        var modRecords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var valid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // mod plugin -> buildable face ids
        string? error = null;
        foreach (var m in o.Mods)
        {
            if (m.FaceIds.Count == 0) continue;
            try
            {
                var r = LibraryBuild.Execute(new LibraryBuild.Options(o.Game, Path.GetTempPath(), "plan.esp", new List<string> { m.Path },
                    new HashSet<string>(m.FaceIds, StringComparer.OrdinalIgnoreCase), o.AssetDirs, null, BakeCrossMod: false), dryRun: true);
                skipped.AddRange(r.SkippedFaces);
                foreach (var kv in r.FaceGenFrom) faceGenFrom[kv.Key] = kv.Value;
                foreach (var kv in r.FacesPerRace) { if (!perRace.TryGetValue(kv.Key, out var c)) perRace[kv.Key] = c = new Library.RaceCount(); c.F += kv.Value.F; c.M += kv.Value.M; }
                modRecords[m.Plugin] = r.Overflow ? int.MaxValue : r.NewRecords;   // overflow: the mod alone exceeds one plugin -> split below
                valid[m.Plugin] = r.FaceIds.Keys.ToList();
            }
            catch (InvalidOperationException e)
            {
                // "no faces to build" => everything skipped (already listed)
                if (!e.Message.StartsWith("no faces")) error = $"{m.Plugin}: {e.Message}";
                valid[m.Plugin] = new();
            }
        }

        // 2. current assignment from the registry (only faces still selected & buildable)
        var allValid = new HashSet<string>(valid.Values.SelectMany(v => v), StringComparer.OrdinalIgnoreCase);
        var assigned = new Dictionary<int, List<string>>();
        for (int i = 1; i <= reg.Plugins; i++) assigned[i] = new();
        int pinnedCount = 0;
        foreach (var f in allValid)
            if (reg.Faces.TryGetValue(f, out var e)) { if (!assigned.ContainsKey(e.plugin)) assigned[e.plugin] = new(); assigned[e.plugin].Add(f); pinnedCount++; }
        int tombstones = reg.Faces.Count(f => !allValid.Contains(f.Key));

        // 3. place NEW faces, biggest mod first; prefer the plugin that already holds the mod, then any with room, then a new one
        var newByMod = o.Mods.Where(m => valid.ContainsKey(m.Plugin))
            .Select(m => (m.Plugin, faces: valid[m.Plugin].Where(f => !reg.Faces.ContainsKey(f)).ToList()))
            .Where(x => x.faces.Count > 0).OrderByDescending(x => modRecords.GetValueOrDefault(x.Plugin)).ToList();
        int newCount = newByMod.Sum(x => x.faces.Count);
        var records = new Dictionary<int, int>();
        foreach (var nb in newByMod)
        {
            var plugin = nb.Plugin;
            var rest = new List<string>(nb.faces);
            var order = assigned.Keys.OrderByDescending(i => assigned[i].Count(f => f.StartsWith(plugin + "#", StringComparison.OrdinalIgnoreCase))).ThenBy(i => i).ToList();
            int pi = 0;
            while (rest.Count > 0)
            {
                int i;
                if (pi < order.Count) i = order[pi++];
                else { i = (assigned.Keys.DefaultIfEmpty(0).Max()) + 1; assigned[i] = new(); }
                // whole?
                var fit = TryFit(o, reg, i, assigned[i].Concat(rest));
                if (fit is not null) { assigned[i].AddRange(rest); records[i] = fit.NewRecords; rest.Clear(); break; }
                // largest prefix that fits (bisection — the count is monotonic)
                int lo = 0, hi = rest.Count - 1; LibraryBuild.Result? best = null;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    var t = TryFit(o, reg, i, assigned[i].Concat(rest.Take(mid)));
                    if (t is not null) { lo = mid; best = t; } else hi = mid - 1;
                }
                if (lo > 0 && best is not null) { assigned[i].AddRange(rest.Take(lo)); records[i] = best.NewRecords; rest.RemoveRange(0, lo); }
                else if (pi >= order.Count && assigned[i].Count == 0)
                { error = $"{plugin}: a single face does not fit an empty plugin ({rest[0]})"; rest.Clear(); }
            }
        }
        // record counts for plugins that received nothing new this time
        foreach (var i in assigned.Keys.ToList())
            if (!records.ContainsKey(i) && assigned[i].Count > 0) records[i] = TryFit(o, reg, i, assigned[i])?.NewRecords ?? 0;

        var plans = assigned.Where(kv => kv.Value.Count > 0 || reg.Plugins >= kv.Key).OrderBy(kv => kv.Key).Select(kv =>
            new PluginPlan(kv.Key, PluginName(o.SetName, kv.Key), kv.Value, records.GetValueOrDefault(kv.Key),
                reg.PinnedFaces(kv.Key).Count + reg.PinnedRecords(kv.Key).Count,
                kv.Value.GroupBy(f => f[..f.IndexOf('#')], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
                kv.Value.Count(f => !reg.Faces.ContainsKey(f)))).ToList();
        return new SetPlan(plans, skipped, faceGenFrom, perRace, modRecords, pinnedCount, newCount, tombstones, error);
    }

    public record SetResult(SetPlan Plan, List<LibraryBuild.Result> Builds, string OutFolder, string MapPath, List<string> Names);

    public static SetResult Build(SetOptions o)
    {
        var plan = Plan(o, out var reg);
        if (plan.Error is not null) throw new InvalidOperationException(plan.Error);
        if (plan.Plugins.All(p => p.FaceIds.Count == 0)) throw new InvalidOperationException("no faces to build (empty selection, or every face failed the FaceGen check)");
        var outFolder = Path.Combine(o.OutRoot, o.SetName);
        if (Directory.Exists(outFolder)) Directory.Delete(outFolder, true);   // our own output; rebuilt whole
        Directory.CreateDirectory(outFolder);

        var builds = new List<LibraryBuild.Result>(); var names = new List<string>();
        var setMap = new LibraryMap { Plugin = PluginName(o.SetName, 1), Built = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        foreach (var p in plan.Plugins.Where(p => p.FaceIds.Count > 0))
        {
            var (srcs, inc) = SourcesFor(p.FaceIds, o.Mods);
            var r = LibraryBuild.Execute(new LibraryBuild.Options(o.Game, outFolder, p.Name, srcs, inc, o.AssetDirs, null,
                BakeCrossMod: o.CrossMod, Bsa: o.Bsa, PinnedFaces: reg.PinnedFaces(p.Index), PinnedRecords: reg.PinnedRecords(p.Index),
                ReadmeName: $"README_{Path.GetFileNameWithoutExtension(p.Name)}.txt"), dryRun: false);
            builds.Add(r); names.Add(p.Name);
            foreach (var kv in r.FaceIds) reg.Faces[kv.Key] = (p.Index, kv.Value);
            foreach (var kv in r.RecordIds) reg.Records[$"{p.Index}|{kv.Key}"] = kv.Value;
            var m = LibraryMap.Load(Path.Combine(outFolder, Path.GetFileNameWithoutExtension(p.Name) + "_map.yaml"));
            if (m is not null) foreach (var row in m.Faces) { row.Plugin = p.Name; setMap.Faces.Add(row); }
            setMap.Plugins.Add(p.Name);
            Console.WriteLine($"[{p.Name}] {r.Donors} donors, {r.NewRecords} records ({(r.Esl ? "ESPFE" : "FULL ESP")}), masters: {string.Join(", ", r.Masters)}"
                              + (r.SkippedFaces.Count > 0 ? $", {r.SkippedFaces.Count} skipped (no FaceGen)" : ""));
            foreach (var n in r.BsaNotes) Console.WriteLine("  " + n);
        }
        reg.Plugins = Math.Max(reg.Plugins, plan.Plugins.Max(p => p.Index));
        reg.Save();
        var mapPath = Path.Combine(LibraryMap.BuildsDir(), o.SetName + ".yaml");
        LibraryMap.Save(mapPath, setMap);
        LibraryMap.Save(Path.Combine(outFolder, o.SetName + "_map.yaml"), setMap);

        // set README
        var rd = new System.Text.StringBuilder();
        rd.Append($"{o.SetName} — FaceDiversityApp LIBRARY SET (personal use only; do not redistribute)\n\n");
        rd.Append($"{setMap.Faces.Count} faces in {builds.Count} plugin(s). Install this folder as ONE mod and enable every plugin listed below (all ESL-flagged\n");
        rd.Append("unless noted — no load-order slots). In Mod Creator the set appears as a single source (\"Library builds\"); a SkyPatcher\n");
        rd.Append("config's own README names the plugins it actually references.\n\n");
        rd.Append("STABLE IDs: library/registry.yaml pins every donor's plugin + FormID. Rebuilding after curating more faces APPENDS —\n");
        rd.Append("existing configs keep working. A face you un-whitelist leaves its ID reserved. 'Re-pack from scratch' resets the\n");
        rd.Append("registry; regenerate your configs afterwards (Mod Creator → Saved recipes → Regenerate all).\n\n");
        var allMasters = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < builds.Count; i++)
        {
            var r = builds[i];
            rd.Append($"== {names[i]} — {r.Donors} donors, {r.NewRecords} records, {(r.Esl ? "ESPFE" : "FULL ESP")}; masters: {string.Join(", ", r.Masters)}\n");
            rd.Append(LibraryBuild.DonorModsText(r.DonorMods)).Append('\n');
            foreach (var n in r.BsaNotes) rd.Append("  ").Append(n).Append('\n');
            foreach (var m in r.Masters) allMasters.Add(m);
            rd.Append('\n');
        }
        rd.Append("MASTERS (all plugins): " + string.Join(", ", allMasters) + (allMasters.All(Classify.BaseMasters.Contains) ? "  — base game only, fully self-contained\n" : "  <- NOTE non-vanilla masters remain (see the per-plugin READMEs)\n"));
        if (builds.Any(b => b.Dummies.Count > 0))
            rd.Append("DUMMY PLUGINS (carry overflow archives, enable them too): " + string.Join(", ", builds.SelectMany(b => b.Dummies)) + "\n");
        rd.Append(LibraryBuild.QaText(plan.Skipped, plan.FaceGenFrom));
        File.WriteAllText(Path.Combine(outFolder, "README.txt"), rd.ToString());
        Console.WriteLine($"Library set {o.SetName}: {setMap.Faces.Count} faces in {builds.Count} plugin(s) -> {outFolder}; registry {LibraryRegistry.PathOf()}");
        return new SetResult(plan, builds, outFolder, mapPath, names);
    }
}
