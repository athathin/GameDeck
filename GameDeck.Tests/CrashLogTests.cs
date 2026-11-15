using GameDeck.Services;

namespace GameDeck.Tests;

public class CrashLogTests
{
    [Fact]
    public void The_users_profile_folder_is_removed_from_a_report()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = CrashLog.Sanitize($"Could not find file '{profile}\\Documents\\game.exe'");

        Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", text);
    }

    [Fact]
    public void The_report_address_opens_the_bug_form_with_the_version_filled_in()
    {
        var url = CrashLog.ReportUrl(new InvalidOperationException("something broke"));

        Assert.StartsWith("https://github.com/athathin/GameDeck/issues/new?template=bug_report.yml", url);
        Assert.Contains("version=" + Uri.EscapeDataString(UpdateService.CurrentVersionText), url);
        Assert.Contains("InvalidOperationException", url);
    }

    [Fact]
    public void A_very_long_error_still_gives_an_address_that_fits()
    {
        var error = new Exception(new string('x', 20000));

        Assert.True(CrashLog.ReportUrl(error).Length <= 6500);
    }

    [Fact]
    public void A_report_without_an_error_does_not_fail()
    {
        Assert.NotEmpty(CrashLog.ReportUrl(null));
        Assert.Contains("(no details)", CrashLog.Describe(null, "test"));
    }
}
