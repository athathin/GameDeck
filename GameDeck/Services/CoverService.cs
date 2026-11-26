using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GameDeck.Models;
using Microsoft.Win32;

namespace GameDeck.Services;

/// <summary>
/// Finds, downloads and caches cover art.
/// Order: Steam's own copy on this PC -> Steam poster online (by app id or exact name match) -> Xbox / Microsoft Store poster
/// -> Epic's own box art -> placeholder in the UI.
/// Covers are downloaded once into %AppData%\GameDeck\covers and reused afterwards.
/// </summary>
public static class CoverService
{
    private const string SteamPrefix = "steam://rungameid/";
    private const string EpicPrefix = "com.epicgames.launcher://apps/";

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "covers");

    // Covers picked by the user live in their own folder, so clearing the download cache never removes them
    private static readonly string CustomDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "custom-covers");

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim NetworkGate = new(4); // max 4 downloads at once

    // Epic image types, best first (all are portrait box art)
    private static readonly string[] EpicImageTypes = ["DieselGameBoxTall", "OfferImageTall", "DieselStoreFrontTall"];

    private static Task<Dictionary<string, string>>? _epicIndex;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameDeck/1.0");
        return client;
    }

    /// <summary>
    /// Loads the cover into game.Cover. Call from the UI thread. Never throws.
    /// Covers only live in memory while the game's tile is on screen (game.CoverWanted): if the tile scrolled away
    /// while the file was being found or downloaded, the file stays cached on disk but isn't decoded.
    /// </summary>
    public static async Task LoadCoverAsync(Game game)
    {
        try
        {
            var file = await GetCoverFileAsync(game);
            if (file is null || !game.CoverWanted) return;

            game.Cover = LoadBitmap(file);

            // the file just found already says whether it is the user's own cover: no need to search that folder again
            game.HasCustomCover = string.Equals(Path.GetDirectoryName(file), CustomDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // offline or server unreachable: keep the placeholder, try again next time
        }
    }

    /// <summary>
    /// Path of the game's cover if it is already on this PC (the user's own, the cached download, or Steam's own copy),
    /// otherwise null. Never uses the internet and never loads the picture into memory.
    /// </summary>
    public static string? FindCachedCover(Game game)
    {
        try
        {
            var key = Hash(game.LaunchTarget);
            if (FindCustomCover(key) is { } custom) return custom;

            var file = Path.Combine(CacheDir, key + ".jpg");
            if (File.Exists(file)) return file;

            Directory.CreateDirectory(CacheDir);
            return TryCopySteamLocalCover(game, file) ? file : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Forget "no cover found" results so a new scan retries them.</summary>
    public static void ClearMisses()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            foreach (var marker in Directory.EnumerateFiles(CacheDir, "*.none"))
                File.Delete(marker);
        }
        catch { }
    }

    private static async Task<string?> GetCoverFileAsync(Game game)
    {
        Directory.CreateDirectory(CacheDir);

        var key = Hash(game.LaunchTarget);
        var file = Path.Combine(CacheDir, key + ".jpg");
        var miss = Path.Combine(CacheDir, key + ".none");

        var custom = FindCustomCover(key);   // a cover the user picked always wins
        if (custom is not null) return custom;

        if (File.Exists(file)) return file;
        if (TryCopySteamLocalCover(game, file)) return file;   // Steam already has this picture on disk
        if (!SettingsService.OnlineCovers) return null;   // offline mode: never touch the internet
        if (File.Exists(miss) && File.GetLastWriteTimeUtc(miss) > DateTime.UtcNow.AddDays(-7)) return null;

        await NetworkGate.WaitAsync();
        try
        {
            // 1) Steam poster: app id from the launch link, otherwise an exact-name search on Steam's store
            var appId = SteamAppId(game) ?? await FindSteamAppIdAsync(game.Name);
            if (appId is not null)
            {
                foreach (var url in SteamPosterUrls(appId))
                {
                    if (await DownloadAsync(url, file))
                        return file;
                }
            }

            // 2) Xbox / Microsoft Store games: the store's own poster, found through the package name
            var xboxUrl = await FindXboxPosterUrlAsync(game);
            if (xboxUrl is not null && await DownloadAsync(xboxUrl, file))
                return file;

            // 3) Epic games that aren't on Steam (e.g. Rocket League): use Epic's own box art
            var epicUrl = await FindEpicCoverUrlAsync(game);
            if (epicUrl is not null && await DownloadAsync(epicUrl, file))
                return file;

            // 4) SteamGridDB, only when the user has entered their own key in Settings
            var gridUrl = await FindSteamGridDbUrlAsync(game);
            if (gridUrl is not null && await DownloadAsync(gridUrl, file))
                return file;

            File.WriteAllBytes(miss, Array.Empty<byte>()); // remember "nothing found" for a week
            return null;
        }
        finally
        {
            NetworkGate.Release();
        }
    }

    /// <summary>Steam's servers keep posters at a few addresses; older games are at the first, newer ones at the others.</summary>
    private static IEnumerable<string> SteamPosterUrls(string appId)
    {
        yield return $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg";
        yield return $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg";
        yield return $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900_2x.jpg";
    }

    /// <summary>
    /// The Steam app keeps the pictures it shows in its own library in Steam\appcache\librarycache\&lt;appid&gt;
    /// (either directly there or in a sub-folder). Using them needs no internet and also covers games that have
    /// no poster on Steam's servers (playtests, brand-new games). The picture is copied: Steam's files are never touched.
    /// </summary>
    private static bool TryCopySteamLocalCover(Game game, string destination)
    {
        try
        {
            var appId = SteamAppId(game);
            if (appId is null || appId.Any(c => !char.IsDigit(c))) return false;

            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrWhiteSpace(steam)) return false;

            var dir = Path.Combine(steam.Replace('/', '\\'), "appcache", "librarycache", appId);
            if (!Directory.Exists(dir)) return false;

            // portrait pictures, best first (library_capsule is the 2:3 box art in newer Steam versions)
            foreach (var name in new[] { "library_600x900.jpg", "library_capsule.jpg" })
            {
                var found = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                if (found is null) continue;

                File.Copy(found, destination, overwrite: true);
                return true;
            }
        }
        catch
        {
            // Steam not installed, or the folder is unreadable: use the internet instead
        }

        return false;
    }

    private static string? SteamAppId(Game game) =>
        game.LaunchTarget.StartsWith(SteamPrefix, StringComparison.OrdinalIgnoreCase)
            ? game.LaunchTarget[SteamPrefix.Length..]
            : null;

    /// <summary>
    /// Searches Steam's store by name; only accepts an exact (normalized) name match. Null when there is none; network and
    /// rate-limit problems throw (so a "not on Steam" is never remembered by mistake). Also used for the genres of non-Steam games.
    /// </summary>
    internal static async Task<string?> FindSteamAppIdAsync(string name)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&l=english&cc=us";

        using var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode(); // network/rate-limit problems throw, so no "miss" gets cached

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("items", out var items)) return null;

        var wanted = Normalize(name);
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var n) &&
                Normalize(n.GetString()) == wanted &&
                item.TryGetProperty("id", out var id))
                return id.GetInt32().ToString();
        }

        return null;
    }

    // ---------- custom covers ----------

    /// <summary>Uses an image the user picked as this game's cover. Call from the UI thread.</summary>
    public static bool SetCustomCover(Game game, string imagePath)
    {
        try
        {
            Directory.CreateDirectory(CustomDir);
            var key = Hash(game.LaunchTarget);

            foreach (var old in Directory.EnumerateFiles(CustomDir, key + ".*"))
                File.Delete(old);

            var copy = Path.Combine(CustomDir, key + Path.GetExtension(imagePath).ToLowerInvariant());
            File.Copy(imagePath, copy, overwrite: true);

            var bitmap = LoadBitmap(copy);   // deletes the copy again if the image can't be read
            if (bitmap is null) return false;

            game.Cover = bitmap;
            game.HasCustomCover = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes the user's cover and goes back to the automatic one. Call from the UI thread.</summary>
    public static async Task RemoveCustomCoverAsync(Game game)
    {
        try
        {
            var key = Hash(game.LaunchTarget);
            if (Directory.Exists(CustomDir))
            {
                foreach (var file in Directory.EnumerateFiles(CustomDir, key + ".*"))
                    File.Delete(file);
            }
        }
        catch { }

        game.HasCustomCover = false;
        game.Cover = null;                 // placeholder until the automatic cover is loaded again
        await LoadCoverAsync(game);
    }

    private static string? FindCustomCover(string key)
    {
        try
        {
            return Directory.Exists(CustomDir)
                ? Directory.EnumerateFiles(CustomDir, key + ".*").FirstOrDefault()
                : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------- Xbox / Microsoft Store ----------

    private const string AppsFolderPrefix = @"shell:AppsFolder\";

    /// <summary>
    /// Asks Microsoft's store catalog for the game by its package family name (it's in the launch link) and returns
    /// the 2:3 "Poster" picture. Null when the game isn't in the catalog.
    /// </summary>
    private static async Task<string?> FindXboxPosterUrlAsync(Game game)
    {
        if (!game.LaunchTarget.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var family = game.LaunchTarget[AppsFolderPrefix.Length..].Split('!')[0];
        var url = "https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?market=US&languages=en-US" +
                  $"&alternateId=PackageFamilyName&value={Uri.EscapeDataString(family)}";

        using var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();   // network problems throw, so no "miss" gets cached

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("Products", out var products) || products.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var product in products.EnumerateArray())
        {
            if (!product.TryGetProperty("LocalizedProperties", out var localized) || localized.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var properties in localized.EnumerateArray())
            {
                if (!properties.TryGetProperty("Images", out var images) || images.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var purpose in new[] { "Poster", "BrandedKeyArt" })
                {
                    foreach (var image in images.EnumerateArray())
                    {
                        if (Str(image, "ImagePurpose") != purpose || Str(image, "Uri") is not { } uri) continue;

                        if (uri.StartsWith("//")) uri = "https:" + uri;
                        return uri + "?w=600&h=900&q=90";   // the store resizes it for us
                    }
                }
            }
        }

        return null;
    }

    // ---------- SteamGridDB (the user's own key) ----------

    private static bool _gridKeyRejected;

    /// <summary>Forgets that a SteamGridDB key was refused (call after the key was changed).</summary>
    public static void ResetKeyCheck() => _gridKeyRejected = false;

    /// <summary>
    /// Looks the game up on SteamGridDB by name (an exact, normalized match only) and returns the address of a 600x900 poster.
    /// Only runs with the user's own key; the game's name is sent to steamgriddb.com for this.
    /// </summary>
    private static async Task<string?> FindSteamGridDbUrlAsync(Game game)
    {
        var key = SettingsService.SteamGridDbKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        // a refused key (no network needed to know): fail without caching a "no cover" result for the game
        if (_gridKeyRejected) throw new InvalidOperationException("The SteamGridDB key was refused.");

        var search = await GridGetAsync($"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(game.Name)}", key);
        if (!search.RootElement.TryGetProperty("data", out var results) || results.ValueKind != JsonValueKind.Array) return null;

        var wanted = Normalize(game.Name);
        int? id = null;
        foreach (var result in results.EnumerateArray())
        {
            if (Normalize(Str(result, "name")) == wanted && result.TryGetProperty("id", out var found))
            {
                id = found.GetInt32();
                break;
            }
        }

        if (id is null) return null;

        var grids = await GridGetAsync($"https://www.steamgriddb.com/api/v2/grids/game/{id}?dimensions=600x900&types=static", key);
        if (!grids.RootElement.TryGetProperty("data", out var images) || images.ValueKind != JsonValueKind.Array) return null;

        foreach (var image in images.EnumerateArray())
        {
            if (Str(image, "url") is { } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url;
        }

        return null;
    }

    private static async Task<JsonDocument> GridGetAsync(string url, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        using var response = await Http.SendAsync(request);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            _gridKeyRejected = true;
            throw new InvalidOperationException("The SteamGridDB key was refused.");
        }

        response.EnsureSuccessStatusCode();   // other problems throw too, so no "miss" gets cached
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    // ---------- Epic ----------

    private static async Task<string?> FindEpicCoverUrlAsync(Game game)
    {
        if (!game.LaunchTarget.StartsWith(EpicPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        // launch link looks like: com.epicgames.launcher://apps/<namespace>%3A<catalogItemId>%3A<appName>?action=...
        var ids = game.LaunchTarget[EpicPrefix.Length..].Split('?')[0].Split("%3A");
        var ns = ids.Length > 0 ? ids[0] : "";
        var item = ids.Length > 1 ? ids[1] : "";

        _epicIndex ??= Task.Run(BuildEpicIndex);   // read once per run, off the UI thread
        var index = await _epicIndex;

        if (index.TryGetValue("id:" + item, out var url)) return url;
        if (index.TryGetValue("title:" + Normalize(game.Name), out url)) return url;
        if (index.TryGetValue("ns:" + ns, out url)) return url;
        return null;
    }

    /// <summary>
    /// Epic's launcher keeps a local copy of its game catalog (catcache.bin: base64-encoded JSON)
    /// that includes box-art URLs. Reading it needs no login and no API key.
    /// </summary>
    private static Dictionary<string, string> BuildEpicIndex()
    {
        var index = new Dictionary<string, string>();

        try
        {
            var file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");

            if (!File.Exists(file)) return index;

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(file))).TrimStart('\uFEFF');
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return index;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var url = PickEpicImage(entry);
                if (url is null) continue;

                if (Str(entry, "id") is { } id) index.TryAdd("id:" + id, url);
                if (Str(entry, "title") is { } title) index.TryAdd("title:" + Normalize(title), url);
                if (Str(entry, "namespace") is { } ns) index.TryAdd("ns:" + ns, url);
            }
        }
        catch
        {
            // unreadable catalog: just no Epic covers
        }

        return index;
    }

    private static string? PickEpicImage(JsonElement entry)
    {
        if (!entry.TryGetProperty("keyImages", out var images) || images.ValueKind != JsonValueKind.Array)
            return null;

        string? best = null;
        var bestRank = int.MaxValue;

        foreach (var image in images.EnumerateArray())
        {
            var rank = Array.IndexOf(EpicImageTypes, Str(image, "type"));
            if (rank < 0 || rank >= bestRank) continue;

            var url = Str(image, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;

            best = url;
            bestRank = rank;
        }

        return best;
    }

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------- shared helpers ----------

    private static async Task<bool> DownloadAsync(string url, string destination)
    {
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return false; // 404 = no poster at this address

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length < 1000) return false;

        var temp = destination + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, destination, overwrite: true);
        return true;
    }

    private static BitmapImage? LoadBitmap(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 240;                   // decode small: saves a lot of memory
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // don't keep the file locked
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            try { File.Delete(path); } catch { } // corrupted download: fetch again next time
            return null;
        }
    }

    /// <summary>A name to compare with another: letters and digits only, lower case ("Hitman™: Absolution" = "hitmanabsolution").</summary>
    internal static string Normalize(string? text) =>
        new((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // A game's key is asked for several times every time its cover is looked up (and a tile does that each time it scrolls
    // into view): work it out once per launch target.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Hashes = new();

    private static string Hash(string text) =>
        Hashes.GetOrAdd(text, t => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(t.ToLowerInvariant())))[..16]);
}
