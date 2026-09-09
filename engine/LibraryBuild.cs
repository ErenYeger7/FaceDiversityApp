using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Binary.Parameters;

// `build-library` — ONE self-contained face-library plugin from the curated Library:
//
//   build-library --game <Skyrim.esm> --out <dir> --name FDA_Library_<yymmdd>.esp --source <esp>...
//                 [--include <file of face ids>] [--asset-dirs <file of mod folders>] [--map <yaml>]
//
// Every included face becomes a NEW, never-placed donor NPC carrying that source's face fields, with the
// source's FaceGen re-keyed under this plugin. Then EVERYTHING the donors reach in the sources and in the
// sources' own (non-vanilla) masters — head parts, texture sets, colors, form lists, custom RACES and their
// skins (ARMO/ARMA), voices, classes, keywords, body-part/movement data — is deep-copied in and re-linked,
// and the meshes/textures those records name are baked in. Result: a plugin whose only masters are the
// base game, installable in any MO2 instance on its own, that SkyPatcher configs reference by
// copyVisualStyle=<this plugin>|<donor>. The map (yaml + csv) records donor <-> original face.
static class LibraryBuild
{
    static readonly HashSet<string> Base = Classify.BaseMasters;

    public static int Run(string[] args)
    {
        string? game = null, outFolder = null, outName = null, includePath = null, assetDirsPath = null, mapPath = null;
        var sources = new List<string>();
        for (int i = 1; i < args.Length; i++)
            switch (args[i])
            {
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

        var include = includePath is not null && File.Exists(includePath)
            ? new HashSet<string>(File.ReadAllLines(includePath).Select(l => l.Trim()).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase)
            : null;
        var assetDirs = assetDirsPath is not null && File.Exists(assetDirsPath)
            ? File.ReadAllLines(assetDirsPath).Select(l => l.Trim()).Where(l => l.Length > 0 && Directory.Exists(l)).ToList()
            : new List<string>();
        var resolver = assetDirs.Count > 0
            ? new LoadOrderAssets(assetDirs.Select(p => (Path.GetFileName(p.TrimEnd('/', '\\')), p)).ToList()) : null;

        var esm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(game), GameCfg.Release);
        var raceName = esm.Races.ToDictionary(r => r.FormKey, r => r.EditorID ?? "");
        var esmNpcRace = esm.Npcs.ToDictionary(n => n.FormKey, n => n.Race.FormKey);
        string RaceOf(FormKey fk) => raceName.TryGetValue(fk, out var s) && s != "" ? s : fk.ToString();
        bool Fem(INpcGetter n) => n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);

