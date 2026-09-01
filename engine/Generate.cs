using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using YamlDotNet.Serialization;
using System.Text.RegularExpressions;

// generate --game <Skyrim.esm> --category bandit
//          --source <plugin>   (auto-classified: deep-copy vs keep)
//          --keep <plugin>     (force keep/master)   --disable <plugin> (force deep-copy)
//          --voice-map <voice_map.yaml> --out <modFolder> --name <Plugin.esp> [--no-feminize]
//
// Each source is classified (Classify.cs) to decide the approach:
//   DEEP-COPY (disable): duplicate the source's own HDPT into the output (remap), copy its loose assets,
//                        and re-key its FaceGen to each target. Output stays standalone of that source.
//   KEEP (master):       reference the source's head parts as-is (it stays enabled and provides them +
//                        its skin/assets), adopt the face's race (custom breeds), re-key FaceGen only.
// Distributes female faces across the category's own-traits targets, matched by race; feminizes males.
static class Generate
{
    record Face(INpcGetter Npc, string Folder, bool Disable, bool Female);
    record VoiceRemap(Dictionary<string, List<string>> Direct, Dictionary<string, List<string>> Fallback);
    static string PoolKey(string race, bool female) => race + (female ? "|F" : "|M");

    public static int Run(string[] args)
    {
        string? game = null, category = "bandit", voiceMapPath = null, outFolder = null, outName = null;
        string? includePath = null, configPath = null, loadOrderPath = null, feminineNamesPath = null, assetDirsPath = null;
        var srcSpecs = new List<(string path, string? forced)>(); bool feminize = true; bool boost = false;
        bool sexplague = false; string? sexplaguePct = null; bool feminineNames = false; bool bakeTextures = false;
        for (int i = 1; i < args.Length; i++)
            switch (args[i])
            {
                case "--game": game = args[++i]; break;
                case "--category": category = args[++i]; break;
                case "--source": srcSpecs.Add((args[++i], null)); break;
                case "--keep": srcSpecs.Add((args[++i], "keep")); break;
                case "--disable": srcSpecs.Add((args[++i], "disable")); break;       // esp off, keep mod for assets
                case "--standalone": srcSpecs.Add((args[++i], "standalone")); break; // esp off + bake assets in
                case "--voice-map": voiceMapPath = args[++i]; break;
                case "--out": outFolder = args[++i]; break;
                case "--name": outName = args[++i]; break;
                case "--no-feminize": feminize = false; break;
                case "--include": includePath = args[++i]; break; // curated face ids (one per line); else use all
                case "--config": configPath = args[++i]; break;   // categories.yaml (target prefixes)
                case "--loadorder": loadOrderPath = args[++i]; break; // scan categories: file of active plugin paths, in load order
                case "--boost": boost = true; break;              // place EXTRA faces as new NPCs via SkyPatcher LL injection
                case "--sexplague": sexplague = true; break;      // tag feminized males with SexPlague factions + ability
                case "--sexplague-pct": sexplaguePct = args[++i]; break; // "60,30,10" tier split (overrides yaml)
                case "--feminine-names": feminineNames = true; feminineNamesPath = args[++i]; break; // apply feminine fullName via SkyPatcher
                case "--bake-textures": bakeTextures = true; break;   // bake cross-mod face textures (brows/eyes/etc.) for self-contained output
                case "--asset-dirs": assetDirsPath = args[++i]; break; // file of enabled mod folders (priority) to resolve textures from
            }
        if (game is null || outFolder is null || outName is null || srcSpecs.Count == 0)
        { Console.Error.WriteLine("need --game --out --name and at least one --source/--keep/--disable"); return 1; }

        Categories.Load(configPath);
        // Curation: if an include file is given, only faces whose id (Faces.FaceId) is listed are pooled.
        var include = includePath is not null && File.Exists(includePath)
            ? new HashSet<string>(File.ReadAllLines(includePath).Select(l => l.Trim()).Where(l => l.Length > 0),
                                  StringComparer.OrdinalIgnoreCase)
            : null;

        var esm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(game), GameCfg.Release);
        var raceName = esm.Races.ToDictionary(r => r.FormKey, r => r.EditorID ?? "");
        var esmNpcRace = esm.Npcs.ToDictionary(n => n.FormKey, n => n.Race.FormKey); // for original-race pooling
        var voiceName = esm.VoiceTypes.ToDictionary(v => v.FormKey, v => v.EditorID ?? "");
        var voiceByName = esm.VoiceTypes.Where(v => v.EditorID != null).ToDictionary(v => v.EditorID!, v => v.FormKey);
        var voiceMap = LoadVoiceMap(voiceMapPath);

