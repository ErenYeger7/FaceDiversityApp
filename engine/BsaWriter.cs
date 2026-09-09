using System.Text;
using K4os.Compression.LZ4.Streams;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

// Writes Skyrim SE archives (BSA version 105) from loose files, optionally LZ4-compressed (the SSE scheme:
// per file, uint32 original size + an LZ4 *frame*). Layout per the TES4 BSA format:
//   header(36) · folder records (24 each, sorted by hash) · per folder: bzstring name + file records (16 each,
//   sorted by hash) · file name block (zstrings) · file data.
// The game finds entries by the TES4 hash, so every name is lower-cased with backslashes before hashing.
// Offsets are 32-bit, so ONE archive must stay under 4 GiB: `WriteSplit` bin-packs a file set into as many
// archives as needed (the caller names them; the game auto-loads exactly two per plugin — <Plugin>.bsa and
// "<Plugin> - Textures.bsa" — so overflow archives hang off tiny dummy ESL plugins).
// Always cross-validated by reading the result back with Mutagen's archive reader (an independent parser)
// and comparing every file byte-for-byte before any loose file is removed — a malformed archive means
// missing faces or a CTD, and that must be caught here, not in game.
static class BsaWriter
{
    public const uint Version105 = 105;
    const uint FlagDirNames = 0x1, FlagFileNames = 0x2, FlagCompressed = 0x4;
    public const ushort FileFlagMeshes = 0x1, FileFlagTextures = 0x2, FileFlagMisc = 0x100;
    // leave headroom under the 32-bit offset space for headers/records. FDA_BSA_MAX (bytes) overrides it —
    // test hook so the split path can be exercised without a 4 GiB fixture.
    public static readonly long MaxArchiveData =
        long.TryParse(Environment.GetEnvironmentVariable("FDA_BSA_MAX"), out var mx) && mx > 0 ? mx : 0xFFFF_FFFFL - 64L * 1024 * 1024;

    public record Result(string Path, int Files, long BytesIn, long BytesOut, bool Compressed);

    // TES4 hash of a name (no extension) + extension (with the dot, or "" for folders).
    public static ulong Hash(string name, string ext)
    {
        name = name.ToLowerInvariant().Replace('/', '\\'); ext = ext.ToLowerInvariant();
        uint hash1 = 0; int len = name.Length;
        if (len > 0)
            hash1 = (uint)(byte)name[len - 1] | ((len > 2 ? (uint)(byte)name[len - 2] : 0u) << 8) | ((uint)len << 16) | ((uint)(byte)name[0] << 24);
        switch (ext)
        {
            case ".kf": hash1 |= 0x80; break;
            case ".nif": hash1 |= 0x8000; break;
            case ".dds": hash1 |= 0x8080; break;
            case ".wav": hash1 |= 0x80000000; break;
        }
        uint hash2 = 0;
        for (int i = 1; i < len - 2; i++) hash2 = unchecked(hash2 * 0x1003f + (byte)name[i]);
        uint hash3 = 0;
        foreach (var c in ext) hash3 = unchecked(hash3 * 0x1003f + (byte)c);
        return ((ulong)unchecked(hash2 + hash3) << 32) | hash1;
    }

    // One entry ready to be written: its (possibly compressed) bytes live in a temp file so a multi-GB set
    // never has to sit in memory.
    sealed record Entry(string Rel, string Folder, string File, ulong FolderHash, ulong FileHash, string Blob, long Size, long RawSize);

    static Entry Prepare(string root, string rel0, bool compress, string tmpDir, int idx)
    {
        var rel = rel0.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        var folder = Path.GetDirectoryName(rel)!.Replace('/', '\\');
        var file = Path.GetFileName(rel);
        var src = Path.Combine(root, rel0);
        var blob = Path.Combine(tmpDir, idx.ToString());
        long raw = new FileInfo(src).Length;
        if (compress)
        {
            using var fin = File.OpenRead(src);
            using var fout = File.Create(blob);
            fout.Write(BitConverter.GetBytes((uint)raw));
            using (var lz = LZ4Stream.Encode(fout, new LZ4EncoderSettings(), leaveOpen: true)) fin.CopyTo(lz);
        }
        else blob = src;   // uncompressed: write straight from the original
        return new Entry(rel, folder, file, Hash(folder, ""), Hash(Path.GetFileNameWithoutExtension(file), Path.GetExtension(file)),
                         blob, new FileInfo(blob).Length, raw);
    }

    // Pack `relFiles` (data-relative, under root) into as many archives as the 4 GiB limit needs. `nameFor(i)`
    // gives the path of archive i (0-based). Files are assigned in order, so the caller can keep related
    // files together by ordering them. Returns one Result per archive written, in order.
    public static List<Result> WriteSplit(string root, IReadOnlyList<string> relFiles, bool compress, ushort fileFlags, Func<int, string> nameFor)
    {
        var tmpDir = Path.Combine(root, ".bsa_tmp"); Directory.CreateDirectory(tmpDir);
        var results = new List<Result>();
        try
        {
            var entries = relFiles.Select((r, i) => Prepare(root, r, compress, tmpDir, i)).ToList();
            var groups = new List<List<Entry>>(); var cur = new List<Entry>(); long curSize = 0;
            foreach (var e in entries)
            {
                if (e.Size > MaxArchiveData) throw new InvalidOperationException($"{e.Rel} alone is {e.Size / (1024.0 * 1024 * 1024):0.00} GiB — cannot fit any BSA");
                if (cur.Count > 0 && curSize + e.Size + 64L * (cur.Count + 1) > MaxArchiveData) { groups.Add(cur); cur = new(); curSize = 0; }
                cur.Add(e); curSize += e.Size;
            }
            if (cur.Count > 0) groups.Add(cur);
            for (int gi = 0; gi < groups.Count; gi++)
                results.Add(WriteOne(nameFor(gi), groups[gi], compress, fileFlags));
            return results;
        }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }

