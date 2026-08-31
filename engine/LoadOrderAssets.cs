// Resolves a relative asset path across an ordered set of mod folders (MO2 priority) — loose files
// first (cheap), then BSAs (lazily indexed per folder). Used by the asset audit (which mod provides a
// texture?) and by the "bake textures" generate option (fetch the bytes to bake in). Mirrors MO2's VFS.
sealed class LoadOrderAssets
{
    readonly List<(string name, string folder)> dirs;
    readonly Dictionary<string, SourceAssets?> bsaCache = new(StringComparer.OrdinalIgnoreCase);

    public LoadOrderAssets(IEnumerable<(string name, string folder)> ordered) => dirs = ordered.ToList();

    static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\');
    static bool HasNonAscii(string s) { foreach (var c in s) if (c > '\x7f') return true; return false; }

    // Loose-file equivalent of SourceAssets' non-ASCII-tolerant BSA lookup: an odd byte in a folder name
    // is decoded one way in our NIF path (Latin1) and another on the real filesystem, so an exact
    // File.Exists misses. Only invoked when the rel path HAS a non-ASCII byte and the exact path failed;
    // walks the tree segment-by-segment, fold-matching any segment that doesn't exist verbatim. Returns
    // the real on-disk path, or null.
    static string? LooseFolded(string folder, string r)
    {
        if (!Directory.Exists(folder)) return null;
        var segs = r.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var cur = folder;
        for (int i = 0; i < segs.Length; i++)
        {
            bool last = i == segs.Length - 1;
            var exact = Path.Combine(cur, segs[i]);
            if (last ? File.Exists(exact) : Directory.Exists(exact)) { cur = exact; continue; }
            var fold = SourceAssets.AsciiFold(segs[i]);
            string? hit = null;
            try
            {
                foreach (var e in last ? Directory.EnumerateFiles(cur) : Directory.EnumerateDirectories(cur))
                    if (SourceAssets.AsciiFold(Path.GetFileName(e)).Equals(fold, StringComparison.OrdinalIgnoreCase))
                    { hit = e; break; }
            }
            catch { return null; }
            if (hit is null) return null;
            cur = hit;
        }
        return cur;
    }

    SourceAssets? Bsa(string folder)
    {
        if (!bsaCache.TryGetValue(folder, out var sa))
        {
            bool has = false;
            try { has = Directory.EnumerateFiles(folder, "*.bsa").Any(); } catch { }
            bsaCache[folder] = sa = has ? new SourceAssets(folder) : null;
        }
        return sa;
    }

    // Providing mod name for a rel path, or null if it resolves nowhere.
    public string? ResolveName(string rel)
    {
        var r = Norm(rel); var odd = HasNonAscii(r);
        foreach (var (name, folder) in dirs)
            if (File.Exists(Path.Combine(folder, r)) || (odd && LooseFolded(folder, r) != null)) return name;
        foreach (var (name, folder) in dirs)
            if (Bsa(folder)?.Get(r) != null) return name;
        return null;
    }

    // Bytes of a rel path from the first folder that has it (loose ∪ BSA), or null.
    public byte[]? ResolveBytes(string rel)
    {
        var r = Norm(rel); var odd = HasNonAscii(r);
        foreach (var (_, folder) in dirs)
        {
            var lp = Path.Combine(folder, r);
            if (File.Exists(lp)) { try { return File.ReadAllBytes(lp); } catch { } }
            else if (odd) { var fp = LooseFolded(folder, r); if (fp != null) { try { return File.ReadAllBytes(fp); } catch { } } }
        }
        foreach (var (_, folder) in dirs)
        {
            var b = Bsa(folder)?.Get(r);
            if (b != null) return b;
        }
        return null;
    }
}
