using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// Lists groups of probable duplicates. For each group the user picks the entry to keep and presses Merge;
/// the merge itself is done by the <c>merge</c> callback, then the group disappears from the list.
/// </summary>
public partial class DuplicatesDialog : Window
{
    private readonly Action<Game, List<Game>> _merge;

    /// <summary>How many groups were merged while the dialog was open.</summary>
    public int MergedGroups { get; private set; }

    public DuplicatesDialog(List<List<Game>> groups, Action<Game, List<Game>> merge)
    {
        InitializeComponent();
        TitleBarStyler.Apply(this);

        _merge = merge;

        foreach (var group in groups)
            GroupList.Children.Add(BuildGroup(group));
    }

    private UIElement BuildGroup(List<Game> group)
    {
        var radios = new List<(RadioButton Radio, Game Game)>();
        var radioPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        var groupName = "Group" + Guid.NewGuid().ToString("N");
        foreach (var game in group)
        {
            var radio = new RadioButton
            {
                GroupName = groupName,
                Style = (Style)FindResource("DarkRadioButton"),
                Margin = new Thickness(0, 0, 0, 10),
                Content = Describe(game)
            };

            radios.Add((radio, game));
            radioPanel.Children.Add(radio);
        }

        radios[0].Radio.IsChecked = true;   // the most played, launcher entry first

        var mergeButton = new Button
        {
            Content = "Merge",
            Style = (Style)FindResource("PillButton"),
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x2B)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 16, 18, 10),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = group[0].DisplayName, Foreground = Brushes.White, FontSize = 16,
                        FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    radioPanel,
                    mergeButton
                }
            }
        };

        mergeButton.Click += (_, _) =>
        {
            var keep = radios.First(r => r.Radio.IsChecked == true).Game;

            _merge(keep, group.Where(g => !ReferenceEquals(g, keep)).ToList());
            MergedGroups++;

            GroupList.Children.Remove(card);
            DoneText.Visibility = GroupList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };

        return card;
    }

    /// <summary>One line per entry: where it comes from, how long it was played, and where it is.</summary>
    private static UIElement Describe(Game game)
    {
        var source = game.Source == "Manual" ? "Added by hand" : game.Source;
        var where = game.InstallDir ?? (System.IO.File.Exists(game.LaunchTarget) ? game.LaunchTarget : null);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"{source}  ·  {(game.PlaytimeSeconds > 0 ? game.PlaytimeText : "Not played yet")}",
            Foreground = Brushes.White, FontSize = 14
        });

        if (where is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = where, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0xA0)), FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = where, MaxWidth = 430
            });
        }

        return panel;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}
