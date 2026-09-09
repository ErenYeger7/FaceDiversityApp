using System.Net;
using System.Text;
using System.Text.Json;

// Local web UI host. A tiny HttpListener (BCL only) serves the static 3-pane frontend and a JSON API
// that reuses the engine (Classify / Faces / Generate). Single-user, single-threaded request loop:
// requests are handled one at a time, so redirecting Console during a generate can't race.
static class Serve
{
    static readonly JsonSerializerOptions J = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    // live paths (from config/settings.json, else the built-in SME fallback; all overridable via flags)
    static string Game = "", Mods = "", Config = "", VoiceMap = "", WebRoot = "", OutDir = "",
                  Profiles = "", Profile = "", BotPresets = "", GameData = "", MugshotRoot = "";
    static Dictionary<string, string> MugshotAliases = new(StringComparer.OrdinalIgnoreCase);
    static bool FaceFinderEnabled = false, FaceFinderCache = false;

    // built-in fallbacks so the app still runs on this PC even with no settings.json
    const string DefGame = "C:/Modlists/SME/Stock Game/Data/Skyrim.esm";
    const string DefMods = "C:/Modlists/SME/mods";
    const string DefProfile = "Skyrim Modding Essentials";
    const string DefBot = "C:/Modlists/SME/mods/600+ 3BA Bodies of Tamriel/CalienteTools/Bodyslide/SliderPresets";
    static string Def(string v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v;
    // Store paths with forward slashes: consistent, and safe to hand-edit in settings.json (a lone '\'
    // is an invalid JSON escape). Windows + .NET + Mutagen all accept '/' paths, so this is display/
    // storage only — the UI renders them back as native '\' for copy-paste.
    static string Norm(string p) => string.IsNullOrEmpty(p) ? p : p.Replace('\\', '/');

    // An MO2 instance keeps mods/ and profiles/ as SIBLINGS, so the profiles dir is derivable from the
    // mods dir. This is what makes moving to another PC "just work" when only the mods path is set — the
    // profiles path (which holds loadorder.txt/plugins.txt the scan needs) follows automatically.
    static string DeriveProfiles(string mods)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(mods.TrimEnd('/', '\\')));
        return parent is null ? mods : Path.Combine(parent, "profiles");
    }
    // Use an explicit profiles path only when set AND it exists; otherwise derive from mods.
    static string ResolveProfiles(string explicitProfiles, string mods)
        => !string.IsNullOrWhiteSpace(explicitProfiles) && Directory.Exists(explicitProfiles)
           ? explicitProfiles : DeriveProfiles(mods);

    public static int Run(string[] args)
    {
        int port = 8930; // avoid DevBench 8920/8921
        var home = AppHome();
        var s = Settings.Current;                 // already loaded (+ GameCfg.Release set) in Program.cs
        Game = Def(s.Game, DefGame);
        Mods = Def(s.Mods, DefMods);
        Profiles = ResolveProfiles(s.Profiles, Mods);   // auto-derive from mods if not explicitly valid
        Profile = Def(s.Profile, DefProfile);
        BotPresets = Def(s.BotPresets, DefBot);
        // Base-game Data folder (vanilla BSAs). Defaults to the folder of Game — correct when Skyrim.esm
        // sits with its textures (normal Steam install, and the SME Stock Game). Overridable for the rare
        // split where the ESM you point at is a cleaned-masters copy separate from the BSA install.
        GameData = Def(s.GameData, Path.GetDirectoryName(Path.GetFullPath(Game)) ?? Game);
        MugshotRoot = Norm(Def(s.MugshotRoot, ""));
        MugshotAliases = new(s.MugshotAliases ?? new(), StringComparer.OrdinalIgnoreCase);
        FaceFinderEnabled = s.FaceFinderEnabled; FaceFinderCache = s.FaceFinderCache;
        Game = Norm(Game); Mods = Norm(Mods); Profiles = Norm(Profiles); BotPresets = Norm(BotPresets); GameData = Norm(GameData);
        Config = Path.Combine(home, "config", "categories.yaml");
        VoiceMap = Path.Combine(home, "config", "voice_map.yaml");
        WebRoot = Path.Combine(home, "web");
        OutDir = Norm(Def(s.OutDir, Path.Combine(home, "out")));   // persisted per-PC; blank = <app>/out
        for (int i = 1; i < args.Length; i++)
            switch (args[i])
            {
                case "--game": Game = args[++i]; break;
                case "--mods": Mods = args[++i]; break;
                case "--profiles": Profiles = args[++i]; break;
                case "--profile": Profile = args[++i]; break;
                case "--bot-presets": BotPresets = args[++i]; break;
                case "--mugshot-root": MugshotRoot = args[++i]; break;
                case "--game-version": /* handled in Program.cs */ i++; break;
                case "--config": Config = args[++i]; break;
                case "--voice-map": VoiceMap = args[++i]; break;
                case "--webroot": WebRoot = args[++i]; break;
                case "--out": OutDir = args[++i]; break;
                case "--port": port = int.Parse(args[++i]); break;
            }
        Categories.Load(File.Exists(Config) ? Config : null);
        RaceCompat.Load(RaceCompatPath());

        // Bind both loopback hosts so either http://127.0.0.1:<port>/ or http://localhost:<port>/ works.
        // "localhost" may need a URL ACL on some Windows setups; if the dual bind fails, fall back to the IP.
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException)
        {
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); }
            catch (HttpListenerException e) { Console.Error.WriteLine($"cannot bind port {port}: {e.Message}"); return 1; }
        }
        Console.WriteLine($"FaceDiversityApp UI  →  http://localhost:{port}/  (or http://127.0.0.1:{port}/)");
        Console.WriteLine($"  game={Game}\n  mods={Mods}\n  webroot={WebRoot}\n  out={OutDir}");
        Console.WriteLine("Ctrl+C to stop.");

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); } catch { break; }
            try { Handle(ctx); }
            catch (Exception e) { TrySend(ctx, 500, "application/json", Json(new { error = e.Message })); }
        }
        return 0;
    }

    static void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var q = ctx.Request.QueryString;

        if (path.StartsWith("/api/"))
        {
            switch (path)
            {
                case "/api/config": Send(ctx, 200, "application/json", Json(ConfigPayload())); return;
                case "/api/profiles": Send(ctx, 200, "application/json", Json(ListProfiles())); return;
                case "/api/mods":
                {
                    var mp = q["mods"]; var pf = q["profile"];
                    var mods = ScanMods(string.IsNullOrWhiteSpace(mp) ? Mods : mp!, pf is null ? Profile : (pf.Length == 0 ? null : pf));
                    // Library builds appear as a pseudo-mod at the top: one "plugin" per build, addressed as
                    // <builds dir>\<plugin>.esp so the picker/addSource/faces flow needs no special casing.
                    var builds = LibraryMap.List();
                    if (builds.Count > 0)
                    {
                        var sums = new Dictionary<string, Dictionary<string, Library.RaceCount>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var b in builds) if (LibraryMap.Load(b.Map) is { } m) sums[b.Plugin] = LibraryMap.Summary(m);
                        mods.Insert(0, new ModEntry("Library builds (self-contained face plugins)", LibraryMap.BuildsDir(), builds.Select(b => b.Plugin).ToArray(), true, -1, sums));
                    }
                    Send(ctx, 200, "application/json", Json(mods)); return;
                }
                case "/api/classify":
                {
                    var src = q["source"];
                    if (src is null) { Send(ctx, 400, "application/json", Json(new { error = "source required" })); return; }
                    if (LibraryMap.IsLibraryPath(src))
                    {
                        var m = LibraryMap.Load(LibraryMap.MapPathFor(src));
                        Send(ctx, 200, "application/json", Json(new
                        {
                            plugin = Path.GetFileName(src), folder = LibraryMap.BuildsDir(),
                            femaleNpcs = m?.Faces.Count(f => f.Sex.Equals("F", StringComparison.OrdinalIgnoreCase)) ?? 0, ownHdpt = 0,
                            overridesVanilla = false, usesCustomRace = false, hasBsa = false, shipsRaceTextures = false, faceGenLoose = true,
                            mode = "library", why = "self-contained library build — its donors are referenced directly; no source mods needed; SkyPatcher runtime output only"
                        })); return;
                    }
                    Send(ctx, 200, "application/json", Json(Classify.Inspect(src))); return;
                }
                case "/api/faces":
                {
                    // GET ?source=..&source=.. (kept for curl/tests) or POST {sources:[...], library:bool} — the
                    // UI POSTs: with many multi-esp mods the query string blew past HttpListener's 16 KB request
                    // limit ("request too long").
                    string[] srcs; bool applyLib;
                    if (ctx.Request.HttpMethod == "POST")
                    {
                        string body; using (var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) body = r.ReadToEnd();
                        FacesReq? fr;
                        try { fr = JsonSerializer.Deserialize<FacesReq>(body, J); }
                        catch (Exception e) { Send(ctx, 400, "application/json", Json(new { error = e.Message })); return; }
                        srcs = fr?.Sources?.ToArray() ?? Array.Empty<string>(); applyLib = fr?.Library ?? false;
                    }
                    else { srcs = q.GetValues("source") ?? Array.Empty<string>(); applyLib = q["library"] == "1"; }
                    var faces = Faces.Enumerate(Game, srcs.Where(s => !LibraryMap.IsLibraryPath(s)));
                    foreach (var s in srcs.Where(LibraryMap.IsLibraryPath))
                        if (LibraryMap.Load(LibraryMap.MapPathFor(s)) is { } lm) faces.AddRange(LibraryMap.ToFaces(lm));
                    // Mod Creator Mode sets library so the library curbs the pool: a source WITH a whitelist is
                    // narrowed to its approved faces; a globally-blacklisted NPC is dropped from every source.
                    // Library Mode omits it (it must see & curate everything).
                    if (applyLib) faces = ApplyLibrary(faces);
                    Send(ctx, 200, "application/json", Json(faces)); return;
                }
                case "/api/library/mod":
                {
                    if (ctx.Request.HttpMethod == "POST") { HandleSaveWhitelist(ctx); return; }
                    var plugin = q["plugin"];
                    if (string.IsNullOrWhiteSpace(plugin)) { Send(ctx, 400, "application/json", Json(new { error = "plugin required" })); return; }
                    var wl = Library.LoadWhitelist(plugin!);
                    Send(ctx, 200, "application/json", Json(new { plugin, hasWhitelist = wl != null,
                        keys = wl?.ToArray() ?? Array.Empty<string>(), overrides = Library.LoadWhitelistOverrides(plugin!),
                        serves = Library.LoadWhitelistServes(plugin!),
                        summary = Library.LoadWhitelistSummary(plugin!) })); return;
                }
                case "/api/races":
                {
                    try { Send(ctx, 200, "application/json", Json(Faces.HumanoidRaces(Game).Select(r => new { key = r.key, name = r.name }))); }
                    catch (Exception e) { Send(ctx, 400, "application/json", Json(new { error = e.Message })); }
                    return;
                }
                // Hot-reload the compat groups on every request so a race_compat.yaml edit shows up in the "Also"
                // dropdown without a restart (generation already re-reads it per run — this keeps both in step).
                case "/api/race-compat": RaceCompat.Load(RaceCompatPath()); Send(ctx, 200, "application/json", Json(RaceCompat.Map())); return;
                // Vanilla playable races — the `serve:` (adopt) dropdown for custom-race faces.
                case "/api/serve-races":
                {
                    try { Send(ctx, 200, "application/json", Json(Faces.PlayableRaces(Game))); }
                    catch (Exception e) { Send(ctx, 400, "application/json", Json(new { error = e.Message })); }
                    return;
                }
                case "/api/library/builds": Send(ctx, 200, "application/json", Json(LibraryMap.List())); return;
                case "/api/library/build" when ctx.Request.HttpMethod == "POST": HandleLibraryBuild(ctx); return;
                case "/api/library/blacklist":
                {
                    if (ctx.Request.HttpMethod == "POST") { HandleSaveBlacklist(ctx); return; }
                    Send(ctx, 200, "application/json", Json(new { keys = Library.LoadBlacklist().ToArray() })); return;
                }
                case "/api/mugshot":
                {
                    var mod = q["mod"] ?? "";
                    var fk = q["formKey"];
                    MugshotAliases.TryGetValue(mod, out var aliasFolder);
                    var p = Mugshots.Resolve(MugshotRoot, fk, mod, aliasFolder);   // 1. local pack (incl. cached)
                    if (p is not null && File.Exists(p)) { SendImage(ctx, p); return; }
                    if (FaceFinderEnabled && !string.IsNullOrWhiteSpace(fk))         // 2. FaceFinder online fallback
                    {
                        var got = FaceFinder.Fetch(fk, mod, aliasFolder);   // manual alias also steers the online match
                        if (got is { } img)
                        {
                            if (FaceFinderCache && FaceFinder.CachePath(MugshotRoot, mod, fk!) is { } cachePath)
                                try
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                                    File.WriteAllBytes(cachePath, img.bytes);
                                    Mugshots.AddToIndex(MugshotRoot, fk!, mod, cachePath);   // resolves locally next time
                                }
                                catch { /* cache is best-effort; still serve the bytes */ }
                            SendBytes(ctx, img.bytes, img.contentType); return;
                        }
                    }
                    Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("no mugshot")); return;   // 3. placeholder
                }
                case "/api/mugshot-mods": Send(ctx, 200, "application/json", Json(Mugshots.AvailableMods(MugshotRoot))); return;
                case "/api/mugshot-alias" when ctx.Request.HttpMethod == "POST": HandleMugshotAlias(ctx); return;
                case "/api/demand":
                {
                    var cat = q["category"] ?? "bandit";
                    try
                    {
                        var d = Categories.IsScan(cat)
                            ? Faces.DemandLoadOrder(Game, ResolveActiveLoadOrder())
                            : Faces.Demand(Game, cat);
                        Send(ctx, 200, "application/json", Json(d));
                    }
                    catch (Exception e) { Send(ctx, 400, "application/json", Json(new { error = e.Message })); }
                    return;
                }
                case "/api/boostinfo":
                {
                    var cat = q["category"] ?? "bandit";
                    try
                    {
                        // Scan categories (all_males) can't be boosted — placed uniques, no leveled-list slots.
                        if (Categories.IsScan(cat)) { Send(ctx, 200, "application/json", Json(new { boostable = false, lists = 0 })); return; }
                        int n = Faces.BoostListCount(Game, cat);
                        Send(ctx, 200, "application/json", Json(new { boostable = n > 0, lists = n }));
                    }
                    catch (Exception e) { Send(ctx, 400, "application/json", Json(new { error = e.Message })); }
                    return;
                }
                case "/api/sexplague": Send(ctx, 200, "application/json", Json(SexPlaguePayload())); return;
                case "/api/femnames":
                {
                    var p = FeminineNamesPath();
                    int n = File.Exists(p) ? FeminineNames.Load(p).Count : 0;
                    Send(ctx, 200, "application/json", Json(new { available = File.Exists(p), count = n })); return;
                }
                case "/api/femheights":
                {
                    var fh = FeminineHeights.Load(FeminineHeightsPath());   // null if the yaml is missing/invalid
                    Send(ctx, 200, "application/json", Json(new
                    {
                        available = fh is not null, races = fh?.Heights.Count ?? 0,
                        defaultHeight = fh is null ? "" : fh.Default.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                        heights = fh?.Heights
                    })); return;
                }
                case "/api/audit":
                {
                    var m = q["mod"];
                    if (string.IsNullOrWhiteSpace(m) || !Directory.Exists(m))
                    { Send(ctx, 400, "application/json", Json(new { error = "mod folder not found: " + m })); return; }
                    try { Send(ctx, 200, "application/json", Json(AssetAudit.AuditWith(m!, Mods, Profiles, Profile, GameData))); }
                    catch (Exception e) { Send(ctx, 500, "application/json", Json(new { error = e.Message })); }
                    return;
                }
                case "/api/generate" when ctx.Request.HttpMethod == "POST": HandleGenerate(ctx); return;
                case "/api/settings" when ctx.Request.HttpMethod == "POST": HandleSettings(ctx); return;
                default: Send(ctx, 404, "application/json", Json(new { error = "no such endpoint" })); return;
            }
        }

        // static frontend
        var rel = path is "/" or "" ? "index.html" : path.TrimStart('/');
        var file = Path.GetFullPath(Path.Combine(WebRoot, rel));
        if (!file.StartsWith(Path.GetFullPath(WebRoot), StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
        { Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("not found")); return; }
        Send(ctx, 200, Mime(file), File.ReadAllBytes(file));
    }

    // ---- endpoint bodies ----

    static object ConfigPayload() => new
    {
        game = Game, mods = Mods, outDir = OutDir, voiceMap = VoiceMap,
        profiles = Profiles, profile = Profile, profileList = ListProfiles(),
        botPresets = BotPresets, gameData = GameData, gameVersion = GameCfg.Canon(GameCfg.Release),
        gameVersions = new[] { "SkyrimSE", "SkyrimVR" },
        mugshotRoot = MugshotRoot, libraryRoot = Library.Root(), mugshotAliases = MugshotAliases,
        faceFinderEnabled = FaceFinderEnabled, faceFinderCache = FaceFinderCache,
        settingsPath = Settings.FilePath,
        feminizeDefault = true,
        categories = Categories.All.Select(c => new { c.Key, c.Label, c.Verified, c.KnownReplacers, scan = !string.IsNullOrEmpty(c.Scan) })
    };

    record SettingsReq(string? GameVersion, string? Game, string? Mods, string? Profiles, string? Profile, string? BotPresets, string? GameData, string? MugshotRoot,
                       bool? FaceFinderEnabled, bool? FaceFinderCache, string? OutDir);

    // Persist per-PC settings from the UI: update the live server config + config/settings.json + the
    // global game release. Only non-empty fields are applied (blank = keep current live value).
    static void HandleSettings(HttpListenerContext ctx)
    {
        string body;
        using (var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) body = r.ReadToEnd();
        SettingsReq? req;
        try { req = JsonSerializer.Deserialize<SettingsReq>(body, J); }
        catch (Exception e) { Send(ctx, 400, "application/json", Json(new { error = e.Message })); return; }
        if (req is null) { Send(ctx, 400, "application/json", Json(new { error = "bad body" })); return; }

        bool gameChanged = false;
        if (!string.IsNullOrWhiteSpace(req.Game)) { Game = req.Game!.Trim(); gameChanged = true; }
        bool modsChanged = false;
        if (!string.IsNullOrWhiteSpace(req.Mods)) { Mods = req.Mods!.Trim(); modsChanged = true; }
        // profiles: explicit override wins; else if mods changed, re-derive the sibling profiles dir so
        // loadorder.txt/plugins.txt are found on this PC (the bug where a stale profiles path returned 0).
        if (!string.IsNullOrWhiteSpace(req.Profiles)) Profiles = req.Profiles!.Trim();
        else if (modsChanged) Profiles = DeriveProfiles(Mods);
        if (!string.IsNullOrWhiteSpace(req.Profile)) Profile = req.Profile!.Trim();
        if (!string.IsNullOrWhiteSpace(req.BotPresets)) BotPresets = req.BotPresets!.Trim();
        // mugshot root is nullable-clearable: unlike the others, an empty string is a legitimate value
        // ("no pack configured"), so apply it whenever the field is PRESENT (non-null), blank included.
        if (req.MugshotRoot is not null) { MugshotRoot = req.MugshotRoot.Trim(); Mugshots.Invalidate(); }
        if (req.FaceFinderEnabled is not null) FaceFinderEnabled = req.FaceFinderEnabled.Value;
        if (req.FaceFinderCache is not null) FaceFinderCache = req.FaceFinderCache.Value;
        // output root: present-but-blank means "back to the default <app>/out" (unlike the keep-if-blank paths)
        if (req.OutDir is not null)
            OutDir = string.IsNullOrWhiteSpace(req.OutDir) ? Path.Combine(Settings.AppHome(), "out") : req.OutDir.Trim();
        // base-game Data folder: explicit override wins; else if game changed, re-derive as its folder.
        if (!string.IsNullOrWhiteSpace(req.GameData)) GameData = req.GameData!.Trim();
        else if (gameChanged) GameData = Path.GetDirectoryName(Path.GetFullPath(Game)) ?? Game;
        if (!string.IsNullOrWhiteSpace(req.GameVersion)) GameCfg.Release = GameCfg.Parse(req.GameVersion);

        Game = Norm(Game); Mods = Norm(Mods); Profiles = Norm(Profiles); BotPresets = Norm(BotPresets); GameData = Norm(GameData); MugshotRoot = Norm(MugshotRoot); OutDir = Norm(OutDir);

        // the selected profile may not exist under a newly-derived profiles dir — fall back to the first.
        var profileList = ListProfiles();
        if (profileList.Count > 0 && !profileList.Contains(Profile, StringComparer.OrdinalIgnoreCase))
            Profile = profileList[0];

        // mirror the live values into the persisted settings and write settings.json
        Settings.Current = new AppSettings
        {
            GameVersion = GameCfg.Canon(GameCfg.Release),
            Game = Game, GameData = GameData, Mods = Mods, Profiles = Profiles, Profile = Profile, BotPresets = BotPresets,
            MugshotRoot = MugshotRoot, MugshotAliases = new(MugshotAliases),
            FaceFinderEnabled = FaceFinderEnabled, FaceFinderCache = FaceFinderCache, OutDir = OutDir
        };
        Settings.Save();
        Send(ctx, 200, "application/json", Json(new
        {
            ok = true, saved = Settings.FilePath, gameVersion = GameCfg.Canon(GameCfg.Release),
            profiles = Profiles, profile = Profile, profileList, gameData = GameData, mugshotRoot = MugshotRoot,
            faceFinderEnabled = FaceFinderEnabled, faceFinderCache = FaceFinderCache, outDir = OutDir
        }));
    }

    // Active plugins resolved to real paths, in load order (for scan categories). Empty if the profile
    // has no loadorder.txt. Stock Data = the folder holding the game master (base/CC masters live there).
    static List<string> ResolveActiveLoadOrder()
    {
        var profileDir = Path.Combine(Profiles, Profile);
        return LoadOrderScan.ResolveActivePaths(profileDir, Mods, GameData);
    }

    // MO2 profiles (subfolders of the profiles dir that carry a modlist.txt).
    static List<string> ListProfiles()
    {
        var r = new List<string>();
        if (!Directory.Exists(Profiles)) return r;
        foreach (var d in Directory.EnumerateDirectories(Profiles))
            if (File.Exists(Path.Combine(d, "modlist.txt"))) r.Add(Path.GetFileName(d));
        return r.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // modlist.txt: "+Name" enabled, "-Name" disabled, top line = highest priority. Name = mods subfolder.
    static Dictionary<string, (bool enabled, int idx)>? ReadModlist(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return null;
        var f = Path.Combine(Profiles, profile, "modlist.txt");
        if (!File.Exists(f)) return null;
        var map = new Dictionary<string, (bool, int)>(StringComparer.OrdinalIgnoreCase);
        int idx = 0;
        foreach (var raw in File.ReadAllLines(f))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            char c = line[0];
            if (c != '+' && c != '-') continue;                       // skip separators / other markers
            var name = line[1..];
            if (name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            if (!map.ContainsKey(name)) map[name] = (c == '+', idx++);
        }
        return map;
    }

    record ModEntry(string Name, string Folder, string[] Plugins, bool Enabled, int Order,
                    Dictionary<string, Dictionary<string, Library.RaceCount>>? Summaries);

    // Immediate subfolders of the mods dir that contain a plugin — the source candidates. When a profile
    // is given, each entry carries its enabled state + load-order index (sorted by profile priority).
    static List<ModEntry> ScanMods(string modsPath, string? profile)
    {
        var order = ReadModlist(profile);
        var outList = new List<ModEntry>();
        if (!Directory.Exists(modsPath)) return outList;
        foreach (var dir in Directory.EnumerateDirectories(modsPath))
        {
            string[] plugins;
            try
            {
                plugins = Directory.EnumerateFiles(dir, "*.es*", SearchOption.TopDirectoryOnly)
                    .Where(f => f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                    .Select(f => Path.GetFileName(f)).ToArray();
            }
            catch { continue; }
            if (plugins.Length == 0) continue;
            var name = Path.GetFileName(dir);
            bool enabled = order is null || (order.TryGetValue(name, out var e) && e.enabled);
            int ord = order is not null && order.TryGetValue(name, out var o) ? o.idx : int.MaxValue;
            // attach the saved per-race face summary for any plugin here that's been curated (has a whitelist)
            var summaries = new Dictionary<string, Dictionary<string, Library.RaceCount>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plugins)
                if (Library.HasWhitelist(p) && Library.LoadWhitelistSummary(p) is { Count: > 0 } s) summaries[p] = s;
            outList.Add(new ModEntry(name, dir, plugins, enabled, ord, summaries.Count > 0 ? summaries : null));
        }
        return outList.OrderBy(o => o.Order).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // SexPlague overlay config for the UI (tier labels + default percents) + whether the plugin is installed.
    static object SexPlaguePayload()
    {
        var yaml = Path.Combine(Path.GetDirectoryName(Config) ?? ".", "sexplague.yaml");
        var cfg = SexPlague.Load(File.Exists(yaml) ? yaml : null);
        if (cfg is null) return new { available = false };
        bool installed = Directory.Exists(Mods) && Directory.EnumerateDirectories(Mods)
            .Any(d => { try { return File.Exists(Path.Combine(d, cfg.Plugin)); } catch { return false; } });
        return new
        {
            available = true,
            installed,
            plugin = cfg.Plugin,
            seedFaction = cfg.SeedFaction,
            controllerSpell = cfg.ControllerSpell,
            tiers = cfg.Tiers.Select(t => new { t.Faction, t.Label, t.Percent })
        };
    }

    record FacesReq(List<string>? Sources, bool Library = false);
    record GenSource(string Path, string? Mode);
    record GenReq(string? Category, List<GenSource>? Sources, List<string>? Include, string? Name, string? Out,
                  bool Feminize = true, bool Boost = false, bool Sexplague = false, List<int>? SexplaguePct = null,
                  bool FeminineNames = false, bool BakeTextures = false, bool FeminineHeights = false, bool Runtime = false);

    static string FeminineNamesPath() => Path.Combine(Path.GetDirectoryName(Config) ?? ".", "feminine_names.yaml");
    static string RaceCompatPath() => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Config)) ?? ".", "race_compat.yaml");
    static string FeminineHeightsPath() => Path.Combine(Path.GetDirectoryName(Config) ?? ".", "feminine_heights.yaml");

    static void HandleGenerate(HttpListenerContext ctx)
    {
        string body;
        using (var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) body = r.ReadToEnd();
        var req = JsonSerializer.Deserialize<GenReq>(body, J);
        if (req?.Sources is null || req.Sources.Count == 0 || string.IsNullOrWhiteSpace(req.Name))
        { Send(ctx, 400, "application/json", Json(new { error = "need sources[] and name" })); return; }

        var outFolder = string.IsNullOrWhiteSpace(req.Out)
            ? Path.Combine(OutDir, Path.GetFileNameWithoutExtension(req.Name!)) : req.Out!;

        // curated ids -> temp file for --include. Selection is authoritative: a present-but-empty list
        // means "no faces", not "all faces". Only a null Include (CLI without --include) pools everything.
        string? includeFile = null;
        if (req.Include is not null)
        {
            includeFile = Path.Combine(Path.GetTempPath(), $"facediv-include-{Guid.NewGuid():N}.txt");
            File.WriteAllLines(includeFile, req.Include);
        }

        var a = new List<string> { "generate", "--game", Game, "--category", req.Category ?? "bandit",
                                   "--config", Config, "--voice-map", VoiceMap,
                                   "--out", outFolder, "--name", req.Name! };
        // Scan categories (all_males) resolve targets from the whole active load order — hand the engine
        // the resolved plugin paths (in order) via a temp file.
        string? loFile = null;
        if (Categories.IsScan(req.Category ?? ""))
        {
            var paths = ResolveActiveLoadOrder();
            loFile = Path.Combine(Path.GetTempPath(), $"facediv-lo-{Guid.NewGuid():N}.txt");
            File.WriteAllLines(loFile, paths);
            a.Add("--loadorder"); a.Add(loFile);
        }
        if (!req.Feminize) a.Add("--no-feminize");
        if (req.Boost) a.Add("--boost");
        if (req.Sexplague)
        {
            a.Add("--sexplague");
            if (req.SexplaguePct is { Count: > 0 }) { a.Add("--sexplague-pct"); a.Add(string.Join(",", req.SexplaguePct)); }
        }
        if (req.FeminineNames && File.Exists(FeminineNamesPath())) { a.Add("--feminine-names"); a.Add(FeminineNamesPath()); }
        if (req.FeminineHeights && File.Exists(FeminineHeightsPath())) { a.Add("--feminine-heights"); a.Add(FeminineHeightsPath()); }
        if (req.Runtime) a.Add("--skypatcher");   // SkyPatcher runtime mode: copyVisualStyle lines, no plugin/FaceGen
        // Bake textures: hand the engine the enabled mod folders (MO2 priority) so it can resolve + bake
        // cross-mod face textures (brows/eyes) into a self-contained output.
        string? assetDirsFile = null;
        if (req.BakeTextures)
        {
            var dirs = LoadOrderScan.EnabledMods(Path.Combine(Profiles, Profile), Mods).Select(m => m.folder);
            assetDirsFile = Path.Combine(Path.GetTempPath(), $"facediv-assetdirs-{Guid.NewGuid():N}.txt");
            File.WriteAllLines(assetDirsFile, dirs);
            a.Add("--bake-textures"); a.Add("--asset-dirs"); a.Add(assetDirsFile);
        }
        if (includeFile is not null) { a.Add("--include"); a.Add(includeFile); }
        // Race merger: collect each source's saved extra races into a TSV — `as:` overlays as
        // faceId<TAB>race, `serve:` adopts as faceId<TAB>race<TAB>adopt. Only curated sources (with a
        // whitelist file) contribute; the engine remaps only the faces it pools.
        string? raceOvFile = null;
        {
            var lines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A library build's faces keep their ORIGINAL plugin in their id, so the extra-race picks come from
            // every original plugin the build holds.
            IEnumerable<string> PluginsOf(GenSource s) =>
                LibraryMap.IsLibraryPath(s.Path) && LibraryMap.Load(LibraryMap.MapPathFor(s.Path)) is { } lm
                    ? lm.Faces.Select(f => f.Source).Distinct(StringComparer.OrdinalIgnoreCase)
                    : new[] { Path.GetFileName(s.Path) };
            foreach (var plugin in req.Sources.SelectMany(PluginsOf).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var kv in Library.LoadWhitelistOverrides(plugin))
                    lines.Add($"{plugin}#{kv.Key}\t{kv.Value}");
                foreach (var kv in Library.LoadWhitelistServes(plugin))
                    lines.Add($"{plugin}#{kv.Key}\t{kv.Value}\tadopt");
            }
            if (lines.Count > 0)
            {
                raceOvFile = Path.Combine(Path.GetTempPath(), $"facediv-raceov-{Guid.NewGuid():N}.txt");
                File.WriteAllLines(raceOvFile, lines);
                a.Add("--race-override"); a.Add(raceOvFile);
            }
        }
        foreach (var s in req.Sources)
        {
            if (LibraryMap.IsLibraryPath(s.Path)) { a.Add("--library-map"); a.Add(LibraryMap.MapPathFor(s.Path)); continue; }
            var flag = s.Mode switch { "keep" => "--keep", "disable" => "--disable", "standalone" => "--standalone", _ => "--source" };
            a.Add(flag); a.Add(s.Path);
        }

        var (code, log) = CaptureRun(a.ToArray());
        if (includeFile is not null) { try { File.Delete(includeFile); } catch { } }
        if (loFile is not null) { try { File.Delete(loFile); } catch { } }
        if (assetDirsFile is not null) { try { File.Delete(assetDirsFile); } catch { } }
        if (raceOvFile is not null) { try { File.Delete(raceOvFile); } catch { } }
        var readme = Path.Combine(outFolder, "README.txt");
        Send(ctx, code == 0 ? 200 : 500, "application/json",
            Json(new { ok = code == 0, log, outFolder, readme = File.Exists(readme) ? File.ReadAllText(readme) : null }));
    }

    // Run a command with stdout+stderr captured (single-threaded loop => no console race).
    static (int code, string log) CaptureRun(string[] a) => CaptureRun(a, Generate.Run);
    static (int code, string log) CaptureRun(string[] a, Func<string[], int> cmd)
    {
        var sw = new StringWriter();
        var oldOut = Console.Out; var oldErr = Console.Error;
        Console.SetOut(sw); Console.SetError(sw);
        int code;
        try { code = cmd(a); }
        catch (Exception e) { sw.WriteLine("EXCEPTION: " + e); code = 1; }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
        return (code, sw.ToString());
    }

    // ---- library build: ONE self-contained plugin from every curated mod's whitelisted faces ----
    record LibraryBuildReq(string? Name, bool BakeTextures = true);
    static void HandleLibraryBuild(HttpListenerContext ctx)
    {
        string body; using (var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) body = r.ReadToEnd();
        LibraryBuildReq? req;
        try { req = JsonSerializer.Deserialize<LibraryBuildReq>(body, J) ?? new LibraryBuildReq(null); }
        catch (Exception e) { Send(ctx, 400, "application/json", Json(new { error = e.Message })); return; }
        var name = string.IsNullOrWhiteSpace(req.Name) ? $"FDA_Library_{DateTime.Now:yyMMdd}.esp" : req.Name!.Trim();
        if (!name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) name += ".esp";
        var stem = Path.GetFileNameWithoutExtension(name);
        var outFolder = Path.Combine(OutDir, stem);

        // curated plugins = every whitelist file; locate each plugin among the installed mods (enabled or not —
        // the build harvests files, it doesn't care about the load order); the include list = whitelisted keys
        // minus the global blacklist, as face ids.
        var modsDir = Path.Combine(Library.Root(), "mods");
        var installed = ScanMods(Mods, null);
        var blacklist = Library.LoadBlacklist();
        var sources = new List<string>(); var include = new List<string>(); var notInstalled = new List<string>(); var empty = new List<string>();
        if (Directory.Exists(modsDir))
            foreach (var y in Directory.EnumerateFiles(modsDir, "*.yaml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var plugin = Path.GetFileNameWithoutExtension(y);   // "<plugin>.yaml" -> "<plugin>"
                var keys = Library.LoadWhitelist(plugin) ?? new HashSet<string>();
                var wanted = keys.Where(k => !blacklist.Contains(k)).ToList();
                if (wanted.Count == 0) { empty.Add(plugin); continue; }
                var mod = installed.FirstOrDefault(m => m.Plugins.Contains(plugin, StringComparer.OrdinalIgnoreCase));
                if (mod is null) { notInstalled.Add(plugin); continue; }
                sources.Add(Path.Combine(mod.Folder, plugin));
                include.AddRange(wanted.Select(k => $"{plugin}#{k}"));
            }
        if (sources.Count == 0)
        { Send(ctx, 400, "application/json", Json(new { error = "no curated faces to build: save at least one whitelist in Library mode" + (notInstalled.Count > 0 ? $" (curated but not installed here: {string.Join(", ", notInstalled)})" : "") })); return; }

        var includeFile = Path.Combine(Path.GetTempPath(), $"facediv-libinc-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(includeFile, include);
        // mod folders to find sources' masters + cross-mod textures in: enabled first (priority), then the rest
        var profileDir = Path.Combine(Profiles, Profile);
        var dirs = LoadOrderScan.EnabledMods(profileDir, Mods).Select(m => m.folder)
            .Concat(LoadOrderScan.DisabledMods(profileDir, Mods).Select(m => m.folder)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dirsFile = Path.Combine(Path.GetTempPath(), $"facediv-libdirs-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(dirsFile, req.BakeTextures ? dirs : dirs.Where(d => sources.Any(s => s.StartsWith(d, StringComparison.OrdinalIgnoreCase))));
        var mapPath = Path.Combine(LibraryMap.BuildsDir(), stem + ".yaml");

        var a = new List<string> { "build-library", "--game", Game, "--out", outFolder, "--name", name, "--include", includeFile, "--asset-dirs", dirsFile, "--map", mapPath };
        foreach (var s in sources) { a.Add("--source"); a.Add(s); }
        var (code, log) = CaptureRun(a.ToArray(), LibraryBuild.Run);
        try { File.Delete(includeFile); File.Delete(dirsFile); } catch { }
        var notes = new List<string>();
        if (notInstalled.Count > 0) notes.Add("curated but NOT installed on this PC (skipped): " + string.Join(", ", notInstalled));
        if (empty.Count > 0) notes.Add("curated with no faces ticked (skipped): " + string.Join(", ", empty));
        var readme = Path.Combine(outFolder, "README.txt");
        Send(ctx, code == 0 ? 200 : 500, "application/json", Json(new
        {
            ok = code == 0, log = (notes.Count > 0 ? string.Join("\n", notes) + "\n\n" : "") + log, outFolder, map = mapPath,
            sources = sources.Count, faces = include.Count, readme = File.Exists(readme) ? File.ReadAllText(readme) : null
        }));
    }

    // ---- mugshots manual override ----
    record MugshotAliasReq(string? Mod, string? Folder);
    static void HandleMugshotAlias(HttpListenerContext ctx)
    {
        string body; using (var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) body = r.ReadToEnd();
        MugshotAliasReq? req;
        try { req = JsonSerializer.Deserialize<MugshotAliasReq>(body, J); }
        catch (Exception e) { Send(ctx, 400, "application/json", Json(new { error = e.Message })); return; }
        if (req is null || string.IsNullOrWhiteSpace(req.Mod)) { Send(ctx, 400, "application/json", Json(new { error = "mod required" })); return; }
        var mod = req.Mod.Trim();
        if (string.IsNullOrWhiteSpace(req.Folder)) MugshotAliases.Remove(mod);   // blank => back to auto
        else MugshotAliases[mod] = req.Folder.Trim();
        Settings.Current.MugshotAliases = new(MugshotAliases);
        Settings.Save();
        Send(ctx, 200, "application/json", Json(new { ok = true, aliases = MugshotAliases }));
    }

    // ---- library ----

    // Apply the personal library to a harvested face list (Mod Creator Mode). Keep a face when its source
    // has no whitelist (uncurated => pool all) OR the face is whitelisted; always drop a face whose NPC is
    // globally blacklisted — the blacklist wins even over a whitelist entry.
    static List<Faces.FaceInfo> ApplyLibrary(List<Faces.FaceInfo> faces)
    {
        var blacklist = Library.LoadBlacklist();
        var wl = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? WlFor(string src)
        {
            if (!wl.TryGetValue(src, out var s)) { s = Library.LoadWhitelist(src); wl[src] = s; }
            return s;
        }
        // Also attach each face's saved `as:` double-dip (compat-guarded, same rule as the engine) so the UI
        // treats the face as serving BOTH races — otherwise a face curated for its vampire variant looks
        // "unusable" in a vampire category, isn't auto-selected, and never reaches the engine.
        var ov = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> OvFor(string src)
        {
            if (!ov.TryGetValue(src, out var m)) { m = Library.LoadWhitelistOverrides(src); ov[src] = m; }
            return m;
        }
        // ...and its saved `serve:` adopt race (a custom-race face pooled under a vanilla race). No compat
        // guard — the target adopts the face's race, so the NPC is a consistent whole (same rule as the engine).
        var sv = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> SvFor(string src)
        {
            if (!sv.TryGetValue(src, out var m)) { m = Library.LoadWhitelistServes(src); sv[src] = m; }
            return m;
        }
        return faces.Where(f =>
        {
            if (blacklist.Contains(f.FormKey)) return false;   // global veto
            var s = WlFor(f.Source);
            return s is null || s.Contains(f.FormKey);          // uncurated source => keep all
        })
        .Select(f => OvFor(f.Source).TryGetValue(f.FormKey, out var a)
                     && !a.Equals(f.PoolRace, StringComparison.OrdinalIgnoreCase) && RaceCompat.AreCompatible(f.PoolRace, a)
                     ? f with { As = a } : f)
        .Select(f => SvFor(f.Source).TryGetValue(f.FormKey, out var r)
                     && !r.Equals(f.PoolRace, StringComparison.OrdinalIgnoreCase)
                     ? f with { Serve = r } : f)
        .ToList();
    }

    record FaceEntryReq(string? Key, string? As, string? EditorID, string? Race, string? Sex, string? Serve = null);
    record SaveWhitelistReq(string? Plugin, List<FaceEntryReq>? Faces, bool Remove = false);
    record SaveBlacklistReq(List<FaceEntryReq>? Npcs);

    static Library.FaceEntry ToEntry(FaceEntryReq r) => new() { Key = r.Key ?? "", As = r.As, Serve = r.Serve, EditorID = r.EditorID, Race = r.Race, Sex = r.Sex };

    static void HandleSaveWhitelist(HttpListenerContext ctx)
    {
        string body; using (var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) body = r.ReadToEnd();
        SaveWhitelistReq? req;
        try { req = JsonSerializer.Deserialize<SaveWhitelistReq>(body, J); }
        catch (Exception e) { Send(ctx, 400, "application/json", Json(new { error = e.Message })); return; }
        if (req is null || string.IsNullOrWhiteSpace(req.Plugin)) { Send(ctx, 400, "application/json", Json(new { error = "plugin required" })); return; }
        if (req.Remove) { var removed = Library.RemoveWhitelist(req.Plugin!); Send(ctx, 200, "application/json", Json(new { ok = true, removed })); return; }
        var faces = (req.Faces ?? new()).Select(ToEntry).ToList();
        var path = Library.SaveWhitelist(req.Plugin!, faces);
        Send(ctx, 200, "application/json", Json(new { ok = true, path, count = faces.Count(f => !string.IsNullOrWhiteSpace(f.Key)) }));
    }

    static void HandleSaveBlacklist(HttpListenerContext ctx)
    {
        string body; using (var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) body = r.ReadToEnd();
        SaveBlacklistReq? req;
        try { req = JsonSerializer.Deserialize<SaveBlacklistReq>(body, J); }
        catch (Exception e) { Send(ctx, 400, "application/json", Json(new { error = e.Message })); return; }
        var npcs = (req?.Npcs ?? new()).Select(ToEntry).ToList();
        var path = Library.SaveBlacklist(npcs);
        Send(ctx, 200, "application/json", Json(new { ok = true, path, count = npcs.Count(n => !string.IsNullOrWhiteSpace(n.Key)) }));
    }

    // ---- http helpers ----
    static byte[] Json(object o) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o, J));

    static void Send(HttpListenerContext ctx, int status, string mime, byte[] body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = mime;
        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.OutputStream.Close();
    }
    static void TrySend(HttpListenerContext ctx, int s, string m, byte[] b) { try { Send(ctx, s, m, b); } catch { } }

    // Mugshot PNGs are static content — let the browser cache them (the grid re-requests per <img>),
    // overriding the no-store default the JSON API uses.
    static void SendImage(HttpListenerContext ctx, string path)
    {
        var ct = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".webp" => "image/webp", ".jpg" or ".jpeg" => "image/jpeg", _ => "image/png"
        };
        SendBytes(ctx, File.ReadAllBytes(path), ct);
    }
    static void SendBytes(HttpListenerContext ctx, byte[] body, string contentType)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Cache-Control"] = "private, max-age=3600";
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.OutputStream.Close();
    }

    static string Mime(string f) => Path.GetExtension(f).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8", ".json" => "application/json",
        ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream"
    };

    // App home = nearest ancestor of the exe that has both config/ and (eventually) web/.
    static string AppHome()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (var p = d; p != null; p = p.Parent)
            if (Directory.Exists(Path.Combine(p.FullName, "config")) &&
                File.Exists(Path.Combine(p.FullName, "config", "categories.yaml")))
                return p.FullName;
        return "C:/Modlists/SME/FaceDiversityApp";
    }
}
