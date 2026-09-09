using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

// Resolves a source mod's assets from LOOSE files first, then any BSA in its folder — so the engine
// works whether a replacer ships loose or packed. Used for FaceGen (needed even in keep-mode when a
// source packs its FaceGen) and for unpacking in deep-copy mode.
sealed class SourceAssets
{
    readonly string folder;
    readonly Dictionary<string, IArchiveFile> bsa = new(StringComparer.OrdinalIgnoreCase);
    // Encoding-tolerant fallback index. A texture folder can carry a non-ASCII byte (e.g. "00Lily·"):
    // Mutagen decodes the BSA name with the game codepage (the odd byte -> a replacement/undisplayable
    // char) while our NIF paths are read byte-preserving (Latin1), so the SAME source byte becomes two
    // different chars and an exact lookup misses. Keying a copy with every non-ASCII run flattened to one
    // sentinel makes both sides agree regardless of how each decoder rendered that byte.
    readonly Dictionary<string, IArchiveFile> bsaAscii = new(StringComparer.OrdinalIgnoreCase);

    public SourceAssets(string folder)
    {
        this.folder = folder;
        if (!Directory.Exists(folder)) return;
        foreach (var b in Directory.EnumerateFiles(folder, "*.bsa"))
            foreach (var f in Archive.CreateReader(GameRelease.SkyrimSE, b).Files)
            {
                var k = Norm(f.Path);
                bsa[k] = f;              // later BSAs win ties; fine for a single mod's own archives
                var ak = AsciiFold(k);
                if (ak != k) bsaAscii[ak] = f;   // only paths with a non-ASCII byte need the fallback
            }
    }

    static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\');

    // Flatten every maximal run of non-ASCII chars to a single '?'. Run-collapsing (not char-by-char)
    // absorbs the case where the two decoders emit a different NUMBER of replacement chars for one byte.
    // Public + reused by LoadOrderAssets so the LOOSE-file path folds identically to the BSA path.
    public static string AsciiFold(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool inRun = false;
        foreach (var c in s)
        {
            if (c <= '\x7f') { sb.Append(c); inRun = false; }
            else if (!inRun) { sb.Append('?'); inRun = true; }
        }
        return sb.ToString();
    }
    static byte[] Bytes(IArchiveFile f) { using var s = f.AsStream(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }

    // Existence only (loose or BSA) — no bytes read; used for the FaceGen QA flag on every harvested face.
    public bool Has(string rel)
    {
        if (File.Exists(Path.Combine(folder, rel))) return true;
        var r = Norm(rel);
        if (bsa.ContainsKey(r)) return true;
        var ar = AsciiFold(r);
        return ar != r && bsaAscii.ContainsKey(ar);
    }

    public byte[]? Get(string rel)
    {
        var loose = Path.Combine(folder, rel);
        if (File.Exists(loose)) return File.ReadAllBytes(loose);
        var r = Norm(rel);
        if (bsa.TryGetValue(r, out var f)) return Bytes(f);                 // exact
        var ar = AsciiFold(r);
        if (ar != r && bsaAscii.TryGetValue(ar, out f)) return Bytes(f);    // non-ASCII-tolerant fallback
        return null;
    }

    // Every meshes\/textures\ asset (loose ∪ BSA) EXCEPT facegendata (FaceGen is re-keyed per-face).
    public IEnumerable<(string rel, byte[] data)> Unpackable()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var top in new[] { "meshes", "textures" })
        {
            var t = Path.Combine(folder, top);
            if (!Directory.Exists(t)) continue;
            foreach (var f in Directory.EnumerateFiles(t, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(folder, f);
                if (Keep(rel) && seen.Add(Norm(rel))) yield return (rel, File.ReadAllBytes(f));
            }
        }
        foreach (var kv in bsa)
            if (Keep(kv.Key) && seen.Add(kv.Key)) yield return (kv.Key, Bytes(kv.Value));
    }

    static bool Keep(string rel)
    {
        var p = rel.Replace('/', '\\').ToLowerInvariant();
        return (p.StartsWith("meshes\\") || p.StartsWith("textures\\")) && !p.Contains("facegendata\\");
    }
}
