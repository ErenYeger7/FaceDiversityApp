// Resolves a face to a local mugshot PNG from an EasyNPC / NPC-Plugin-Chooser-2 style pack.
//
// PROVEN join (verified 87/87 against a real Pandorable pack + PAN_NPCs.esp): these packs key images by
// the NPC's ORIGIN identity, laid out as  <packRoot>/<any mod-grouping folder>/<originMaster>/<file>.png
// where <file> = the 8-hex FormID with its first byte (load-order index) zeroed, i.e. "00" + the low
// 6 hex of the FormID. Since a replacer's override record keeps the ORIGIN FormKey, Faces already hands
// us exactly (originMaster, originId) — so resolution is a dictionary lookup, no per-mod mapping needed.
//
// The mod-grouping folder (Males/Vampires/Faces/…) is intentionally collapsed: we key only on
// (master, file). One NPC can appear under several grouping folders in a pack (e.g. a close-up under
// Faces/ and a body shot under NPCs/); first-wins is fine for a thumbnail.
//
// NOT tested: a pack for a mod that ADDS new NPCs under its own light (ESPFE) plugin — the FE-index
// filename convention there is undocumented. Vanilla-override replacers (the entire basis of face
// harvesting) are fully covered.
static class Mugshots
{
    // packRoot -> ("<master-lower>/<file-lower>" -> full path). Built once per root, cheap to rebuild.
    static readonly Dictionary<string, Dictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new();

    public static void Invalidate() { lock (_lock) _cache.Clear(); }

    static Dictionary<string, string> Index(string root)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(root, out var idx)) return idx;
            idx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories))
                {
                    var parent = Path.GetFileName(Path.GetDirectoryName(f)) ?? "";   // = origin master (e.g. skyrim.esm)
                    if (parent.Length == 0) continue;
                    var key = parent.ToLowerInvariant() + "/" + Path.GetFileName(f).ToLowerInvariant();
                    idx.TryAdd(key, f);   // first-wins across grouping folders
                }
            }
            catch { /* unreadable root => empty index, everything falls back */ }
            _cache[root] = idx;
            return idx;
        }
    }

    // formKey as Faces emits it, e.g. "01326A:Skyrim.esm". Returns the PNG path, or null if none.
    public static string? Resolve(string? root, string? formKey)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(formKey))
            return null;
        int colon = formKey!.IndexOf(':');
        if (colon <= 0 || colon >= formKey.Length - 1) return null;
        var idHex = formKey[..colon].Trim();
        var master = formKey[(colon + 1)..].Trim();
        if (idHex.Length == 0 || master.Length == 0) return null;
        // low 6 hex, first byte zeroed => "00" + last6
        var last6 = idHex.Length <= 6 ? idHex.PadLeft(6, '0') : idHex[^6..];
        var file = "00" + last6 + ".png";
        var key = master.ToLowerInvariant() + "/" + file.ToLowerInvariant();
        return Index(root).TryGetValue(key, out var p) ? p : null;
    }
}
