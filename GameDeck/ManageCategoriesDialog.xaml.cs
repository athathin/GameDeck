using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// Lists the user's categories with Rename and Delete buttons. The dialog doesn't know where categories are stored:
/// the main window supplies the list and carries out the changes.
/// </summary>
public partial class ManageCategoriesDialog : Window
{
    public record Row(string Name, string CountText);

    private readonly Func<List<(string Name, int Games)>> _load;
    private readonly Action<string, string> _rename;
    private readonly Action<string> _delete;

    public ManageCategoriesDialog(Func<List<(string Name, int Games)>> load, Action<string, string> rename, Action<string> delete)
    {
        InitializeComponent();
        TitleBarStyler.Apply(this);

        _load = load;
        _rename = rename;
        _delete = delete;
        Reload();
    }

    private void Reload()
    {
        var categories = _load();

        Rows.ItemsSource = categories
            .Select(c => new Row(c.Name, c.Games == 1 ? "1 game" : $"{c.Games} games"))
            .ToList();
        EmptyText.Visibility = categories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;

        var prompt = new TextPromptDialog("Rename category", $"New name for \"{name}\":", name, "Rename") { Owner = this };
        if (prompt.ShowDialog() != true || prompt.Value.Equals(name, StringComparison.CurrentCulture)) return;

        _rename(name, prompt.Value);
        Reload();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;

        if (!MessageDialog.Ask(this, "Delete category?",
                $"Delete the category \"{name}\"?\nThe games in it stay in your library.", "Delete"))
            return;

        _delete(name);
        Reload();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}
