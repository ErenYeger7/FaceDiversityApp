using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;

// Inspects a face-source plugin and decides HOW to harvest from it — programmatically, so the UI can
// show the auto-decision (and let the user override). This is the "test the esp first" step.
static class Classify
{
    public static readonly HashSet<string> BaseMasters = new(StringComparer.OrdinalIgnoreCase)
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    public record Result(
        string Plugin, string Folder, int FemaleNpcs, int OwnHdpt,
        bool OverridesVanilla, bool UsesCustomRace, bool HasBsa, bool ShipsRaceTextures, bool FaceGenLoose,
        string Mode, string Why);

    static bool Fem(INpcGetter n) => n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);

    public static Result Inspect(string pluginPath)
    {
        using var sm = SkyrimMod.CreateFromBinaryOverlay(new ModPath(pluginPath), GameCfg.Release);
        var folder = Path.GetDirectoryName(Path.GetFullPath(pluginPath))!;
        var females = sm.Npcs.Where(Fem).ToList();

        // custom race = a female face whose Race record lives outside the base masters (e.g. ja-Kha'jay breeds)
        bool custom = females.Any(n => !BaseMasters.Contains(n.Race.FormKey.ModKey.FileName));
        bool overridesVanilla = sm.Npcs.Any(n => BaseMasters.Contains(n.FormKey.ModKey.FileName));
        bool hasBsa = Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.bsa").Any();
        bool shipsTex = ShipsRaceTextures(folder);
        bool faceGenLoose = Directory.Exists(folder) &&
            Directory.EnumerateFiles(folder, "*.nif", SearchOption.AllDirectories)
                     .Any(f => f.Replace('\\', '/').ToLowerInvariant().Contains("/facegendata/facegeom/"));

        // Decision. KEEP (master the source) is always safe; DEEP-COPY only when clearly a disposable
        // loose face replacer. Ambiguity errs toward KEEP.
        string mode, why;
        if (custom)
            (mode, why) = ("keep", "uses custom races — a harvested face needs the source as a master to adopt its race");
        else if (shipsTex)
            (mode, why) = ("keep", "ships head/skin textures under actors\\character (whole-race overhaul) — disabling risks a head/body skin-tone seam");
        else if (hasBsa)
            (mode, why) = ("keep", "assets are packed in a BSA — keeping the source avoids unpacking (override to deep-copy + unpack)");
        else
            (mode, why) = ("disable", "loose assets, overrides vanilla, no custom race or race textures — safe to deep-copy into a standalone mod");

        return new(Path.GetFileName(pluginPath), folder, females.Count, sm.HeadParts.Count,
            overridesVanilla, custom, hasBsa, shipsTex, faceGenLoose, mode, why);
    }

    // A race/skin overhaul ships .dds under textures\actors\character OUTSIDE facegendata (skin/head).
    // A bandit-specific replacer keeps its facetint under facegendata and any hair textures elsewhere.
    static bool ShipsRaceTextures(string folder)
    {
        var tex = Path.Combine(folder, "textures", "actors", "character");
        if (!Directory.Exists(tex)) return false;
        return Directory.EnumerateFiles(tex, "*.dds", SearchOption.AllDirectories)
            .Any(f => !f.Replace('\\', '/').ToLowerInvariant().Contains("/facegendata/"));
    }

    public static int Run(string[] args)
    {
        var srcs = new List<string>();
        for (int i = 1; i < args.Length; i++) if (args[i] == "--source") srcs.Add(args[++i]);
        if (srcs.Count == 0) { Console.Error.WriteLine("classify --source <plugin> [--source ...]"); return 1; }
        foreach (var sp in srcs)
        {
            var r = Inspect(sp);
            Console.WriteLine(r.Plugin);
            Console.WriteLine($"  femaleNPCs={r.FemaleNpcs} ownHDPT={r.OwnHdpt} overridesVanilla={r.OverridesVanilla} " +
                              $"customRace={r.UsesCustomRace} bsa={r.HasBsa} raceTextures={r.ShipsRaceTextures} faceGenLoose={r.FaceGenLoose}");
            Console.WriteLine($"  => MODE: {r.Mode.ToUpperInvariant()}  ({r.Why})");
        }
        return 0;
    }
}