        bool OwnTraits(INpcGetter n) => !(n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !n.Template.IsNull);
        bool Fem(INpcGetter n) => n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        string RaceOf(FormKey fk) => raceName.TryGetValue(fk, out var s) && s != "" ? s : fk.ToString();

        // Safety net: never target child races (feminizing/refacing a child is a hard no) — a bare "WE"
        // prefix, for one, would otherwise sweep in NordRaceChild. Belt-and-braces even though the
        // shipped prefixes are scoped to adults.
        bool AdultHumanoid(INpcGetter n) => !RaceOf(n.Race.FormKey).Contains("Child", StringComparison.OrdinalIgnoreCase);

        // Target set. Two shapes:
        //  (a) prefix categories (bandit, forsworn, ...): own-traits Skyrim.esm NPCs by EditorID prefix.
        //  (b) scan categories (all_males): winning UNIQUE named-male overrides across the whole active
        //      load order — many defining plugins. Kept alive (loScan) through the override loop because
        //      the target getters lazily read from the memory-mapped source; disposed at the very end.
        bool scan = Categories.IsScan(category);
        LoadOrder<IModListingGetter<ISkyrimModGetter>>? loScan = null, loBase = null;
        List<INpcGetter> targets;
        if (scan)
        {
            if (loadOrderPath is null || !File.Exists(loadOrderPath))
            { Console.Error.WriteLine($"category '{category}' is a load-order scan but no --loadorder file was given"); return 1; }
            var paths = File.ReadAllLines(loadOrderPath).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            loScan = LoadOrderScan.Build(paths);
            targets = LoadOrderScan.UniqueNamedMales(loScan, RaceOf);
            Console.WriteLine($"Load-order scan: {paths.Count} plugins → {targets.Count} unique named males.");
        }
        else
        {
            // Scan the vanilla base masters (Skyrim + DLC) so DLC-defined groups (Solstheim reavers/
            // cultists, Dawnguard hunters/Volkihar) are reachable. Winning overrides = what the game uses.
            // Kept alive through the override loop AND Boost (target getters read lazily); disposed after.
            var prefix = Categories.TargetPrefix(category);
            loBase = LoadOrderScan.Build(LoadOrderScan.BaseMasterPaths(game));
            targets = loBase.PriorityOrder.Npc().WinningOverrides()
                .Where(n => Categories.MatchesPrefix(n.EditorID ?? "", prefix) && OwnTraits(n) && AdultHumanoid(n))
                .ToList();
        }

        // Build as a full ESP (new FormKeys still allocate from 0x800). The ESL/ESPFE flag is decided once
        // at the very end from the ACTUAL new-record count — an ESPFE holds only 0x800–0xFFF (2048) new
        // records, and an over-limit source must NOT force an invalid ESL that then fails to write.
        var outMod = new SkyrimMod(ModKey.FromNameAndExtension(outName), GameCfg.Release) { IsSmallMaster = false };
        var remap = new Dictionary<FormKey, FormKey>();                 // disable-source HDPT -> our copies
        var pool = new Dictionary<string, List<Face>>(StringComparer.OrdinalIgnoreCase); // race -> faces
        var unpackFolders = new List<string>();  // only --standalone sources get their assets baked in
        var hdptModelByKey = new Dictionary<FormKey, (string folder, string rel)>(); // source HDPT -> its mesh
        var assets = new Dictionary<string, SourceAssets>(StringComparer.OrdinalIgnoreCase); // folder -> loose+BSA resolver
        var srcModes = new List<(string plugin, string mode, string why)>();

        foreach (var (sp, forced) in srcSpecs)
        {
            var cls = Classify.Inspect(sp);
            var mode = forced ?? cls.Mode;
            srcModes.Add((cls.Plugin, mode, forced != null ? "forced" : cls.Why));
            bool disable = mode == "disable" || mode == "standalone"; // esp off -> deep-copy records
            bool unpack = mode == "standalone";                       // also bake assets in (fully removable)

            var sm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(sp), GameCfg.Release);
            var folder = Path.GetDirectoryName(Path.GetFullPath(sp))!;
            if (!assets.ContainsKey(folder)) assets[folder] = new SourceAssets(folder);

