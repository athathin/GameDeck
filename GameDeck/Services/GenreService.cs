using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// The genres of Steam games ("Action", "RPG", "Indie"...), from the Steam store: only the game's app id is sent. Each game is
/// asked about once, slowly (one request every 1.5 seconds, far below Steam's limit), and the answer is kept on this PC
/// (%AppData%\GameDeck\genres.json). Nothing is asked while "Download cover art from the internet" is off.
/// </summary>
public static class GenreService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "genres.json");

    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.5);
    private static readonly HttpClient Http = CreateClient();

    // app id -> its genres (an empty list: asked, and Steam has none, or the game isn't in the store any more)
    private static Dictionary<string, List<string>> _genres = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static bool _fetching;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameDeck/1.0");
        return client;
    }

    /// <summary>The genres known for the game (none for games that aren't Steam's, or not asked about yet).</summary>
    public static List<string> For(Game game)
    {
        if (!_loaded) Load();
        return game.SteamAppId is { } id && _genres.TryGetValue(id, out var genres) ? genres : [];
    }

    /// <summary>
    /// Asks the Steam store for the genres of the Steam games that have none saved yet (in the background, one at a time).
    /// <paramref name="changed"/> is called (on the calling thread) after each batch that brought new genres.
    /// </summary>
    public static async Task FetchMissingAsync(IReadOnlyCollection<Game> games, Action changed)
    {
        if (_fetching || !SettingsService.OnlineCovers) return;
        if (!_loaded) Load();

        var missing = games.Select(g => g.SteamAppId).OfType<string>().Distinct().Where(id => !_genres.ContainsKey(id)).ToList();
        if (missing.Count == 0) return;

        _fetching = true;
        try
        {
            var fresh = 0;
            foreach (var appId in missing)
            {
                if (!SettingsService.OnlineCovers) break;   // (turned off meanwhile)

                var genres = await AskSteamAsync(appId);
                if (genres is null) break;   // offline, or Steam asks to slow down: the rest wait for the next start

                _genres[appId] = genres;
                if (++fresh % 10 == 0)
                {
                    Save();
                    changed();
                }

                await Task.Delay(Pause);
            }

            if (fresh % 10 != 0)
            {
                Save();
                changed();
            }
        }
        finally
        {
            _fetching = false;
        }
    }

    /// <summary>The game's genres from the store; an empty list when it has none; null when Steam couldn't be asked.</summary>
    private static async Task<List<string>?> AskSteamAsync(string appId)
    {
        try
        {
            using var response = await Http.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=genres&l=english");
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden) return null;
            if (!response.IsSuccessStatusCode) return [];

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            return Parse(json.RootElement, appId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>The genre names of a store answer: { "440": { "success": true, "data": { "genres": [{ "description": "Action" }] } } }.</summary>
    internal static List<string> Parse(JsonElement root, string appId)
    {
        if (!root.TryGetProperty(appId, out var app) || !app.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("genres", out var genres) ||
            genres.ValueKind != JsonValueKind.Array)
            return [];

        return genres.EnumerateArray()
            .Select(g => g.TryGetProperty("description", out var name) ? name.GetString()?.Trim() : null)
            .OfType<string>()
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Load()
    {
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;

            var saved = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath));
            if (saved is not null)
                _genres = new Dictionary<string, List<string>>(saved, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // a damaged file: the genres are asked for again
        }
    }

    private static void Save()
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_genres));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory: saved with the next batch
        }
    }
}
