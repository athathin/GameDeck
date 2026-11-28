using System.Windows;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

// The small types Big Picture is built from: what it can ask of the main window, and the rows of its lists and menus.

/// <summary>What Big Picture's settings panel can ask of the main window: the actions of the desktop settings page.</summary>
/// <remarks>Each action takes the window its dialogs belong to and returns a result line to show (null = nothing to say).</remarks>
public record BigPictureSettingsActions(
    Func<Task<string?>> Scan,
    Func<Action<string>, Task<string?>> CheckForUpdates,
    Func<Window, string?> AddGames,
    Func<IReadOnlyList<string>, string?> AddFiles,
    Func<Window, string?> FindDuplicates,
    Action<Window> ExportBackup,
    Func<Window, string?> ImportBackup,
    Action<Window> Uninstall,
    Func<(IReadOnlyList<Game> Games, IReadOnlySet<string> HiddenKeys)> GetLibrary,
    Action PlaytimeSourcesChanged);

/// <summary>What the game menu (the controller's Menu button, the right-click menu of the desktop library) can ask of the main window.</summary>
/// <remarks>Everything here works without a mouse or keyboard: Big Picture asks for text with its own on-screen keyboard and
/// for files with its own browser, and passes the result.</remarks>
public record BigPictureGameActions(
    Action<Game, string> SetName,
    Action<Game, string?, bool> SetLaunchOptions,
    Action<Game, LaunchProfile?> SaveRoutine,
    Func<IReadOnlyList<string>> AllCategories,
    Action<Game, string> ToggleCategory,
    Action<Game, string> AddToCategory,
    Action<string, string> RenameCategory,
    Action<string> DeleteCategory,
    Action<Game, string?> SetNotes,
    Action<Game, GameStatus> SetStatus,
    Action<Game, int> SetRating,
    Action<Game> ToggleHidden,
    Func<Game, string, bool> SetCover,
    Func<Game, Task> RemoveCover,
    Action<Game, Window> CreateShortcut,
    Action<Game> Uninstall,
    Action<Game, Window> RemoveFromLibrary,
    Func<Game?> RunningGame,
    Action<Game> Resume,
    Func<Game, Task<bool>> CloseGame,
    Func<Game, Task<bool>> ForceCloseGame,
    Action<Game, string?> SetLeftOff,
    Func<AfterGame> GetAfterGame,
    Action<AfterGame> SetAfterGame);

/// <summary>
/// One line of a list panel (the filters, the game menu, the file browser): a heading, a tick box, a radio button or a plain
/// action. (The settings have their own rows: <see cref="SettingRow"/>.) With <see cref="Adjust"/>, left / right change its value
/// (-1 / +1), and it shows ‹ › arrows that the mouse can click.
/// </summary>
internal record FilterRow(string Text, bool IsHeader = false, Func<bool>? IsOn = null, Action? Activate = null, bool IsRadio = false,
                          Action<int>? Adjust = null);

/// <summary>One entry of the Power menu. With a ConfirmTitle it asks "are you sure?" first.</summary>
internal record PowerEntry(string Text, Action Run, string? ConfirmTitle = null, string? ConfirmButton = null);

/// <summary>One page of the game menu: the main list, or a submenu (Categories, Status), and the row selected in it.</summary>
internal sealed class MenuPage(string title, Func<List<FilterRow>> build)
{
    public string Title { get; } = title;
    public Func<List<FilterRow>> Build { get; } = build;
    public int Index { get; set; }
}

/// <summary>
/// One line of a settings section. What it shows on the right follows from what it has:
/// <see cref="IsOn"/> a switch, <see cref="Value"/> with <see cref="Adjust"/> a "‹ choice ›", <see cref="Steps"/> a bar,
/// <see cref="Button"/> a button, <see cref="Value"/> alone a plain value (not selectable). A heading has only a title.
/// </summary>
internal sealed record SettingRow(
    string Title,
    string? Subtitle = null,
    bool IsHeader = false,
    Func<bool>? IsOn = null,
    Func<string>? Value = null,
    Action<int>? Adjust = null,
    Func<(int Index, int Count)>? Steps = null,
    string? Button = null,
    Action? Activate = null)
{
    public bool Selectable => !IsHeader && (Activate is not null || Adjust is not null);
}

/// <summary>One section of the settings: its name and icon in the list on the left, and its settings.</summary>
internal sealed record SettingsSection(string Name, string Glyph, Func<List<SettingRow>> Rows);
