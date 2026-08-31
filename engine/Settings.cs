using System.Text.Json;
using System.Text.Encodings.Web;
using Mutagen.Bethesda.Skyrim;

// Portable per-PC configuration. Everything game-specific (which Skyrim, where it lives, the MO2 paths,
// the Bodies-of-Tamriel preset dir) is read from config/settings.json so the app runs unchanged on an
// SE PC and a VR PC. GameCfg.Release is the single source of truth for the Mutagen game release, read
// by every SkyrimMod call in the engine.
static class GameCfg
{
    public static SkyrimRelease Release = SkyrimRelease.SkyrimSE;

    public static SkyrimRelease Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "vr" or "skyrimvr" => SkyrimRelease.SkyrimVR,
        "le" or "skyrimle" => SkyrimRelease.SkyrimLE,
        _ => SkyrimRelease.SkyrimSE,   // SE/AE and anything unknown
    };

    public static string Canon(SkyrimRelease r) => r switch
    {
        SkyrimRelease.SkyrimVR => "SkyrimVR",
        SkyrimRelease.SkyrimLE => "SkyrimLE",
        _ => "SkyrimSE",
    };
}

class AppSettings
{
    public string GameVersion { get; set; } = "SkyrimSE";
    public string Game { get; set; } = "";        // path to Skyrim.esm (may be a cleaned-masters copy)
    public string GameData { get; set; } = "";    // base-game Data folder holding the vanilla BSAs (Steam\...\Data); default = folder of Game
    public string Mods { get; set; } = "";        // MO2 mods dir
    public string Profiles { get; set; } = "";    // MO2 profiles dir
    public string Profile { get; set; } = "";     // active MO2 profile name
    public string BotPresets { get; set; } = "";  // Bodies of Tamriel BodySlide SliderPresets dir
}

static class Settings
{
    static readonly JsonSerializerOptions J = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // keep '+', '/' etc. literal in the file
    };

    public static AppSettings Current = new();
    public static string FilePath = "";           // resolved settings.json path (for Save)

    // App home = nearest ancestor of the exe that contains config/categories.yaml.
    public static string AppHome()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (var p = d; p != null; p = p.Parent)
            if (File.Exists(Path.Combine(p.FullName, "config", "categories.yaml")))
                return p.FullName;
        return AppContext.BaseDirectory;
    }

    public static string ConfigDir() => Path.Combine(AppHome(), "config");

    // Load settings.json (if present) and set the global game release. Safe to call once at startup for
    // every command, so a standalone CLI run (e.g. sexplague-bodies from regen-bodies.cmd) honours the
    // same game version and paths as the UI.
    public static AppSettings Load()
    {
        FilePath = Path.Combine(ConfigDir(), "settings.json");
        if (File.Exists(FilePath))
            try { Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), J) ?? new(); }
            catch { Current = new(); }
        GameCfg.Release = GameCfg.Parse(Current.GameVersion);
        return Current;
    }

    public static void Save()
    {
        Current.GameVersion = GameCfg.Canon(GameCfg.Release);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, J));
        }
        catch { }
    }
}
