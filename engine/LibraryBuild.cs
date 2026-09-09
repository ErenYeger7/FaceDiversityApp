using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Binary.Parameters;

// `build-library` — ONE self-contained face-library plugin from curated faces:
//
//   build-library --game <Skyrim.esm> --out <dir> --name <plugin>.esp --source <esp>...
//                 [--include <file of face ids>] [--asset-dirs <file of mod folders>] [--map <yaml>] [--bsa] [--no-cross-mod]
//
// Every included face becomes a NEW, never-placed donor NPC carrying that source's face fields, with the
// source's FaceGen re-keyed under this plugin. Then EVERYTHING the donors reach in the sources and in the
// sources' own (non-vanilla) masters — head parts, texture sets, colors, form lists, custom RACES and their
// skins (ARMO/ARMA), voices, classes, keywords, body-part/movement data — is deep-copied in and re-linked,
// and the meshes/textures those records name are baked in. Result: a plugin whose only masters are the
// base game, installable in any MO2 instance on its own, that SkyPatcher configs reference by
// copyVisualStyle=<this plugin>|<donor>. The map (yaml + csv) records donor <-> original face.
//
// QA at the origin: a face whose FaceGen mesh OR tint cannot be found — in the source's own folder (loose or
// BSA), then in any other installed mod folder (a patch plugin's unchanged faces live in the original mod's
// folder) — is NOT built (it would be the dark-face bug in game) and is listed with the reason.
//
// Stable IDs: `Options.PinnedFaces/PinnedRecords` (from the library registry) fix the FormID of a donor /
// copied record across rebuilds, so SkyPatcher configs made against an older build keep working; new ones
// are allocated above `NextFree`. `Execute(..., dryRun: true)` runs the SAME indexing + closure without
// writing or baking, so the UI's projection is the engine's own count, never a parallel estimate.
static class LibraryBuild
{
    static readonly HashSet<string> Base = Classify.BaseMasters;
    // the ESL-flagged plugin's whole space. FDA_ID_LIMIT (hex) lowers the top — test hook to exercise the
    // multi-plugin packing without a 2048-record fixture.
    public const uint FirstId = 0x800;
    public static readonly uint LastId = uint.TryParse(Environment.GetEnvironmentVariable("FDA_ID_LIMIT"), System.Globalization.NumberStyles.HexNumber, null, out var lim) && lim > FirstId ? lim : 0xFFF;

    public record Options(string Game, string OutFolder, string OutName, List<string> Sources, HashSet<string>? Include,
                          List<string> AssetDirs, string? MapPath, bool BakeCrossMod = true, bool Bsa = false,
                          Dictionary<string, uint>? PinnedFaces = null, Dictionary<string, uint>? PinnedRecords = null,
                          string ReadmeName = "README.txt");
    public record Skipped(string Key, string Source, string Npc, string Name, string Reason);
    public record Result(int Donors, int NewRecords, bool Esl, Dictionary<string, int> CopiedByType, List<string> Masters,
                         List<string> ExtraMasters, Dictionary<string, int> Unresolved, List<string> MissingMasters, int Baked,
                         List<Skipped> SkippedFaces, int DanglingDropped, Dictionary<string, int> FacesPerPlugin,
                         Dictionary<string, Library.RaceCount> FacesPerRace, string? Esp, List<string> BsaNotes,
                         Dictionary<string, uint> FaceIds, Dictionary<string, uint> RecordIds,   // what this build allocated/used (face key / source FormKey -> ID)
                         Dictionary<string, Dictionary<string, Library.RaceCount>> DonorMods,   // source plugin -> race -> counts (README "donor mods")
                         Dictionary<string, string> FaceGenFrom,                                 // face key -> other mod folder its FaceGen came from
                         List<string> Dummies, bool Overflow = false);                           // dry run only: the faces do not fit the ID space (split needed)

    public static int Run(string[] args)
    {
        string? game = null, outFolder = null, outName = null, includePath = null, assetDirsPath = null, mapPath = null;
        var sources = new List<string>(); bool crossMod = true, bsa = false;
        for (int i = 1; i < args.Length; i++)
            switch (args[i])
            {
                case "--no-cross-mod": crossMod = false; break;         // masters are still found in --asset-dirs; only other packs' textures aren't baked
                case "--bsa": bsa = true; break;                         // pack meshes/textures into <stem>.bsa + "<stem> - Textures.bsa" (LZ4), verified, loose removed
                case "--game": game = args[++i]; break;
                case "--out": outFolder = args[++i]; break;
                case "--name": outName = args[++i]; break;
                case "--source": sources.Add(args[++i]); break;
                case "--include": includePath = args[++i]; break;      // face ids (Faces.FaceId), one per line; absent => every NPC of every source
                case "--asset-dirs": assetDirsPath = args[++i]; break; // mod folders (priority order) to find masters + cross-mod textures in
                case "--map": mapPath = args[++i]; break;              // extra copy of the build map (library/builds/<stem>.yaml)
            }
        if (game is null || outFolder is null || outName is null || sources.Count == 0)
        { Console.Error.WriteLine("need --game --out --name and at least one --source"); return 1; }
        var include = ReadLines(includePath) is { } inc ? new HashSet<string>(inc, StringComparer.OrdinalIgnoreCase) : null;
        var assetDirs = ReadLines(assetDirsPath)?.Where(Directory.Exists).ToList() ?? new List<string>();

        Result r;
        try { r = Execute(new Options(game, outFolder, outName, sources, include, assetDirs, mapPath, crossMod, bsa), dryRun: false); }
        catch (InvalidOperationException e) { Console.Error.WriteLine(e.Message); return 1; }
        Report(r, outName, sources.Count, mapPath);
        return 0;
    }

