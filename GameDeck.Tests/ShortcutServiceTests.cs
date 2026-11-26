using System.IO;
using GameDeck.Models;
using GameDeck.Services;
using Xunit;

namespace GameDeck.Tests;

public class ShortcutServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gd-shortcut-test-" + Guid.NewGuid().ToString("N"));
    private const string Exe = @"C:\Games\GameDeck\GameDeck.exe";

    public ShortcutServiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private static dynamic ReadLink(string file)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        return shell.CreateShortcut(file);
    }

    [Theory]
    [InlineData(new[] { "--launch=steam://rungameid/220" }, "steam://rungameid/220")]
    [InlineData(new[] { "--startup", @"--LAUNCH=C:\Games\My Game\game.exe" }, @"C:\Games\My Game\game.exe")]
    [InlineData(new[] { "--launch=" }, null)]
    [InlineData(new[] { "--startup" }, null)]
    public void Reads_the_game_from_the_command_line(string[] args, string? expected) =>
        Assert.Equal(expected, ShortcutService.LaunchTargetFrom(args));

    [Fact]
    public void Makes_a_shortcut_that_starts_the_game_through_GameDeck()
    {
        var game = new Game { Name = "Half-Life: Source?", LaunchTarget = "steam://rungameid/280" };

        var file = ShortcutService.CreateShortcut(game, _folder, Exe);

        Assert.NotNull(file);
        Assert.Equal("Half-Life Source.lnk", Path.GetFileName(file));   // ':' and '?' can't be in a file name
        var link = ReadLink(file!);
        Assert.Equal(Exe, (string)link.TargetPath, ignoreCase: true);
        Assert.Equal("\"--launch=steam://rungameid/280\"", (string)link.Arguments);
    }

    [Fact]
    public void Making_it_again_replaces_its_own_shortcut()
    {
        var game = new Game { Name = "Portal", LaunchTarget = "steam://rungameid/400" };

        var first = ShortcutService.CreateShortcut(game, _folder, Exe);
        var second = ShortcutService.CreateShortcut(game, _folder, Exe);

        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Leaves_a_different_shortcut_with_the_same_name_alone()
    {
        var mine = new Game { Name = "Portal", LaunchTarget = "steam://rungameid/400" };
        var other = new Game { Name = "Portal", LaunchTarget = @"C:\Other\portal.exe" };

        var first = ShortcutService.CreateShortcut(other, _folder, Exe);
        var second = ShortcutService.CreateShortcut(mine, _folder, Exe);

        Assert.Equal("Portal.lnk", Path.GetFileName(first));
        Assert.Equal("Portal (GameDeck).lnk", Path.GetFileName(second));
        Assert.Contains(@"C:\Other\portal.exe", (string)ReadLink(first!).Arguments);   // untouched
    }

    [Fact]
    public void A_second_start_hands_the_game_to_the_running_GameDeck()
    {
        var received = new TaskCompletionSource<string>();
        ShortcutService.Listen(target => received.TrySetResult(target));

        Assert.True(ShortcutService.SendToRunningInstance(@"C:\Games\My Game\game.exe"));
        Assert.True(received.Task.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(@"C:\Games\My Game\game.exe", received.Task.Result);
    }
}
