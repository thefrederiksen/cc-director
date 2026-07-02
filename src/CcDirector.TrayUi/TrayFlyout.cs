using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace CcDirector.TrayUi;

/// <summary>
/// A OneDrive-style tray flyout: a borderless, shadowed panel that slides up at the bottom-right of
/// the screen (above the taskbar) when the user LEFT-CLICKS the tray icon. It is the app's ONE
/// local surface: header with a live status pill, label/value status rows, a quieter details
/// section, a full-width primary action, a grid of secondary actions, and a footer of quiet links
/// plus Quit. Auto-closes when it loses focus (click away) or on Escape. Built entirely in code so
/// it drops into each app's existing FluentTheme with no AXAML / resource wiring. Shared by the
/// Launcher and Gateway trays so they look and behave identically.
/// </summary>
public sealed class TrayFlyout : Window
{
    /// <summary>
    /// Close the flyout when it loses focus (click-away), like OneDrive. Default true; set false only
    /// for previews/tests that need the panel to stay on screen without holding foreground focus.
    /// </summary>
    public bool AutoCloseOnDeactivate { get; set; } = true;

    /// <summary>Panel width. Roomy enough for two-column secondary buttons and wrapped paths/URLs.</summary>
    private const double PanelWidth = 420;

    // Palette (dark, matches the apps' existing dark surfaces).
    private static readonly Color Surface = Color.Parse("#1F2024");
    private static readonly Color SurfaceInset = Color.Parse("#26282D");
    private static readonly Color Hairline = Color.Parse("#2E3138");
    private static readonly Color TextStrong = Color.Parse("#F0F1F3");
    private static readonly Color TextMid = Color.Parse("#CBD1DA");
    private static readonly Color TextDim = Color.Parse("#8B939E");
    private static readonly Color BtnBg = Color.Parse("#2A2C31");
    private static readonly Color BtnHover = Color.Parse("#34373D");
    private static readonly Color BtnPressed = Color.Parse("#3C4046");

