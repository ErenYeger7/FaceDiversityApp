using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
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
    // Overlay = this pooling is a compat-group "also serve" (double-dip): the target keeps ITS race and
    // just wears this head-compatible face, so assignment must NOT SetTo the face's race.
    // Library = the face lives in a LIBRARY BUILD plugin already (Npc is a synthesized stand-in carrying the
    // donor's FormKey/race/skin/weight); runtime lines reference it directly — nothing to copy or write.
    record Face(INpcGetter Npc, string Folder, bool Disable, bool Female, bool Overlay = false, string Source = "", bool Library = false);
    record VoiceRemap(Dictionary<string, List<string>> Direct, Dictionary<string, List<string>> Fallback);
    static string PoolKey(string race, bool female) => race + (female ? "|F" : "|M");

    public static int Run(string[] args)
    {
        string? game = null, category = "bandit", voiceMapPath = null, outFolder = null, outName = null;
        string? includePath = null, configPath = null, loadOrderPath = null, feminineNamesPath = null, assetDirsPath = null, raceOverridePath = null, feminineHeightsPath = null;
        var srcSpecs = new List<(string path, string? forced)>(); bool feminize = true; bool boost = false;
        var libraryMaps = new List<string>();   // --library-map: a library build's yaml — its donors are the faces (runtime mode only)
        string? targetMod = null;               // --target-mod: per-mod category
        bool sexplague = false; string? sexplaguePct = null; bool feminineNames = false; bool bakeTextures = false; bool feminineHeights = false;
        bool skypatcher = false;   // SkyPatcher runtime mode: donor NPCs in the plugin + copyVisualStyle lines; targets are not overridden
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
                case "--feminine-heights": feminineHeights = true; feminineHeightsPath = args[++i]; break; // race-based height= op per feminized male via SkyPatcher (feminine_heights.yaml)
                case "--skypatcher": skypatcher = true; break;    // runtime mode: donor NPC per face in the plugin + one copyVisualStyle line per target (no overrides)
                case "--bake-textures": bakeTextures = true; break;   // bake cross-mod face textures (brows/eyes/etc.) for self-contained output
                case "--asset-dirs": assetDirsPath = args[++i]; break; // file of enabled mod folders (priority) to resolve textures from
                case "--race-override": raceOverridePath = args[++i]; break; // TSV faceId<TAB>race — pool a face as another race (library merger)
                case "--library-map": libraryMaps.Add(args[++i]); break;     // faces from a self-contained library build (FDA_Library_*.esp)
                case "--target-mod": targetMod = args[++i]; break;           // per-mod category: the plugin whose NPCs are the targets
            }
        if (game is null || outFolder is null || outName is null || (srcSpecs.Count == 0 && libraryMaps.Count == 0))
        { Console.Error.WriteLine("need --game --out --name and at least one --source/--keep/--disable/--library-map"); return 1; }
        if (libraryMaps.Count > 0 && !skypatcher)
        { Console.Error.WriteLine("A library build has no NPC records to override with — it can only feed a SkyPatcher runtime config. Enable 'Build as SkyPatcher file'."); return 1; }

        Categories.Load(configPath);
        // Head-compatibility groups for the merger, next to categories.yaml (base<->vampire by default).
        RaceCompat.Load(configPath is not null ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "race_compat.yaml") : null);
        // Curation: if an include file is given, only faces whose id (Faces.FaceId) is listed are pooled.
        var include = includePath is not null && File.Exists(includePath)
            ? new HashSet<string>(File.ReadAllLines(includePath).Select(l => l.Trim()).Where(l => l.Length > 0),
                                  StringComparer.OrdinalIgnoreCase)
            : null;

        // Race merger (library `as:` / `serve:`): faceId -> ADDITIONAL races the face also serves, and HOW:
        //   overlay (`as:`)    — compat-group double-dip: the target KEEPS its race (a vampire stays a vampire)
        //                        and wears this head-compatible face. Compat-guarded at pooling.
        //   adopt   (`serve:`) — a CUSTOM-race face (unreachable on its own: it pools under a hex race no
        //                        vanilla slot ever draws) pooled under a vanilla race; the target then ADOPTS
        //                        the face's own race, keeping the author's custom skin/head — the same
        //                        mechanism the ja-Kha'jay breeds use. The face's source must stay enabled.
        // TSV: faceId<TAB>race[<TAB>adopt] — no third column means overlay (back-compat with older callers).
        var raceOverride = new Dictionary<string, List<(string race, bool adopt)>>(StringComparer.OrdinalIgnoreCase);
        if (raceOverridePath is not null && File.Exists(raceOverridePath))
            foreach (var p in File.ReadAllLines(raceOverridePath).Select(l => l.Split('\t')).Where(p => p.Length >= 2 && p[0].Trim().Length > 0 && p[1].Trim().Length > 0))
            {
                if (!raceOverride.TryGetValue(p[0].Trim(), out var lst)) raceOverride[p[0].Trim()] = lst = new();
                lst.Add((p[1].Trim(), p.Length > 2 && p[2].Trim().Equals("adopt", StringComparison.OrdinalIgnoreCase)));
            }

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
        //  (c) the per-MOD category: every own-traits NPC one chosen plugin defines (both sexes, unique or not),
        //      as winning overrides; Boost draws its lists from the whole load order (whatever references them).
        bool modCat = Categories.IsMod(category);
        bool scan = Categories.IsScan(category) && !modCat;
        LoadOrder<IModListingGetter<ISkyrimModGetter>>? loScan = null, loBase = null;
        List<INpcGetter> targets;
        if (scan || modCat)
        {
            if (loadOrderPath is null || !File.Exists(loadOrderPath))
            { Console.Error.WriteLine($"category '{category}' is a load-order scan but no --loadorder file was given"); return 1; }
            if (modCat && string.IsNullOrWhiteSpace(targetMod))
            { Console.Error.WriteLine("the per-mod category needs --target-mod <plugin filename>"); return 1; }
            var paths = File.ReadAllLines(loadOrderPath).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            loScan = LoadOrderScan.Build(paths);
            if (modCat)
            {
                targets = LoadOrderScan.ModNpcs(loScan, targetMod!, RaceOf);
                var ts = LoadOrderScan.TemplatesOf(loScan, targetMod!);
                Console.WriteLine($"Mod {targetMod}: {targets.Count} own-traits NPCs ({targets.Count(t => !Fem(t))} M / {targets.Count(Fem)} F) as targets; "
                                + $"{ts.Templated} templated NPCs have no face of their own (their faces come from: {ts.LeavesInMod} leaves in this mod (targets here), {ts.LeavesVanilla} vanilla, {ts.LeavesOtherMods} other mods{(ts.OtherMods.Count > 0 ? " [" + string.Join(", ", ts.OtherMods) + "]" : "")}, {ts.Unresolved} unresolved).");
            }
            else
            {
                targets = LoadOrderScan.UniqueNamedMales(loScan, RaceOf);
                Console.WriteLine($"Load-order scan: {paths.Count} plugins → {targets.Count} unique named males.");
            }
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
        // Belt and braces for EVERY target shape: the player's own record and the character-gen face
        // presets are never refaced/feminized (the scan already drops them; no future category may reach them).
        int playerDropped = targets.RemoveAll(LoadOrderScan.IsPlayerOrPreset);
        if (playerDropped > 0) Console.WriteLine($"Excluded {playerDropped} player/chargen-preset record(s) from the targets.");

        // Build as a full ESP (new FormKeys still allocate from 0x800). The ESL/ESPFE flag is decided once
        // at the very end from the ACTUAL new-record count — an ESPFE holds only 0x800–0xFFF (2048) new
        // records, and an over-limit source must NOT force an invalid ESL that then fails to write.
        var outMod = new SkyrimMod(ModKey.FromNameAndExtension(outName), GameCfg.Release) { IsSmallMaster = false };
        var remap = new Dictionary<FormKey, FormKey>();                 // disable-source HDPT -> our copies
        var pool = new Dictionary<string, List<Face>>(StringComparer.OrdinalIgnoreCase); // race -> faces
        var unpackFolders = new List<string>();  // only --standalone sources get their assets baked in
        var hdptModelByKey = new Dictionary<FormKey, (string folder, string rel)>(); // source HDPT -> its mesh
        // disable-source records a transplanted face can reference (HDPT + the TXST/CLFM/FLST they point
        // to) — indexed cheaply; only the ones actually reached get deep-copied, post-assignment.
        var srcRec = new Dictionary<FormKey, (IMajorRecordGetter rec, string folder)>();
        var assets = new Dictionary<string, SourceAssets>(StringComparer.OrdinalIgnoreCase); // folder -> loose+BSA resolver
        var srcModes = new List<(string plugin, string mode, string why)>();
        var disabledKeys = new HashSet<ModKey>();   // disable/standalone sources — a per-NPC skin (WNAM) living there can't be carried

        foreach (var (sp, forced) in srcSpecs)
        {
            var cls = Classify.Inspect(sp);
            var mode = forced ?? cls.Mode;
            srcModes.Add((cls.Plugin, mode, forced != null ? "forced" : cls.Why));
            // Same per-source semantics in BOTH output modes: runtime mode's donor NPCs live in this plugin and
            // deep-copy/bake exactly like overrides do, so a disable/standalone source is detachable there too.
            bool disable = mode == "disable" || mode == "standalone"; // esp off -> deep-copy records
            bool unpack = mode == "standalone";                       // also bake assets in (fully removable)

            var sm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(sp), GameCfg.Release);
            var folder = Path.GetDirectoryName(Path.GetFullPath(sp))!;
            if (!assets.ContainsKey(folder)) assets[folder] = new SourceAssets(folder);

            if (disable)
            {
                disabledKeys.Add(sm.ModKey);
                if (unpack) unpackFolders.Add(folder);
                // INDEX the source's own head-related records (cheap — just references). We deep-copy ONLY
                // the closure an assigned face actually reaches, post-assignment — not every record the
                // source shipped. Indexing HDPT + the TXST/CLFM/FLST they reference lets the copy fully
                // DETACH from this esp (so it can truly be disabled, not left as a hidden master).
                foreach (var h in sm.HeadParts) srcRec[h.FormKey] = (h, folder);
                foreach (var t in sm.TextureSets) srcRec[t.FormKey] = (t, folder);
                foreach (var c in sm.Colors) srcRec[c.FormKey] = (c, folder);
                foreach (var fl in sm.FormLists) srcRec[fl.FormKey] = (fl, folder);
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
                void Pool(string race, bool overlay) {
                    var key = PoolKey(race, Fem(n));
                    if (!pool.TryGetValue(key, out var l)) { l = new(); pool[key] = l; }
                    l.Add(new Face(n, folder, disable, Fem(n), overlay, Path.GetFileName(sp)));
                }
                Pool(r, false);   // native bucket (own race) — assignment adopts the face's race as before
                // Merger: ALSO pool under each saved extra race.
                //  - `as:` (overlay) ONLY if head-compatible (same race_compat group): the target keeps ITS race
                //    (a vampire stays a vampire) and wears this face; enforced so head/skin still match.
                //  - `serve:` (adopt) is a NATIVE-style bucket: drawn from a vanilla slot, the target adopts this
                //    face's (custom) race exactly like a same-race native draw would — no compat guard needed,
                //    the NPC becomes a consistent whole (race + skin + head all the face's own).
                if (raceOverride.TryGetValue(Faces.FaceId(sp, n.FormKey), out var ovs))
                    foreach (var (ov, adopt) in ovs)
                    {
                        if (ov.Equals(r, StringComparison.OrdinalIgnoreCase)) continue;
                        if (adopt) Pool(ov, false);
                        else if (RaceCompat.AreCompatible(r, ov)) Pool(ov, true);
                    }
            }
        }
        // Library builds: the donors already exist in FDA_Library_*.esp. Each map row becomes a stand-in Npc
        // carrying the donor's FormKey, race (copied into the library when custom), skin, weight and sex, pooled
        // under the ORIGINAL pool race so targets, whitelists and `as:`/`serve:` behave exactly as for the source
        // mod — without the source mod. Runtime lines then point straight at the library plugin.
        var libraryPlugins = new List<string>();
        var libOrigEid = new Dictionary<FormKey, string>();   // library donor -> original NPC EditorID (for the ini comment)
        // A library COPIES a custom race (TeenNord -> 000816:FDA_Library_1.esp). A target that already IS the
        // original race must not be switched to the copy (same race, different record — it would break the
        // mod's own race conditions), so "same race" also means "the copy's origin".
        var libRaceOrigin = new Dictionary<FormKey, FormKey>();
        bool SameRace(FormKey donorRace, FormKey targetRace) =>
            donorRace == targetRace || (libRaceOrigin.TryGetValue(donorRace, out var org) && org == targetRace);
        foreach (var lm in libraryMaps)
        {
            var map = LibraryMap.Load(lm);
            if (map is null) { Console.WriteLine($"library map not found/invalid: {lm} — skipped"); continue; }
            int pooled = 0;
            foreach (var row in map.Faces)
            {
                if (include is not null && !include.Contains(row.Key)) continue;
                uint id; try { id = Convert.ToUInt32(row.Library, 16); } catch { continue; }
                var libKey = ModKey.FromNameAndExtension(map.PluginOf(row));   // a SET spreads its donors over several plugins
                bool fem = row.Sex.Equals("F", StringComparison.OrdinalIgnoreCase);
                var n = new Npc(new FormKey(libKey, id), GameCfg.Release) { EditorID = row.EditorId, Name = row.Name, Weight = row.Weight };
                n.Configuration.Flags = (fem ? NpcConfiguration.Flag.Female : 0) | NpcConfiguration.Flag.Unique;
                if (FormKey.TryFactory(row.Race, out var rk))
                {
                    n.Race.SetTo(rk);
                    if (row.RaceOrigin.Length > 0 && FormKey.TryFactory(row.RaceOrigin, out var ro) && ro != rk) libRaceOrigin[rk] = ro;
                }
                if (row.Skin.Length > 0 && FormKey.TryFactory(row.Skin, out var sk)) n.WornArmor.SetTo(sk);
                libOrigEid[n.FormKey] = row.Npc;
                void PoolLib(string race, bool overlay)
                {
                    var key = PoolKey(race, fem);
                    if (!pool.TryGetValue(key, out var l)) { l = new(); pool[key] = l; }
                    l.Add(new Face(n, "", false, fem, overlay, row.Source, Library: true));
                }
                PoolLib(row.PoolRace, false);
                if (raceOverride.TryGetValue(row.Key, out var ovs))
                    foreach (var (ov, adopt) in ovs)
                    {
                        if (ov.Equals(row.PoolRace, StringComparison.OrdinalIgnoreCase)) continue;
                        if (adopt) PoolLib(ov, false);
                        else if (RaceCompat.AreCompatible(row.PoolRace, ov)) PoolLib(ov, true);
                    }
                pooled++;
            }
            srcModes.Add((map.Plugin, "library", $"library build ({pooled} faces pooled) — self-contained, no source mods needed"));
            Console.WriteLine($"Library build {map.Plugin}: {pooled} faces pooled.");
        }
        // The plugins a config ACTUALLY references are collected while lines are emitted (a set may hold three
        // plugins of which a small config uses one) — see the runtime branch.
        // (RemapLinks deferred: we copy only the WORN source HDPTs after assignment, then remap.)

        var cursor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var report = new Dictionary<string, (int assigned, int skipped, int faces)>(StringComparer.OrdinalIgnoreCase);
        int femCount = 0, unmappedVoice = 0, raceSwitched = 0;   // raceSwitched: targets that ADOPT the face's race (native/adopt draw, face race != target race; `race=` in runtime mode)
        int skinCarried = 0, skinDropped = 0;                    // per-NPC skin (WNAM) carried from the face's author / dropped (lives in a disabled source)
        int weightMatched = 0;                                   // runtime-mode `weight=` ops (target weight set to the donor's so head and body meet)
        var feminizedNpcs = new List<(string plugin, uint id, string name)>(); // males we flipped female (SexPlague + feminine names)
        var femRace = new Dictionary<(string plugin, uint id), string>();      // FINAL in-game race of a feminized male (for race-based heights)
        // SkyPatcher runtime mode: per-target base ops, in assignment order, plus a human-readable note
        // ("; Target <= Donor (source)") written as a comment line above the ini line — a bare FormID
        // like copyVisualStyle=Skyrim.esm|13388 is otherwise impossible to attribute when a face looks off.
        var runtimeTargets = new List<((string plugin, uint id) key, List<string> ops, string note)>();
        var missingFaceGen = new List<string>();
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
        void ApplyFaceFields(Npc npc, Face face, bool keepRace = false)
        {
            var s = face.Npc;
            // Native draw: adopt the face's race (custom breeds for khajiit; no-op same-race). Overlay draw
            // (compat double-dip): KEEP the target's race so a vampire stays a vampire — the head is
            // group-compatible, and the target's race supplies its own skin/normal/eyes.
            if (!keepRace) { if (npc.Race.FormKey != s.Race.FormKey) raceSwitched++; npc.Race.SetTo(s.Race.FormKey); }
            // Per-NPC skin (WNAM): the author's body for THIS face — e.g. CS_Foundation puts a CS_Visions body on
            // 300/301 of its NPCs, which the user's hand-written SkyPatcher lines carried as `skin=`. Carried on
            // every draw, incl. an overlay (the vampire target keeps its race for eyes/face but wears the face's
            // body; assumes the body's ARMA covers the vampire race, as vanilla + BodySlide bodies do). Skipped
            // only when the ARMO lives in a DISABLED source (copying the link would pin that esp as a master —
            // its ARMO/ARMA/meshes aren't deep-copied); counted so the summary says so.
            if (!s.WornArmor.IsNull)
            {
                if (disabledKeys.Contains(s.WornArmor.FormKey.ModKey)) skinDropped++;
                else { npc.WornArmor.SetTo(s.WornArmor.FormKey); skinCarried++; }
            }
            npc.HeadParts.Clear();
            // Link to the ORIGINAL source FormKey; for a disable face the deferred deep-copy below copies
            // the reached records and RemapLinks redirects these to our copies. Keep faces stay as-is.
            foreach (var hp in s.HeadParts) npc.HeadParts.Add(hp);
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
        // A direct entry only wins if AT LEAST ONE of its female voices exists here — otherwise fall
        // through to the race pool, so a direct mapping whose target voice is absent doesn't strand the
        // NPC as "unmapped" when a race-appropriate voice would have served.
        bool TryFemVoice(FormKey maleVoice, string raceName, uint id, out FormKey fem)
        {
            fem = default;
            var ov = voiceName.GetValueOrDefault(maleVoice, "");
            List<string>? candidates = null;
            if (voiceMap.Direct.TryGetValue(ov, out var dc) && dc.Any(voiceByName.ContainsKey)) candidates = dc;
            else if (voiceMap.Fallback.TryGetValue(raceName, out var fc)) candidates = fc;
            if (candidates is null) return false;
            var valid = candidates.Where(voiceByName.ContainsKey).ToList();
            if (valid.Count == 0) return false;
            var pick = valid[(int)(unchecked(id * 2654435761u) % (uint)valid.Count)];
            fem = voiceByName[pick];
            return true;
        }
        static string ShortRace(string r) => r.Replace("Race", "").Replace("Vampire", "V");

        // ---- Runtime-mode DONORS. `copyVisualStyle` takes a FormID, and every replacer of Bryling shares
        // Skyrim.esm|13265 — so referencing the origin record copies whichever replacer WINS the load order, not
        // the one picked (that is how Siddgeir ended up with an unknown Betrid). Instead each distinct face used
        // becomes a NEW, never-placed NPC in THIS plugin carrying exactly the selected source's face fields, with
        // that source's FaceGen re-keyed under this plugin; disable/standalone sources deep-copy their head parts
        // like ESP mode, so the plugin is a self-contained face library that travels between MO2 instances and
        // makes the load order irrelevant. Minimal record on purpose: only face/race/skin/class/voice are linked,
        // so a mod-added source's factions/outfits/packages never pin it as a master.
        var donorByFace = new Dictionary<string, Npc>(StringComparer.OrdinalIgnoreCase);
        int donorSeq = 0;
        Npc Donor(Face face)
        {
            if (face.Library) return (Npc)face.Npc;   // already a donor in the library plugin — reference it as-is
            var dkey = face.Source + "#" + face.Npc.FormKey;
            if (donorByFace.TryGetValue(dkey, out var d)) return d;
            var s = face.Npc;
            var fk = outMod.GetNextFormKey();
            d = new Npc(fk, GameCfg.Release) { EditorID = $"FDAdonor{donorSeq++:D4}_{s.EditorID}", Name = s.Name?.String };
            d.Race.SetTo(s.Race.FormKey);
            d.Configuration.Flags = (Fem(s) ? NpcConfiguration.Flag.Female : 0) | NpcConfiguration.Flag.Unique;
            if (!s.Class.IsNull && !disabledKeys.Contains(s.Class.FormKey.ModKey)) d.Class.SetTo(s.Class.FormKey);
            if (!s.Voice.IsNull && !disabledKeys.Contains(s.Voice.FormKey.ModKey)) d.Voice.SetTo(s.Voice.FormKey);
            ApplyFaceFields(d, face, keepRace: true);   // race set above; keepRace so it isn't counted as an adoption
            outMod.Npcs.Add(d);
            var fg = CopyFaceGen(assets[face.Folder], s.FormKey.ModKey.FileName, s.FormKey.ID, fk.ID, outFolder, outName);
            if (fg == null) missingFaceGen.Add($"DONOR {d.EditorID} <= {s.EditorID} ({s.FormKey.ModKey.FileName})");
            else if (face.Disable || bakeTextures) foreach (var tex in DdsPathsInNif(fg)) ExtractAsset(assets[face.Folder], tex);
            donorByFace[dkey] = d;
            return d;
        }
        int skinOps = 0;   // runtime lines carrying skin= (skinCarried counts donors, once each)

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

            if (skypatcher)
            {
                // SkyPatcher runtime: the target's record is NOT overridden. One ini line per target: copyVisualStyle
                // from the DONOR NPC this plugin carries for the picked face (see Donor above) + race/skin/weight so
                // head, body and race agree, + setFlags=female / voiceType when feminizing.
                var dn = Donor(face);
                if (face.Library && !libraryPlugins.Any(p => string.Equals(p, dn.FormKey.ModKey.FileName, StringComparison.OrdinalIgnoreCase))) libraryPlugins.Add(dn.FormKey.ModKey.FileName);
                var baseOps = new List<string> { $"copyVisualStyle={dn.FormKey.ModKey.FileName}|{dn.FormKey.ID:X}" };   // this plugin, or the library build's
                // Race must match the donor or the game can crash (SkyPatcher doc: "gender and race also match —
                // those can also be modified with SkyPatcher"). A NATIVE draw adopts the donor's race exactly as
                // ESP mode does (custom khajiit breeds etc.) via `race=`; an OVERLAY draw keeps the target's race by
                // design (head-compat group, e.g. base<->vampire) and emits nothing. Same race -> nothing.
                if (!face.Overlay && !SameRace(dn.Race.FormKey, t.Race.FormKey))
                {
                    var rk = dn.Race.FormKey;
                    baseOps.Add($"race={rk.ModKey.FileName}|{rk.ID:X}");
                    raceSwitched++;
                }
                // Per-NPC skin (WNAM): copyVisualStyle copies face+hair only, so the author's body for this face
                // (CS_Foundation -> CS_Visions body) needs its own `skin=` op — the line the user's hand-made
                // configs carried. The donor carries it unless it lived in a disabled source (see ApplyFaceFields).
                if (!dn.WornArmor.IsNull && dn.WornArmor.FormKey != t.WornArmor.FormKey)
                {
                    var wk = dn.WornArmor.FormKey;
                    baseOps.Add($"skin={wk.ModKey.FileName}|{wk.ID:X}");
                    skinOps++;
                }
                // WEIGHT: the face renders from the donor's FaceGen, baked at the DONOR's weight, while the body is
                // built at the TARGET's weight — any gap between the two is a neck seam. ESP mode copies Weight in
                // ApplyFaceFields; runtime mode must say so explicitly (89% of bandit lines differed, median 35).
                if (Math.Abs(dn.Weight - t.Weight) > 0.01f)
                {
                    baseOps.Add("weight=" + dn.Weight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    weightMatched++;
                }
                if (feminize && !Fem(t))
                {
                    baseOps.Add("setFlags=female");
                    if (TryFemVoice(t.Voice.FormKey, race, t.FormKey.ID, out var rv)) baseOps.Add($"voiceType={rv.ModKey.FileName}|{rv.ID:X}"); else unmappedVoice++;
                    femCount++;
                    feminizedNpcs.Add((t.FormKey.ModKey.FileName, t.FormKey.ID, t.Name?.String ?? ""));
                    // FINAL race, same as ESP mode: an overlay keeps the target's; a native/adopt draw takes the face's
                    // (via race= above) — a custom race resolves to hex -> the default feminine height.
                    femRace[(t.FormKey.ModKey.FileName, t.FormKey.ID)] = face.Overlay ? race : RaceOf(face.Npc.Race.FormKey);
                }
                var note = (face.Library
                         ? $"; {t.EditorID} \"{t.Name?.String}\" <= library donor {dn.EditorID} ({libOrigEid.GetValueOrDefault(dn.FormKey, "")} \"{face.Npc.Name?.String}\" from {face.Source}, build {dn.FormKey.ModKey.FileName})"
                         : $"; {t.EditorID} \"{t.Name?.String}\" <= donor {dn.EditorID} = {face.Npc.EditorID} \"{face.Npc.Name?.String}\" from {face.Source}")
                         + (face.Overlay ? " [overlay: keeps target race]" : "");
                runtimeTargets.Add(((t.FormKey.ModKey.FileName, t.FormKey.ID), baseOps, note));
                continue;
            }

            var npc = outMod.Npcs.GetOrAddAsOverride(t);
            var s = face.Npc;
            ApplyFaceFields(npc, face, keepRace: face.Overlay);

            if (feminize && !Fem(t))
            {
                npc.Configuration.Flags |= NpcConfiguration.Flag.Female;
                if (TryFemVoice(t.Voice.FormKey, race, t.FormKey.ID, out var fvk)) npc.Voice.SetTo(fvk); else unmappedVoice++;
                femCount++;
                feminizedNpcs.Add((t.FormKey.ModKey.FileName, t.FormKey.ID, t.Name?.String ?? ""));
                femRace[(t.FormKey.ModKey.FileName, t.FormKey.ID)] = RaceOf(npc.Race.FormKey);   // FINAL race (an overlay draw keeps the target's)
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
        // Scan targets are fully read now — release the load order's ~90 memory-mapped file handles (the
        // per-mod category still needs it for Boost's leveled lists; released after Boost).
        if (loScan is not null && !modCat) { LoadOrderScan.DisposeLoadOrder(loScan); loScan = null; }

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
        // Runtime mode + Boost = the "partial SkyPatcher" build: the clones are NEW records in this (ESPFE)
        // plugin, injected into the leveled lists exactly as in ESP mode, but they carry NO face data of their
        // own — each gets a copyVisualStyle line from its donor (in-plugin or library) like every other target.
        if (boost && !scan)
        {
            // which LeveledNpc lists each target sits in, with the entry's level+count. Prefix categories: the
            // game esm's lists. Per-mod: every WINNING list in the load order that references a target (the mod's
            // own lists, or a vanilla list the mod edits).
            var lvlnOfNpc = new Dictionary<FormKey, List<(FormKey ll, short lvl, short cnt)>>();
            IEnumerable<ILeveledNpcGetter> lists = modCat && loScan is not null ? loScan.PriorityOrder.LeveledNpc().WinningOverrides() : esm.LeveledNpcs;
            foreach (var ll in lists)
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
                    clone.EditorID = $"FDA{ShortRace(race)}{(female ? "F" : "M")}{seq++:D3}";
                    if (skypatcher)
                    {
                        // face at load via copyVisualStyle — same ops as an override target (race/skin/weight)
                        var dn = Donor(face);
                        if (face.Library && !libraryPlugins.Any(p => string.Equals(p, dn.FormKey.ModKey.FileName, StringComparison.OrdinalIgnoreCase))) libraryPlugins.Add(dn.FormKey.ModKey.FileName);
                        var ops = new List<string> { $"copyVisualStyle={dn.FormKey.ModKey.FileName}|{dn.FormKey.ID:X}" };
                        if (!face.Overlay && !SameRace(dn.Race.FormKey, clone.Race.FormKey))
                        { ops.Add($"race={dn.Race.FormKey.ModKey.FileName}|{dn.Race.FormKey.ID:X}"); raceSwitched++; }
                        if (!dn.WornArmor.IsNull && dn.WornArmor.FormKey != clone.WornArmor.FormKey)
                        { ops.Add($"skin={dn.WornArmor.FormKey.ModKey.FileName}|{dn.WornArmor.FormKey.ID:X}"); skinOps++; }
                        if (Math.Abs(dn.Weight - clone.Weight) > 0.01f)
                        { ops.Add("weight=" + dn.Weight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)); weightMatched++; }
                        if (female && !Fem(donor))
                        {
                            clone.Configuration.Flags |= NpcConfiguration.Flag.Female;   // on the record itself (it's ours)
                            if (TryFemVoice(donor.Voice.FormKey, race, fk.ID, out var fvk)) clone.Voice.SetTo(fvk); else unmappedVoice++;
                            feminizedNpcs.Add((outName, fk.ID, donor.Name?.String ?? ""));
                            femRace[(outName, fk.ID)] = face.Overlay ? race : RaceOf(dn.Race.FormKey);
                        }
                        runtimeTargets.Add(((outName, fk.ID), ops, $"; BOOST clone {clone.EditorID} (of {donor.EditorID}) <= donor {dn.EditorID} \"{face.Npc.Name?.String}\" from {face.Source}"));
                    }
                    else
                    {
                        ApplyFaceFields(clone, face);
                        if (female && !Fem(donor))
                        {
                            clone.Configuration.Flags |= NpcConfiguration.Flag.Female;
                            if (TryFemVoice(donor.Voice.FormKey, race, fk.ID, out var fvk)) clone.Voice.SetTo(fvk); else unmappedVoice++;
                            feminizedNpcs.Add((outName, fk.ID, donor.Name?.String ?? ""));
                            femRace[(outName, fk.ID)] = RaceOf(clone.Race.FormKey);   // clone's FINAL race (adopted from the face) -> feminine height
                        }
                        var fg = CopyFaceGen(assets[face.Folder], face.Npc.FormKey.ModKey.FileName, face.Npc.FormKey.ID, fk.ID, outFolder, outName);
                        if (fg == null) missingFaceGen.Add($"BOOST {clone.EditorID} <= {face.Npc.EditorID}");
                        else if (face.Disable || bakeTextures) foreach (var tex in DdsPathsInNif(fg)) ExtractAsset(assets[face.Folder], tex);
                    }
                    foreach (var (ll, lvl, cnt) in lvlnOfNpc[donor.FormKey])
                        skyLines.Add($"filterByLLs={ll.ModKey.FileName}|{ll.ID:X}:addOnceToLLs={outName}|{fk.ID:X}~{lvl}~{cnt}");
                    boostAdded++; boostReport[race] = boostReport.GetValueOrDefault(race) + 1;
                }
            }
        }

        // Base-master targets are fully read now (override loop + Boost done) — release those handles.
        if (loBase is not null) { LoadOrderScan.DisposeLoadOrder(loBase); loBase = null; }
        if (loScan is not null) { LoadOrderScan.DisposeLoadOrder(loScan); loScan = null; }

        // Deep-copy ONLY the disable-source records a transplanted face actually reaches — the worn head
        // parts AND the TXST/CLFM/FLST/sub-HDPT they reference (transitive closure) — NOT every record the
        // source shipped. Two fixes in one: (a) the record bloat (was: copy every source head part), and
        // (b) DETACHMENT — a copied HDPT points at the source's own TextureSet, so without copying those the
        // output silently kept the source as a MASTER (its esp couldn't actually be disabled). Seed from
        // every in-source link still present in the output, BFS the closure, copy, then remap all links.
        var toCopy = new HashSet<FormKey>();
        var q = new Queue<FormKey>();
        foreach (var rec in outMod.EnumerateMajorRecords())
            foreach (var l in rec.EnumerateFormLinks())
                if (!l.FormKey.IsNull && srcRec.ContainsKey(l.FormKey)) q.Enqueue(l.FormKey);
        while (q.Count > 0)
        {
            var fk = q.Dequeue();
            if (!toCopy.Add(fk) || !srcRec.TryGetValue(fk, out var e)) continue;
            foreach (var l in e.rec.EnumerateFormLinks())
                if (!l.FormKey.IsNull && srcRec.ContainsKey(l.FormKey)) q.Enqueue(l.FormKey);
        }
        void AddCopy(IMajorRecord dup)
        {
            switch (dup)
            {
                case HeadPart h: outMod.HeadParts.Add(h); break;
                case TextureSet t: outMod.TextureSets.Add(t); break;
                case ColorRecord c: outMod.Colors.Add(c); break;
                case FormList f: outMod.FormLists.Add(f); break;
            }
        }
        foreach (var fk in toCopy)
        {
            var e = srcRec[fk];
            var nfk = outMod.GetNextFormKey();
            var dup = e.rec.Duplicate(nfk);
            AddCopy(dup);
            remap[fk] = nfk;
            if (dup is IHeadPartGetter hp && hp.Model?.File.ToString() is { Length: > 0 } mf) hdptModelByKey[fk] = (e.folder, mf);
        }
        outMod.RemapLinks(remap);

        // Bake only the HDPT meshes actually shipped (+ their textures) — not every hair the source shipped.
        foreach (var hm in hdptModelByKey.Values.ToList())
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
        // A runtime config fed ONLY by library builds has nothing to write: every donor already lives in
        // FDA_Library_*.esp, so the output is the SkyPatcher ini alone (no load-order slot at all).
        bool writePlugin = !skypatcher || outMod.EnumerateMajorRecords().Any();
        if (writePlugin)
        {
            outMod.WriteToBinary(esp, new BinaryWriteParameters { MastersListContent = MastersListContentOption.Iterate });
            Console.WriteLine($"Output is {(droppedEsl ? "a FULL ESP" : "ESL-flagged (ESPFE)")} — {newRecs} new records" +
                              (droppedEsl ? $" exceed the 2048 ESPFE limit (uses a load-order slot)." : " (fits the ESPFE limit)."));
        }
        else Console.WriteLine($"No plugin written — every face comes from a library build ({(libraryPlugins.Count > 0 ? string.Join(", ", libraryPlugins) + " referenced" : "no donor used")}); the output is the SkyPatcher config only.");
        if (skypatcher)
            Console.WriteLine($"SkyPatcher runtime mode: the plugin holds {donorByFace.Count} DONOR faces (never placed); {runtimeTargets.Count} targets get their face at load via copyVisualStyle from them (no overrides); {raceSwitched} adopt the donor's race via race=; {skinOps} carry the donor's per-NPC skin via skin=; {weightMatched} take the donor's weight via weight= (neck seam otherwise).");

        int totalAssigned = report.Values.Sum(v => v.assigned), totalSkipped = report.Values.Sum(v => v.skipped);
        Console.WriteLine("Source classification:");
        foreach (var (plugin, mode, why) in srcModes) Console.WriteLine($"  [{mode.ToUpperInvariant(),7}] {plugin}  — {why}");
        Console.WriteLine(skypatcher
            ? $"\nGenerated {outName}: {totalAssigned} targets via {donorByFace.Count} donor NPCs ({femCount} feminized), {outMod.HeadParts.Count} HDPT copied."
            : $"\nGenerated {outName}: {totalAssigned} overrides ({femCount} feminized), {outMod.HeadParts.Count} HDPT copied.");
        if (!skypatcher && raceSwitched > 0) Console.WriteLine($"Race: {raceSwitched} NPCs adopt their face's race (custom breed/follower race — that source stays a master).");
        if (skinCarried + skinDropped > 0)
            Console.WriteLine($"Per-NPC skin (WNAM): {skinCarried} NPCs carry their face's author-set body skin"
                              + (skinDropped > 0 ? $"; {skinDropped} DROPPED — the skin record lives in a disabled source (use keep mode for that source to carry it)." : "."));
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
        string? npcIni = null; int spTagged = 0; string spSummary = ""; int renamed = 0; bool femNamesOn = false; int heighted = 0; bool femHeightsOn = false;
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

            // Feminine height op per NPC by FINAL race (vampire -> base race; unlisted -> default), from feminine_heights.yaml.
            var femHeights = feminineHeights ? FeminineHeights.Load(feminineHeightsPath) : null;
            femHeightsOn = femHeights is not null;
            if (feminineHeights && femHeights is null) Console.WriteLine("\nFeminine heights: enabled but feminine_heights.yaml not found/invalid — skipped.");

            // One line per NPC that carries at least one op (SkyPatcher merges multiple : ops per NPC).
            // Per-feminized-NPC ops: SexPlague tier/seed/ability, race-based height, feminine name. Names MUST use
            // SkyPatcher's ~tilde~ string syntax — a bare fullName= is silently ignored in game (confirmed by the
            // user); shortName gets the first token. Height decimal is invariant-culture (a pt-BR comma would break
            // SkyPatcher's parse).
            List<string> FemOps(string plugin, uint id, string name)
            {
                var ops = new List<string>();
                if (sexOp.TryGetValue((plugin, id), out var so)) ops.Add(so);
                if (femHeights is not null && femRace.TryGetValue((plugin, id), out var fr))
                { ops.Add("height=" + femHeights.For(fr).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)); heighted++; }
                if (femNameMap is not null && !string.IsNullOrEmpty(name) && femNameMap.TryGetValue(name, out var fn))
                {
                    ops.Add($"fullName=~{fn}~");
                    var first = fn.Split(' ', 2)[0];
                    if (first.Length > 0 && first != fn) ops.Add($"shortName=~{first}~");
                    renamed++;
                }
                return ops;
            }
            var npcLines = new List<string>();
            if (skypatcher)
            {
                // Runtime mode: EVERY assigned target gets a line — copyVisualStyle [+ setFlags/voiceType] — plus the
                // feminized-only ops when it was feminized. One line per NPC, all ops colon-joined.
                var femName = new Dictionary<(string plugin, uint id), string>();
                foreach (var f in feminizedNpcs) femName.TryAdd((f.plugin, f.id), f.name);
                foreach (var (key, baseOps, note) in runtimeTargets)
                {
                    var ops = new List<string>(baseOps);
                    if (femName.TryGetValue(key, out var nm)) ops.AddRange(FemOps(key.plugin, key.id, nm));
                    npcLines.Add(note);   // `;` comment lines are what the SkyPatcher NPC Replacer Converter itself emits
                    npcLines.Add($"filterByNpcs={key.plugin}|{key.id:X}:" + string.Join(":", ops));
                }
            }
            else
                foreach (var f in feminizedNpcs)
                {
                    var ops = FemOps(f.plugin, f.id, f.name);
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
            if (femHeightsOn)
                Console.WriteLine($"Feminine heights: {heighted} of {feminizedNpcs.Count} feminized males scaled by race ({femHeights!.Heights.Count} races listed, default {femHeights.Default.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}).");
            if (npcIni is not null) Console.WriteLine($"  wrote SkyPatcher NPC config: {npcIni}");
        }

        // Masters are computed during write, not back-populated on the in-memory mod — re-read the file.
        // Dispose the overlay immediately (it memory-maps the file): a long-running server would otherwise
        // keep the output ESP locked, blocking a regenerate to the same folder.
        List<string> masters = new();
        if (writePlugin)
            using (var mm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(esp), GameCfg.Release))
                masters = mm.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
        masters.AddRange(libraryPlugins.Where(p => !masters.Contains(p, StringComparer.OrdinalIgnoreCase)));   // the config needs the build enabled
        // Classify sources for the manifest. After the deep-copy above, a disable/standalone source is no
        // longer a master (its records are copied in), so it can genuinely be turned off. Reconcile against
        // the ACTUAL master list: anything still mastered (a referenced record we couldn't copy out) must
        // stay enabled and is never listed as disable-able — this is what prevents the old contradiction of
        // the same mod appearing under both KEEP ENABLED and DISABLE.
        var masterSet = new HashSet<string>(masters, StringComparer.OrdinalIgnoreCase);
        var espDisable = srcModes.Where(m => m.mode == "disable").Select(m => m.plugin).Distinct().ToList();
        var espStandalone = srcModes.Where(m => m.mode == "standalone").Select(m => m.plugin).Distinct().ToList();
        var stuck = espDisable.Concat(espStandalone).Where(masterSet.Contains).Distinct().ToList();
        var offKeepMod = espDisable.Where(p => !masterSet.Contains(p)).ToList();       // esp off, mod stays (assets)
        var offRemoveMod = espStandalone.Where(p => !masterSet.Contains(p)).ToList();   // esp off + assets baked -> gone
        string Bullets(IEnumerable<string> xs) => string.Join("\n", xs.Select(p => "  - " + p));

        var rd = new System.Text.StringBuilder();
        rd.Append($"{outName} — generated by FaceDiversityApp (personal use only; do not redistribute)\n\n");
        rd.Append($"Category: {category}.  {totalAssigned} faces ({femCount} feminized males), {totalSkipped} skipped.\n");
        if (skypatcher)
            rd.Append($"Output type: SKYPATCHER RUNTIME (self-contained face library). This plugin holds {donorByFace.Count} DONOR NPCs, never\n"
                    + "  placed in the world: each one IS the picked face — the selected source's head parts, tints, morphs, weight and\n"
                    + "  skin — with that source's FaceGen re-keyed under this plugin. No NPC record is overridden: the\n"
                    + $"  {runtimeTargets.Count} targets get their look at load via copyVisualStyle={outName}|<donor> (+ race= when the donor's\n"
                    + $"  race differs — {raceSwitched} here; + skin= when the face's author set a body — {skinOps} here; + weight= so the\n"
                    + $"  body is built at the weight the FaceGen was baked at — {weightMatched} here; + setFlags=female / voiceType when\n"
                    + "  feminized). Coexists with other mods that edit the same NPCs, and the load order no longer decides which\n"
                    + "  replacer's face you get. REQUIRES SkyPatcher enabled AND this plugin enabled. Sources follow the lists\n"
                    + "  below exactly as in ESP mode: a keep source stays a master; a disable/standalone source's head parts are\n"
                    + "  copied in, so the plugin travels to another MO2 instance on its own.\n"
                    + (libraryPlugins.Count > 0 ? $"  Faces from LIBRARY BUILD(S) {string.Join(", ", libraryPlugins)} are referenced there directly — keep those builds installed and enabled.\n" : "")
                    + (writePlugin ? $"Plugin type: {(droppedEsl ? $"FULL ESP — {newRecs} new records exceed the 2048 ESPFE limit, so this uses a load-order slot" : $"ESL-flagged (ESPFE), {newRecs} new records")}.\n"
                                   : "No plugin in this output: every face comes from the library build(s) above, so this is the SkyPatcher config alone.\n"));
        else
        {
            rd.Append($"Plugin type: {(droppedEsl ? $"FULL ESP — {newRecs} new records exceed the 2048 ESPFE limit, so this uses a load-order slot" : $"ESL-flagged (ESPFE), {newRecs} new records")}.\n");
            if (raceSwitched > 0) rd.Append($"Race: {raceSwitched} NPCs adopt their face's race (a custom breed or follower race) — that race's mod is a master above.\n");
            if (skinCarried > 0) rd.Append($"Skin: {skinCarried} NPCs carry the body skin their face's author set (per-NPC WNAM) — its mod is a master above.\n");
            if (skinDropped > 0) rd.Append($"Skin: {skinDropped} NPCs LOST their face's author-set body skin (the skin record lives in a disabled source; use keep mode for that source to carry it).\n");
        }
        if (bakeTextures)
            rd.Append($"Textures: BAKED — {bakedCrossMod} cross-mod face textures (brows/eyes/hair from other packs)\n"
                    + "  are copied into THIS mod; only shared skin/body is left to its overhaul.\n");
        else
            rd.Append("Textures: NOT baked — a face's brows/eyes/hair may load from OTHER mods (texture packs),\n"
                    + "  which this mod does NOT contain. Keep those packs enabled, or regenerate with 'Bake\n"
                    + "  textures' for a self-contained result. Run 'Verify assets' to list exactly which mods.\n");
        rd.Append("\n");
        rd.Append("KEEP ENABLED — required masters (leave these ON):\n" + Bullets(masters) + "\n");
        if (stuck.Count > 0)
            rd.Append("  NOTE — these harvested sources are STILL referenced by this mod (a record it uses lives\n"
                    + "  in them and could not be copied out), so they must stay ENABLED too:\n" + Bullets(stuck) + "\n");
        rd.Append("\n");
        if (offRemoveMod.Count > 0)
            rd.Append("SAFE TO REMOVE — records AND assets are baked in (standalone); the mod can be deleted:\n"
                    + Bullets(offRemoveMod) + "\n\n");
        if (offKeepMod.Count > 0)
            rd.Append("DISABLE THE .ESP, KEEP THE MOD — the face records are self-contained here, but the mod\n"
                    + "still supplies the hair/skin meshes+textures: turn its .esp OFF, leave the mod installed\n"
                    + "(loose files load with the esp off; a packed BSA needs force-loading in MO2's Archives tab):\n"
                    + Bullets(offKeepMod) + "\n\n");
        if (offRemoveMod.Count == 0 && offKeepMod.Count == 0 && stuck.Count == 0)
            rd.Append("(No harvested sources to disable — all inputs are kept as masters.)\n\n");
        rd.Append("Per-race:\n" + string.Join("\n", report.OrderBy(k => k.Key).Select(kv => $"  {kv.Key}: assigned {kv.Value.assigned}, skipped {kv.Value.skipped}")) + "\n");
        if (boost && boostAdded > 0)
            rd.Append($"\nLEVELED-LIST BOOST: +{boostAdded} extra NPCs injected into vanilla leveled lists via SkyPatcher\n"
                    + $"(SKSE/Plugins/SkyPatcher/leveledList/{Path.GetFileNameWithoutExtension(outName)}.ini).\nREQUIRES SkyPatcher installed and enabled.\n");
        if (npcIni is not null)
        {
            rd.Append($"\nSKYPATCHER NPC CONFIG (SKSE/Plugins/SkyPatcher/npc/{Path.GetFileNameWithoutExtension(outName)}_npc.ini) — REQUIRES SkyPatcher enabled:\n");
            if (spTagged > 0) rd.Append($"  - SexPlague: {spTagged} feminized males tagged ({spSummary}) — REQUIRES SexPlagueFactions.esp.\n");
            if (femNamesOn) rd.Append($"  - Feminine names: {renamed} feminized males given a feminine display name.\n");
            if (femHeightsOn && heighted > 0) rd.Append($"  - Feminine heights: {heighted} feminized males scaled by race via height= (config/feminine_heights.yaml; a vampire uses its base race's value).\n");
        }
        if (missingFaceGen.Count > 0)
            rd.Append($"\nWARNING missing FaceGen ({missingFaceGen.Count}):\n  " + string.Join("\n  ", missingFaceGen) + "\n");
        File.WriteAllText(Path.Combine(outFolder, "README.txt"), rd.ToString());
        Console.WriteLine(skypatcher ? (writePlugin ? $"Wrote {esp} (donor faces) + SkyPatcher runtime config" : $"Wrote SkyPatcher runtime config under {outFolder} (faces from the library build)") : $"Wrote {esp}");
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
    internal static byte[]? CopyFaceGen(SourceAssets src, string srcSub, uint srcId, uint tgtId, string outFolder, string tgtSub = "Skyrim.esm")
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
    internal static IEnumerable<string> DdsPathsInNif(byte[] nif)
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

    internal static void WriteBytes(string outFolder, string rel, byte[] data)
    {
        var d = Path.Combine(outFolder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(d)!);
        File.WriteAllBytes(d, data);
    }
}