            if (disable)
            {
                if (unpack) unpackFolders.Add(folder);
                foreach (var h in sm.HeadParts)   // deep-copy the source's HDPT records so links resolve with its esp off
                {
                    var fk = outMod.GetNextFormKey();
                    outMod.HeadParts.Add((HeadPart)h.Duplicate(fk));
                    remap[h.FormKey] = fk;
                    if (h.Model?.File.ToString() is { Length: > 0 } mf) hdptModelByKey[h.FormKey] = (folder, mf);
                }
            }
            foreach (var n in sm.Npcs)
            {
                if (include is not null && !include.Contains(Faces.FaceId(sp, n.FormKey))) continue; // curated out
                // Pool by the ORIGINAL vanilla race of the overridden NPC (so ja-Kha'jay faces that
                // changed a vanilla Khajiit to a custom breed still reach KhajiitRace targets — the
                // breed race is then adopted at assignment). New (non-vanilla) records pool by their race.
                // Keyed by race+sex: female targets (and feminized males) draw female faces; if feminize is
                // off, male targets draw male faces.
                var r = Classify.BaseMasters.Contains(n.FormKey.ModKey.FileName) && esmNpcRace.TryGetValue(n.FormKey, out var vr)
                    ? RaceOf(vr) : RaceOf(n.Race.FormKey);
                var key = PoolKey(r, Fem(n));
                if (!pool.TryGetValue(key, out var l)) { l = new(); pool[key] = l; }
                l.Add(new Face(n, folder, disable, Fem(n)));
            }
        }
        outMod.RemapLinks(remap);

        var cursor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var report = new Dictionary<string, (int assigned, int skipped, int faces)>(StringComparer.OrdinalIgnoreCase);
        int femCount = 0, unmappedVoice = 0;
        var feminizedNpcs = new List<(string plugin, uint id, string name)>(); // males we flipped female (SexPlague + feminine names)
        var missingFaceGen = new List<string>();
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedHdpt = new HashSet<FormKey>(); // source HDPT actually worn by an assigned face

        // Optional cross-mod texture baking: resolve textures a face references from ANY enabled mod
        // (not just the source's own folder), so shared brow/eye packs get baked and the output is
        // self-contained. Built from the mod folders Serve resolves (mods only — never stock, so vanilla
        // textures are left to load normally). Skin/body textures are skipped (huge + shared; baking one
        // as a loose file would override that skin globally — the source skin overhaul provides those).
        LoadOrderAssets? bakeResolver = null; int bakedCrossMod = 0;
        if (bakeTextures && assetDirsPath is not null && File.Exists(assetDirsPath))
        {
            var dirs = File.ReadAllLines(assetDirsPath).Select(l => l.Trim()).Where(l => l.Length > 0)
                           .Select(p => (Path.GetFileName(p.TrimEnd('/', '\\')), p)).ToList();
            bakeResolver = new LoadOrderAssets(dirs);
        }
        static bool IsSharedSkin(string rel)
        {
            var p = rel.ToLowerInvariant().Replace('/', '\\');
            return p.Contains("\\character\\female\\") || p.Contains("\\character\\male\\");
        }

        // Bake in only assets the harvested faces actually reference (pulled loose-or-BSA), skipping
        // vanilla (not in source) and facegendata (facetint is re-keyed separately). With bake-textures
        // on, fall back to the load-order resolver so cross-mod face textures are baked too.
        void ExtractAsset(SourceAssets src, string rel)
        {
            rel = rel.Replace('/', '\\').TrimStart('\\');
            if (rel.Contains("facegendata\\", StringComparison.OrdinalIgnoreCase)) return;
            if (!extracted.Add(rel)) return;
            var data = src.Get(rel);
            if (data == null && bakeResolver is not null && !IsSharedSkin(rel))
            { data = bakeResolver.ResolveBytes(rel); if (data != null) bakedCrossMod++; }
            if (data != null) WriteBytes(outFolder, rel, data);
        }

        // Transplant a source face's fields onto an output NPC (shared by the override + boost paths).
        void ApplyFaceFields(Npc npc, Face face)
        {
            var s = face.Npc;
            npc.Race.SetTo(s.Race.FormKey);           // adopt source race (custom breeds for khajiit; no-op otherwise)
            npc.HeadParts.Clear();
            foreach (var hp in s.HeadParts)
            {
                if (face.Disable && remap.TryGetValue(hp.FormKey, out var nf))
                { npc.HeadParts.Add((IFormLinkGetter<IHeadPartGetter>)new FormLink<IHeadPartGetter>(nf)); usedHdpt.Add(hp.FormKey); }
                else npc.HeadParts.Add(hp);           // keep: reference source as-is
            }
            npc.TintLayers.Clear();
            foreach (var x in s.TintLayers) npc.TintLayers.Add(x.DeepCopy());
            npc.HairColor.SetTo(s.HairColor.FormKey);
            npc.FaceMorph = s.FaceMorph?.DeepCopy();
            npc.FaceParts = s.FaceParts?.DeepCopy();
            npc.Height = s.Height; npc.Weight = s.Weight;
            npc.TextureLighting = s.TextureLighting;   // QNAM: must match the FaceGen or head/body seam
        }
        // Map a male voice to a female one: a direct voice_map entry wins; otherwise the target's race
        // fallback pool (handles MaleUnique*/custom voices). Candidates are validated against the load
        // order's VTCK; the pick is deterministic per NPC (hash of FormID) so it's stable on regenerate.
        bool TryFemVoice(FormKey maleVoice, string raceName, uint id, out FormKey fem)
        {
            fem = default;
            var ov = voiceName.GetValueOrDefault(maleVoice, "");
            var candidates = voiceMap.Direct.TryGetValue(ov, out var dc) ? dc
                           : voiceMap.Fallback.TryGetValue(raceName, out var fc) ? fc : null;
            if (candidates is null) return false;
            var valid = candidates.Where(voiceByName.ContainsKey).ToList();
            if (valid.Count == 0) return false;
            var pick = valid[(int)(unchecked(id * 2654435761u) % (uint)valid.Count)];
            fem = voiceByName[pick];
            return true;
        }
        static string ShortRace(string r) => r.Replace("Race", "").Replace("Vampire", "V");

        foreach (var t in targets)
        {
            var race = RaceOf(t.Race.FormKey);
            report.TryGetValue(race, out var rc);
            // A female target always needs a female face; a male target needs a female face when we're
            // feminizing it, otherwise a male face. Draw from the matching race+sex pool.
            bool needFemale = Fem(t) || feminize;
            var key = PoolKey(race, needFemale);
            if (!pool.TryGetValue(key, out var faces) || faces.Count == 0)
            { report[race] = (rc.assigned, rc.skipped + 1, 0); continue; }
            int idx = cursor.GetValueOrDefault(key);
            var face = faces[idx % faces.Count]; cursor[key] = idx + 1;
            report[race] = (rc.assigned + 1, rc.skipped, faces.Count);

            var npc = outMod.Npcs.GetOrAddAsOverride(t);
            var s = face.Npc;
            ApplyFaceFields(npc, face);

            if (feminize && !Fem(t))
            {
                npc.Configuration.Flags |= NpcConfiguration.Flag.Female;
                if (TryFemVoice(t.Voice.FormKey, race, t.FormKey.ID, out var fvk)) npc.Voice.SetTo(fvk); else unmappedVoice++;
                femCount++;
                feminizedNpcs.Add((t.FormKey.ModKey.FileName, t.FormKey.ID, t.Name?.String ?? ""));
            }

            // FaceGen lives under the folder named for the record's origin plugin — the game looks under
            // the plugin that DEFINES the overridden NPC (its FormID origin). For prefix categories that's
            // always Skyrim.esm; for a load-order scan the target can originate in any plugin. Re-key to
            // the TARGET FormID under the target's own origin folder.
            var fgNif = CopyFaceGen(assets[face.Folder], s.FormKey.ModKey.FileName, s.FormKey.ID, t.FormKey.ID, outFolder, t.FormKey.ModKey.FileName);
            if (fgNif == null) { missingFaceGen.Add($"{t.EditorID} <= {s.EditorID} ({s.FormKey.ModKey.FileName})"); continue; }
            // Bake in the textures THIS face's FaceGen references (hair/eyes/brows), so it renders with
            // the source disabled and no BSA force-load — the fix for CW-sourced purple eyes/hair.
            if (face.Disable || bakeTextures) foreach (var tex in DdsPathsInNif(fgNif)) ExtractAsset(assets[face.Folder], tex);
        }
        // Scan targets are fully read now — release the load order's ~90 memory-mapped file handles.
        if (loScan is not null) { LoadOrderScan.DisposeLoadOrder(loScan); loScan = null; }

        // ---- Leveled-List Boost: place EXTRA faces (beyond vanilla slots) as NEW cloned NPCs, and inject
        // them into the vanilla leveled lists via a shipped SkyPatcher config. Each clone copies a real
        // category NPC (AI / factions / level / combat style), then swaps in the face + baked FaceGen.
        var skyLines = new List<string>();
        var boostReport = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var boostNoList = new List<string>();
        int boostAdded = 0;
        // Boost injects EXTRA clones into vanilla leveled lists — meaningless for a load-order scan of
        // placed unique actors (no per-NPC "slot count"). Silently a no-op there; the UI hides it too.
        if (boost && scan) Console.WriteLine("\nLeveled-List Boost: not applicable to a load-order scan category — skipped.");
        if (boost && !scan)
        {
            // which LeveledNpc lists each vanilla NPC sits in, with the entry's level+count
            var lvlnOfNpc = new Dictionary<FormKey, List<(FormKey ll, short lvl, short cnt)>>();
            foreach (var ll in esm.LeveledNpcs)
            {
                if (ll.Entries is null) continue;
                foreach (var e in ll.Entries)
                    if (e.Data is not null)
                    {
                        var rk = e.Data.Reference.FormKey;
                        if (!lvlnOfNpc.TryGetValue(rk, out var lst)) lvlnOfNpc[rk] = lst = new();
                        lst.Add((ll.FormKey, e.Data.Level, e.Data.Count));
                    }
            }
            var targetsByRace = targets.GroupBy(t => RaceOf(t.Race.FormKey))
                                       .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            // donors of a race that actually appear in a leveled list (so the clone has somewhere to spawn);
            // for a female clone prefer a female donor (falls back to a feminized male), male clone needs male.
            List<INpcGetter> DonorsFor(string race, bool female)
            {
                if (!targetsByRace.TryGetValue(race, out var ts)) return new();
                var inLists = ts.Where(d => lvlnOfNpc.ContainsKey(d.FormKey)).ToList();
                return female ? inLists.OrderByDescending(Fem).ToList() : inLists.Where(d => !Fem(d)).ToList();
            }

            int seq = 0;
            foreach (var kv in cursor.ToList())
            {
                if (!pool.TryGetValue(kv.Key, out var faces)) continue;
                int filled = kv.Value, extra = faces.Count - filled;
                if (extra <= 0) continue;                                   // no unplaced faces for this race/sex
                var parts = kv.Key.Split('|'); string race = parts[0]; bool female = parts[1] == "F";
                var donors = DonorsFor(race, female);
                if (donors.Count == 0) { boostNoList.Add($"{race} {(female ? "F" : "M")} (+{extra} unplaced, no leveled list)"); continue; }
                for (int j = 0; j < extra; j++)
                {
                    var face = faces[filled + j];
                    var donor = donors[j % donors.Count];
                    var fk = outMod.GetNextFormKey();
                    var clone = (Npc)donor.Duplicate(fk);
                    outMod.Npcs.Add(clone);
                    ApplyFaceFields(clone, face);
                    if (female && !Fem(donor))
                    {
                        clone.Configuration.Flags |= NpcConfiguration.Flag.Female;
                        if (TryFemVoice(donor.Voice.FormKey, race, fk.ID, out var fvk)) clone.Voice.SetTo(fvk); else unmappedVoice++;
                        feminizedNpcs.Add((outName, fk.ID, donor.Name?.String ?? ""));
                    }
                    clone.EditorID = $"FDA{ShortRace(race)}{(female ? "F" : "M")}{seq++:D3}";
                    var fg = CopyFaceGen(assets[face.Folder], face.Npc.FormKey.ModKey.FileName, face.Npc.FormKey.ID, fk.ID, outFolder, outName);
                    if (fg == null) missingFaceGen.Add($"BOOST {clone.EditorID} <= {face.Npc.EditorID}");
                    else if (face.Disable || bakeTextures) foreach (var tex in DdsPathsInNif(fg)) ExtractAsset(assets[face.Folder], tex);
                    foreach (var (ll, lvl, cnt) in lvlnOfNpc[donor.FormKey])
                        skyLines.Add($"filterByLLs=Skyrim.esm|{ll.ID:X}:addOnceToLLs={outName}|{fk.ID:X}~{lvl}~{cnt}");
                    boostAdded++; boostReport[race] = boostReport.GetValueOrDefault(race) + 1;
                }
            }
        }

        // Base-master targets are fully read now (override loop + Boost done) — release those handles.
        if (loBase is not null) { LoadOrderScan.DisposeLoadOrder(loBase); loBase = null; }

        // Bake only the HDPT meshes actually worn (+ their textures) — not every hair the source shipped.
        foreach (var key in usedHdpt)
            if (hdptModelByKey.TryGetValue(key, out var hm))
            {
                ExtractAsset(assets[hm.folder], hm.rel);
                var nif = assets[hm.folder].Get(hm.rel);
                if (nif != null) foreach (var tex in DdsPathsInNif(nif)) ExtractAsset(assets[hm.folder], tex);
            }

        // Only --standalone sources bake their assets in (fully removable). Plain --disable leaves assets
        // in the still-enabled source MOD (esp off, mod on): loose files auto-load; a BSA needs force-loading
        // in MO2's Archives tab. This keeps output tiny (records + FaceGen only) instead of unpacking GBs.
        foreach (var folder in unpackFolders.Distinct())
            foreach (var (rel, data) in assets[folder].Unpackable())
            {
                var d = Path.Combine(outFolder, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                File.WriteAllBytes(d, data);
            }

        // ESL/ESPFE feasibility (ALL categories). Only records THIS plugin INTRODUCES count against the
        // 2048 limit — overrides keep their master's FormID. Flag ESL when the new records fit; otherwise
        // ship a full ESP (uses a load-order slot but always writes). This is what lets a big multi-source
        // build succeed instead of forcing an invalid ESL that throws on write.
        int newRecs = outMod.EnumerateMajorRecords().Count(r => r.FormKey.ModKey == outMod.ModKey);
        outMod.IsSmallMaster = newRecs <= 2048;
        bool droppedEsl = !outMod.IsSmallMaster;

        Directory.CreateDirectory(outFolder);
        var esp = Path.Combine(outFolder, outName);
        outMod.WriteToBinary(esp, new BinaryWriteParameters { MastersListContent = MastersListContentOption.Iterate });
        Console.WriteLine($"Output is {(droppedEsl ? "a FULL ESP" : "ESL-flagged (ESPFE)")} — {newRecs} new records" +
                          (droppedEsl ? $" exceed the 2048 ESPFE limit (uses a load-order slot)." : " (fits the ESPFE limit)."));

        int totalAssigned = report.Values.Sum(v => v.assigned), totalSkipped = report.Values.Sum(v => v.skipped);
        Console.WriteLine("Source classification:");
        foreach (var (plugin, mode, why) in srcModes) Console.WriteLine($"  [{mode.ToUpperInvariant(),7}] {plugin}  — {why}");
        Console.WriteLine($"\nGenerated {outName}: {totalAssigned} overrides ({femCount} feminized), {outMod.HeadParts.Count} HDPT copied.");
        Console.WriteLine($"{"Race",-14}{"assigned",10}{"skipped",9}{"faces",7}");
        foreach (var kv in report.OrderBy(k => k.Key))
            Console.WriteLine($"{kv.Key,-14}{kv.Value.assigned,10}{kv.Value.skipped,9}{kv.Value.faces,7}");
        Console.WriteLine($"TOTAL assigned={totalAssigned} skipped={totalSkipped}  unmappedVoice={unmappedVoice}  missingFaceGen={missingFaceGen.Count}");
        if (bakeTextures) Console.WriteLine($"Bake textures: {bakedCrossMod} cross-mod face textures baked in (skin/body left to their overhaul).");
        if (missingFaceGen.Count > 0) Console.WriteLine("  missing: " + string.Join("; ", missingFaceGen.Take(8)));

        // Leveled-List Boost: write the SkyPatcher config that spawns the new NPCs.
        string? skyIni = null;
        if (boost)
        {
            Console.WriteLine($"\nLeveled-List Boost: +{boostAdded} new NPCs across {skyLines.Count} leveled-list entries.");
            foreach (var kv in boostReport.OrderByDescending(k => k.Value)) Console.WriteLine($"  +{kv.Value,-4} {kv.Key}");
            if (boostNoList.Count > 0)
                Console.WriteLine("  NOT boostable (no vanilla leveled list references these): " + string.Join("; ", boostNoList));
            if (boostAdded == 0)
                Console.WriteLine("  (nothing to boost — no extra usable faces beyond the vanilla slot count for a list-backed race.)");
            if (skyLines.Count > 0)
            {
                var iniDir = Path.Combine(outFolder, "SKSE", "Plugins", "SkyPatcher", "leveledList");
                Directory.CreateDirectory(iniDir);
                skyIni = Path.Combine(iniDir, Path.GetFileNameWithoutExtension(outName) + ".ini");
                File.WriteAllLines(skyIni, skyLines);
                Console.WriteLine($"  wrote SkyPatcher config: {skyIni}");
            }
        }

        // ---- Unified SkyPatcher NPC config: for each feminized male, optionally SexPlague tier/seed/
        // ability ops AND a feminine fullName, emitted as ONE `filterByNpcs=...` line each in
        // <stem>_npc.ini (runtime; no master dep on SexPlagueFactions.esp, no ESP name edit).
        string? npcIni = null; int spTagged = 0; string spSummary = ""; int renamed = 0; bool femNamesOn = false;
        {
            // SexPlague tier op per NPC (deterministic spread across the population, not in blocks)
            var sexOp = new Dictionary<(string plugin, uint id), string>();
            SexPlague.Config? sp = null;
            if (sexplague && feminizedNpcs.Count > 0)
            {
                var spYaml = configPath is not null
                    ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "sexplague.yaml") : null;
                sp = SexPlague.Load(spYaml);
                if (sp is null) Console.WriteLine("\nSexPlague: enabled but sexplague.yaml not found/invalid — skipped.");
                else
                {
                    var pct = sp.Tiers.Select(t => t.Percent).ToList();
                    if (sexplaguePct is not null)
                    {
                        var parts = sexplaguePct.Split(',').Select(x => int.TryParse(x.Trim(), out var v) ? v : 0).ToList();
                        for (int i = 0; i < pct.Count && i < parts.Count; i++) pct[i] = parts[i];
                    }
                    var counts = SexPlague.Distribute(feminizedNpcs.Count, pct);
                    var ordered = feminizedNpcs.OrderBy(x => unchecked(x.id * 2654435761u)).ToList();
                    int idx = 0;
                    for (int ti = 0; ti < sp.Tiers.Count; ti++)
                        for (int c = 0; c < counts[ti] && idx < ordered.Count; c++)
                        {
                            var e = ordered[idx++];
                            sexOp[(e.plugin, e.id)] = $"factionsToAdd={sp.Plugin}|{sp.Tiers[ti].Faction}=0,{sp.Plugin}|{sp.SeedFaction}=0:spellsToAdd={sp.Plugin}|{sp.ControllerSpell}";
                        }
                    spTagged = sexOp.Count;
                    spSummary = string.Join(", ", sp.Tiers.Select((t, i) => $"{t.Label} {counts[i]}"));
                }
            }

            // Feminine fullName op per NPC, matched on the ORIGINAL display name.
            var femNameMap = feminineNames ? FeminineNames.Load(feminineNamesPath) : null;
            femNamesOn = femNameMap is not null;

            // One line per NPC that carries at least one op (SkyPatcher merges multiple : ops per NPC).
            var npcLines = new List<string>();
            foreach (var f in feminizedNpcs)
            {
                var ops = new List<string>();
                if (sexOp.TryGetValue((f.plugin, f.id), out var so)) ops.Add(so);
                if (femNameMap is not null && !string.IsNullOrEmpty(f.name)
                    && femNameMap.TryGetValue(f.name, out var fn)) { ops.Add($"fullName={fn}"); renamed++; }
                if (ops.Count == 0) continue;
                npcLines.Add($"filterByNpcs={f.plugin}|{f.id:X}:" + string.Join(":", ops));
            }

            var iniDir = Path.Combine(outFolder, "SKSE", "Plugins", "SkyPatcher", "npc");
            if (npcLines.Count > 0)
            {
                Directory.CreateDirectory(iniDir);
                npcIni = Path.Combine(iniDir, Path.GetFileNameWithoutExtension(outName) + "_npc.ini");
                File.WriteAllLines(npcIni, npcLines);
            }
            // supersede a stale SexPlague-only ini from an earlier build in the same folder
            var oldSex = Path.Combine(iniDir, Path.GetFileNameWithoutExtension(outName) + "_SexPlague.ini");
            if (File.Exists(oldSex)) { try { File.Delete(oldSex); } catch { } }

            if (sp is not null && spTagged > 0)
                Console.WriteLine($"\nSexPlague: {spTagged} feminized males tagged ({spSummary}) + seed {sp.SeedFaction} + ability {sp.ControllerSpell}.");
            if (femNamesOn)
                Console.WriteLine($"Feminine names: {renamed} of {feminizedNpcs.Count} feminized males renamed ({femNameMap!.Count} map entries).");
            if (npcIni is not null) Console.WriteLine($"  wrote SkyPatcher NPC config: {npcIni}");
        }

        // Masters are computed during write, not back-populated on the in-memory mod — re-read the file.
        // Dispose the overlay immediately (it memory-maps the file): a long-running server would otherwise
        // keep the output ESP locked, blocking a regenerate to the same folder.
        List<string> masters;
        using (var mm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(esp), GameCfg.Release))
            masters = mm.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
        var espOff = srcModes.Where(m => m.mode is "disable" or "standalone").Select(m => m.plugin).ToList();
        File.WriteAllText(Path.Combine(outFolder, "README.txt"),
            $"{outName} — generated by FaceDiversityApp (personal use only; do not redistribute)\n\n" +
            $"Category: {category}.  {totalAssigned} faces ({femCount} feminized males), {totalSkipped} skipped.\n" +
            $"Plugin type: {(droppedEsl ? $"FULL ESP — {newRecs} new records exceed the 2048 ESPFE limit, so this uses a load-order slot" : $"ESL-flagged (ESPFE), {newRecs} new records")}.\n\n" +
            $"KEEP ENABLED — required masters (records + assets; leave the whole mod on):\n" +
                string.Join("\n", masters.Select(m => "  - " + m)) + "\n\n" +
            $"DISABLE THESE ESPs — self-contained: each face's records, FaceGen, and the exact hairs/eyes/\n" +
            $"textures it uses are baked into THIS mod. Turn these .esp off; you can also remove the mods:\n" +
                (espOff.Count > 0 ? string.Join("\n", espOff.Select(p => "  - " + p)) : "  (none)") + "\n\n" +
            $"Per-race:\n" + string.Join("\n", report.OrderBy(k => k.Key).Select(kv => $"  {kv.Key}: assigned {kv.Value.assigned}, skipped {kv.Value.skipped}")) + "\n" +
            (boost && boostAdded > 0
                ? $"\nLEVELED-LIST BOOST: +{boostAdded} extra NPCs injected into vanilla leveled lists via SkyPatcher\n" +
                  $"(SKSE/Plugins/SkyPatcher/leveledList/{Path.GetFileNameWithoutExtension(outName)}.ini).\n" +
                  $"REQUIRES SkyPatcher installed and enabled.\n"
                : "") +
            (npcIni is not null
                ? $"\nSKYPATCHER NPC CONFIG (SKSE/Plugins/SkyPatcher/npc/{Path.GetFileNameWithoutExtension(outName)}_npc.ini) — REQUIRES SkyPatcher enabled:\n" +
                  (spTagged > 0 ? $"  · SexPlague: {spTagged} feminized males tagged ({spSummary}) — REQUIRES SexPlagueFactions.esp.\n" : "") +
                  (femNamesOn ? $"  · Feminine names: {renamed} feminized males given a feminine display name.\n" : "")
                : "") +
            (missingFaceGen.Count > 0 ? $"\nWARNING missing FaceGen ({missingFaceGen.Count}):\n  " + string.Join("\n  ", missingFaceGen) + "\n" : ""));
        Console.WriteLine($"Wrote {esp}");
        return 0;
    }

    static VoiceRemap LoadVoiceMap(string? path)
    {
        var direct = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var fallback = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (path is null || !File.Exists(path)) return new VoiceRemap(direct, fallback);
        var root = new Deserializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        // a value may be a YAML list or a comma-separated scalar — normalise both to a candidate list
        static List<string> ToList(object v) => v switch
        {
            List<object> l => l.Select(x => x.ToString()!.Trim()).Where(s => s.Length > 0).ToList(),
            _ => (v?.ToString() ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
        };
        if (root.TryGetValue("voice_map", out var vm) && vm is Dictionary<object, object> d)
            foreach (var kv in d) direct[kv.Key.ToString()!] = ToList(kv.Value);
        if (root.TryGetValue("race_voice_fallbacks", out var rf) && rf is Dictionary<object, object> f)
            foreach (var kv in f) fallback[kv.Key.ToString()!] = ToList(kv.Value);
        return new VoiceRemap(direct, fallback);
    }

    // Returns the source FaceGen NIF bytes (or null if absent) so the caller can extract its textures.
    // tgtSub = the plugin folder the TARGET record belongs to: "Skyrim.esm" for an override, or the output
    // plugin name for a NEW (boost) NPC — the game looks for FaceGen under the plugin that defines the NPC.
    static byte[]? CopyFaceGen(SourceAssets src, string srcSub, uint srcId, uint tgtId, string outFolder, string tgtSub = "Skyrim.esm")
    {
        string sName = "00" + srcId.ToString("x6"), tName = "00" + tgtId.ToString("x6");
        var nif = src.Get(Path.Combine("meshes", "actors", "character", "facegendata", "facegeom", srcSub, sName + ".nif"));
        var dds = src.Get(Path.Combine("textures", "actors", "character", "facegendata", "facetint", srcSub, sName + ".dds"));
        if (nif != null) WriteBytes(outFolder, Path.Combine("meshes", "actors", "character", "facegendata", "facegeom", tgtSub, tName + ".nif"), nif);
        if (dds != null) WriteBytes(outFolder, Path.Combine("textures", "actors", "character", "facegendata", "facetint", tgtSub, tName + ".dds"), dds);
        return nif;
    }

    // Texture paths a NIF references (BSShaderTextureSet strings). Require a real "textures\" anchor
    // (optionally "Data\Textures\") so binary noise isn't matched; strip the "data\" game-root prefix so
    // paths resolve; skip facegendata (facetint/geom are re-keyed separately). Vanilla textures won't
    // resolve in the source (and, when baking, are left to load from stock).
    static readonly Regex DdsRx = new(@"(?:data[\\/])?textures[\\/][\w \-\\/().]+?\.dds", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static IEnumerable<string> DdsPathsInNif(byte[] nif)
    {
        foreach (Match m in DdsRx.Matches(System.Text.Encoding.Latin1.GetString(nif)))
        {
            var p = m.Value.Replace('/', '\\').TrimStart('\\');
            if (p.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) p = p[5..];
            if (p.Contains("facegendata\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.IndexOf('\\', "textures\\".Length) < 0) continue;   // need a real subfolder
            yield return p;
        }
    }

    static void WriteBytes(string outFolder, string rel, byte[] data)
    {
        var d = Path.Combine(outFolder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(d)!);
        File.WriteAllBytes(d, data);
    }
}
