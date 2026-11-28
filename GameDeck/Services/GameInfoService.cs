using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>What the stores say about a game: kept per store id in %AppData%\GameDeck\game-info.json.</summary>
public sealed class GameInfo
{
    /// <summary>"Steam" or "GOG"; "" when the store was asked and has nothing (the game isn't in it any more).</summary>
    public string Source { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Developers { get; set; } = [];
    public List<string> Publishers { get; set; } = [];
    public List<string> Genres { get; set; } = [];

    /// <summary>"17 Sep 2020" (or the store's own words: "Coming soon", "Q3 2026"...).</summary>
    public string? Released { get; set; }

    public int? Metacritic { get; set; }

    /// <summary>Only the genres are known (kept from before GameDeck fetched the rest): asked again for the rest.</summary>
    public bool Partial { get; set; }

    /// <summary>A wide picture of the game (the store's hero / background art), for the top of its page. Steam's is known by
    /// the app id alone (see BackgroundService), so Steam answers leave it empty.</summary>
    public string? Background { get; set; }

    /// <summary>What GameDeck read from the store: 1 = before backgrounds were read (asked again once), 2 = with them.</summary>
    public int Version { get; set; }

    public const int CurrentVersion = 2;

    public bool IsEmpty => Source.Length == 0;

    /// <summary>Read before GameDeck kept backgrounds: a store other than Steam is asked again (Steam's needs no answer).</summary>
    public bool IsStale => !IsEmpty && Version < CurrentVersion && Source != "Steam";
}

/// <summary>
/// A game's details from the stores, without any account: the description, developers, publishers, release date, Metacritic
/// score and genres. From the Steam store first: a Steam game by its app id, another game (Epic, GOG, Ubisoft...) by its name
/// (an exact name match, the same search the covers use). For the games Steam doesn't have: the game's own store (GOG's public
/// API, Microsoft's store catalog, Epic's store page; Riot's games have pages on Epic's store), then Epic's store page by the
/// game's name for the launchers with no store to ask (EA, Ubisoft, Battle.net, Rockstar, Amazon, itch.io), and for Epic games
/// last what Epic's launcher keeps on this PC (see <see cref="OtherKeys"/>). Games none of them has get none (and their page
/// says so). Each game is asked about once, slowly (one request every 1.5 seconds, far below Steam's limit), and the answers
/// are kept on this PC. Nothing is asked while "Download cover art from the internet" is off.
/// </summary>
public static class GameInfoService
{
    private static string DataFile(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, name);

    private static readonly string InfoPath = DataFile("game-info.json");
    private static readonly string NamesPath = DataFile("genre-names.json");    // (the name it had when it only fetched genres)
    private static readonly string OldGenresPath = DataFile("genres.json");    // genres saved before the rest was fetched

    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.5);
    private static readonly HttpClient Http = CreateClient();

    // "steam:440" / "gog:1207664663" -> what the store said
    private static Dictionary<string, GameInfo> _info = new(StringComparer.OrdinalIgnoreCase);

