using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace GameDeck.Services;

/// <summary>
/// IGDB (igdb.com, Twitch's game database), only with the user's own Twitch app keys (Settings › Library): no keys, no IGDB.
/// It knows games no store has. Asked by the game's name (an exact name match only, like the stores), for its details, its
/// cover (after the stores and SteamGridDB) and its artwork (the background at the top of its page). The keys stay on this PC;
/// the game's name is sent to IGDB. A short-lived access token is asked of Twitch with the keys and kept in memory.
/// </summary>
public static class IgdbService
{
    private static readonly HttpClient Http = CreateClient();

    private static string? _token;
    private static DateTime _tokenUntil;
    private static bool _rejected;   // Twitch refused the keys: nothing asked again until they change

    // a game's normalized name -> what IGDB has under that name (null: nothing), so the details and the cover ask once
    private static readonly Dictionary<string, Task<IgdbGame?>> Asked = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameDeck/1.0");
        return client;
    }

    /// <summary>True when the user entered both keys (and Twitch hasn't refused them).</summary>
    public static bool Configured =>
        !_rejected && !string.IsNullOrWhiteSpace(SettingsService.IgdbClientId) && !string.IsNullOrWhiteSpace(SettingsService.IgdbClientSecret);

    /// <summary>The keys changed: forget the token, the refusal and what was asked.</summary>
    public static void Reset()
    {
        _token = null;
        _rejected = false;
        lock (Asked) Asked.Clear();
    }

    /// <summary>What IGDB says about a game (by its name).</summary>
    public sealed record IgdbGame(string Name, string? Summary, DateTime? Released, List<string> Genres, List<string> Developers,
                                  List<string> Publishers, string? CoverId, string? ArtworkId);

    /// <summary>A picture of IGDB in one of its sizes ("cover_big_2x": 528 x 748, "1080p": 1920 x 1080).</summary>
    public static string ImageUrl(string imageId, string size) => $"https://images.igdb.com/igdb/image/upload/t_{size}/{imageId}.jpg";

    /// <summary>
    /// Checks the keys by asking Twitch for a token. Null when they work; otherwise what went wrong, in words for Settings.
    /// </summary>
    public static async Task<string?> CheckKeysAsync()
    {
        Reset();
        if (string.IsNullOrWhiteSpace(SettingsService.IgdbClientId) || string.IsNullOrWhiteSpace(SettingsService.IgdbClientSecret))
            return "Enter both the Client ID and the Client Secret.";

        try
        {
            return await TokenAsync() is null
                ? _rejected ? "Twitch refused these keys. Check the Client ID and Client Secret." : "Twitch couldn't be reached. Is this PC online?"
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "Twitch couldn't be reached. Is this PC online?";
        }
    }

    /// <summary>
    /// The name as IGDB's search wants it: without ™, ® and © ("Rocket League®" finds nothing, "Rocket League" does) and
    /// without doubled spaces.
    /// </summary>
    public static string SearchName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.Replace("™", " ").Replace("®", " ").Replace("©", " ").Replace("℠", " "), @"\s+", " ").Trim();

    /// <summary>The game IGDB has under exactly this name (normalized), or null; asked once per name.</summary>
    public static Task<IgdbGame?> FindAsync(string name)
    {
        name = SearchName(name);
        var key = CoverService.Normalize(name);
        lock (Asked)
        {
            if (!Asked.TryGetValue(key, out var task) || task.IsFaulted)
                Asked[key] = task = SearchAsync(name, key);
            return task;
        }
    }

    /// <summary>
    /// The details of a game for GameInfoService (by its name as it reads: IGDB's search needs the words); an empty GameInfo when
    /// IGDB has nothing; null when it couldn't be asked (offline, refused keys, too many requests).
    /// </summary>
    internal static async Task<GameInfo?> InfoAsync(string name)
    {
        if (!Configured) return new GameInfo();

        IgdbGame? game;
        try
        {
            game = await FindAsync(name);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException)
        {
            return null;
        }

        return game is null ? new GameInfo() : ToInfo(game);
    }

    internal static GameInfo ToInfo(IgdbGame game) => new()
    {
        Source = "IGDB",
        Version = GameInfo.CurrentVersion,
        Description = game.Summary,
        Developers = game.Developers,
        Publishers = game.Publishers,
        Genres = game.Genres,
        Released = game.Released?.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        Background = game.ArtworkId is { } artwork ? ImageUrl(artwork, "1080p") : null
    };

    private static async Task<IgdbGame?> SearchAsync(string name, string normalized)
    {
        var token = await TokenAsync() ?? throw new InvalidOperationException("No IGDB token.");

        // (the name as typed is searched; only an exact match, normalized, is taken: never a game of a similar name)
        var query = $"search \"{name.Replace("\\", "").Replace("\"", "")}\"; " +
                    "fields name,summary,first_release_date,genres.name,involved_companies.company.name,involved_companies.developer," +
                    "involved_companies.publisher,cover.image_id,artworks.image_id,screenshots.image_id; limit 20;";

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games")
        {
            Content = new StringContent(query, Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("Client-ID", SettingsService.IgdbClientId!.Trim());
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _token = null;   // (expired: a new one next time)
            throw new InvalidOperationException("IGDB refused the token.");
        }
        response.EnsureSuccessStatusCode();   // (429 and other problems throw: nothing is remembered as "not there")

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Pick(json.RootElement, normalized);
    }

    /// <summary>The result whose name is exactly the wanted one (normalized), read into an IgdbGame; null when none is.</summary>
    internal static IgdbGame? Pick(JsonElement results, string normalized)
    {
        if (results.ValueKind != JsonValueKind.Array) return null;

        foreach (var game in results.EnumerateArray())
        {
            if (Text(game, "name") is not { } title || CoverService.Normalize(title) != normalized) continue;

            var developers = new List<string>();
            var publishers = new List<string>();
            if (game.TryGetProperty("involved_companies", out var companies) && companies.ValueKind == JsonValueKind.Array)
            {
                foreach (var company in companies.EnumerateArray())
                {
                    if (!company.TryGetProperty("company", out var c) || Text(c, "name") is not { } companyName) continue;
                    if (Flag(company, "developer") && !developers.Contains(companyName)) developers.Add(companyName);
                    if (Flag(company, "publisher") && !publishers.Contains(companyName)) publishers.Add(companyName);
                }
            }

            DateTime? released = game.TryGetProperty("first_release_date", out var date) && date.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                : null;

            return new IgdbGame(
                title,
                Text(game, "summary") is { } summary ? Shorten(summary) : null,
                released,
                game.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array
                    ? genres.EnumerateArray().Select(g => Text(g, "name")).OfType<string>().ToList()
                    : [],
                developers,
                publishers,
                game.TryGetProperty("cover", out var cover) ? Text(cover, "image_id") : null,
                FirstImage(game, "artworks") ?? FirstImage(game, "screenshots"));
        }

        return null;
    }

    /// <summary>A short-lived access token for IGDB, asked of Twitch with the user's keys (kept until shortly before it ends).</summary>
    private static async Task<string?> TokenAsync()
    {
        if (_token is not null && DateTime.UtcNow < _tokenUntil) return _token;
        if (!Configured) return null;

        var url = "https://id.twitch.tv/oauth2/token?grant_type=client_credentials" +
                  $"&client_id={Uri.EscapeDataString(SettingsService.IgdbClientId!.Trim())}" +
                  $"&client_secret={Uri.EscapeDataString(SettingsService.IgdbClientSecret!.Trim())}";

        using var response = await Http.PostAsync(url, null);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _rejected = true;   // wrong keys: not asked again until they change
            return null;
        }
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _token = Text(json.RootElement, "access_token");
        var lifetime = json.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt64(out var s) ? s : 3600;
        _tokenUntil = DateTime.UtcNow.AddSeconds(Math.Max(60, lifetime - 300));
        return _token;
    }

    private static string? FirstImage(JsonElement game, string property) =>
        game.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(i => Text(i, "image_id")).FirstOrDefault(id => id is not null)
            : null;

    private static bool Flag(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String &&
        v.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>IGDB's summaries can be long: cut at the last sentence end before 450 letters.</summary>
    private static string Shorten(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length <= 450) return text;

        var cut = text.LastIndexOfAny(['.', '!', '?'], 449);
        return cut >= 225 ? text[..(cut + 1)] : text[..text.LastIndexOf(' ', 449)] + "…";
    }
}
