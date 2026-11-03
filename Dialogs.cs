using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ets2Gps;

/// <summary>Dark theme brushes and small control factories shared by the window and the dialogs.</summary>
public static class Ui
{
    public static readonly Brush Panel = Frozen(Color.FromRgb(28, 36, 49));
    public static readonly Brush PanelDark = Frozen(Color.FromArgb(235, 14, 18, 24));
    public static readonly Brush Btn = Frozen(Color.FromRgb(30, 40, 54));
    public static readonly Brush BtnHover = Frozen(Color.FromRgb(46, 60, 80));
    public static readonly Brush Accent = Frozen(Color.FromRgb(25, 118, 210));
    public static readonly Brush Green = Frozen(Color.FromRgb(18, 92, 58));
    public static readonly Brush Dim = Frozen(Color.FromRgb(170, 179, 192));
    public static readonly Brush Line = Frozen(Color.FromArgb(40, 255, 255, 255));

    /// <summary>Current scale of the main window; dialogs use it so they grow and shrink with the window.</summary>
    public static double Scale = 1.0;

    public static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static Button Button(string text, double w, double h, Brush? bg = null, double size = 15)
    {
        var tpl = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        tpl.VisualTree = border;
        var b = new Button { Content = text, Width = w, Height = h, Background = bg ?? Btn, Foreground = Brushes.White, FontSize = size, BorderThickness = new Thickness(0), Template = tpl, Cursor = Cursors.Hand, Focusable = false };
        var normal = b.Background;
        b.MouseEnter += (_, _) => { if (ReferenceEquals(b.Background, normal)) b.Background = BtnHover; };
        b.MouseLeave += (_, _) => { if (ReferenceEquals(b.Background, BtnHover)) b.Background = normal; };
        return b;
    }

    public static TextBlock Text(string t, double size = 15, Brush? fg = null, FontWeight? w = null) =>
        new() { Text = t, FontSize = size, Foreground = fg ?? Brushes.White, FontWeight = w ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
}

/// <summary>Modal dialogs in the app's dark style. Callbacks run after the dialog has closed.</summary>
public static class Dlg
{
    static Window Make(Window owner, string title, double width = 640)
    {
        return new Window
        {
            Title = title, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = width, SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(300, owner.ActualHeight - 60), ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = owner.Topmost,
            Background = Ui.Panel, Foreground = Brushes.White, WindowStyle = WindowStyle.ToolWindow,
        };
    }