    // a non-Steam game's name -> the Steam app id of the same game ("": looked for, it isn't on Steam)
    private static Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    private static bool _loaded;
    private static bool _fetching;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameDeck/1.0");
        return client;
    }

    /// <summary>The game's Steam app id: its own (a Steam game), or the one its name was found under (null: none, or not yet).</summary>
    internal static string? AppIdOf(Game game) =>
        game.SteamAppId ?? (_names.TryGetValue(game.Name, out var id) && id.Length > 0 ? id : null);

    /// <summary>A GOG game's product id: from the scan, or from the goggame-{id}.info file GOG puts in the game's folder.</summary>
    internal static string? GogIdOf(Game game)
    {
        if (game.Source != "GOG") return null;
        if (!string.IsNullOrWhiteSpace(game.GogId)) return game.GogId;
        if (game.InstallDir is not { } folder) return null;

        // (games scanned before GameDeck kept the id: read from the folder once, not at every redraw)
        if (_gogIdsByFolder.TryGetValue(folder, out var known)) return known;

        string? id = null;
        try
        {
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "goggame-*.info").FirstOrDefault() is { } file)
            {
                var digits = Path.GetFileNameWithoutExtension(file)["goggame-".Length..];
                id = digits.Length > 0 && digits.All(char.IsDigit) ? digits : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _gogIdsByFolder[folder] = id;
        return id;
    }

    private static readonly Dictionary<string, string?> _gogIdsByFolder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What is known about the game (null: nothing yet: not asked yet, or the stores couldn't be reached).</summary>
    public static GameInfo? For(Game game)
    {
        if (!_loaded) Load();

        GameInfo? steam = null;
        if (AppIdOf(game) is { } appId && _info.TryGetValue("steam:" + appId, out var found))
        {
            if (!found.IsEmpty) return found;
            steam = found;
        }

        foreach (var key in OtherKeys(game))
        {
            if (_info.TryGetValue(key, out var other) && !other.IsEmpty)
                return other;
        }

        return steam;
    }

    // ---------- after Steam: the game's own store, then Epic's store by name ----------

    /// <summary>
    /// Where else to look when Steam has nothing, in order: the game's own store (GOG, the Microsoft Store, Epic's store page,
    /// Riot's page on Epic's store), Epic's store page by the game's name for the launchers that have no store of their own to
    /// ask (EA, Ubisoft, Battle.net, Rockstar, Amazon, itch.io, games added by hand: many of them are sold there), and last,
    /// for Epic games, what Epic's launcher keeps on this PC. Keys: "gog:{id}", "xbox:{package family}", "epic:{page}",
    /// "epiclocal:{catalog item}".
    /// </summary>
    internal static List<string> OtherKeys(Game game)
    {
        var keys = new List<string>();

        if (GogIdOf(game) is { } gogId)
            keys.Add("gog:" + gogId);

        if (XboxFamilyOf(game) is { } family)
            keys.Add("xbox:" + family);

        // (Steam games are Steam's; a Steam game the store no longer has isn't looked for elsewhere by its name)
        if (game.Source != "Steam" && EpicPageOf(game) is { } page)
            keys.Add("epic:" + page);

        if (EpicItemOf(game) is { } item)
            keys.Add("epiclocal:" + item);

        // last, IGDB (only with the user's own Twitch keys in Settings): it knows games no store has, by their name
        if (IgdbService.Configured && CoverService.Normalize(game.Name) is { Length: > 0 } name)
            keys.Add("igdb:" + name);

        return keys;
    }

    private const string AppsFolderPrefix = @"shell:AppsFolder\";

    /// <summary>An Xbox / Microsoft Store game's package family name (in its launch link), or null.</summary>
    private static string? XboxFamilyOf(Game game) =>
        game.LaunchTarget.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
            ? game.LaunchTarget[AppsFolderPrefix.Length..].Split('!')[0] is { Length: > 0 } family ? family : null
            : null;

    /// <summary>The Epic store page to read: Riot's games have known pages there; any other game its name, store style ("rocket-league").</summary>
    private static string? EpicPageOf(Game game)
    {
        if (game.LaunchTarget.StartsWith(Scanners.RiotScanner.LaunchPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var product = game.LaunchTarget[Scanners.RiotScanner.LaunchPrefix.Length..].Split('/')[0];
            if (CoverService.RiotStorePages.TryGetValue(product, out var known)) return known;
        }

        var slug = CoverService.StoreSlug(game.Name);
        return slug.Length > 0 ? slug : null;
    }

    /// <summary>An Epic game's catalog item id (in its launch link: apps/{namespace}%3A{item}%3A{app}), or null.</summary>
    private static string? EpicItemOf(Game game)
    {
        if (!game.LaunchTarget.StartsWith(CoverService.EpicPrefix, StringComparison.OrdinalIgnoreCase)) return null;

        var ids = game.LaunchTarget[CoverService.EpicPrefix.Length..].Split('?')[0].Split("%3A");
        return ids.Length > 1 && ids[1].Length > 0 ? ids[1] : null;
    }

    /// <summary>Asks the store a key belongs to; null when it couldn't be asked (offline, too many requests).</summary>
    private static Task<GameInfo?> AskAsync(string key)
    {
        var (store, id) = (key[..key.IndexOf(':')], key[(key.IndexOf(':') + 1)..]);
        return store switch
        {
            "gog" => AskGogAsync(id),
            "xbox" => AskXboxAsync(id),
            "epic" => AskEpicPageAsync(id),
            "epiclocal" => Task.Run(() => (GameInfo?)EpicLocal(id)),
            "igdb" => IgdbService.InfoAsync(id),
            _ => Task.FromResult<GameInfo?>(new GameInfo())
        };
    }

    /// <summary>
    /// True when every store that could know the game was asked and none has it (its page then says so). False while it
    /// may still come (not asked yet, offline, or the setting is off).
    /// </summary>
    public static bool NothingFound(Game game)
    {
        if (!_loaded) Load();
        if (For(game) is { IsEmpty: false }) return false;

        var appId = AppIdOf(game);
        var steamAsked = appId is null
            ? game.SteamAppId is null && _names.ContainsKey(game.Name)      // searched by name: not on Steam
            : _info.TryGetValue("steam:" + appId, out var s) && s.IsEmpty && !s.Partial;  // asked: the store has nothing

        var othersAsked = OtherKeys(game).All(key => _info.TryGetValue(key, out var other) && other.IsEmpty);

        return steamAsked && othersAsked;
    }

    /// <summary>
    /// Asks the stores, in the background and one request at a time: the Steam app ids of the non-Steam games not looked for
    /// yet, the Steam details not saved yet, then GOG's for the GOG games Steam doesn't have. <paramref name="changed"/> is
    /// called (on the calling thread) after each batch.
    /// </summary>
    public static async Task FetchMissingAsync(IReadOnlyCollection<Game> games, Action changed)
    {
        if (_fetching || !SettingsService.OnlineCovers) return;
        if (!_loaded) Load();

        _fetching = true;
        try
        {
            // 1) the non-Steam games' app ids, by name (stops when Steam can't be asked: the rest wait for the next start)
            var unnamed = games.Where(g => g.SteamAppId is null && g.Name.Trim().Length > 0 && !_names.ContainsKey(g.Name))
                .Select(g => g.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var found = 0;
            foreach (var name in unnamed)
            {
                if (!SettingsService.OnlineCovers) return;

                string? appId;
                try
                {
                    appId = await CoverService.FindSteamAppIdAsync(name);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
                {
                    break;
                }

                _names[name] = appId ?? "";
                if (++found % 10 == 0) Write(NamesPath, _names);
                await Task.Delay(Pause);
            }
            if (found > 0) Write(NamesPath, _names);

            // 2) Steam's details of every app id not asked about yet (or only known by its genres)
            var steamIds = games.Select(AppIdOf).OfType<string>().Distinct()
                .Where(id => !_info.TryGetValue("steam:" + id, out var known) || known.Partial).ToList();
            if (!await AskAllAsync(steamIds.Select(id => ("steam:" + id, (Func<Task<GameInfo?>>)(() => AskSteamAsync(id)))), changed))
                return;

            // 3) the games Steam has nothing for: their other stores in order, until one has them
            var fresh = 0;
            // (and those read before GameDeck kept backgrounds: their store once more)
            foreach (var game in games.Where(g => For(g) is null or { IsEmpty: true } or { IsStale: true }).ToList())
            {
                foreach (var key in OtherKeys(game))
                {
                    if (_info.TryGetValue(key, out var known) && !known.IsStale)
                    {
                        if (known.IsEmpty) continue;   // asked before, nothing there: the next store
                        break;                         // (found already, under a key another game shares)
                    }

                    if (!SettingsService.OnlineCovers) return;

                    var info = await AskAsync(key);
                    if (info is null)   // offline, or a store asks to slow down: the rest wait for the next start
                    {
                        if (fresh > 0) { Write(InfoPath, _info); changed(); }
                        return;
                    }

                    _info[key] = info;
                    if (++fresh % 10 == 0) { Write(InfoPath, _info); changed(); }

                    if (!key.StartsWith("epiclocal:", StringComparison.Ordinal))
                        await Task.Delay(Pause);   // (only the stores on the internet are asked slowly)

                    if (!info.IsEmpty) break;
                }
            }

            if (fresh % 10 != 0) { Write(InfoPath, _info); changed(); }
        }
        finally
        {
            _fetching = false;
        }
    }

    /// <summary>Asks one store about each key in turn, saving every 10 answers. False when it had to stop (offline, too fast, turned off).</summary>
    private static async Task<bool> AskAllAsync(IEnumerable<(string Key, Func<Task<GameInfo?>> Ask)> requests, Action changed)
    {
        var fresh = 0;
        foreach (var (key, ask) in requests)
        {
            if (!SettingsService.OnlineCovers) return false;   // (turned off meanwhile)

            var info = await ask();
            if (info is null)   // offline, or the store asks to slow down: the rest wait for the next start
            {
                if (fresh > 0) { Write(InfoPath, _info); changed(); }
                return false;
            }

            _info[key] = info;
            if (++fresh % 10 == 0)
            {
                Write(InfoPath, _info);
                changed();
            }

            await Task.Delay(Pause);
        }

        if (fresh % 10 != 0)
        {
            Write(InfoPath, _info);
            changed();
        }

        return true;
    }

    // ---------- Steam ----------

    /// <summary>What the Steam store says about the app; an empty GameInfo when it has nothing; null when Steam couldn't be asked.</summary>
    private static async Task<GameInfo?> AskSteamAsync(string appId)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&filters=basic,genres,developers,publishers,release_date,metacritic");
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden) return null;
            if (!response.IsSuccessStatusCode) return new GameInfo();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            return ParseSteam(json.RootElement, appId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>A Steam store answer: { "440": { "success": true, "data": { "short_description": "...", "developers": [...], ... } } }.</summary>
    internal static GameInfo ParseSteam(JsonElement root, string appId)
    {
        if (!root.TryGetProperty(appId, out var app) || !app.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new GameInfo();

        var info = new GameInfo
        {
            Source = "Steam",
            Version = GameInfo.CurrentVersion,
            Description = CleanText(Text(data, "short_description")),
            Developers = Strings(data, "developers"),
            Publishers = Strings(data, "publishers"),
            Genres = data.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array
                ? genres.EnumerateArray().Select(g => Text(g, "description")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : []
        };

        if (data.TryGetProperty("release_date", out var release) && Text(release, "date") is { } date)
            info.Released = FormatDate(date);

        if (data.TryGetProperty("metacritic", out var meta) && meta.TryGetProperty("score", out var score) && score.TryGetInt32(out var value))
            info.Metacritic = value;

        return info;
    }

    // ---------- GOG ----------

    /// <summary>What GOG's public API says about the product; an empty GameInfo when it has nothing; null when GOG couldn't be asked.</summary>
    private static async Task<GameInfo?> AskGogAsync(string gogId)
    {
        try
        {
            using var response = await Http.GetAsync($"https://api.gog.com/v2/games/{gogId}?locale=en-US");
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden) return null;
            if (!response.IsSuccessStatusCode) return new GameInfo();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            return ParseGog(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>A GOG answer: { "description": "&lt;html&gt;", "_embedded": { "developers": [{ "name" }], "publisher": { "name" }, "product": { "globalReleaseDate" }, "tags": [{ "name" }] } }.</summary>
    internal static GameInfo ParseGog(JsonElement root)
    {
        if (!root.TryGetProperty("_embedded", out var embedded) || embedded.ValueKind != JsonValueKind.Object)
            return new GameInfo();

        static List<string> Names(JsonElement parent, string property) =>
            parent.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(e => Text(e, "name")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];

        var info = new GameInfo
        {
            Source = "GOG",
            Version = GameInfo.CurrentVersion,
            Description = GogSummary(Text(root, "description")),
            Developers = Names(embedded, "developers"),
            Publishers = embedded.TryGetProperty("publisher", out var publisher) && Text(publisher, "name") is { } name ? [name] : [],
            Genres = Names(embedded, "tags").Take(3).ToList(),   // (GOG's first tags are its genres: "Role-playing, Adventure, Fantasy")
            Background = Link(root, "galaxyBackgroundImage") ?? Link(root, "backgroundImage")
        };

        if (embedded.TryGetProperty("product", out var product) && Text(product, "globalReleaseDate") is { } date &&
            DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var released))
            info.Released = released.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

        return info;
    }

    // words of GOG's own offers, bonuses and launcher notes: such paragraphs aren't the game's description
    private static readonly string[] GogPromo =
        ["gog galaxy", "gog.com", "discount", "pre-order", "preorder", "reward", "bonus", "expansion pass", "available on", "season pass", "goodies"];

    /// <summary>
    /// GOG's description is a whole store page (pictures, offers, bonuses): the first paragraph that reads like a description
    /// of the game (long enough, and not about GOG's offers), cut at a sentence end near 450 letters. Null when there is none.
    /// </summary>
    internal static string? GogSummary(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        // paragraphs: block ends become line breaks, then every tag goes
        var text = Regex.Replace(html, @"<\s*(br|/p|/div|hr|/h\d|/li)[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');

        static bool Promo(string p) => GogPromo.Any(w => p.Contains(w, StringComparison.OrdinalIgnoreCase));

        // a paragraph after a short heading about an offer ("BONUS: Extended soundtrack") describes that offer, not the game
        var afterPromoHeading = false;
        foreach (var paragraph in Regex.Split(text, @"\n").Select(p => Regex.Replace(p, @"\s+", " ").Trim()).Where(p => p.Length > 0))
        {
            if (paragraph.Length >= 120 && !Promo(paragraph) && !afterPromoHeading)
                return Shorten(paragraph, 450);

            afterPromoHeading = paragraph.Length < 120 && Promo(paragraph);
        }

        return null;
    }

    // ---------- the Microsoft Store (Xbox games) ----------

    /// <summary>What Microsoft's store catalog says about the package; an empty GameInfo when nothing; null when it couldn't be asked.</summary>
    private static async Task<GameInfo?> AskXboxAsync(string family)
    {
        try
        {
            // (fieldsTemplate=Details: without it the catalog leaves out the description, developer and publisher)
            using var response = await Http.GetAsync("https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?market=US&languages=en-US" +
                                                     $"&alternateId=PackageFamilyName&value={Uri.EscapeDataString(family)}&fieldsTemplate=Details");
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden) return null;
            if (!response.IsSuccessStatusCode) return new GameInfo();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            return ParseXbox(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>A catalog answer: { "Products": [ { "LocalizedProperties": [ { "ShortDescription", "DeveloperName", ... } ], "MarketProperties": [ { "OriginalReleaseDate" } ], "Properties": { "Categories" } } ] }.</summary>
    internal static GameInfo ParseXbox(JsonElement root)
    {
        if (!root.TryGetProperty("Products", out var products) || products.ValueKind != JsonValueKind.Array ||
            products.GetArrayLength() == 0)
            return new GameInfo();

        var product = products[0];
        var localized = product.TryGetProperty("LocalizedProperties", out var all) && all.ValueKind == JsonValueKind.Array && all.GetArrayLength() > 0
            ? all[0]
            : default;

        var description = CleanText(Text(localized, "ShortDescription")) ?? CleanText(Text(localized, "ProductDescription"));
        var developer = Text(localized, "DeveloperName");
        var publisher = Text(localized, "PublisherName");

        // (a product with no words about it, like the Minecraft Launcher's, counts as nothing: its page says so)
        if (description is null && developer is null) return new GameInfo();

        var info = new GameInfo
        {
            Source = "Microsoft",
            Version = GameInfo.CurrentVersion,
            Description = description is null ? null : FirstParagraph(description),
            Developers = developer is null ? [] : [developer],
            Publishers = publisher is null ? [] : [publisher],
            Background = XboxImage(localized, "SuperHeroArt", "TitledHeroArt", "Hero")
        };

        if (product.TryGetProperty("MarketProperties", out var markets) && markets.ValueKind == JsonValueKind.Array && markets.GetArrayLength() > 0 &&
            Text(markets[0], "OriginalReleaseDate") is { } date)
            info.Released = IsoDate(date);

        if (product.TryGetProperty("Properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            info.Genres = Strings(properties, "Categories") is { Count: > 0 } categories ? categories
                : Text(properties, "Category") is { } category ? [category] : [];

        return info;
    }

    // ---------- the Epic Games Store (Epic's and Riot's games, and others sold there) ----------

    /// <summary>What a page of the Epic Games Store says; an empty GameInfo when there is no such page; null when it couldn't be asked.</summary>
    private static async Task<GameInfo?> AskEpicPageAsync(string page)
    {
        try
        {
            using var response = await Http.GetAsync($"https://store-content-ipv4.ak.epicgames.com/api/en-US/content/products/{Uri.EscapeDataString(page)}");
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden) return null;
            if (!response.IsSuccessStatusCode) return new GameInfo();   // (404: no page of that name)

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            return ParseEpicPage(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>A store page: { "pages": [ { "data": { "about": { "shortDescription", "developerAttribution", "publisherAttribution" }, "meta": { "releaseDate" } } } ] }.</summary>
    internal static GameInfo ParseEpicPage(JsonElement root)
    {
        if (!root.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array) return new GameInfo();

        // the game's own page first ("home"): the others are its editions and offers (a car bundle, an Ultimate Edition)
        var ordered = pages.EnumerateArray()
            .OrderBy(p => Text(p, "_slug") == "home" || Text(p, "type") == "productHome" ? 0 : 1)
            .ToList();

        foreach (var page in ordered)
        {
            if (!page.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("about", out var about) || about.ValueKind != JsonValueKind.Object)
                continue;

            var description = CleanText(Text(about, "shortDescription")) ?? CleanText(Markdown(Text(about, "description")));
            var developer = Text(about, "developerAttribution");
            if (description is null && developer is null) continue;

            var info = new GameInfo
            {
                Source = "Epic",
                Version = GameInfo.CurrentVersion,
                Description = description is null ? null : FirstParagraph(description),
                Developers = developer is null ? [] : [developer],
                Publishers = Text(about, "publisherAttribution") is { } publisher ? [publisher] : [],
                Background = data.TryGetProperty("hero", out var hero) ? Text(hero, "backgroundImageUrl") : null
            };

            if (data.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object && Text(meta, "releaseDate") is { } date)
                info.Released = IsoDate(date);

            return info;
        }

        return new GameInfo();
    }

    /// <summary>GOG's links: { "_links": { "galaxyBackgroundImage": { "href": "https://..." } } }.</summary>
    private static string? Link(JsonElement root, string name) =>
        root.TryGetProperty("_links", out var links) && links.ValueKind == JsonValueKind.Object &&
        links.TryGetProperty(name, out var link) && Text(link, "href") is { } href && href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? href
            : null;

    /// <summary>The first of the store's pictures with one of these purposes (in that order), 1920 pixels wide.</summary>
    private static string? XboxImage(JsonElement localized, params string[] purposes)
    {
        if (localized.ValueKind != JsonValueKind.Object || !localized.TryGetProperty("Images", out var images) || images.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var purpose in purposes)
        {
            foreach (var image in images.EnumerateArray())
            {
                if (Text(image, "ImagePurpose") != purpose || Text(image, "Uri") is not { } uri) continue;
                return (uri.StartsWith("//") ? "https:" + uri : uri) + "?w=1920";   // (the store resizes it)
            }
        }

        return null;
    }

    private static Dictionary<string, (string? Description, string? Developer, string? Wide)>? _epicCatalog;

    /// <summary>
    /// What Epic's launcher keeps on this PC about an Epic game (its catalog, catcache.bin: base64-encoded JSON): the description
    /// and developer, for the Epic games whose store page wasn't found. No internet needed.
    /// </summary>
    private static GameInfo EpicLocal(string item)
    {
        _epicCatalog ??= ReadEpicCatalog();
        if (!_epicCatalog.TryGetValue(item, out var entry) || (entry.Description is null && entry.Developer is null))
            return new GameInfo();

        return new GameInfo
        {
            Source = "EpicLauncher",
            Version = GameInfo.CurrentVersion,
            Description = entry.Description is null ? null : FirstParagraph(entry.Description),
            Developers = entry.Developer is null ? [] : [entry.Developer],
            Background = entry.Wide
        };
    }

    private static Dictionary<string, (string? Description, string? Developer, string? Wide)> ReadEpicCatalog()
    {
        var catalog = new Dictionary<string, (string?, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                    "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");
            if (!File.Exists(file)) return catalog;

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(file).Trim())).TrimStart('\uFEFF');
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return catalog;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (Text(entry, "id") is not { } id) continue;

                // (some games' description is only their name again: that's no description)
                var description = CleanText(Text(entry, "description"));
                if (description is not null && (description.Length < 40 ||
                    CoverService.Normalize(description) == CoverService.Normalize(Text(entry, "title"))))
                    description = null;

                // the store's wide picture of the game, for the top of its page
                string? wide = null;
                if (entry.TryGetProperty("keyImages", out var images) && images.ValueKind == JsonValueKind.Array)
                    foreach (var type in new[] { "DieselStoreFrontWide", "OfferImageWide", "DieselGameBox" })
                        wide ??= images.EnumerateArray().Where(i => Text(i, "type") == type).Select(i => Text(i, "url")).FirstOrDefault(u => u is not null);

                catalog[id] = (description, Text(entry, "developer"), wide);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            // unreadable: those games just get nothing from it
        }

        return catalog;
    }

    // ---------- helpers ----------

    /// <summary>The first paragraph of a longer text (the stores' descriptions run on), cut near 450 letters.</summary>
    private static string FirstParagraph(string text) =>
        Shorten(text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text, 450);

    /// <summary>"2020-09-23T22:00:00.000Z" as "23 Sep 2020" (null when it isn't a date).</summary>
    private static string? IsoDate(string date) =>
        DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : null;

    /// <summary>Markdown as plain text: "**Bold**", "# Heading", links and pictures out (Epic's long descriptions are Markdown).</summary>
    private static string? Markdown(string? text) =>
        text is null ? null
            : Regex.Replace(Regex.Replace(Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", ""), @"\[([^\]]*)\]\([^)]*\)", "$1"), @"[#*_>`]+", "");

    private static string? Text(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;

    private static List<string> Strings(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim())
                .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    /// <summary>Plain text: tags out, "&amp;quot;" and the like turned back into characters, spaces tidied.</summary>
    private static string? CleanText(string? text)
    {
        if (text is null) return null;
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>Cut at the last sentence end before <paramref name="max"/> letters (or at a word, with "…").</summary>
    private static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;

        var cut = text.LastIndexOfAny(['.', '!', '?'], max - 1);
        if (cut >= max / 2) return text[..(cut + 1)];

        var space = text.LastIndexOf(' ', max - 1);
        return text[..(space > 0 ? space : max)].TrimEnd(',', ';', ':') + "…";
    }

    /// <summary>Steam's "17 Sep, 2020" (or "Sep 17, 2020") as "17 Sep 2020"; anything else ("Coming soon", "Q3 2026") as it is.</summary>
    internal static string FormatDate(string date)
    {
        string[] formats = ["d MMM, yyyy", "MMM d, yyyy", "d MMMM, yyyy", "MMMM d, yyyy", "d MMM yyyy"];
        return DateTime.TryParseExact(date, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : date;
    }

    private static void Load()
    {
        _loaded = true;
        _info = new Dictionary<string, GameInfo>(Read<GameInfo>(InfoPath), StringComparer.OrdinalIgnoreCase);
        _names = new Dictionary<string, string>(Read<string>(NamesPath), StringComparer.OrdinalIgnoreCase);

        // the genres saved before GameDeck fetched the rest: shown until the rest comes (then they are asked again with it)
        foreach (var (appId, genres) in Read<List<string>>(OldGenresPath))
        {
            if (!_info.ContainsKey("steam:" + appId))
                _info["steam:" + appId] = genres.Count == 0 ? new GameInfo { Partial = true } : new GameInfo { Source = "Steam", Genres = genres, Partial = true };
        }
    }

    /// <summary>A saved dictionary; empty when there is none (or it is damaged: its answers are asked for again).</summary>
    private static Dictionary<string, T> Read<T>(string file)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(file)) is { } saved)
                return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return [];
    }

    private static void Write<T>(string file, Dictionary<string, T> values)
    {
        try
        {
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(values));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory: saved with the next batch
        }
    }
}