    public static List<string>? ReadLines(string? path) =>
        path is not null && File.Exists(path) ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : null;

    public static void Report(Result r, string outName, int sourceCount, string? mapPath)
    {
        var byType = string.Join(", ", r.CopiedByType.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {k.Key}"));
        if (r.DanglingDropped > 0) Console.WriteLine($"Dropped {r.DanglingDropped} dangling link(s) to records that do not exist in their plugin (broken in the source; the game ignores them too).");
        Console.WriteLine($"Library build {outName}: {r.Donors} donor faces from {sourceCount} source(s); copied {byType}; {r.Baked} assets baked.");
        if (r.SkippedFaces.Count > 0) Console.WriteLine($"QA: {r.SkippedFaces.Count} face(s) NOT built (no FaceGen found) — listed in the README.");
        if (r.FaceGenFrom.Count > 0) Console.WriteLine($"QA: {r.FaceGenFrom.Count} face(s) took their FaceGen from another mod folder (patch plugins) — listed in the README.");
        Console.WriteLine($"Output is {(r.Esl ? "ESL-flagged (ESPFE)" : "a FULL ESP")} — {r.NewRecords} new records.");
        Console.WriteLine("Masters: " + string.Join(", ", r.Masters) + (r.ExtraMasters.Count == 0 ? "  (base game only — fully self-contained)" : "  <- NOTE non-vanilla masters remain, see README"));
        if (r.MissingMasters.Count > 0) Console.WriteLine("WARNING masters not found: " + string.Join(", ", r.MissingMasters));
        foreach (var n in r.BsaNotes) Console.WriteLine(n);
        var stem = Path.GetFileNameWithoutExtension(outName);
        Console.WriteLine($"Wrote {r.Esp} + {stem}_map.yaml/.csv" + (mapPath is not null ? $" (+ {mapPath})" : ""));
    }

    public static Result Execute(Options o, bool dryRun)
    {
        var (game, outFolder, outName, sources, include, assetDirs, mapPath, crossMod, packBsa, pinnedFaces, pinnedRecords, readmeName) = o;
        pinnedFaces ??= new(StringComparer.OrdinalIgnoreCase); pinnedRecords ??= new(StringComparer.OrdinalIgnoreCase);
        // ALL mod folders are searched for masters and FaceGen; the cross-mod texture resolver only when asked.
        var finder = assetDirs.Count > 0 ? new LoadOrderAssets(assetDirs.Select(p => (Path.GetFileName(p.TrimEnd('/', '\\')), p)).ToList()) : null;
        var resolver = crossMod ? finder : null;

        var esm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(game), GameCfg.Release);
        var raceName = esm.Races.ToDictionary(r => r.FormKey, r => r.EditorID ?? "");
        var esmNpcRace = esm.Npcs.ToDictionary(n => n.FormKey, n => n.Race.FormKey);
        string RaceOf(FormKey fk) => raceName.TryGetValue(fk, out var s) && s != "" ? s : fk.ToString();
        bool Fem(INpcGetter n) => n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);

        var outMod = new SkyrimMod(ModKey.FromNameAndExtension(outName), GameCfg.Release) { IsSmallMaster = false };
        var assets = new Dictionary<string, SourceAssets>(StringComparer.OrdinalIgnoreCase);
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int bakedAssets = 0;

        // ---- FormID allocation: pinned IDs (registry) are honoured and reserved even when not reached this
        // time (tombstones keep old configs valid); everything else is allocated from the lowest free ID.
        var reserved = new HashSet<uint>(pinnedFaces.Values.Concat(pinnedRecords.Values));
        uint cursor = FirstId;
        var faceIds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var recordIds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        bool overflow = false; uint overflowSeq = 0x100000;   // dry-run placeholders: unique, outside any real range, never written
        uint Alloc()
        {
            while (cursor <= LastId && reserved.Contains(cursor)) cursor++;
            if (cursor > LastId)
            {
                // a dry run keeps going (so the QA list and the face set stay complete) and reports Overflow —
                // the set planner then splits; a real build must never get here
                if (dryRun) { overflow = true; return overflowSeq++; }
                throw new InvalidOperationException("FormID space exhausted (2048 records incl. reserved) — split the build");
            }
            reserved.Add(cursor);
            return cursor++;
        }

