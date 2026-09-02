// Resolves a face to a local mugshot PNG from EasyNPC / NPC-Plugin-Chooser-2 style packs.
//
// Layout (verified): <packRoot>/<AppearanceModName>/<originMaster>/00<low6hex>.png. The middle+leaf identify
// the NPC (origin master + origin FormID, first byte zeroed); the TOP folder identifies WHICH appearance
// mod's rendition it is. EasyNPC/NPC-PC-2 match that AppearanceModName folder to the installed mod (NPC-PC-2
// "selects mods"); on a mismatch the user picks the folder manually. We do the same:
//   1. exact override (a saved alias: this source mod -> that pack folder), else
//   2. normalized match of the face's SOURCE MOD folder name to the pack's AppearanceModName folder, else
//   3. the sole candidate if this NPC exists under exactly one folder (why a single installed pack "just
//      works"), else
//   4. null — several packs have this NPC and none matched, so we DON'T guess (that guess was the bug:
//      first-wins always returned the same/alphabetically-first pack's face).
//
// Keying only on (master, file) — the old behavior — collapsed the AppearanceModName level, so with several
// packs installed it always returned the first indexed one regardless of which mod the face came from.
static class Mugshots
{
    // packRoot -> ( "<master>/<file>" (lower) -> candidates ), candidate = (mod folder name, normalized, path)
    static readonly Dictionary<string, Dictionary<string, List<(string mod, string modNorm, string path)>>> _cache
        = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, List<string>> _modsCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new();

    public static void Invalidate() { lock (_lock) { _cache.Clear(); _modsCache.Clear(); } }

    // Fold a name to letters+digits, lowercased — tolerant of apostrophes/spaces/punctuation differences
    // between a mod-manager folder and a pack folder ("Pandorable's NPCs" == "Pandorables NPCs").
    static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    static Dictionary<string, List<(string mod, string modNorm, string path)>> Index(string root)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(root, out var idx)) return idx;
            idx = new(StringComparer.OrdinalIgnoreCase);
            var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".png" && ext != ".webp" && ext != ".jpg" && ext != ".jpeg") continue; // FaceFinder cache = .webp
                    var masterDir = Path.GetDirectoryName(f);
                    var master = Path.GetFileName(masterDir) ?? "";
                    if (master.Length == 0) continue;
                    var modName = Path.GetFileName(Path.GetDirectoryName(masterDir)) ?? ""; // grandparent = AppearanceModName
                    // key on the filename STEM (no extension) so a .webp cache and a .png pack collide correctly
                    var key = master.ToLowerInvariant() + "/" + Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if (!idx.TryGetValue(key, out var list)) { list = new(); idx[key] = list; }
                    list.Add((modName, Norm(modName), f));
                    if (modName.Length > 0) mods.Add(modName);
                }
            }
            catch { /* unreadable root => empty index */ }
            _cache[root] = idx;
            _modsCache[root] = mods.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return idx;
        }
    }

    // The distinct appearance-mod folders across the pack root — for the manual "pick a folder" dropdown.
    public static List<string> AvailableMods(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return new();
        Index(root);
        lock (_lock) { return _modsCache.TryGetValue(root, out var m) ? new List<string>(m) : new(); }
    }

    // formKey as Faces emits it ("01326A:Skyrim.esm"); sourceMod = the face's mod-manager folder name;
    // aliasFolder = an explicit saved override for that source mod. Returns the PNG path, or null.
    public static string? Resolve(string? root, string? formKey, string? sourceMod, string? aliasFolder)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(formKey)) return null;
        int colon = formKey!.IndexOf(':');
        if (colon <= 0 || colon >= formKey.Length - 1) return null;
        var idHex = formKey[..colon].Trim();
        var master = formKey[(colon + 1)..].Trim();
        if (idHex.Length == 0 || master.Length == 0) return null;
        var last6 = idHex.Length <= 6 ? idHex.PadLeft(6, '0') : idHex[^6..];
        var key = master.ToLowerInvariant() + "/00" + last6.ToLowerInvariant();   // stem — matches .png OR .webp
        var idx = Index(root);
        if (!idx.TryGetValue(key, out var candidates) || candidates.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(aliasFolder))                      // 1. explicit manual override
        {
            var an = Norm(aliasFolder);
            foreach (var c in candidates) if (c.modNorm == an) return c.path;
        }
        if (!string.IsNullOrWhiteSpace(sourceMod))                        // 2. auto: source mod == pack folder
        {
            var sn = Norm(sourceMod);
            foreach (var c in candidates) if (c.modNorm == sn) return c.path;
        }
        if (candidates.Count == 1) return candidates[0].path;            // 3. unambiguous single pack
        return null;                                                      // 4. ambiguous -> don't guess
    }

    // Insert a just-cached FaceFinder image into the live index so the NEXT request resolves it locally
    // (no re-fetch), without rebuilding the whole index. Keyed by (master, stem) + tagged with its source mod.
    public static void AddToIndex(string root, string formKey, string sourceMod, string path)
    {
        int c = formKey.IndexOf(':');
        if (c <= 0 || c >= formKey.Length - 1) return;
        var id = formKey[..c].Trim();
        var master = formKey[(c + 1)..].Trim();
        if (id.Length == 0 || master.Length == 0) return;
        var last6 = id.Length <= 6 ? id.PadLeft(6, '0') : id[^6..];
        var key = master.ToLowerInvariant() + "/00" + last6.ToLowerInvariant();
        lock (_lock)
        {
            if (!_cache.TryGetValue(root, out var idx)) return;   // root not indexed yet -> next rebuild picks it up
            if (!idx.TryGetValue(key, out var list)) { list = new(); idx[key] = list; }
            list.Add((sourceMod, Norm(sourceMod), path));
            if (_modsCache.TryGetValue(root, out var mods) && !string.IsNullOrEmpty(sourceMod) && !mods.Contains(sourceMod)) mods.Add(sourceMod);
        }
    }
}