    public TrayFlyout(TrayFlyoutModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        SizeToContent = SizeToContent.Height;
        Width = PanelWidth;
        RequestedThemeVariant = ThemeVariant.Dark;
        Opacity = 0; // revealed in Opened, once positioned, to avoid a position flash

        AddStyles(model.Accent);
        Content = BuildRoot(model);

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) => { if (AutoCloseOnDeactivate) Close(); };   // click away => close, like OneDrive
        Opened += (_, _) => { PositionBottomRight(); Opacity = 1; };
    }

    // ---- layout -----------------------------------------------------------

    private Control BuildRoot(TrayFlyoutModel m)
    {
        var stack = new StackPanel { Spacing = 0 };

        stack.Children.Add(BuildHeader(m));

        // Optional secondary status line under the header.
        if (!string.IsNullOrWhiteSpace(m.StatusDetail))
            stack.Children.Add(new TextBlock
            {
                Text = m.StatusDetail,
                FontSize = 12,
                Foreground = Brush(TextDim),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            });

        // Status rows
        if (m.Rows.Count > 0)
            stack.Children.Add(RowGrid(m.Rows, labelSize: 12.5, valueSize: 12.5, valueColor: TextMid,
                margin: new Thickness(0, 14, 0, 0), rowSpacing: 7));

        // Actions: primaries full-width on top, then secondaries two per row.
        if (m.Actions.Count > 0)
        {
            stack.Children.Add(Separator());
            var actions = new StackPanel { Spacing = 8 };
            foreach (var a in m.Actions.Where(a => a.Primary))
                actions.Children.Add(PrimaryButton(a, m.Accent));
            foreach (var pair in Pairs(m.Actions.Where(a => !a.Primary).ToList()))
                actions.Children.Add(SecondaryRow(pair));
            stack.Children.Add(actions);
        }

        // Details: the quieter diagnostic block (build, paths, versions).
        if (m.DetailRows.Count > 0)
        {
            stack.Children.Add(Separator());
            var details = new StackPanel { Spacing = 8 };
            details.Children.Add(new TextBlock
            {
                Text = m.DetailsTitle.ToUpperInvariant(),
                FontSize = 10.5,
                FontWeight = FontWeight.SemiBold,
                LetterSpacing = 0.8,
                Foreground = Brush(TextDim),
            });
            details.Children.Add(RowGrid(m.DetailRows, labelSize: 11.5, valueSize: 11.5, valueColor: TextDim,
                margin: default, rowSpacing: 5));
            stack.Children.Add(details);
        }

        // Toggle
        if (m.Toggle is { } t)
        {
            stack.Children.Add(Separator());
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var label = new TextBlock { Text = t.Label, FontSize = 12.5, Foreground = Brush(TextMid), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 0);
            var sw = new ToggleSwitch { IsChecked = t.IsOn, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            sw.IsCheckedChanged += (_, _) => t.OnChanged(sw.IsChecked == true);
            Grid.SetColumn(sw, 1);
            g.Children.Add(label); g.Children.Add(sw);
            stack.Children.Add(g);
        }

        // Footer: quiet links on the left, Quit on the right.
        if (m.FooterLinks.Count > 0 || m.OnQuit is not null)
        {
            stack.Children.Add(Separator());
            var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var link in m.FooterLinks)
            {
                var b = new Button { Content = link.Text };
                b.Classes.Add("flyoutLink");
                b.Click += (_, _) => { Close(); link.OnClick(); };
                links.Children.Add(b);
            }
            Grid.SetColumn(links, 0);
            footer.Children.Add(links);

            if (m.OnQuit is { } quit)
            {
                var q = new Button { Content = "Quit" };
                q.Classes.Add("flyoutQuit");
                q.Click += (_, _) => { Close(); quit(); };
                Grid.SetColumn(q, 1);
                footer.Children.Add(q);
            }
            stack.Children.Add(footer);
        }

        return new Border
        {
            Background = Brush(Surface),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush(Hairline),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20),
            Margin = new Thickness(14), // room for the shadow inside the transparent window
            BoxShadow = BoxShadows.Parse("0 10 30 0 #90000000"),
            Child = stack,
        };
    }

    /// <summary>Header: icon + app name + status title, with the coloured status pill on the right.</summary>
    private Control BuildHeader(TrayFlyoutModel m)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        if (m.Icon is not null)
        {
            var img = new Image { Source = m.Icon, Width = 30, Height = 30, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(img, 0);
            header.Children.Add(img);
        }

        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = m.AppName, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Brush(TextStrong) });
        titles.Children.Add(new TextBlock { Text = m.StatusTitle, FontSize = 12, Foreground = Brush(TextDim), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        var pill = StatusPill(m);
        Grid.SetColumn(pill, 2);
        header.Children.Add(pill);
        return header;
    }

    /// <summary>The status chip: coloured dot + one word (or a bare dot when no pill text is set).</summary>
    private Control StatusPill(TrayFlyoutModel m)
    {
        var color = StatusColor(m.Status);
        var dot = new Ellipse { Width = 8, Height = 8, Fill = Brush(color), VerticalAlignment = VerticalAlignment.Center };
        if (string.IsNullOrWhiteSpace(m.StatusPillText))
            return dot;

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(dot);
        content.Children.Add(new TextBlock
        {
            Text = m.StatusPillText,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(color),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            Background = Brush(SurfaceInset),
            BorderBrush = Brush(Hairline),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
    }

    private static Control RowGrid(IReadOnlyList<StatusRow> rows, double labelSize, double valueSize,
        Color valueColor, Thickness margin, double rowSpacing)
    {
        var panel = new StackPanel { Spacing = rowSpacing, Margin = margin };
        foreach (var r in rows)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
            var l = new TextBlock { Text = r.Label, FontSize = labelSize, Foreground = Brush(TextDim) };
            var v = new TextBlock { Text = r.Value, FontSize = valueSize, Foreground = Brush(valueColor), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(l, 0); Grid.SetColumn(v, 1);
            g.Children.Add(l); g.Children.Add(v);
            panel.Children.Add(g);
        }
        return panel;
    }

    private Button PrimaryButton(FlyoutAction a, Color accent)
    {
        var b = new Button { Content = a.Text };
        b.Classes.Add("flyoutPrimary");
        b.Background = Brush(accent);
        b.Click += (_, _) => { Close(); a.OnClick(); };
        return b;
    }

    /// <summary>One row of secondary buttons: two side by side, or one full-width leftover.</summary>
    private Control SecondaryRow(IReadOnlyList<FlyoutAction> pair)
    {
        if (pair.Count == 1)
            return SecondaryButton(pair[0]);

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        var left = SecondaryButton(pair[0]);
        var right = SecondaryButton(pair[1]);
        Grid.SetColumn(left, 0); Grid.SetColumn(right, 2);
        g.Children.Add(left); g.Children.Add(right);
        return g;
    }

    private Button SecondaryButton(FlyoutAction a)
    {
        var b = new Button { Content = a.Text };
        b.Classes.Add("flyoutBtn");
        b.Click += (_, _) => { Close(); a.OnClick(); };
        return b;
    }

    private static IEnumerable<IReadOnlyList<FlyoutAction>> Pairs(IReadOnlyList<FlyoutAction> actions)
    {
        for (int i = 0; i < actions.Count; i += 2)
            yield return i + 1 < actions.Count
                ? new[] { actions[i], actions[i + 1] }
                : new[] { actions[i] };
    }

    private static Border Separator() => new()
    {
        Height = 1,
        Background = Brush(Hairline),
        Margin = new Thickness(0, 14, 0, 14),
    };

    // ---- styling ----------------------------------------------------------

    private void AddStyles(Color accent)
    {
        // Secondary buttons: filled, centered, comfortable hit target.
        Styles.Add(BtnStyle(null, BtnBg));
        Styles.Add(BtnStyle(":pointerover", BtnHover));
        Styles.Add(BtnStyle(":pressed", BtnPressed));

        // Primary: full-width accent button; dims slightly on hover/press (keeps its inline background).
        var primary = new Style(x => x.OfType<Button>().Class("flyoutPrimary"));
        primary.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brushes.White));
        primary.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)));
        primary.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)));
        primary.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(14, 11)));
        primary.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, 13.0));
        primary.Setters.Add(new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold));
        primary.Setters.Add(new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        primary.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        Styles.Add(primary);
        Styles.Add(OpacityStyle("flyoutPrimary", ":pointerover", 0.90));
        Styles.Add(OpacityStyle("flyoutPrimary", ":pressed", 0.82));

        // Footer links: borderless quiet text buttons that brighten on hover.
        var link = new Style(x => x.OfType<Button>().Class("flyoutLink"));
        link.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        link.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(TextDim)));
        link.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)));
        link.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, 11.5));
        link.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 5)));
        Styles.Add(link);
        var linkHover = new Style(x => x.OfType<Button>().Class("flyoutLink").Class(":pointerover"));
        linkHover.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        linkHover.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(TextStrong)));
        Styles.Add(linkHover);

        // Quit: same quiet link, but reddens on hover.
        var quit = new Style(x => x.OfType<Button>().Class("flyoutQuit"));
        quit.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        quit.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(TextDim)));
        quit.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)));
        quit.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, 11.5));
        quit.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 5)));
        Styles.Add(quit);
        var quitHover = new Style(x => x.OfType<Button>().Class("flyoutQuit").Class(":pointerover"));
        quitHover.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        quitHover.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(Color.Parse("#E06C75"))));
        Styles.Add(quitHover);
    }

    private static Style BtnStyle(string? pseudo, Color bg)
    {
        var style = pseudo is null
            ? new Style(x => x.OfType<Button>().Class("flyoutBtn"))
            : new Style(x => x.OfType<Button>().Class("flyoutBtn").Class(pseudo));
        style.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brush(bg)));
        style.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(TextStrong)));
        style.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)));
        style.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 10)));
        style.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, 12.5));
        style.Setters.Add(new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        return style;
    }

    private static Style OpacityStyle(string cls, string pseudo, double opacity)
    {
        var style = new Style(x => x.OfType<Button>().Class(cls).Class(pseudo));
        style.Setters.Add(new Setter(Visual.OpacityProperty, opacity));
        return style;
    }

    // ---- helpers ----------------------------------------------------------

    private static SolidColorBrush Brush(Color c) => new(c);

    private static Color StatusColor(StatusLevel s) => s switch
    {
        StatusLevel.Ok => Color.Parse("#3FB950"),
        StatusLevel.Warn => Color.Parse("#D29922"),
        StatusLevel.Error => Color.Parse("#F85149"),
        _ => Color.Parse("#3FB950"),
    };

    private void PositionBottomRight()
    {
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;
        var wa = screen.WorkingArea;            // physical px, excludes the taskbar
        var scale = screen.Scaling;
        var wPx = (int)Math.Ceiling(ClientSize.Width * scale);
        var hPx = (int)Math.Ceiling(ClientSize.Height * scale);
        var margin = (int)Math.Round(8 * scale);
        Position = new PixelPoint(
            wa.X + wa.Width - wPx - margin,
            wa.Y + wa.Height - hPx - margin);
    }
}