        // ---- index every non-vanilla record of the sources AND of their non-vanilla masters (a CS_Foundation
        // face's body lives in CS_Visions.esp). Masters first, then the source, so a source's override of its
        // master's record wins — the same precedence the game applies.
        var rec = new Dictionary<FormKey, (IMajorRecordGetter rec, string folder)>();
        var loaded = new Dictionary<string, (ISkyrimModDisposableGetter mod, string path)>(StringComparer.OrdinalIgnoreCase);
        var missingMasters = new List<string>();
        string? FindPlugin(string fileName, string nearFolder)
        {
            var p = Path.Combine(nearFolder, fileName);
            if (File.Exists(p)) return p;
            foreach (var d in assetDirs) { p = Path.Combine(d, fileName); if (File.Exists(p)) return p; }
            return null;
        }
        void IndexMod(string path)
        {
            var name = Path.GetFileName(path);
            if (loaded.ContainsKey(name)) return;
            ISkyrimModDisposableGetter sm;
            try { sm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(path), GameCfg.Release); }
            catch (Exception e) { Console.WriteLine($"  cannot read {name}: {e.Message}"); return; }
            loaded[name] = (sm, path);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            if (!assets.ContainsKey(folder)) assets[folder] = new SourceAssets(folder);
            foreach (var m in sm.ModHeader.MasterReferences)
            {
                var mn = m.Master.FileName.ToString();
                if (Base.Contains(mn) || loaded.ContainsKey(mn)) continue;
                var mp = FindPlugin(mn, folder);
                if (mp is null) { if (!missingMasters.Contains(mn)) missingMasters.Add(mn); continue; }
                IndexMod(mp);
            }
            foreach (var r in sm.EnumerateMajorRecords())
                if (!Base.Contains(r.FormKey.ModKey.FileName)) rec[r.FormKey] = (r, folder);   // later (the source) overwrites earlier (its master)
        }
        foreach (var sp in sources) IndexMod(sp);

        // ---- asset baking (loose OR BSA of the record's own mod, else any mod folder given). Never bakes the
        // shared vanilla-path body skin (\character\female\ / \male\): as a loose file it would override the
        // user's skin mod globally — a custom race's own texture folder (e.g. actors\character\Audrey S\) IS baked.
        static bool IsSharedSkin(string rel)
        {
            var p = rel.ToLowerInvariant().Replace('/', '\\');
            return p.Contains("\\character\\female\\") || p.Contains("\\character\\male\\");
        }
        byte[]? Fetch(string folder, string rel)
        {
            var data = assets.TryGetValue(folder, out var sa) ? sa.Get(rel) : null;
            if (data is null && resolver is not null) data = resolver.ResolveBytes(rel);
            return data;
        }
        void Bake(string folder, string rel)
        {
            if (dryRun) return;
            rel = rel.Replace('/', '\\').TrimStart('\\');
            if (rel.Length == 0 || rel.Contains("facegendata\\", StringComparison.OrdinalIgnoreCase) || IsSharedSkin(rel)) return;
            if (!extracted.Add(rel)) return;
            var data = Fetch(folder, rel);
            if (data is null) return;   // vanilla (stock BSA) or genuinely absent — left to load normally
            Generate.WriteBytes(outFolder, rel, data);
            bakedAssets++;
        }
        void BakeModel(string folder, string? model)
        {
            if (dryRun || string.IsNullOrWhiteSpace(model)) return;
            var rel = model.Replace('/', '\\').TrimStart('\\');
            if (!rel.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)) rel = "meshes\\" + rel;
            var variants = new List<string> { rel };
            if (rel.EndsWith("_1.nif", StringComparison.OrdinalIgnoreCase)) variants.Add(rel[..^6] + "_0.nif");   // weight pair
            else if (rel.EndsWith("_0.nif", StringComparison.OrdinalIgnoreCase)) variants.Add(rel[..^6] + "_1.nif");
            foreach (var v in variants)
            {
                var nif = Fetch(folder, v);
                if (nif is null) continue;
                Bake(folder, v);
                foreach (var tex in Generate.DdsPathsInNif(nif)) Bake(folder, tex);
            }
        }
        void BakeTexture(string folder, string? tex)
        {
            if (dryRun || string.IsNullOrWhiteSpace(tex)) return;
            var rel = tex.Replace('/', '\\').TrimStart('\\');
            if (!rel.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase)) rel = "textures\\" + rel;
            Bake(folder, rel);
        }

        // ---- donors: one never-placed NPC per included face, the source's exact face fields — but ONLY when
        // its FaceGen (mesh AND tint) exists: the source's own folder first, then any installed mod folder.
        var map = new LibraryMap { Plugin = outName, Built = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        var donors = new List<(Npc npc, INpcGetter src, string sp, string folder)>();
        var skipped = new List<Skipped>();
        var faceGenFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var facesPerPlugin = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var facesPerRace = new Dictionary<string, Library.RaceCount>(StringComparer.OrdinalIgnoreCase);
        var donorMods = new Dictionary<string, Dictionary<string, Library.RaceCount>>(StringComparer.OrdinalIgnoreCase);
        int seq = 0;
        foreach (var sp in sources)
        {
            var name = Path.GetFileName(sp);
            facesPerPlugin[name] = 0;
            if (!loaded.TryGetValue(name, out var ld)) continue;
            var folder = Path.GetDirectoryName(Path.GetFullPath(sp))!;
            foreach (var s in ld.mod.Npcs)
            {
                var id = Faces.FaceId(sp, s.FormKey);
                if (include is not null && !include.Contains(id)) continue;
                if (LoadOrderScan.IsPlayerOrPreset(s)) continue;
                // QA: traits inherited from a template = no face of its own (the user's audit: ~40% of the "missing
                // FaceGen" cases were these) — listed with that reason rather than as a FaceGen problem.
                if (s.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !s.Template.IsNull)
                {
                    skipped.Add(new Skipped(id, name, s.EditorID ?? "", s.Name?.String ?? "", "traits inherited from a template (no face of its own) — not a harvestable face"));
                    continue;
                }

                // QA: FaceGen must exist, or the face is the dark-face bug in game — not built, listed.
                var sub = s.FormKey.ModKey.FileName.ToString(); var hex = "00" + s.FormKey.ID.ToString("x6");
                var nifRel = Path.Combine("meshes", "actors", "character", "facegendata", "facegeom", sub, hex + ".nif");
                var ddsRel = Path.Combine("textures", "actors", "character", "facegendata", "facetint", sub, hex + ".dds");
                bool ownNif = assets[folder].Has(nifRel), ownDds = assets[folder].Has(ddsRel);
                string? otherNif = ownNif ? null : finder?.ResolveName(nifRel), otherDds = ownDds ? null : finder?.ResolveName(ddsRel);
                if (!ownNif && otherNif is null || !ownDds && otherDds is null)
                {
                    var why = (!ownNif && otherNif is null ? "FaceGen mesh (facegeom nif) not found" : "")
                            + (!ownNif && otherNif is null && !ownDds && otherDds is null ? "; " : "")
                            + (!ownDds && otherDds is null ? "face tint (facetint dds) not found" : "")
                            + (" — looked in the source's folder" + (finder is not null ? " and every installed mod folder" : ""));
                    skipped.Add(new Skipped(id, name, s.EditorID ?? "", s.Name?.String ?? "", why));
                    continue;
                }
                if (otherNif is not null || otherDds is not null) faceGenFrom[id] = otherNif ?? otherDds!;

                uint fid = pinnedFaces.TryGetValue(id, out var pf) ? pf : Alloc();
                faceIds[id] = fid;
                var fk = new FormKey(outMod.ModKey, fid);
                var d = new Npc(fk, GameCfg.Release) { EditorID = $"FDAdonor{seq++:D4}_{s.EditorID}", Name = s.Name?.String, Height = s.Height, Weight = s.Weight };
                d.Configuration.Flags = (Fem(s) ? NpcConfiguration.Flag.Female : 0) | NpcConfiguration.Flag.Unique;
                d.Race.SetTo(s.Race.FormKey);
                if (!s.WornArmor.IsNull) d.WornArmor.SetTo(s.WornArmor.FormKey);
                if (!s.Class.IsNull) d.Class.SetTo(s.Class.FormKey);
                if (!s.Voice.IsNull) d.Voice.SetTo(s.Voice.FormKey);
                foreach (var hp in s.HeadParts) d.HeadParts.Add(hp);
                foreach (var x in s.TintLayers) d.TintLayers.Add(x.DeepCopy());
                d.HairColor.SetTo(s.HairColor.FormKey);
                d.FaceMorph = s.FaceMorph?.DeepCopy();
                d.FaceParts = s.FaceParts?.DeepCopy();
                d.TextureLighting = s.TextureLighting;   // QNAM — must match the FaceGen or the neck seams
                outMod.Npcs.Add(d);
                if (!dryRun)
                {
                    var nif = ownNif ? assets[folder].Get(nifRel) : finder!.ResolveBytes(nifRel);
                    var dds = ownDds ? assets[folder].Get(ddsRel) : finder!.ResolveBytes(ddsRel);
                    var tName = "00" + fid.ToString("x6");
                    Generate.WriteBytes(outFolder, Path.Combine("meshes", "actors", "character", "facegendata", "facegeom", outName, tName + ".nif"), nif!);
                    Generate.WriteBytes(outFolder, Path.Combine("textures", "actors", "character", "facegendata", "facetint", outName, tName + ".dds"), dds!);
                    foreach (var tex in Generate.DdsPathsInNif(nif!)) Bake(folder, tex);
                }
                donors.Add((d, s, sp, folder));
                facesPerPlugin[name]++;
                var pr = Base.Contains(s.FormKey.ModKey.FileName) && esmNpcRace.TryGetValue(s.FormKey, out var vr0) ? RaceOf(vr0) : RaceOf(s.Race.FormKey);
                if (!facesPerRace.TryGetValue(pr, out var rc)) facesPerRace[pr] = rc = new Library.RaceCount();
                if (Fem(s)) rc.F++; else rc.M++;
                if (!donorMods.TryGetValue(name, out var dm)) donorMods[name] = dm = new(StringComparer.OrdinalIgnoreCase);
                if (!dm.TryGetValue(pr, out var dc)) dm[pr] = dc = new Library.RaceCount();
                if (Fem(s)) dc.F++; else dc.M++;
            }
        }
        if (donors.Count == 0) throw new InvalidOperationException("no faces to build (empty selection, or every face failed the FaceGen check)");

        // ---- transitive closure: every non-vanilla record the donors reach, copied in and re-linked. A type
        // outside the allow-list (spells, factions, outfits, packages...) is deliberately NOT copied — it is
        // gameplay, not looks; such a link would keep its plugin as a master and is reported below.
        static bool Copyable(IMajorRecordGetter r) => r is IHeadPartGetter or ITextureSetGetter or IColorRecordGetter or IFormListGetter
            or IRaceGetter or IArmorGetter or IArmorAddonGetter or IVoiceTypeGetter or IClassGetter or IBodyPartDataGetter
            or IMovementTypeGetter or IKeywordGetter or IEquipTypeGetter or IMaterialTypeGetter;
        var toCopy = new HashSet<FormKey>();
        var unresolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // master -> links left pointing at it
        var q = new Queue<FormKey>();
        void Consider(FormKey fk)
        {
            if (fk.IsNull || Base.Contains(fk.ModKey.FileName) || fk.ModKey == outMod.ModKey) return;
            if (rec.TryGetValue(fk, out var e) && Copyable(e.rec)) { if (!toCopy.Contains(fk)) q.Enqueue(fk); }
            else unresolved[fk.ModKey.FileName] = unresolved.GetValueOrDefault(fk.ModKey.FileName) + 1;
        }
        foreach (var r in outMod.EnumerateMajorRecords()) foreach (var l in r.EnumerateFormLinks()) Consider(l.FormKey);
        while (q.Count > 0)
        {
            var fk = q.Dequeue();
            if (!toCopy.Add(fk)) continue;
            foreach (var l in rec[fk].rec.EnumerateFormLinks()) Consider(l.FormKey);
        }
        var remap = new Dictionary<FormKey, FormKey>();
        var copiedByType = new Dictionary<string, int>();
        var bakeLater = new List<Action>();
        foreach (var fk in toCopy.OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase))   // deterministic order => deterministic new IDs
        {
            var (src, folder) = rec[fk];
            var key = fk.ToString();
            var nid = pinnedRecords.TryGetValue(key, out var pr) ? pr : Alloc();
            recordIds[key] = nid;
            var nfk = new FormKey(outMod.ModKey, nid);
            var dup = src.Duplicate(nfk);
            switch (dup)
            {
                case HeadPart h: outMod.HeadParts.Add(h); bakeLater.Add(() => BakeModel(folder, h.Model?.File.ToString())); break;
                case TextureSet t: outMod.TextureSets.Add(t);
                    bakeLater.Add(() => { foreach (var p in new[] { t.Diffuse, t.NormalOrGloss, t.EnvironmentMaskOrSubsurfaceTint, t.GlowOrDetailMap, t.Height, t.Environment, t.Multilayer, t.BacklightMaskOrSpecular }) BakeTexture(folder, p?.ToString()); });
                    break;
                case ColorRecord c: outMod.Colors.Add(c); break;
                case FormList f: outMod.FormLists.Add(f); break;
                case Race ra: outMod.Races.Add(ra);
                    // racial spells/abilities are gameplay (and often script-backed) — not part of the look
                    ra.ActorEffect?.RemoveAll(l => !Base.Contains(l.FormKey.ModKey.FileName) && !rec.ContainsKey(l.FormKey) || (rec.TryGetValue(l.FormKey, out var se) && !Copyable(se.rec)));
                    bakeLater.Add(() => { BakeModel(folder, ra.SkeletalModel?.Male?.File.ToString()); BakeModel(folder, ra.SkeletalModel?.Female?.File.ToString()); });
                    break;
                case Armor ar: outMod.Armors.Add(ar); break;
                case ArmorAddon aa: outMod.ArmorAddons.Add(aa);
                    bakeLater.Add(() => { BakeModel(folder, aa.WorldModel?.Male?.File.ToString()); BakeModel(folder, aa.WorldModel?.Female?.File.ToString());
                                          BakeModel(folder, aa.FirstPersonModel?.Male?.File.ToString()); BakeModel(folder, aa.FirstPersonModel?.Female?.File.ToString()); });
                    break;
                case VoiceType v: outMod.VoiceTypes.Add(v); break;
                case Class cl: outMod.Classes.Add(cl); break;
                case BodyPartData b: outMod.BodyParts.Add(b); break;
                case MovementType mt: outMod.MovementTypes.Add(mt); break;
                case Keyword k: outMod.Keywords.Add(k); break;
                case EquipType et: outMod.EquipTypes.Add(et); break;
                case MaterialType mat: outMod.MaterialTypes.Add(mat); break;
                default: continue;
            }
            remap[fk] = nfk;
            var tn = dup.GetType().Name; copiedByType[tn] = copiedByType.GetValueOrDefault(tn) + 1;
        }
        outMod.RemapLinks(remap);
        foreach (var b in bakeLater) b();

        // Dangling links: a source can point at a record that does not exist in its master (CS_Foundation's
        // Alexandra wears skin 00088A:CS_Visions.esp, which CS_Visions.esp never defines). Nothing to copy —
        // the game ignores it too — but the reference alone would keep that plugin as a master, so drop it.
        int danglingDropped = 0;
        bool Dangling(FormKey fk) => !fk.IsNull && !Base.Contains(fk.ModKey.FileName) && fk.ModKey != outMod.ModKey && !rec.ContainsKey(fk);
        foreach (var (d, _, _, _) in donors)
        {
            if (Dangling(d.WornArmor.FormKey)) { d.WornArmor.Clear(); danglingDropped++; }
            if (Dangling(d.Class.FormKey)) { d.Class.Clear(); danglingDropped++; }
            if (Dangling(d.Voice.FormKey)) { d.Voice.Clear(); danglingDropped++; }
            if (Dangling(d.HairColor.FormKey)) { d.HairColor.Clear(); danglingDropped++; }
            danglingDropped += d.HeadParts.RemoveAll(h => Dangling(h.FormKey));
        }
        if (danglingDropped > 0)
        {
            unresolved.Clear();   // those links are no longer 'unresolved'
            foreach (var r in outMod.EnumerateMajorRecords()) foreach (var l in r.EnumerateFormLinks())
                if (!l.FormKey.IsNull && !Base.Contains(l.FormKey.ModKey.FileName) && l.FormKey.ModKey != outMod.ModKey)
                    unresolved[l.FormKey.ModKey.FileName] = unresolved.GetValueOrDefault(l.FormKey.ModKey.FileName) + 1;
        }
        missingMasters = missingMasters.Where(m => unresolved.ContainsKey(m)).ToList();   // only matters if still pointed at

        int newRecs = outMod.EnumerateMajorRecords().Count(r => r.FormKey.ModKey == outMod.ModKey);
        // ESL: every ID (incl. reserved tombstones) must sit in 0x800-0xFFF — Alloc guarantees it or throws.
        outMod.IsSmallMaster = newRecs <= 2048;

        if (dryRun)
        {
            foreach (var l in loaded.Values) if (l.mod is IDisposable dd) { try { dd.Dispose(); } catch { } }
            var extra = unresolved.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return new Result(donors.Count, newRecs, outMod.IsSmallMaster, copiedByType, new List<string>(), extra, unresolved,
                              missingMasters, 0, skipped, danglingDropped, facesPerPlugin, facesPerRace, null, new List<string>(),
                              faceIds, recordIds, donorMods, faceGenFrom, new List<string>(), overflow);
        }

        // ---- write (ESL when the record count fits), re-read masters, map, README
        Directory.CreateDirectory(outFolder);
        var esp = Path.Combine(outFolder, outName);
        outMod.WriteToBinary(esp, new BinaryWriteParameters { MastersListContent = MastersListContentOption.Iterate });
        foreach (var l in loaded.Values) if (l.mod is IDisposable dd) { try { dd.Dispose(); } catch { } }
        List<string> masters;
        using (var mm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(esp), GameCfg.Release))
            masters = mm.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
        var extraMasters = masters.Where(m => !Base.Contains(m)).ToList();

        foreach (var (d, s, sp, folder) in donors)
            map.Faces.Add(new LibraryMap.Row
            {
                Key = Faces.FaceId(sp, s.FormKey), Source = Path.GetFileName(sp), SourceMod = Path.GetFileName(folder),
                Npc = s.EditorID ?? "", Name = s.Name?.String ?? "", Origin = s.FormKey.ToString(),
                Library = d.FormKey.ID.ToString("X6"), EditorId = d.EditorID ?? "",
                PoolRace = Base.Contains(s.FormKey.ModKey.FileName) && esmNpcRace.TryGetValue(s.FormKey, out var vr) ? RaceOf(vr) : RaceOf(s.Race.FormKey),
                Race = d.Race.FormKey.ToString(), Skin = d.WornArmor.IsNull ? "" : d.WornArmor.FormKey.ToString(),
                Weight = d.Weight, Sex = Fem(s) ? "F" : "M"
            });
        var stem = Path.GetFileNameWithoutExtension(outName);
        LibraryMap.Save(Path.Combine(outFolder, stem + "_map.yaml"), map);
        if (mapPath is not null) LibraryMap.Save(mapPath, map);

        // ---- optional BSA packing. The game auto-loads exactly TWO archives per plugin: <Plugin>.bsa and
        // "<Plugin> - Textures.bsa". Meshes (incl. FaceGen geometry) go to the first, textures to the second, LZ4-
        // compressed (SSE v105). Offsets are 32-bit, so a group over ~4 GiB is split across further archives, each
        // hung off a tiny empty ESL-flagged dummy plugin (<stem>_2.esp -> <stem>_2.bsa / "<stem>_2 - Textures.bsa")
        // — the engine-native way to attach more archives; no load-order slot, no mod-manager step. Every archive
        // is read back with Mutagen and compared byte-for-byte before the loose files are deleted; on any doubt
        // the loose files stay (they override a BSA anyway) and the archives are removed.
        var bsaNotes = new List<string>();
        var dummyPlugins = new SortedSet<int>();
        var dummies = new List<string>();
        if (packBsa)
        {
            string OwnerStem(int i) => i == 0 ? stem : $"{stem}_{i + 1}";
            foreach (var (top, suffix, flags) in new[] { ("meshes", "", BsaWriter.FileFlagMeshes), ("textures", " - Textures", BsaWriter.FileFlagTextures) })
            {
                var dir = Path.Combine(outFolder, top);
                if (!Directory.Exists(dir)) continue;
                var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(outFolder, f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count == 0) continue;
                var written = new List<BsaWriter.Result>();
                try
                {
                    written = BsaWriter.WriteSplit(outFolder, files, compress: true, flags, i => Path.Combine(outFolder, OwnerStem(i) + suffix + ".bsa"));
                    var err = BsaWriter.Verify(written.Select(r => r.Path), outFolder, files);
                    if (err is not null)
                    {
                        foreach (var r in written) try { File.Delete(r.Path); } catch { }
                        bsaNotes.Add($"BSA ({top}): VERIFY FAILED ({err}) — archives discarded, {files.Count} {top} files left loose.");
                        continue;
                    }
                    foreach (var f in files) File.Delete(Path.Combine(outFolder, f));
                    foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                        try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
                    try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
                    for (int i = 0; i < written.Count; i++)
                    {
                        var r = written[i];
                        if (i > 0) dummyPlugins.Add(i);
                        bsaNotes.Add($"BSA {Path.GetFileName(r.Path)}: {r.Files} {top} files, {r.BytesIn / 1048576.0:0.0} MB -> {r.BytesOut / 1048576.0:0.0} MB (LZ4), verified by read-back"
                                     + (i > 0 ? $" — loaded by dummy plugin {OwnerStem(i)}.esp" : "") + ".");
                    }
                }
                catch (InvalidOperationException e)
                {
                    foreach (var r in written) try { File.Delete(r.Path); } catch { }
                    bsaNotes.Add($"BSA ({top}): not packed — {e.Message}; {files.Count} {top} files left loose.");
                }
            }
            // dummy ESL plugins that carry the overflow archives (header only — no records, no masters)
            foreach (var i in dummyPlugins)
            {
                var dk = ModKey.FromNameAndExtension(OwnerStem(i) + ".esp");
                var dummy = new SkyrimMod(dk, GameCfg.Release) { IsSmallMaster = true };
                dummy.ModHeader.Author = "FaceDiversityApp";
                dummy.ModHeader.Description = $"Empty ESL-flagged plugin whose only job is to make the game load {OwnerStem(i)}.bsa / \"{OwnerStem(i)} - Textures.bsa\" (overflow archives of {outName}). Keep it enabled with {outName}.";
                dummy.WriteToBinary(Path.Combine(outFolder, dk.FileName), new BinaryWriteParameters { MastersListContent = MastersListContentOption.Iterate });
                dummies.Add(dk.FileName);
                bsaNotes.Add($"Dummy plugin {dk.FileName} written (empty, ESL-flagged): enable it alongside {outName} so its archives load.");
            }
        }

        var rd = new System.Text.StringBuilder();
        rd.Append($"{outName} — FaceDiversityApp LIBRARY BUILD (personal use only; do not redistribute)\n\n");
        rd.Append($"{donors.Count} donor NPCs (never placed in the world), one per curated face, each carrying that face's head parts,\n");
        rd.Append("tints, morphs, weight and skin with the source's FaceGen re-keyed under this plugin. Everything they reference\n");
        rd.Append("from the source mods was copied in: " + string.Join(", ", copiedByType.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {k.Key}")) + $"; {bakedAssets} meshes/textures baked.\n");
        rd.Append($"Plugin type: {(outMod.IsSmallMaster ? $"ESL-flagged (ESPFE), {newRecs} new records" : $"FULL ESP — {newRecs} new records exceed the 2048 ESPFE limit (uses a load-order slot)")}.\n\n");
        rd.Append("DONOR MODS in this plugin (faces per race):\n" + DonorModsText(donorMods) + "\n");
        rd.Append("MASTERS:\n" + string.Join("\n", masters.Select(m => "  - " + m)) + "\n");
        if (extraMasters.Count > 0)
            rd.Append("  NOTE — records of a kind this build does not copy (spells, factions, outfits, packages...) are still referenced\n"
                    + "  from these plugins, so they must stay enabled: " + string.Join(", ", extraMasters) + "\n"
                    + "  (" + string.Join("; ", unresolved.Where(u => extraMasters.Contains(u.Key)).Select(u => $"{u.Value} links -> {u.Key}")) + ")\n");
        else rd.Append("  No source mod is needed: this plugin depends on the base game only.\n");
        if (danglingDropped > 0) rd.Append($"  {danglingDropped} dangling link(s) dropped (records a source's master never defines — broken in the source; the game ignores them).\n");
        if (missingMasters.Count > 0) rd.Append("WARNING — masters not found on this PC (their records could not be copied): " + string.Join(", ", missingMasters) + "\n");
        rd.Append("\nUSE: add this build as a source in Mod Creator (it appears under \"Library builds\") with 'Build as SkyPatcher file' —\n"
                + $"the config references copyVisualStyle={outName}|<donor>. Keep the build installed and enabled wherever those configs run.\n"
                + $"Map of donor <-> original face: {stem}_map.yaml / .csv (next to this file).\n");
        rd.Append("Textures: body skin at vanilla paths is NOT baked (your skin mod supplies it); a face's brows/eyes/hair from other\n"
                + "packs were baked when found in the mod folders searched. Run 'Verify assets' on this folder to list what's still missing.\n");
        if (bsaNotes.Count > 0)
            rd.Append("\nARCHIVES: assets are packed into this plugin's own BSA(s), which the game loads automatically for a plugin of\n"
                    + "this name (no MO2 Archives-tab step). A loose file with the same path would override them."
                    + (dummyPlugins.Count > 0 ? "\n  A group over the 4 GiB per-archive limit was split: the extra archives hang off empty ESL-flagged dummy\n  plugins named below — ENABLE THEM TOO (they use no load-order slot)." : "")
                    + "\n  " + string.Join("\n  ", bsaNotes) + "\n");
        rd.Append(QaText(skipped, faceGenFrom));
        File.WriteAllText(Path.Combine(outFolder, readmeName), rd.ToString());

        return new Result(donors.Count, newRecs, outMod.IsSmallMaster, copiedByType, masters, extraMasters, unresolved,
                          missingMasters, bakedAssets, skipped, danglingDropped, facesPerPlugin, facesPerRace, esp, bsaNotes,
                          faceIds, recordIds, donorMods, faceGenFrom, dummies);
    }

    // "  NorthernWomen.esp — 47 faces (32 Nord, 15 Imperial)"
    public static string DonorModsText(Dictionary<string, Dictionary<string, Library.RaceCount>> donorMods) =>
        string.Join("\n", donorMods.OrderByDescending(m => m.Value.Sum(r => r.Value.F + r.Value.M)).ThenBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
            .Select(m => $"  {m.Key} — {m.Value.Sum(r => r.Value.F + r.Value.M)} faces ("
                        + string.Join(", ", m.Value.OrderByDescending(r => r.Value.F + r.Value.M).Select(r => $"{r.Value.F + r.Value.M} {(r.Key.Contains(':') ? "custom race " + r.Key : r.Key.Replace("Race", ""))}{(r.Value.M > 0 ? $" [{r.Value.M}M]" : "")}"))
                        + ")"));

    public static string QaText(List<Skipped> skipped, Dictionary<string, string> faceGenFrom)
    {
        var sb = new System.Text.StringBuilder();
        if (faceGenFrom.Count > 0)
            sb.Append($"\nQA — {faceGenFrom.Count} face(s) took their FaceGen from ANOTHER mod folder (the source plugin ships none; typical for a patch\n"
                    + "plugin whose faces are unchanged). Built normally; listed so you can confirm the face you get is the one you picked:\n"
                    + string.Join("\n", faceGenFrom.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k => $"  {k.Key}  <-  {k.Value}")) + "\n");
        if (skipped.Count > 0)
            sb.Append($"\nQA — {skipped.Count} face(s) NOT BUILT: no FaceGen found anywhere, so they would render as the dark-face bug. Check the\n"
                    + "source mod (missing/unpacked FaceGen, a BSA not installed, a replacer that expects another mod's files) and rebuild:\n"
                    + string.Join("\n", skipped.Select(s => $"  {s.Source}  {s.Npc} \"{s.Name}\"  ({s.Key}) — {s.Reason}")) + "\n");
        return sb.ToString();
    }
}