    static Result WriteOne(string bsaPath, List<Entry> entries, bool compress, ushort fileFlags)
    {
        var enc = Encoding.Latin1;
        var byFolder = new SortedDictionary<ulong, (string folder, List<Entry> files)>();
        foreach (var e in entries)
        {
            if (!byFolder.TryGetValue(e.FolderHash, out var g)) byFolder[e.FolderHash] = g = (e.Folder, new());
            g.files.Add(e);
        }
        var folders = byFolder.Values.ToList();
        foreach (var f in folders) f.files.Sort((a, b) => a.FileHash.CompareTo(b.FileHash));
        int fileCount = entries.Count;
        uint totalFolderNameLen = (uint)folders.Sum(f => enc.GetByteCount(f.folder) + 1);
        uint totalFileNameLen = (uint)folders.Sum(f => f.files.Sum(x => enc.GetByteCount(x.File) + 1));
        long headerLen = 36, folderRecsLen = 24L * folders.Count;
        long fileRecBlocksLen = folders.Sum(f => 1 + enc.GetByteCount(f.folder) + 1 + 16L * f.files.Count);
        long dataStart = headerLen + folderRecsLen + fileRecBlocksLen + totalFileNameLen;
        long total = dataStart + entries.Sum(e => e.Size);
        if (total > 0xFFFF_FFFFL) throw new InvalidOperationException($"archive would be {total / (1024.0 * 1024 * 1024):0.00} GiB — over the 4 GiB BSA limit");

        using var fs = new FileStream(bsaPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new BinaryWriter(fs);
        w.Write(Encoding.ASCII.GetBytes("BSA\0"));
        w.Write(Version105);
        w.Write((uint)36);
        w.Write(FlagDirNames | FlagFileNames | (compress ? FlagCompressed : 0));
        w.Write((uint)folders.Count);
        w.Write((uint)fileCount);
        w.Write(totalFolderNameLen);
        w.Write(totalFileNameLen);
        w.Write(fileFlags);
        w.Write((ushort)0);
        // folder records: offset = this folder's file-record block position + totalFileNameLength (format quirk)
        long blockPos = headerLen + folderRecsLen;
        foreach (var (fh, g) in byFolder)
        {
            w.Write(fh); w.Write((uint)g.files.Count); w.Write((uint)0); w.Write((ulong)(blockPos + totalFileNameLen));
            blockPos += 1 + enc.GetByteCount(g.folder) + 1 + 16L * g.files.Count;
        }
        long dataPos = dataStart;
        foreach (var f in folders)
        {
            var fb = enc.GetBytes(f.folder);
            w.Write((byte)(fb.Length + 1)); w.Write(fb); w.Write((byte)0);
            foreach (var x in f.files) { w.Write(x.FileHash); w.Write((uint)x.Size); w.Write((uint)dataPos); dataPos += x.Size; }
        }
        foreach (var f in folders) foreach (var x in f.files) { w.Write(enc.GetBytes(x.File)); w.Write((byte)0); }
        if (fs.Position != dataStart) throw new InvalidOperationException($"BSA layout mismatch: header/records ended at {fs.Position}, expected {dataStart}");
        w.Flush();
        foreach (var f in folders) foreach (var x in f.files) { using var bin = File.OpenRead(x.Blob); bin.CopyTo(fs); }
        fs.Flush();
        return new Result(bsaPath, fileCount, entries.Sum(e => e.RawSize), fs.Length, compress);
    }

    // Independent-parser gate over a SET of archives: every listed file must be found in exactly one archive
    // and read back byte-for-byte equal to its loose original. Returns null when OK, else the reason.
    public static string? Verify(IEnumerable<string> bsaPaths, string root, IReadOnlyList<string> relFiles)
    {
        try
        {
            var want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in relFiles) want[r.Replace('/', '\\').TrimStart('\\')] = r;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bsa in bsaPaths)
                foreach (var f in Archive.CreateReader(GameRelease.SkyrimSE, bsa).Files)
                {
                    var p = f.Path.Replace('/', '\\').TrimStart('\\');
                    if (!want.TryGetValue(p, out var rel)) return $"unexpected entry {p} in {Path.GetFileName(bsa)}";
                    if (!seen.Add(p)) return $"{p} appears in more than one archive";
                    byte[] got; using (var s = f.AsStream()) { using var ms = new MemoryStream(); s.CopyTo(ms); got = ms.ToArray(); }
                    var orig = File.ReadAllBytes(Path.Combine(root, rel));
                    if (!got.AsSpan().SequenceEqual(orig)) return $"content mismatch for {p} ({got.Length} vs {orig.Length} bytes)";
                }
            if (seen.Count != want.Count) return $"{want.Count - seen.Count} file(s) missing from the archives";
            return null;
        }
        catch (Exception e) { return "read-back failed: " + e.Message; }
    }
}
