using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace GameDeck.Services;

/// <summary>One entry of the tray menu's "recent games" list.</summary>
/// <param name="Name">Shown next to the icon.</param>
/// <param name="CoverFile">Picture file the small icon is cut from, or null for a letter icon.</param>
/// <param name="Play">Starts the game.</param>
public sealed record TrayGame(string Name, string? CoverFile, Action Play);

/// <summary>
/// The GameDeck icon in the system tray: click to open the window; right-click for the games played last
/// (click one to play it), Settings, Big Picture mode and Exit. (No "Open" entry: a click on the icon does that.)
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int RecentIconSize = 24;
    private const int MaxMenuWidth = 420;   // at 100% display scale; longer names end in "…"

    private readonly Forms.NotifyIcon _icon;
    private readonly List<Forms.ToolStripItem> _recentItems = [];   // only exist while the menu is open

    /// <param name="afterGame">While a game from GameDeck runs: its name and what happens when it closes (null = no game running).</param>
    /// <param name="setAfterGame">One of the "when it closes" lines was clicked.</param>
    public TrayIcon(Action open, Action settings, Action bigPicture, Action exit, Func<IReadOnlyList<TrayGame>> recentGames,
                    Func<(string Game, AfterGame Choice)?>? afterGame = null, Action<AfterGame>? setAfterGame = null)
    {
        var scale = DpiScale();

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(scale),
            BackColor = DarkMenuRenderer.Background,
            ForeColor = DarkMenuRenderer.Text,
            Font = new Drawing.Font("Segoe UI", 10f),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            DropShadowEnabled = false,   // a shadow would show square corners behind the rounded menu
            Padding = new Forms.Padding((int)(4 * scale))
        };

        menu.Items.Add(MenuItem("Settings", settings, scale));
        menu.Items.Add(MenuItem("Big Picture mode", bigPicture, scale));
        menu.Items.Add(new Forms.ToolStripSeparator { Margin = new Forms.Padding(0, (int)(3 * scale), 0, (int)(3 * scale)) });
        menu.Items.Add(MenuItem("Exit", exit, scale));

        // the recent games are looked up each time the menu opens, and their icons are freed when it closes
        menu.Opening += (_, _) =>
        {
            RemoveRecentItems(menu);

            IReadOnlyList<TrayGame> games;
            try { games = recentGames(); }
            catch { games = []; }

            var index = 0;

            // while a game runs: "When Elden Ring closes", then the three choices, the chosen one ticked
            (string Game, AfterGame Choice)? running = null;
            try { running = afterGame?.Invoke(); }
            catch { }

            if (running is { } now && setAfterGame is not null)
            {
                var header = new Forms.ToolStripMenuItem("When " + now.Game + " closes:")
                {
                    Enabled = false,
                    Padding = new Forms.Padding((int)(TextLeft * scale), (int)(8 * scale), (int)(24 * scale), (int)(4 * scale)),
                    TextAlign = Drawing.ContentAlignment.MiddleLeft,
                    AutoSize = true
                };
                menu.Items.Insert(index++, header);
                _recentItems.Add(header);

                foreach (var choice in Enum.GetValues<AfterGame>())
                {
                    var chosen = choice;
                    var item = MenuItem((choice == now.Choice ? "✓  " : "     ") + choice.Label(), () => setAfterGame(chosen), scale);
                    menu.Items.Insert(index++, item);
                    _recentItems.Add(item);
                }

                var line = new Forms.ToolStripSeparator { Margin = new Forms.Padding(0, (int)(3 * scale), 0, (int)(3 * scale)) };
                menu.Items.Insert(index++, line);
                _recentItems.Add(line);
            }

            foreach (var game in games)
            {
                var item = RecentItem(game, scale);
                menu.Items.Insert(index++, item);
                _recentItems.Add(item);
            }

            if (games.Count > 0)
            {
                var separator = new Forms.ToolStripSeparator { Margin = new Forms.Padding(0, (int)(3 * scale), 0, (int)(3 * scale)) };
                menu.Items.Insert(index, separator);
                _recentItems.Add(separator);
            }

            FitWidth(menu, scale);
        };
        menu.Closed += (_, _) =>
        {
            // not right away: a click on an item is still being handled while the menu closes
            try { menu.BeginInvoke(new Action(() => RemoveRecentItems(menu))); }
            catch { }
        };

        // round the corners: a menu is a rectangular window, so the corners are cut off with a region. The region must
        // follow the menu's size, which can still change after it opened (the items are measured late).
        void RoundCorners()
        {
            if (menu.Width <= 0 || menu.Height <= 0) return;

            using var path = RoundedRectangle(new Drawing.Rectangle(0, 0, menu.Width, menu.Height), (int)(8 * scale));
            menu.Region?.Dispose();
            menu.Region = new Drawing.Region(path);
        }
        menu.Opened += (_, _) => RoundCorners();
        menu.SizeChanged += (_, _) => RoundCorners();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = AppInfo.DisplayName,
            ContextMenuStrip = menu,
            Visible = false
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                open();
        };
    }

    public void Show() => _icon.Visible = true;

    public void Hide() => _icon.Visible = false;

    public void Dispose()
    {
        _icon.Visible = false;   // otherwise a ghost icon stays in the tray until the mouse passes over it
        if (_icon.ContextMenuStrip is { } menu)
            RemoveRecentItems(menu);
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    private static Forms.ToolStripMenuItem MenuItem(string text, Action click, float scale)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            Padding = new Forms.Padding((int)(TextLeft * scale), (int)(8 * scale), (int)(24 * scale), (int)(8 * scale)),
            TextAlign = Drawing.ContentAlignment.MiddleLeft,
            AutoSize = true
        };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Space left of the text (a plain item), in pixels at 100% display scale.</summary>
    private const int TextLeft = 14;

    /// <summary>Space left of the text on a recent-game item: the icon and the gap after it fit in there.</summary>
    private const int RecentTextLeft = TextLeft + RecentIconSize + 10;

    private static Forms.ToolStripMenuItem RecentItem(TrayGame game, float scale)
    {
        var item = new Forms.ToolStripMenuItem(game.Name)
        {
            Padding = new Forms.Padding((int)(RecentTextLeft * scale), (int)(6 * scale), (int)(24 * scale), (int)(6 * scale)),
            TextAlign = Drawing.ContentAlignment.MiddleLeft,
            AutoSize = true,
            Tag = MakeGameIcon(game, (int)(RecentIconSize * scale))   // drawn by the renderer, freed in RemoveRecentItems
        };
        item.Click += (_, _) => game.Play();
        return item;
    }

    /// <summary>Makes the menu exactly as wide as its longest entry (icon, text and a little room), up to a maximum.</summary>
    private static void FitWidth(Forms.ContextMenuStrip menu, float scale)
    {
        const Forms.TextFormatFlags flags = Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.SingleLine;

        var widest = 0;
        foreach (var item in menu.Items.OfType<Forms.ToolStripMenuItem>())
        {
            var left = (int)((item.Tag is Drawing.Bitmap ? RecentTextLeft : TextLeft) * scale);
            var text = Forms.TextRenderer.MeasureText(item.Text, menu.Font, Drawing.Size.Empty, flags).Width;
            widest = Math.Max(widest, left + text + (int)(18 * scale));
        }

        var width = Math.Min(widest, (int)(MaxMenuWidth * scale)) + menu.Padding.Horizontal;

        // the height stays automatic (0): only the width is fixed
        menu.MinimumSize = new Drawing.Size(width, 0);
        menu.MaximumSize = new Drawing.Size(width, 0);
    }

    private void RemoveRecentItems(Forms.ContextMenuStrip menu)
    {
        foreach (var item in _recentItems)
        {
            menu.Items.Remove(item);
            if (item.Tag is Drawing.Bitmap icon)
                icon.Dispose();
            item.Dispose();
        }

        _recentItems.Clear();
    }

    /// <summary>A small rounded-square icon: the middle of the game's cover, or a purple square with its first letter.</summary>
    private static Drawing.Bitmap MakeGameIcon(TrayGame game, int size)
    {
        var icon = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Drawing.Graphics.FromImage(icon);
        g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;

        using var shape = RoundedRectangle(new Drawing.Rectangle(0, 0, size, size), Math.Max(3, size / 6));
        g.SetClip(shape);

        try
        {
            if (game.CoverFile is { } file && System.IO.File.Exists(file))
            {
                using var stream = System.IO.File.OpenRead(file);   // read through a stream so the file isn't kept locked
                using var picture = Drawing.Image.FromStream(stream);

                // covers are tall posters: use the square in the middle
                var side = Math.Min(picture.Width, picture.Height);
                var source = new Drawing.Rectangle((picture.Width - side) / 2, (picture.Height - side) / 2, side, side);
                g.DrawImage(picture, new Drawing.Rectangle(0, 0, size, size), source, Drawing.GraphicsUnit.Pixel);
                return icon;
            }
        }
        catch
        {
            // unreadable picture: fall through to the letter icon
        }

        var accent = ThemeService.AccentColor;
        using (var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(accent.R, accent.G, accent.B)))
            g.FillRectangle(brush, 0, 0, size, size);

        var letter = game.Name.Trim().Length > 0 ? game.Name.Trim()[..1].ToUpperInvariant() : "?";
        using var font = new Drawing.Font("Segoe UI", size * 0.42f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        using var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
        g.DrawString(letter, font, Drawing.Brushes.White, new Drawing.RectangleF(0, 0, size, size), format);
        return icon;
    }

    private static float DpiScale()
    {
        using var graphics = Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return graphics.DpiX / 96f;
    }

    private static Drawing2D.GraphicsPath RoundedRectangle(Drawing.Rectangle bounds, int radius)
    {
        var d = radius * 2;
        var path = new Drawing2D.GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri(AppInfo.IconUri))!.Stream;
            using (stream)
                return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch
        {
            return Drawing.SystemIcons.Application;
        }
    }

    /// <summary>Draws the tray menu in the same dark, rounded style as the menus inside GameDeck.</summary>
    private sealed class DarkMenuRenderer(float scale) : Forms.ToolStripProfessionalRenderer(new DarkColors())
    {
        public static readonly Drawing.Color Background = Drawing.Color.FromArgb(0x1E, 0x22, 0x2B);
        public static readonly Drawing.Color Border = Drawing.Color.FromArgb(0x2A, 0x2F, 0x3A);
        public static readonly Drawing.Color Highlight = Drawing.Color.FromArgb(0x2A, 0x2F, 0x3A);
        public static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xC5, 0xCB, 0xD6);

        /// <summary>
        /// The visible area of an item. Windows makes the items wider than the menu itself (they were measured with
        /// their padding, the menu was not), so the width is limited to what the menu shows: the same gap on the right
        /// as on the left, so the highlight sits in the middle. (The menu's DisplayRectangle is a few pixels too wide on
        /// the right, which left the highlight off-center.)
        /// </summary>
        private static Drawing.Rectangle Row(Forms.ToolStripItem item)
        {
            var visibleWidth = item.Owner is { } menu ? menu.Width - 2 * item.Bounds.Left : item.Width;
            return new Drawing.Rectangle(0, 0, Math.Max(1, Math.Min(item.Width, visibleWidth)), item.Height);
        }

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new Drawing.SolidBrush(Background);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            var bounds = new Drawing.Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using var path = RoundedRectangle(bounds, (int)(8 * scale));
            using var pen = new Drawing.Pen(Border, 1.5f);
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;

            e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            using var path = RoundedRectangle(Row(e.Item), (int)(5 * scale));
            using var brush = new Drawing.SolidBrush(Highlight);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            // drawn by hand, in the middle of the row from top to bottom (the built-in text position drifts towards the top)
            var row = Row(e.Item);
            var color = e.Item.Selected ? Drawing.Color.White : Text;

            var textLeft = (int)(TextLeft * scale);
            if (e.Item.Tag is Drawing.Bitmap icon)
            {
                e.Graphics.DrawImage(icon, textLeft, (row.Height - icon.Height) / 2, icon.Width, icon.Height);
                textLeft = (int)(RecentTextLeft * scale);
            }

            var textArea = new Drawing.Rectangle(textLeft, 0, Math.Max(1, row.Width - textLeft - (int)(10 * scale)), row.Height);
            Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, textArea, color,
                Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine |
                Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.EndEllipsis);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Border);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, (int)(8 * scale), y, Row(e.Item).Width - (int)(8 * scale), y);
        }
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => DarkMenuRenderer.Background;
        public override Drawing.Color MenuBorder => DarkMenuRenderer.Border;
        public override Drawing.Color MenuItemBorder => DarkMenuRenderer.Highlight;
        public override Drawing.Color MenuItemSelected => DarkMenuRenderer.Highlight;
        public override Drawing.Color ImageMarginGradientBegin => DarkMenuRenderer.Background;
        public override Drawing.Color ImageMarginGradientMiddle => DarkMenuRenderer.Background;
        public override Drawing.Color ImageMarginGradientEnd => DarkMenuRenderer.Background;
        public override Drawing.Color SeparatorDark => DarkMenuRenderer.Border;
        public override Drawing.Color SeparatorLight => DarkMenuRenderer.Border;
    }
}
