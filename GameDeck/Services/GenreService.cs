using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// The genres of games ("Action", "RPG", "Indie"...), from the Steam store. A Steam game is asked about by its app id; another
/// game (Epic, GOG, Ubisoft...) by its name first, the way its cover is looked for (an exact name match on the Steam store,
/// the same request CoverService makes), and then by the app id found. Games that aren't on Steam get none. Each game is asked
/// about once, slowly (one request every 1.5 seconds, far below Steam's limit), and the answers are kept on this PC
/// (%AppData%\GameDeck\genres.json and genre-names.json). Nothing is asked while "Download cover art from the internet" is off.
/// </summary>
public static class GenreService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "genres.json");

    private static readonly string NamesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "genre-names.json");

    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.5);
    private static readonly HttpClient Http = CreateClient();

    // app id -> its genres (an empty list: asked, and Steam has none, or the game isn't in the store any more)
    private static Dictionary<string, List<string>> _genres = new(StringComparer.OrdinalIgnoreCase);

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
    private static string? AppIdOf(Game game) =>
        game.SteamAppId ?? (_names.TryGetValue(game.Name, out var id) && id.Length > 0 ? id : null);

    /// <summary>The genres known for the game (none when it isn't on Steam, or it hasn't been asked about yet).</summary>
    public static List<string> For(Game game)
    {
        if (!_loaded) Load();
        return AppIdOf(game) is { } id && _genres.TryGetValue(id, out var genres) ? genres : [];
    }

    /// <summary>
    /// Asks the Steam store, in the background and one at a time, first for the app ids of the non-Steam games not looked for
    /// yet, then for the genres not saved yet. <paramref name="changed"/> is called (on the calling thread) after each batch.
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
                if (++found % 10 == 0) SaveNames();
                await Task.Delay(Pause);
            }
            if (found > 0) SaveNames();

            // 2) the genres of every app id not asked about yet
            var missing = games.Select(AppIdOf).OfType<string>().Distinct().Where(id => !_genres.ContainsKey(id)).ToList();
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
        _genres = new Dictionary<string, List<string>>(Read<List<string>>(FilePath), StringComparer.OrdinalIgnoreCase);
        _names = new Dictionary<string, string>(Read<string>(NamesPath), StringComparer.OrdinalIgnoreCase);
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

    private static void Save() => Write(FilePath, _genres);

    private static void SaveNames() => Write(NamesPath, _names);

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