    static StackPanel ButtonRow(Window w, IEnumerable<(string Label, Action? Fn, bool Primary)> buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 10, 14, 14) };
        foreach (var (label, fn, primary) in buttons)
        {
            var b = Ui.Button(label, double.NaN, 36, primary ? Ui.Accent : Ui.Btn, 15);
            b.MinWidth = 84; b.Padding = new Thickness(14, 0, 14, 0); b.Margin = new Thickness(8, 0, 0, 0);
            b.Click += (_, _) => { w.Tag = fn; w.Close(); };
            row.Children.Add(b);
        }
        return row;
    }

    static void Run(Window w)
    {
        double s = Math.Clamp(Ui.Scale, 0.75, 1.0);
        if (Math.Abs(s - 1.0) > 0.01 && w.Content is FrameworkElement fe)
        {
            fe.LayoutTransform = new ScaleTransform(s, s);
            w.Width *= s;
            if (w.SizeToContent == SizeToContent.Manual) w.Height *= s;
            w.MaxHeight = SystemParameters.WorkArea.Height;
        }
        var outer = open; open = w;
        try { w.ShowDialog(); } finally { open = outer; }
        (w.Tag as Action)?.Invoke();
    }

    static Window? open;

    /// <summary>True while one of these dialogs is on screen.</summary>
    public static bool IsOpen => open != null;

    /// <summary>Closes the dialog on screen (for voice control), as if a button doing <paramref name="then"/> had been pressed.</summary>
    public static bool CloseOpen(Action? then = null)
    {
        if (open == null) return false;
        open.Tag = then; open.Close();
        return true;
    }

    /// <summary>A list to pick from; picking closes the dialog and calls onPick(index).</summary>
    public static void Pick(Window owner, string title, IList<string> items, Action<int> onPick, string? message = null, params (string Label, Action Fn)[] extra)
    {
        var w = Make(owner, title);
        var root = new DockPanel { LastChildFill = true };
        var buttons = new List<(string, Action?, bool)>();
        foreach (var (l, f) in extra) buttons.Add((l, f, false));
        buttons.Add(("Close", null, false));
        var row = ButtonRow(w, buttons);
        DockPanel.SetDock(row, Dock.Bottom);
        root.Children.Add(row);
        if (message != null) { var t = Ui.Text(message, 15, Ui.Dim); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(18, 4, 18, 6); DockPanel.SetDock(t, Dock.Top); root.Children.Add(t); }
        if (items.Count > 0)
        {
            var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, FontSize = 15, MaxHeight = 420, Margin = new Thickness(8, 0, 8, 0) };
            for (int i = 0; i < items.Count; i++) list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = items[i], TextWrapping = TextWrapping.Wrap, Padding = new Thickness(8, 8, 8, 8) }, Foreground = Brushes.White });
            list.MouseDoubleClick += (_, _) => { }; // single click picks (below)
            list.SelectionChanged += (_, _) =>
            {
                int idx = list.SelectedIndex;
                if (idx < 0) return;
                w.Tag = new Action(() => onPick(idx));
                w.Close();
            };
            root.Children.Add(list);
        }
        w.Content = root;
        Run(w);
    }

    public static void Message(Window owner, string title, string text, params (string Label, Action? Fn, bool Primary)[] buttons)
    {
        var w = Make(owner, title, 460);
        var root = new DockPanel();
        var row = ButtonRow(w, buttons.Length > 0 ? buttons : new (string, Action?, bool)[] { ("OK", null, true) });
        DockPanel.SetDock(row, Dock.Bottom); root.Children.Add(row);
        var t = Ui.Text(text, 15); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(18, 10, 18, 6);
        root.Children.Add(t);
        w.Content = root;
        Run(w);
    }

    public static void Input(Window owner, string title, string value, Action<string> ok, string okLabel = "OK", string? prompt = null)
    {
        var w = Make(owner, title);
        var root = new DockPanel();
        var tb = new TextBox { Text = value, FontSize = 16, Margin = new Thickness(18, 4, 18, 4), Padding = new Thickness(6), Background = new SolidColorBrush(Color.FromRgb(16, 21, 28)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = Brushes.Gray };
        var row = ButtonRow(w, new (string, Action?, bool)[] { ("Cancel", null, false), (okLabel, () => ok(tb.Text), true) });
        DockPanel.SetDock(row, Dock.Bottom); root.Children.Add(row);
        if (prompt != null) { var p = Ui.Text(prompt, 14, Ui.Dim); p.Margin = new Thickness(18, 10, 18, 0); DockPanel.SetDock(p, Dock.Top); root.Children.Add(p); }
        root.Children.Add(tb);
        w.Content = root;
        w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { w.Tag = new Action(() => ok(tb.Text)); w.Close(); } };
        Run(w);
    }

    /// <summary>City search with a live filter; picking calls onPick(city).</summary>
    public static void Search(Window owner, string title, IReadOnlyList<City> all, Action<City> onPick)
    {
        var w = Make(owner, title, 480);
        w.Height = Math.Min(600, owner.ActualHeight - 60); w.SizeToContent = SizeToContent.Manual;
        var root = new DockPanel();
        var row = ButtonRow(w, new (string, Action?, bool)[] { ("Cancel", null, false) });
        DockPanel.SetDock(row, Dock.Bottom); root.Children.Add(row);
        var tb = new TextBox { FontSize = 16, Margin = new Thickness(18, 8, 18, 6), Padding = new Thickness(6), Background = new SolidColorBrush(Color.FromRgb(16, 21, 28)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = Brushes.Gray };
        DockPanel.SetDock(tb, Dock.Top); root.Children.Add(tb);
        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, FontSize = 15, Margin = new Thickness(8, 0, 8, 0) };
        root.Children.Add(list);
        List<City> shown = all.ToList();
        void Refresh()
        {
            string key = City.Normalize(tb.Text.Trim());
            shown = key.Length == 0 ? all.ToList() : all.Where(c => c.Key.Contains(key)).OrderBy(c => c.Key.StartsWith(key) ? 0 : 1).ToList();
            list.Items.Clear();
            foreach (var c in shown.Take(200)) list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = c.Name, Padding = new Thickness(8, 6, 8, 6) }, Foreground = Brushes.White });
        }
        tb.TextChanged += (_, _) => Refresh();
        void Choose(int idx) { if (idx < 0 || idx >= shown.Count) return; var c = shown[idx]; w.Tag = new Action(() => onPick(c)); w.Close(); }
        list.SelectionChanged += (_, _) => Choose(list.SelectedIndex);
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Choose(0);
            else if (e.Key == Key.Down && list.Items.Count > 0) { list.Focus(); list.SelectedIndex = -1; }
        };
        w.Content = root;
        w.Loaded += (_, _) => { Refresh(); tb.Focus(); };
        Run(w);
    }

    public static void Custom(Window owner, string title, UIElement content, IEnumerable<(string Label, Action? Fn, bool Primary)> buttons, double width = 560)
    {
        var w = Make(owner, title, width);
        var root = new DockPanel();
        var row = ButtonRow(w, buttons);
        DockPanel.SetDock(row, Dock.Bottom); root.Children.Add(row);
        root.Children.Add(content);
        w.Content = root;
        Run(w);
    }
}

