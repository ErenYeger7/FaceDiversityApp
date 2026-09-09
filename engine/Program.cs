// FaceDiversity engine (v1, CLI-first). Commands: analyze | generate
// Load per-PC settings (config/settings.json) first so GameCfg.Release + default paths are set for
// EVERY command — including a standalone CLI run outside the server. An explicit --game-version arg
// still wins (applied here before dispatch).
Settings.Load();
for (int gi = 0; gi < args.Length - 1; gi++)
    if (args[gi] == "--game-version") GameCfg.Release = GameCfg.Parse(args[gi + 1]);
return (args.Length == 0 ? "" : args[0]) switch
{
    "analyze"    => Analyze.Run(args),
    "classify"   => Classify.Run(args),
    "generate"   => Generate.Run(args),
    "build-library" => LibraryBuild.Run(args),
    "list-faces" => Faces.Run(args),
    "serve"      => Serve.Run(args),
    "scan-lo"    => ScanLo(args),
    "femnames"   => FeminineNames.Run(args),
    "sexplague-bodies" => SexPlagueBodies.Run(args),
    "audit-assets" => AssetAudit.Run(args),
    _ => Usage(),
};

// Debug: print (or write) the resolved active load order — the plugin paths a scan category will read.
//   scan-lo --profile <profileDir> --mods <modsDir> --stock <stockDataDir> [--out <file>]
static int ScanLo(string[] args)
{
    string? profile = null, mods = null, stock = null, outFile = null;
    for (int i = 1; i < args.Length; i++)
        switch (args[i])
        {
            case "--profile": profile = args[++i]; break;
            case "--mods": mods = args[++i]; break;
            case "--stock": stock = args[++i]; break;
            case "--out": outFile = args[++i]; break;
        }
    if (profile is null || mods is null || stock is null)
    { Console.Error.WriteLine("scan-lo --profile <dir> --mods <dir> --stock <dir> [--out <file>]"); return 1; }
    var paths = LoadOrderScan.ResolveActivePaths(profile, mods, stock);
    if (outFile is not null) File.WriteAllLines(outFile, paths);
    foreach (var p in paths) Console.WriteLine(p);
    Console.Error.WriteLine($"[{paths.Count} active plugins resolved]");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("commands:");
    Console.Error.WriteLine("  analyze  --game <Skyrim.esm> --category bandit --source <plugin> [--source ...] [--out <json>]");
    Console.Error.WriteLine("  generate --game <Skyrim.esm> --category bandit --source <plugin> [--source ...] \\");
    Console.Error.WriteLine("           --voice-map <voice_map.yaml> --out <modFolder> --name <Plugin.esp> [--include <ids>] [--config <categories.yaml>] [--no-feminize]");
    Console.Error.WriteLine("  list-faces --game <Skyrim.esm> --source <plugin> [--source ...]");
    Console.Error.WriteLine("  serve --game <Skyrim.esm> --mods <MO2 mods dir> --config <categories.yaml> --voice-map <voice_map.yaml> --webroot <web dir> --out <out dir> [--port 8930]");
    return 1;
}
