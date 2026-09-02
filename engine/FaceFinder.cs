using System.Net.Http;
using System.Text.Json;

// Online fallback mugshot source: npcfacefinder.com public API (no auth). Queried ONLY when the local
// pack has no image for a face. Like the local resolver, it disambiguates by SOURCE MOD — the NPC face
// search returns every mod's rendition tagged with mod.name, and we pick the one whose name matches the
// face's source mod (else the sole result, else nothing — never a wrong guess).
//
// Good-citizen: a single shared HttpClient with a short timeout, and a global back-off that honors a 429's
// Retry-After so we stop hammering. The server is single-threaded, so a fetch briefly blocks it — that's
// why this is opt-in, lazy (only visible thumbnails fetch), and cacheable to disk (one-time per face).
static class FaceFinder
{
    const string Base = "https://npcfacefinder.com";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    static DateTime _backoffUntil = DateTime.MinValue;

    static string Norm(string? s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // App FormKey "01326A:Skyrim.esm" -> FaceFinder's 8-hex "0001326A:Skyrim.esm".
    static string? EightHexKey(string formKey)
    {
        int c = formKey.IndexOf(':');
        if (c <= 0 || c >= formKey.Length - 1) return null;
        var id = formKey[..c].Trim();
        var plugin = formKey[(c + 1)..].Trim();
        if (id.Length == 0 || id.Length > 8 || plugin.Length == 0) return null;
        return id.PadLeft(8, '0') + ":" + plugin;
    }

    // A Nexus-download folder embeds the mod id: "<Name>-<modid>-<version>-<timestamp>". The id is the FIRST
    // dash-delimited number after the name (versions follow it). e.g. "...World Encounters-169838-1-1768..." -> 169838.
    static string? NexusIdFromFolder(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"-(\d{3,8})-");
        return m.Success ? m.Groups[1].Value : null;
    }
    // FaceFinder tags each mod with its Nexus page: ".../mods/169838".
    static string? NexusIdFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(url, @"/mods/(\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    // Fetch the best-matching face image for this NPC from FaceFinder. Returns (bytes, contentType) or null.
    // Match priority: Nexus mod id (folder suffix <-> mod.external_url; robust to naming differences like
    // "Literal Who - Female Bandits" vs "Literal Who Female Bandits-145511-…") > normalized name (source mod
    // or the user's manual alias) > the sole rendition > nothing.
    public static (byte[] bytes, string contentType)? Fetch(string? formKey, string? sourceMod, string? aliasName = null)
    {
        if (string.IsNullOrWhiteSpace(formKey) || DateTime.UtcNow < _backoffUntil) return null;
        var key8 = EightHexKey(formKey!);
        if (key8 is null) return null;
        try
        {
            var url = $"{Base}/api/public/v2/npc/faces/search?formKey={Uri.EscapeDataString(key8)}";
            using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
            if ((int)resp.StatusCode == 429) { Backoff(resp); return null; }
            if (!resp.IsSuccessStatusCode) return null;
            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;

            var nexusId = NexusIdFromFolder(sourceMod);
            var names = new HashSet<string>();
            if (!string.IsNullOrWhiteSpace(sourceMod)) names.Add(Norm(sourceMod));
            if (!string.IsNullOrWhiteSpace(aliasName)) names.Add(Norm(aliasName));
            string? byNexus = null, byName = null, sole = null; int count = 0;
            foreach (var r in results.EnumerateArray())
            {
                var full = r.TryGetProperty("images", out var im) && im.TryGetProperty("full", out var fu)
                           && fu.ValueKind == JsonValueKind.String ? fu.GetString() : null;
                if (string.IsNullOrEmpty(full)) continue;
                count++; sole ??= full;
                string? modName = null, modUrl = null;
                if (r.TryGetProperty("mod", out var md))
                {
                    if (md.TryGetProperty("name", out var nm)) modName = nm.GetString();
                    if (md.TryGetProperty("external_url", out var eu)) modUrl = eu.GetString();
                }
                if (byNexus is null && nexusId is not null && NexusIdFromUrl(modUrl) == nexusId) byNexus = full;
                var mn = Norm(modName);
                if (byName is null && mn.Length > 0 && names.Contains(mn)) byName = full;
            }
            var pick = byNexus ?? byName ?? (count == 1 ? sole : null);
            if (pick is null) return null;

            using var imgResp = Http.GetAsync(pick).GetAwaiter().GetResult();
            if ((int)imgResp.StatusCode == 429) { Backoff(imgResp); return null; }
            if (!imgResp.IsSuccessStatusCode) return null;
            var bytes = imgResp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var ct = imgResp.Content.Headers.ContentType?.MediaType ?? "image/webp";
            return (bytes, ct);
        }
        catch { return null; }
    }

    static void Backoff(HttpResponseMessage r)
    {
        int secs = 10;
        if (r.Headers.TryGetValues("Retry-After", out var v) && int.TryParse(v.FirstOrDefault(), out var s)) secs = s;
        _backoffUntil = DateTime.UtcNow.AddSeconds(Math.Clamp(secs, 1, 120));
    }

    // Where a fetched image is persisted so the LOCAL resolver finds it next time (matched by source mod):
    // <cacheRoot>/FaceFinder Cache/<sourceMod>/<master>/00<id6>.webp  (grandparent-of-file = sourceMod).
    public static string? CachePath(string? cacheRoot, string? sourceMod, string formKey)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot)) return null;
        int c = formKey.IndexOf(':');
        if (c <= 0 || c >= formKey.Length - 1) return null;
        var id = formKey[..c].Trim();
        var master = formKey[(c + 1)..].Trim();
        if (id.Length == 0 || master.Length == 0) return null;
        var last6 = id.Length <= 6 ? id.PadLeft(6, '0') : id[^6..];
        var mod = Sanitize(string.IsNullOrWhiteSpace(sourceMod) ? "_unknown" : sourceMod!);
        return Path.Combine(cacheRoot!, "FaceFinder Cache", mod, Sanitize(master), "00" + last6 + ".webp");
    }

    static string Sanitize(string s)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s;
    }
}