/// <summary>Altitude profile of the trip so far (x axis in game kilometres).</summary>
public sealed class ElevationChart : FrameworkElement
{
    readonly ElevationLog log; readonly float distScale;
    public ElevationChart(ElevationLog log, float distScale) { this.log = log; this.distScale = distScale; Height = 240; MinWidth = 480; }

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 21, 28)), null, new Rect(0, 0, W, H));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText T(string s, Brush b) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, b, dpi);
        var white = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
        int n = log.Alt.Count;
        double l = 50, r = W - 12, t = 12, b = H - 24;
        if (n < 2) { var ft = T("Drive a little to build the profile", white); dc.DrawText(ft, new Point((W - ft.Width) / 2, H / 2)); return; }
        float lo = log.Alt.Min(), hi = log.Alt.Max();
        if (hi - lo < 10) { float m = (hi + lo) / 2; lo = m - 5; hi = m + 5; }
        float d0 = log.Dist[0], d1 = Math.Max(log.Dist[n - 1], d0 + 1);
        double Px(int i) => l + (log.Dist[i] - d0) / (d1 - d0) * (r - l);
        double Py(int i) => b - (log.Alt[i] - lo) / (hi - lo) * (b - t);
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1);
        for (int k = 0; k <= 3; k++)
        {
            double y = t + (b - t) * k / 3;
            dc.DrawLine(grid, new Point(l, y), new Point(r, y));
            var ft = T(Units.AltText((float)(hi - (hi - lo) * k / 3)), white);
            dc.DrawText(ft, new Point(l - 6 - ft.Width, y - ft.Height / 2));
        }
        var area = new StreamGeometry();
        using (var c = area.Open())
        {
            c.BeginFigure(new Point(Px(0), b), true, true);
            for (int i = 0; i < n; i++) c.LineTo(new Point(Px(i), Py(i)), true, false);
            c.LineTo(new Point(Px(n - 1), b), true, false);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(70, 90, 190, 255)), null, area);
        var line = new StreamGeometry();
        using (var c = line.Open())
        {
            c.BeginFigure(new Point(Px(0), Py(0)), false, false);
            for (int i = 1; i < n; i++) c.LineTo(new Point(Px(i), Py(i)), true, true);
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(90, 190, 255)), 2.2) { LineJoin = PenLineJoin.Round }, line);
        dc.DrawText(T("0", white), new Point(l, H - 18));
        var km = T(Units.LongText((float)((d1 - d0) * distScale / 1000)), white);
        dc.DrawText(km, new Point(r - km.Width, H - 18));
    }
}