        var outMod = new SkyrimMod(ModKey.FromNameAndExtension(outName), GameCfg.Release) { IsSmallMaster = false };
        var assets = new Dictionary<string, SourceAssets>(StringComparer.OrdinalIgnoreCase);
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int bakedAssets = 0;

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
            if (string.IsNullOrWhiteSpace(model)) return;
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
            if (string.IsNullOrWhiteSpace(tex)) return;
            var rel = tex.Replace('/', '\\').TrimStart('\\');
            if (!rel.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase)) rel = "textures\\" + rel;
            Bake(folder, rel);
        }

        // ---- donors: one never-placed NPC per included face, the source's exact face fields.
        var map = new LibraryMap { Plugin = outName, Built = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        var donors = new List<(Npc npc, INpcGetter src, string sp, string folder)>();
        var missingFaceGen = new List<string>();
        int seq = 0;
        foreach (var sp in sources)
        {
            var name = Path.GetFileName(sp);
            if (!loaded.TryGetValue(name, out var ld)) continue;
            var folder = Path.GetDirectoryName(Path.GetFullPath(sp))!;
            foreach (var s in ld.mod.Npcs)
            {
                var id = Faces.FaceId(sp, s.FormKey);
                if (include is not null && !include.Contains(id)) continue;
                if (LoadOrderScan.IsPlayerOrPreset(s)) continue;
                var fk = outMod.GetNextFormKey();
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
                var fg = Generate.CopyFaceGen(assets[folder], s.FormKey.ModKey.FileName, s.FormKey.ID, fk.ID, outFolder, outName);
                if (fg is null) missingFaceGen.Add($"{d.EditorID} <= {s.EditorID} ({name})");
                else foreach (var tex in Generate.DdsPathsInNif(fg)) Bake(folder, tex);
                donors.Add((d, s, sp, folder));
            }
        }
        if (donors.Count == 0) { Console.Error.WriteLine("no faces to build (empty selection?)"); return 1; }

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
        foreach (var fk in toCopy)
        {
            var (src, folder) = rec[fk];
            var nfk = outMod.GetNextFormKey();
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
            Console.WriteLine($"Dropped {danglingDropped} dangling link(s) to records that do not exist in their plugin (broken in the source; the game ignores them too).");
            // those links are no longer 'unresolved'
            unresolved.Clear();
            foreach (var r in outMod.EnumerateMajorRecords()) foreach (var l in r.EnumerateFormLinks())
                if (!l.FormKey.IsNull && !Base.Contains(l.FormKey.ModKey.FileName) && l.FormKey.ModKey != outMod.ModKey)
                    unresolved[l.FormKey.ModKey.FileName] = unresolved.GetValueOrDefault(l.FormKey.ModKey.FileName) + 1;
        }

        // ---- write (ESL when the record count fits), re-read masters, map, README
        int newRecs = outMod.EnumerateMajorRecords().Count(r => r.FormKey.ModKey == outMod.ModKey);
        outMod.IsSmallMaster = newRecs <= 2048;
        Directory.CreateDirectory(outFolder);
        var esp = Path.Combine(outFolder, outName);
        outMod.WriteToBinary(esp, new BinaryWriteParameters { MastersListContent = MastersListContentOption.Iterate });
        foreach (var l in loaded.Values) if (l.mod is IDisposable dd) { try { dd.Dispose(); } catch { } }
        List<string> masters;
        using (var mm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(esp), GameCfg.Release))
            masters = mm.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
        var extraMasters = masters.Where(m => !Base.Contains(m)).ToList();
        // a master we couldn't open only matters if something actually still points into it
        missingMasters = missingMasters.Where(m => unresolved.ContainsKey(m)).ToList();

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

        var rd = new System.Text.StringBuilder();
        rd.Append($"{outName} — FaceDiversityApp LIBRARY BUILD (personal use only; do not redistribute)\n\n");
        rd.Append($"{donors.Count} donor NPCs (never placed in the world), one per curated face, each carrying that face's head parts,\n");
        rd.Append("tints, morphs, weight and skin with the source's FaceGen re-keyed under this plugin. Everything they reference\n");
        rd.Append("from the source mods was copied in: " + string.Join(", ", copiedByType.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {k.Key}")) + $"; {bakedAssets} meshes/textures baked.\n");
        rd.Append($"Plugin type: {(outMod.IsSmallMaster ? $"ESL-flagged (ESPFE), {newRecs} new records" : $"FULL ESP — {newRecs} new records exceed the 2048 ESPFE limit (uses a load-order slot)")}.\n\n");
        rd.Append("MASTERS:\n" + string.Join("\n", masters.Select(m => "  - " + m)) + "\n");
        if (extraMasters.Count > 0)
            rd.Append("  NOTE — records of a kind this build does not copy (spells, factions, outfits, packages...) are still referenced\n"
                    + "  from these plugins, so they must stay enabled: " + string.Join(", ", extraMasters) + "\n"
                    + "  (" + string.Join("; ", unresolved.Where(u => extraMasters.Contains(u.Key)).Select(u => $"{u.Value} links -> {u.Key}")) + ")\n");
        else rd.Append("  No source mod is needed: this plugin depends on the base game only.\n");
        if (missingMasters.Count > 0) rd.Append("WARNING — masters not found on this PC (their records could not be copied): " + string.Join(", ", missingMasters) + "\n");
        rd.Append("\nUSE: add this build as a source in Mod Creator (it appears under \"Library builds\") with 'Build as SkyPatcher file' —\n"
                + $"the config references copyVisualStyle={outName}|<donor>. Keep the build installed and enabled wherever those configs run.\n"
                + $"Map of donor <-> original face: {stem}_map.yaml / .csv (next to this file).\n");
        rd.Append("Textures: body skin at vanilla paths is NOT baked (your skin mod supplies it); a face's brows/eyes/hair from other\n"
                + "packs were baked when found in the mod folders searched. Run 'Verify assets' on this folder to list what's still missing.\n");
        if (missingFaceGen.Count > 0) rd.Append($"\nWARNING missing FaceGen ({missingFaceGen.Count}):\n  " + string.Join("\n  ", missingFaceGen) + "\n");
        File.WriteAllText(Path.Combine(outFolder, "README.txt"), rd.ToString());

        Console.WriteLine($"Library build {outName}: {donors.Count} donor faces from {sources.Count} source(s); copied " +
                          string.Join(", ", copiedByType.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {k.Key}")) + $"; {bakedAssets} assets baked.");
        Console.WriteLine($"Output is {(outMod.IsSmallMaster ? "ESL-flagged (ESPFE)" : "a FULL ESP")} — {newRecs} new records.");
        Console.WriteLine("Masters: " + string.Join(", ", masters) + (extraMasters.Count == 0 ? "  (base game only — fully self-contained)" : "  <- NOTE non-vanilla masters remain, see README"));
        if (missingMasters.Count > 0) Console.WriteLine("WARNING masters not found: " + string.Join(", ", missingMasters));
        if (missingFaceGen.Count > 0) Console.WriteLine($"missingFaceGen={missingFaceGen.Count}: " + string.Join("; ", missingFaceGen.Take(6)));
        Console.WriteLine($"Wrote {esp} + {stem}_map.yaml/.csv" + (mapPath is not null ? $" (+ {mapPath})" : ""));
        return 0;
    }
}
