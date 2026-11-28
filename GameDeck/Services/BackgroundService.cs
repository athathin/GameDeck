using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// The wide background art at the top of a game's page: the store's own (Steam's library hero, GOG's, Epic's, the Microsoft
/// Store's), or IGDB's artwork with the user's keys. Downloaded once and kept on this PC (%AppData%\GameDeck\backgrounds);
/// a game without one is asked again after a week. Nothing is downloaded while "Download cover art from the internet" is off.
/// </summary>
public static class BackgroundService
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "backgrounds");

    private static readonly TimeSpan RetryMissAfter = TimeSpan.FromDays(7);
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameDeck/1.0");
        return client;
    }

    /// <summary>Stands for IGDB's artwork in the list of candidates: looked up (by the game's name) only when it is its turn.</summary>
    internal const string IgdbCandidate = "igdb:artwork";

    /// <summary>
    /// Where the picture may be, best first: the store's own, then Steam's (by the game's own or found app id), last IGDB's
    /// artwork when the user has keys (any game: also the ones whose stores have no background picture).
    /// </summary>
    internal static List<string> Candidates(Game game)
    {
        var urls = new List<string>();
        if (game.Info?.Background is { } background) urls.Add(background);

        if (GameInfoService.AppIdOf(game) is { } appId)
        {
            urls.Add($"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_hero.jpg");
            urls.Add($"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/page_bg_raw.jpg");
        }

        if (IgdbService.Configured && game.Info?.Source != "IGDB")   // (an IGDB game's artwork is already first)
            urls.Add(IgdbCandidate);

        return urls;
    }

    /// <summary>IGDB's artwork of the game (or a screenshot of it), or null when IGDB has none.</summary>
    private static async Task<string?> IgdbArtworkAsync(Game game) =>
        await IgdbService.FindAsync(game.Name) is { ArtworkId: { } artwork } ? IgdbService.ImageUrl(artwork, "1080p") : null;

    /// <summary>
    /// The game's background, read in the background (null when it has none, or it can't be downloaded now). Never throws.
    /// </summary>
    public static async Task<ImageSource?> LoadAsync(Game game)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var key = Hash(game.LaunchTarget);
            var file = Path.Combine(Folder, key + ".jpg");
            var miss = Path.Combine(Folder, key + ".none");

            if (!File.Exists(file))
            {
                if (!SettingsService.OnlineCovers) return null;

                var candidates = Candidates(game);
                if (candidates.Count == 0) return null;   // (not known yet: the details may still come; no "none" is written)

                // tried these same addresses lately and none had a picture (a new address, from new details, is tried at once)
                var tried = string.Join("\n", candidates);
                if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < RetryMissAfter &&
                    await File.ReadAllTextAsync(miss) == tried)
                    return null;

                var found = false;
                foreach (var candidate in candidates)
                {
                    var url = candidate == IgdbCandidate ? await IgdbArtworkAsync(game) : candidate;
                    if (url is not null && await DownloadAsync(url, file)) { found = true; break; }
                }

                if (!found)
                {
                    await File.WriteAllTextAsync(miss, tried);
                    return null;
                }
            }

            return await Task.Run(() => Decode(file));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or
                                         System.Text.Json.JsonException or InvalidOperationException)   // (the last two: IGDB)
        {
            return null;   // offline or unreadable: the page simply has no background (tried again next time)
        }
    }

    private static async Task<bool> DownloadAsync(string url, string destination)
    {
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return false;   // (404: no picture at this address)

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length < 5000) return false;            // (an error page or a placeholder, not a picture)

        var temp = destination + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, destination, overwrite: true);
        return true;
    }

    private static ImageSource? Decode(string file)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1920;   // (a 4K picture would cost a lot of memory for no gain)
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            try { File.Delete(file); } catch (IOException) { }   // (damaged: downloaded again next time)
            return null;
        }
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..16];
}
