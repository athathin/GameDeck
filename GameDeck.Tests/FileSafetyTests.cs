using System.IO;
using System.Text.Json;
using GameDeck.Services;

namespace GameDeck.Tests;

/// <summary>Saving and recovering the data files (in a temporary folder, never the real %AppData%).</summary>
public class FileSafetyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gamedeck-tests-" + Guid.NewGuid().ToString("N"));

    public FileSafetyTests()
    {
        Directory.CreateDirectory(_folder);
        AutoBackupService.UseFolder(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Fact]
    public void A_saved_file_has_the_new_content_and_leaves_no_temporary_file()
    {
        var path = Path.Combine(_folder, "data.json");

        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");

        Assert.Equal("two", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_missing_file_gives_nothing()
    {
        Assert.Null(AutoBackupService.LoadJson<Dictionary<string, int>>(Path.Combine(_folder, "nope.json")));
    }

    [Fact]
    public void A_damaged_file_is_replaced_by_the_newest_backup_and_set_aside()
    {
        var path = Path.Combine(_folder, "userdata.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, int> { ["game"] = 7 }));

        AutoBackupService.RunIfDue();                   // today's copy of the good file
        File.WriteAllText(path, "{ this is not json");  // then the file gets damaged

        var restored = AutoBackupService.LoadJson<Dictionary<string, int>>(path);

        Assert.NotNull(restored);
        Assert.Equal(7, restored!["game"]);
        Assert.Single(Directory.GetFiles(_folder, "userdata.json.corrupt-*"));   // the bad file was kept, not deleted
    }

    [Fact]
    public void A_damaged_file_without_any_backup_gives_nothing_instead_of_failing()
    {
        var path = Path.Combine(_folder, "sessions.json");
        File.WriteAllText(path, "not json at all");

        Assert.Null(AutoBackupService.LoadJson<List<int>>(path));
    }

    [Fact]
    public void A_backup_that_was_cut_short_is_made_again()
    {
        File.WriteAllText(Path.Combine(_folder, "userdata.json"), "{}");

        // today's folder exists (a crash during the copy) but has no "finished" mark
        var today = Path.Combine(_folder, "backups", DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(today);

        AutoBackupService.RunIfDue();

        Assert.True(File.Exists(Path.Combine(today, "userdata.json")));
    }

    [Fact]
    public void Only_the_last_seven_daily_backups_are_kept()
    {
        File.WriteAllText(Path.Combine(_folder, "userdata.json"), "{}");

        var backups = Path.Combine(_folder, "backups");
        for (var day = 1; day <= 10; day++)
            Directory.CreateDirectory(Path.Combine(backups, $"2020-01-{day:00}"));

        AutoBackupService.RunIfDue();

        Assert.Equal(7, Directory.GetDirectories(backups).Length);
    }
}
