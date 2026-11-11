using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using SD = System.Drawing;

public class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public int Minutes { get; set; } = 60;
    public string Icon { get; set; }           // picked glyph; null = guess from the title
    public bool Done { get; set; }
    public bool Notified { get; set; }
    public DateTime End() => Start.AddMinutes(Minutes);
    public bool IsRunning(DateTime now) => !Done && Start <= now && now < End();
}

// user preferences, settings.json next to the exe
public class Settings
{
    public int Volume { get; set; } = 2;      // 0 off, 1 quiet, 2 normal, 3 loud
    public bool Popups { get; set; } = true;  // notification cards
    public int Corner { get; set; }           // floating timer: 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right
    public int Monitor { get; set; }          // floating timer screen: 0 leftmost, 1 main, 2 rightmost
    public int DefaultMin { get; set; } = 60; // length of a new task
    public string Accent { get; set; } = "#E2B44E"; // app colour, see Glass.Palette
    public bool? Autostart { get; set; }      // null = never chosen; mirrors the HKCU Run entry

    static readonly string FilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
    public static readonly Settings Cur = Load();
    static Settings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); } // unreadable prefs just fall back to defaults; nothing worth keeping is lost
    }
    public void Save()
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, true);
    }
}

// a saved sequence of tasks that can be laid onto any day in one click
public class RoutineStep
{
    public string Title { get; set; } = "";
    public int Minutes { get; set; } = 30;
    public string Icon { get; set; }
}
public class Routine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<RoutineStep> Steps { get; set; } = new();
    public int TotalMinutes => Steps.Sum(s => s.Minutes);

    static readonly string FilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "routines.json");
    public static List<Routine> All = Load();
    static List<Routine> Load()
    {
        if (!File.Exists(FilePath)) return new();
        try { return JsonSerializer.Deserialize<List<Routine>>(File.ReadAllText(FilePath)) ?? new(); }
        catch
        {
            File.Move(FilePath, FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); // keep it, don't overwrite with nothing
            return new();
        }
    }
    public static void SaveAll()
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, true);
    }

    // back-to-back tasks from `start`
    public List<TaskItem> Expand(DateTime start)
    {
        var list = new List<TaskItem>();
        foreach (var s in Steps)
        {
            list.Add(new TaskItem { Title = s.Title, Minutes = s.Minutes, Icon = s.Icon, Start = start });
            start = start.AddMinutes(s.Minutes);
        }
        return list;
    }

    // today: now rounded up to 5 min, but never on top of tasks already planned; other days: after the last task, else 09:00
    public static DateTime StartFor(DateTime day, DateTime now, DateTime? lastEnd)
    {
        if (day.Date == now.Date)
        {
            var t = now.Date.AddMinutes(Math.Ceiling(now.TimeOfDay.TotalMinutes / 5) * 5);
            return lastEnd > t ? lastEnd.Value : t;
        }
        return lastEnd ?? day.Date.AddHours(9);
    }
}

// start with Windows: per-user Run entry, no admin rights needed
static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "TaskScheduler";
    public static string Command => $"\"{Environment.ProcessPath}\" --tray";
    public static void Set(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
            if (on) k.SetValue(Name, Command); else k.DeleteValue(Name, false);
        }
        catch { } // locked-down registry: the toggle just won't stick, nothing else breaks
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-GB"); // English dates whatever the Windows region is
        if (args.Contains("--selftest")) { SelfTest(); return; }

        using var mutex = new Mutex(true, "TaskTimeline_single", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "TaskTimeline_show");
        using var newSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "TaskTimeline_new");
        bool wantNew = args.Contains("--new");
        if (!first)
        {
            if (!args.Contains("--tray")) (wantNew ? newSignal : showSignal).Set(); // already running: hand the request over
            return; // a second autostart entry must not pop the window open at logon
        }
        // started by a logon entry before autostart was ever set here (old scheduled-task setup): adopt it as "on"
        if (Settings.Cur.Autostart == null && args.Contains("--tray")) { Settings.Cur.Autostart = true; Settings.Cur.Save(); }
        if (Settings.Cur.Autostart == true) Autostart.Set(true); // rewrites the path, so a moved exe keeps starting

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var win = new MainWindow();
        new Thread(() => { while (true) { showSignal.WaitOne(); app.Dispatcher.Invoke(win.ShowMe); } }) { IsBackground = true }.Start();
        new Thread(() => { while (true) { newSignal.WaitOne(); app.Dispatcher.Invoke(win.ShowNew); } }) { IsBackground = true }.Start();
        if (wantNew) win.ShowNew();
        else if (!args.Contains("--tray")) win.Show();
        app.Run();
    }

    static void SelfTest()
    {
        var t = new TaskItem { Start = new DateTime(2026, 11, 5, 12, 0, 0), Minutes = 120 };
        Trace.Assert(!t.IsRunning(t.Start.AddSeconds(-1)));
        Trace.Assert(t.IsRunning(t.Start));
        Trace.Assert(t.IsRunning(t.Start.AddMinutes(119)));
        Trace.Assert(!t.IsRunning(t.End()));
        t.Done = true;
        Trace.Assert(!t.IsRunning(t.Start));
        Trace.Assert(MainWindow.Dur(30) == "30 min" && MainWindow.Dur(60) == "1 hr" && MainWindow.Dur(120) == "2 hrs" && MainWindow.Dur(90) == "1 hr 30 min" && MainWindow.LeftText(26) == "26m" && MainWindow.LeftText(107) == "1h 47m");
        double h30 = MainWindow.PillHeight(30);
        Trace.Assert(MainWindow.PillHeight(60) == 2 * h30 && MainWindow.PillHeight(300) == 10 * h30 && MainWindow.PillHeight(15) == h30 / 2);
        Trace.Assert(MainWindow.PillHeight(5) == MainWindow.PillHeight(15) && MainWindow.PillHeight(10) == MainWindow.PillHeight(15)); // floor
        Trace.Assert(MainWindow.Glyph("Long break") == "\uE708" && MainWindow.Glyph("Job applications") == "\uE821" && MainWindow.Glyph("Games middleware") == "\uE7FC");
        Trace.Assert(MainWindow.Glyph("Final year project") == "\uE70F" && MainWindow.Glyph("Вайб код") == "\uE943" && MainWindow.Glyph("Something else") == "\uE9D5");
        var wav = Toast.Chime();
        Trace.Assert(wav.Length > 44 && wav[0] == 'R' && wav[8] == 'W' && BitConverter.ToInt32(wav, 4) == wav.Length - 8 && BitConverter.ToInt32(wav, 40) == wav.Length - 44);
        static int Peak(byte[] w) { int m = 0; for (int i = 44; i + 1 < w.Length; i += 2) m = Math.Max(m, Math.Abs((int)BitConverter.ToInt16(w, i))); return m; }
        Trace.Assert(Peak(Toast.Chime(0.14)) > 1.9 * Peak(Toast.Chime(0.07))); // louder setting = proportionally bigger samples
        var st = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { Volume = 0, Corner = 3, DefaultMin = 30 }));
        Trace.Assert(st.Volume == 0 && st.Corner == 3 && st.DefaultMin == 30 && st.Popups);
        Glass.SetAccent("#FF0000");
        Trace.Assert(Glass.AccentColor == Colors.Red && Glass.Accent.GradientStops[0].Color.G > 0 && Glass.Accent.GradientStops[1].Color.R < 255 && Glass.AccentDim.Color.A == 0x99);
        Glass.SetAccent("not a colour");
        Trace.Assert(Glass.AccentColor == (Color)ColorConverter.ConvertFromString(Glass.Palette[0].hex));
        var morning = new Routine { Name = "Morning", Steps = { new() { Title = "Reading", Minutes = 20 }, new() { Title = "Mail", Minutes = 10 }, new() { Title = "Writing", Minutes = 20 } } };
        var day = new DateTime(2026, 11, 11);
        var ex = morning.Expand(day.AddHours(18));
        Trace.Assert(ex.Count == 3 && ex[1].Start == day.AddHours(18).AddMinutes(20) && ex[2].End() == day.AddHours(18).AddMinutes(50) && morning.TotalMinutes == 50);
        Trace.Assert(Routine.StartFor(day, day.AddHours(18).AddMinutes(2), null) == day.AddHours(18).AddMinutes(5));            // round up to 5 min
        Trace.Assert(Routine.StartFor(day, day.AddHours(18), day.AddHours(19)) == day.AddHours(19));                           // after planned tasks
        Trace.Assert(Routine.StartFor(day.AddDays(1), day.AddHours(18), null) == day.AddDays(1).AddHours(9));                   // other day, empty
        Trace.Assert(OverlayWindow.Clock(TimeSpan.FromSeconds(65)) == "01:05" && OverlayWindow.Clock(new TimeSpan(1, 2, 3)) == "1:02:03");
        Console.WriteLine("selftest ok");
    }
}

// "liquid glass" look: translucent white fills over the acrylic window backdrop, with a bright top rim that fades down
static class Glass
{
    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static Brush V(params string[] stops) // vertical gradient, stops evenly spaced
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        for (int i = 0; i < stops.Length; i++) g.GradientStops.Add(new GradientStop(C(stops[i]), i / (double)(stops.Length - 1)));
        g.Freeze(); return g;
    }
    public static readonly Brush Fill = V("#2EFFFFFF", "#14FFFFFF"),
                                 Rim = V("#73FFFFFF", "#14FFFFFF", "#2EFFFFFF"),
                                 AccentRim = V("#99FFFFFF", "#1AFFFFFF", "#33FFFFFF"),
                                 Frost = V("#B01A1A20", "#C0101014"),       // full-window panels (blur underneath does the rest)
                                 Solid = V("#E62A2A30", "#F0161619");       // separate popup windows: no backdrop to blur
    public static T Card<T>(T b, double radius) where T : Border
    { b.Background = Fill; b.BorderBrush = Rim; b.BorderThickness = new Thickness(1); b.CornerRadius = new CornerRadius(radius); return b; }
    // accent brushes are live (not frozen): SetAccent recolours everything that uses them in place
    public static readonly SolidColorBrush AccentBrush = new(), AccentDim = new(), AccentTrail = new(), AccentGlow = new();
    public static readonly LinearGradientBrush Accent = new(new GradientStopCollection { new(Colors.Gold, 0), new(Colors.Gold, 1) }, new Point(0, 0), new Point(0, 1));
    public static event Action AccentChanged;
    public static readonly (string name, string hex)[] Palette =
    {
        ("Gold", "#E2B44E"), ("Red", "#E5484D"), ("Orange", "#F0883E"), ("Green", "#3FB950"),
        ("Teal", "#2EC4B6"), ("Blue", "#4C8DF6"), ("Purple", "#A371F7"), ("Pink", "#F06BA8"),
    };
    static Glass() => SetAccent(Settings.Cur.Accent);
    public static Color AccentColor => AccentBrush.Color;
    public static void SetAccent(string hex)
    {
        Color c;
        try { c = C(hex); } catch { c = C(Palette[0].hex); } // bad value in settings.json: fall back to gold
        static Color Mix(Color a, Color b, double t, byte alpha) => Color.FromArgb(alpha, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        AccentBrush.Color = c;
        Accent.GradientStops[0].Color = Mix(c, Colors.White, 0.18, 0xF2); // lit top
        Accent.GradientStops[1].Color = Mix(c, Colors.Black, 0.28, 0xE6); // deeper bottom
        AccentDim.Color = Mix(c, c, 0, 0x99);
        AccentTrail.Color = Mix(c, c, 0, 0x38);
        AccentGlow.Color = Mix(c, c, 0, 0x40);
        AccentChanged?.Invoke();
    }
    public static T Button<T>(T b) where T : Border { b.Background = Accent; b.BorderBrush = AccentRim; b.BorderThickness = new Thickness(1); return b; }
}

public class MainWindow : Window
{
    static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
    static readonly Brush Card = Glass.Frost, Field = Glass.Fill, Sub = B("#A0A0A8"),
                          Soft = B("#CCFFFFFF"), White = Brushes.White;
    static Brush Accent => Glass.AccentBrush;
    static readonly Color PillC = (Color)ColorConverter.ConvertFromString("#26FFFFFF");
    static Color AccentDimC => Glass.AccentDim.Color;
    static readonly int[] DurChoices = { 5, 10, 15, 30, 45, 60, 75, 90, 120, 150, 180, 240, 300, 360 };
    const double SlotH = 40, ChipW = 66;

    readonly string dataPath = System.IO.Path.Combine(AppContext.BaseDirectory, "tasks.json");
    List<TaskItem> tasks = new();
    DateTime selected = DateTime.Today;
    bool exiting;

    readonly TextBlock header = new() { FontSize = 30, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, ToolTip = "Open calendar" };
    readonly Border calOverlay = new() { Background = Card, Visibility = Visibility.Collapsed };
    readonly StackPanel calHost = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
    Action<DateTime> calPick;
    DateTime calSel;
    bool calOpen;
    readonly SolidColorBrush titleBg = new(Colors.Transparent);
    readonly System.Windows.Media.Effects.BlurEffect behind = new() { Radius = 0 }; // frosts the main view while a panel covers it
    DateTime lastDay;           // day last rendered, to animate only on day change
    bool sheetOpen;
    readonly System.Windows.Controls.Primitives.UniformGrid week = new() { Columns = 7, Margin = new Thickness(0, 14, 0, 0) };
    readonly StackPanel timeline = new() { Margin = new Thickness(16, 20, 16, 90) };
    readonly WF.NotifyIcon tray;
    readonly WF.ToolStripMenuItem overlayItem;

    // ---- add/edit sheet state ----
    readonly Border sheet = new() { Background = Card, Visibility = Visibility.Collapsed };
    TaskItem editing;           // null = creating a new task
    DateTime sDate;
    TimeSpan sStart;
    int sMin;
    bool syncing;               // true while code (not the user) writes into sheet text boxes
    readonly TextBox sTitle = new() { Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0), FontSize = 26, FontWeight = FontWeights.Bold, Padding = new Thickness(0, 4, 0, 6) };
    readonly TextBlock sHint = new() { Text = "Task name", Foreground = B("#80FFFFFF"), FontSize = 26, FontWeight = FontWeights.Bold, Padding = new Thickness(2, 5, 0, 6), IsHitTestVisible = false };
    readonly Border sUnderline = new() { BorderBrush = B("#80FFFFFF"), BorderThickness = new Thickness(0, 0, 0, 1.5) };
    readonly TextBlock sRange =new() { Foreground = Soft, FontSize = 15 }, sLetter = new() { Foreground = Brushes.White, FontSize = 34, FontFamily = IconFont, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock sDateText = new() { Foreground = White, FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
    readonly Border sToday = QuickChip("Today"), sTomorrow = QuickChip("Tomorrow");
    readonly TextBox sMinBox = SmallBox();
    readonly StackPanel slots = new(), suggestions = new();
    readonly ScrollViewer slotsScroll = new() { Height = 5 * SlotH, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
    readonly StackPanel chips = new() { Orientation = Orientation.Horizontal };
    // one red pill per wheel that slides to the picked item (instead of each item toggling its own background)
    // red pill fixed in the middle of each wheel; the list scrolls underneath and the pill's text flips once the next item passes the centre
    readonly TextBlock slotPillText = PillText(), chipPillText = PillText();
    static TextBlock PillText() => new() { Foreground = Brushes.White, FontSize = 19, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    static Border Pill(TextBlock text) => Glass.Button(new Border
    {
        CornerRadius = new CornerRadius(25), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Child = text,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.45 }
    });
    readonly ScrollViewer chipsScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly TextBlock sSaveText = new() { Foreground = White, FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Border sDelete = new() { Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent, Child = new TextBlock { Text = "Delete task", Foreground = B("#E5484D"), FontSize = 15 } };

    public MainWindow()
    {
        Title = "Task Scheduler"; Width = 440; Height = 800; ResizeMode = ResizeMode.CanMinimize; Background = Brushes.Transparent; // acrylic shows through
        Load();

        // header: ‹ 5 November 2026 ›
        var headRow = new DockPanel();
        var prev = Clickable(new TextBlock { Text = "‹", FontSize = 30, Foreground = Accent, Margin = new Thickness(0, 0, 12, 0) }, () => { selected = selected.AddDays(-7); Render(); });
        var next = Clickable(new TextBlock { Text = "›", FontSize = 30, Foreground = Accent, Margin = new Thickness(12, 0, 0, 0) }, () => { selected = selected.AddDays(7); Render(); });
        DockPanel.SetDock(prev, Dock.Left); DockPanel.SetDock(next, Dock.Right);
        headRow.Children.Add(prev); headRow.Children.Add(next); headRow.Children.Add(header);
        Clickable(header, () => OpenCal(selected, d => { selected = d; Render(); }));
        var top = new StackPanel { Margin = new Thickness(20, 8, 20, 16) };
        top.Children.Add(headRow); top.Children.Add(week);

        var card = Glass.Card(new Border
        {
            Margin = new Thickness(10, 0, 10, 10),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = timeline }
        }, 30);
        var main = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        main.Children.Add(top); main.Children.Add(card);

        var fab = Clickable(Glass.Button(new Border
        {
            Width = 62, Height = 62, CornerRadius = new CornerRadius(31), ToolTip = "New task (Ctrl+N)",
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 22, 22),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5 },
            Child = new TextBlock { Text = "+", FontSize = 34, Foreground = White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -4, 0, 0) }
        }), () => OpenSheet(null));
        // routines: smaller glass button left of "+"
        var rtBtn = Clickable(Glass.Card(new Border
        {
            Width = 50, Height = 50, ToolTip = "Routines: saved task sequences",
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 96, 28),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45 },
            Child = new TextBlock { Text = "\uE8EE", FontFamily = IconFont, FontSize = 20, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        }, 25), OpenRoutines);

        BuildSheet();
        var body = new Grid();
        BuildCalOverlay();
        body.Children.Add(new Grid { Effect = behind, Children = { main, fab, rtBtn } }); body.Children.Add(sheet); body.Children.Add(calOverlay); body.Children.Add(setOverlay); body.Children.Add(iconOverlay); body.Children.Add(rtOverlay);
        var root = new DockPanel();
        var bar = TitleBar();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar); root.Children.Add(body);
        // dark tint keeps text readable over any wallpaper; two soft colour glows give the glass something to catch
        RadialGradientBrush Glow(string c, double x, double y) => new(((SolidColorBrush)B(c)).Color, Colors.Transparent) { Center = new Point(x, y), GradientOrigin = new Point(x, y), RadiusX = 0.75, RadiusY = 0.55 };
        var accentGlow = Glow("#40E2B44E", 0.1, 0.05);
        Glass.AccentChanged += () => { accentGlow.GradientStops[0].Color = Glass.AccentGlow.Color; Render(); RefreshIcons(); };
        accentGlow.GradientStops[0].Color = Glass.AccentGlow.Color;
        Content = new Grid { Children = {
            new Border { Background = B("#A60A0A0E") },
            new Border { Background = accentGlow },
            new Border { Background = Glow("#334A5BD6", 0.95, 0.9) },
            root } };
        // own title bar instead of the Windows one; glass frame over the whole window = acrylic backdrop behind everything
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 40, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0) });
        PreviewKeyDown += (s, e) =>
        {
            if (iconOpen) { if (e.Key == Key.Escape) { CloseIcons(); e.Handled = true; } return; }
            if (rtOpen) { if (e.Key == Key.Escape) { if (rtEditing) ShowRoutineList(); else CloseRoutines(); e.Handled = true; } return; }
            if (setOpen) { if (e.Key == Key.Escape) { CloseSettings(); e.Handled = true; } return; }
            if (calOpen) { if (e.Key == Key.Escape) { CloseCal(); e.Handled = true; } return; }
            if (!sheetOpen) { if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) OpenSheet(null); return; }
            if (e.Key == Key.Escape) { CloseSheet(); e.Handled = true; }
            else if (e.Key == Key.Enter) { SaveSheet(); e.Handled = true; }
        };

        // tray
        tray = new WF.NotifyIcon { Icon = SD.Icon.ExtractAssociatedIcon(Environment.ProcessPath), Visible = true, Text = "Task Scheduler" };
        tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowMe(); };
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Open", null, (s, e) => ShowMe());
        menu.Items.Add("New task", null, (s, e) => ShowNew());
        overlayItem = new WF.ToolStripMenuItem("Floating timer", null, (s, e) => ToggleOverlay());
        menu.Items.Add(overlayItem);
        // one-click routines straight from the tray; list rebuilt each time the menu opens
        var rtMenu = new WF.ToolStripMenuItem("Start routine");
        rtMenu.DropDownItems.Add("(none)");
        menu.Opening += (s, e) =>
        {
            rtMenu.DropDownItems.Clear();
            foreach (var r in Routine.All) { var rr = r; rtMenu.DropDownItems.Add($"{r.Name}  ({Dur(r.TotalMinutes)})", null, (s2, e2) => { selected = DateTime.Today; StartRoutine(rr); ShowMe(); }); }
            rtMenu.DropDownItems.Add(Routine.All.Count == 0 ? "Create a routine…" : "Edit routines…", null, (s2, e2) => { ShowMe(); OpenRoutines(); });
        };
        menu.Items.Add(rtMenu);
        menu.Items.Add("Settings", null, (s, e) => { ShowMe(); OpenSettings(); });
        menu.Items.Add("Exit", null, (s, e) => Exit());
        tray.ContextMenuStrip = menu;
        RefreshIcons();
        Closing += (s, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
        SourceInitialized += (s, e) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1, round = 2, border = 0x00302C2C; // COLORREF is BGR
            DwmSetWindowAttribute(h, 20, ref dark, 4);    // dark frame
            DwmSetWindowAttribute(h, 33, ref round, 4);   // Win11 rounded corners
            DwmSetWindowAttribute(h, 34, ref border, 4);  // subtle border
            int acrylic = 3;
            DwmSetWindowAttribute(h, 38, ref acrylic, 4); // Win11 22H2+ system backdrop: acrylic (blurred desktop behind the window)
            System.Windows.Interop.HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;
            Native.SetWindowLong(h, -16, Native.GetWindowLong(h, -16) & ~0x00080000); // no WS_SYSMENU: else DWM paints ghost caption buttons on the glass
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (s, e) => Tick();
        timer.Start();
        Tick();
        if (File.Exists(OverlayFlag)) SetOverlay(true);
    }

    public void ShowMe() { Show(); WindowState = WindowState.Normal; Activate(); }

    UIElement TitleBar()
    {
        var bar = new DockPanel { Height = 40, Background = titleBg };
        Border Btn(string glyph, string hover, Action a, string tip)
        {
            var b = new Border
            {
                Width = 46, Background = Brushes.Transparent, ToolTip = tip, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Foreground = White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var hb = (SolidColorBrush)B(hover); var tb = new SolidColorBrush(Colors.Transparent);
            b.Background = tb;
            b.MouseEnter += (s, e) => tb.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(hb.Color, TimeSpan.FromMilliseconds(120)));
            b.MouseLeave += (s, e) => tb.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Colors.Transparent, TimeSpan.FromMilliseconds(180)));
            b.MouseLeftButtonUp += (s, e) => a();
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            DockPanel.SetDock(b, Dock.Right);
            return b;
        }
        bar.Children.Add(Btn("", "#E5484D", Close, "Hide to tray"));
        bar.Children.Add(Btn("", "#33FFFFFF", () => WindowState = WindowState.Minimized, "Minimize"));
        var ovBtn = Btn("", "#33FFFFFF", ToggleOverlay, "Floating timer");
        ovIcon = (TextBlock)ovBtn.Child; ovIcon.FontSize = 14;
        bar.Children.Add(ovBtn);
        var gear = Btn("", "#33FFFFFF", () => { if (setOpen) CloseSettings(); else OpenSettings(); }, "Settings");
        ((TextBlock)gear.Child).FontSize = 14;
        bar.Children.Add(gear);
        titleIcon = new Image { Width = 18, Height = 18, Margin = new Thickness(14, 0, 8, 0) }; // source set by RefreshIcons
        DockPanel.SetDock(titleIcon, Dock.Left);
        bar.Children.Add(titleIcon);
        bar.Children.Add(new TextBlock { Text = "Task Scheduler", Foreground = Soft, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return bar;
    }
    public void ShowNew() { ShowMe(); OpenSheet(null); }

    // the app icon (same drawing as make-icon.ps1) in the current accent: title bar, taskbar and tray follow the colour setting
    Image titleIcon;
    IntPtr trayHicon;
    public static SD.Bitmap DrawAppIcon(int s, Color accent)
    {
        var bmp = new SD.Bitmap(s, s);
        using var g = SD.Graphics.FromImage(bmp);
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        float k = s / 256f;
        SD.Color Mix(Color to, double t) => SD.Color.FromArgb((int)(accent.R + (to.R - accent.R) * t), (int)(accent.G + (to.G - accent.G) * t), (int)(accent.B + (to.B - accent.B) * t));
        var rect = new SD.RectangleF(4 * k, 4 * k, 248 * k, 248 * k);
        using (var grad = new SD.Drawing2D.LinearGradientBrush(rect, Mix(Colors.White, 0.3), Mix(Colors.Black, 0.25), 45f)) g.FillEllipse(grad, rect);
        using var pen = new SD.Pen(SD.Color.White, Math.Max(1.6f, 20 * k)) { StartCap = SD.Drawing2D.LineCap.Round, EndCap = SD.Drawing2D.LineCap.Round, LineJoin = SD.Drawing2D.LineJoin.Round };
        SD.PointF P(float x, float y) => new(x * k, y * k);
        g.DrawLines(pen, new[] { P(62, 104), P(84, 126), P(120, 82) });
        g.DrawLine(pen, P(142, 92), P(196, 92));
        g.DrawLine(pen, P(142, 124), P(196, 124));
        g.DrawLine(pen, P(62, 166), P(196, 166));
        if (s >= 32) g.DrawLine(pen, P(62, 198), P(196, 198));
        return bmp;
    }

    void RefreshIcons()
    {
        var c = Glass.AccentColor;
        using (var small = DrawAppIcon(32, c))
        {
            var h = small.GetHicon();
            tray.Icon = SD.Icon.FromHandle(h);
            if (trayHicon != IntPtr.Zero) Native.DestroyIcon(trayHicon); // FromHandle doesn't own it
            trayHicon = h;
        }
        using var big = DrawAppIcon(64, c);
        var ms = new MemoryStream();
        big.Save(ms, SD.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(ms, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        titleIcon.Source = frame;
        Icon = frame; // taskbar button
    }

    void Exit() { exiting = true; tray.Visible = false; tray.Dispose(); Application.Current.Shutdown(); }

    // ---- floating timer (top-left of the leftmost monitor, above all windows) ----
    TextBlock ovIcon;
    OverlayWindow overlay;
    string OverlayFlag => System.IO.Path.Combine(AppContext.BaseDirectory, "overlay.on"); // exists = overlay was on, restore at start

    void ToggleOverlay() => SetOverlay(overlay == null);

    void SetOverlay(bool on)
    {
        if (on && overlay == null)
        {
            overlay = new OverlayWindow(() => tasks, ShowMe);
            overlay.Show();
            File.WriteAllText(OverlayFlag, "");
        }
        else if (!on && overlay != null)
        {
            overlay.Close(); overlay = null;
            File.Delete(OverlayFlag);
        }
        ovIcon.Foreground = overlay != null ? Accent : White;
        overlayItem.Checked = overlay != null;
    }

    void Tick()
    {
        var now = DateTime.Now;
        var due = tasks.Where(t => !t.Notified && t.IsRunning(now)).OrderBy(t => t.Start).ToList();
        foreach (var t in due) t.Notified = true;
        if (due.Count > 0)
        {
            if (Settings.Cur.Popups) foreach (var t in due.TakeLast(3)) Toast.Pop(t, ShowMe); // more than 3 at once would just cover the screen
            Toast.PlayChime();
            Save();
        }
        tray.Text = Trunc("Task Scheduler" + (tasks.FirstOrDefault(x => x.IsRunning(now)) is TaskItem cur ? " — " + cur.Title : ""), 63);
        Render();
    }

    void Render()
    {
        var now = DateTime.Now;
        header.Inlines.Clear();
        header.Inlines.Add(new Run(selected.ToString("d MMMM ")) { Foreground = White });
        header.Inlines.Add(new Run(selected.Year.ToString()) { Foreground = Accent });

        week.Children.Clear();
        var monday = selected.AddDays(-(((int)selected.DayOfWeek + 6) % 7));
        for (int i = 0; i < 7; i++)
        {
            var d = monday.AddDays(i);
            bool isSel = d == selected, isToday = d == DateTime.Today;
            var col = new StackPanel { Background = Brushes.Transparent };
            col.Children.Add(new TextBlock { Text = d.ToString("ddd"), Foreground = Sub, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center });
            col.Children.Add(new Border
            {
                Width = 40, Height = 40, Margin = new Thickness(0, 6, 0, 4), CornerRadius = new CornerRadius(20),
                Background = isSel ? White : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = d.Day.ToString(), FontSize = 20, FontWeight = FontWeights.SemiBold,
                    Foreground = isSel ? Brushes.Black : isToday ? Accent : White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            });
            col.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = tasks.Any(t => t.Start.Date == d) ? Accent : Brushes.Transparent });
            week.Children.Add(Clickable(col, () => { selected = d; Render(); }));
        }

        timeline.Children.Clear();
        var day = tasks.Where(t => t.Start.Date == selected).OrderBy(t => t.Start).ToList();
        if (day.Count == 0)
            timeline.Children.Add(new TextBlock { Text = "No tasks — press +", Foreground = Sub, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) });
        for (int i = 0; i < day.Count; i++) timeline.Children.Add(Row(day[i], now, i == 0, i == day.Count - 1));
        if (selected != lastDay) { lastDay = selected; SlideIn(timeline, 18); } // not on the 15 s refresh
    }

    UIElement Row(TaskItem t, DateTime now, bool first, bool last)
    {
        const double gap = 10;
        bool running = t.IsRunning(now);
        double pillH = PillHeight(t.Minutes);
        // the gap lives inside the row (not as a margin) so the connector line can run through it
        var row = new Grid { Height = pillH + gap, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        foreach (var w in new[] { 52.0, 72, -1, 44 })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });

        Put(row, new TextBlock { Text = t.Start.ToString("HH:mm"), Foreground = running ? White : Sub, FontSize = 13, FontWeight = running ? FontWeights.Bold : FontWeights.Normal, Margin = new Thickness(0, 6, 0, 0) }, 0);

        // thin line linking this pill to its neighbours, drawn behind the pill
        // only in the gap: pills are translucent now, a line behind them would show through
        if (!last)
            Put(row, new Rectangle { Width = 6, Height = gap + 2, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -1), Fill = new SolidColorBrush(PillC) }, 1);

        // pill fills with red as the running task progresses
        double f = running ? (now - t.Start).TotalMinutes / t.Minutes : 0;
        var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fill.GradientStops.Add(new GradientStop(AccentDimC, 0)); fill.GradientStops.Add(new GradientStop(AccentDimC, f));
        fill.GradientStops.Add(new GradientStop(PillC, f)); fill.GradientStops.Add(new GradientStop(PillC, 1));
        Put(row, new Border
        {
            Width = 64, CornerRadius = new CornerRadius(32), Background = fill, BorderBrush = Glass.Rim, BorderThickness = new Thickness(1), Opacity = t.Done ? 0.45 : 1, Margin = new Thickness(0, 0, 0, gap),
            Child = new TextBlock { Text = IconOf(t), FontFamily = IconFont, Foreground = running ? White : Accent, FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        }, 1);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, gap) };
        info.Children.Add(new TextBlock
        {
            Foreground = running ? Accent : Sub, FontSize = 14,
            Text = running ? $"{LeftText((int)Math.Ceiling((t.End() - now).TotalMinutes))} remaining" : $"{t.Start:HH:mm}–{t.End():HH:mm} ({Dur(t.Minutes)})"
        });
        var title = new TextBlock { Text = t.Title, Foreground = t.Done ? Sub : White, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        if (t.Done) title.TextDecorations = TextDecorations.Strikethrough;
        info.Children.Add(title);
        Put(row, info, 2);

        Put(row, Clickable(new Ellipse
        {
            Width = 30, Height = 30, Stroke = Accent, StrokeThickness = 2.5, Fill = t.Done ? Accent : Brushes.Transparent, Margin = new Thickness(0, 0, 0, gap),
            ToolTip = t.Done ? "Mark as not done" : "Mark as done"
        }, () => { t.Done = !t.Done; Save(); Render(); }), 3);

        row.MouseLeftButtonUp += (s, e) => OpenSheet(t);
        Hover(row);
        row.MouseRightButtonUp += (s, e) =>
        {
            e.Handled = true;
            ShowMenu(
                ("", "Edit", White, () => OpenSheet(t)),
                (t.Done ? "" : "", t.Done ? "Mark as not done" : "Mark as done", White, () => { t.Done = !t.Done; Save(); Render(); }),
                ("", "Push back 15 min", White, () => { t.Start = t.Start.AddMinutes(15); t.Notified = false; Save(); Tick(); }),
                ("", "Delete", B("#FF6B6B"), () => { tasks.Remove(t); Save(); Render(); }));
        };
        return row;
    }

    // glass right-click menu at the mouse: icon + label rows, fades and grows in, closes on any outside click
    static void ShowMenu(params (string glyph, string text, Brush color, Action act)[] items)
    {
        var popup = new System.Windows.Controls.Primitives.Popup { AllowsTransparency = true, StaysOpen = false, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.None };
        var list = new StackPanel();
        for (int i = 0; i < items.Length; i++)
        {
            var (glyph, text, color, act) = items[i];
            if (i == items.Length - 1 && items.Length > 1) list.Children.Add(new Border { Height = 1, Background = B("#1FFFFFFF"), Margin = new Thickness(10, 4, 10, 4) }); // keep Delete apart
            var hover = new SolidColorBrush(Colors.Transparent);
            var row = new Border
            {
                Background = hover, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 18, 9), Cursor = Cursors.Hand,
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = {
                    new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 15, Foreground = color, Width = 22, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, FontSize = 14, Foreground = color, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } } }
            };
            row.MouseEnter += (s, e) => hover.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0x26, 255, 255, 255), TimeSpan.FromMilliseconds(100)));
            row.MouseLeave += (s, e) => hover.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Colors.Transparent, TimeSpan.FromMilliseconds(150)));
            row.MouseLeftButtonUp += (s, e) => { e.Handled = true; popup.IsOpen = false; act(); };
            list.Children.Add(row);
        }
        var scale = new ScaleTransform(0.92, 0.92);
        var card = new Border
        {
            Background = Glass.Solid, BorderBrush = Glass.Rim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(5), Margin = new Thickness(8, 6, 14, 16),
            MinWidth = 200, RenderTransform = scale, RenderTransformOrigin = new Point(0, 0), Opacity = 0, Child = list,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.5 }
        };
        popup.Child = card;
        popup.IsOpen = true;
        Anim(card, OpacityProperty, 0, 1, 140);
        Anim(scale, ScaleTransform.ScaleXProperty, 0.92, 1, 160);
        Anim(scale, ScaleTransform.ScaleYProperty, 0.92, 1, 160);
    }

    // ---- add / edit sheet ----
    // shown when Add is pressed with no name: bright hint, thick underline, a shake and a dark chip under the field
    readonly Border sWarn = new()
    {
        Background = B("#8C000000"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 4, 10, 5), Margin = new Thickness(0, 8, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed,
        Child = new TextBlock { Text = "⚠  Type a name to add the task", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold }
    };
    void NameWarning(bool on)
    {
        sHint.Text = on ? "Name required" : "Task name";
        sHint.Foreground = on ? White : B("#80FFFFFF");
        sUnderline.BorderBrush = on ? White : B("#80FFFFFF");
        sUnderline.BorderThickness = new Thickness(0, 0, 0, on ? 3 : 1.5);
        if (!on) { sWarn.Visibility = Visibility.Collapsed; return; }
        sWarn.Visibility = Visibility.Visible;
        Anim(sWarn, OpacityProperty, 0, 1, 200);
        var tt = sUnderline.RenderTransform as TranslateTransform ?? new TranslateTransform();
        sUnderline.RenderTransform = tt;
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        double[] xs = { -12, 12, -9, 9, -5, 5, 0 };
        for (int i = 0; i < xs.Length; i++) shake.KeyFrames.Add(new LinearDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60 * (i + 1)))));
        tt.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    void BuildSheet()
    {
        // red header: ✕, big circle with letter, time range + title
        var close = Clickable(new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Glass.Fill, BorderBrush = Glass.Rim, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Close (Esc)",
            Child = new TextBlock { Text = "✕", FontSize = 20, Foreground = White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        }, CloseSheet);
        var circle = Clickable(new Border { Width = 84, Height = 84, CornerRadius = new CornerRadius(42), Background = B("#40000000"), BorderBrush = Glass.AccentRim, BorderThickness = new Thickness(3), Child = sLetter, ToolTip = "Choose icon" }, OpenIcons);
        var titleGrid = new Grid();
        titleGrid.Children.Add(sHint); titleGrid.Children.Add(sTitle);
        sUnderline.Child = titleGrid; // own underline: the TextBox template turns its border blue on focus
        var headInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        headInfo.Children.Add(sRange); headInfo.Children.Add(sUnderline); headInfo.Children.Add(sWarn);
        var headRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headRow.ColumnDefinitions.Add(new ColumnDefinition());
        Put(headRow, circle, 0); Put(headRow, headInfo, 1);
        var head = new StackPanel { Margin = new Thickness(18, 12, 18, 16) };
        head.Children.Add(close); head.Children.Add(headRow);
        sTitle.TextChanged += (s, e) =>
        {
            sHint.Visibility = sTitle.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            sLetter.Text = sIcon ?? Glyph(sTitle.Text);
            NameWarning(false);
            if (!syncing) RenderSuggestions();
        };

        // date row: 📅 Thu 5 Nov 2026   [Today] [Tomorrow]; click the row → month calendar
        var dateRow = new DockPanel();
        var quick = new Grid { Children = { quickHl, new StackPanel { Orientation = Orientation.Horizontal, Children = { sToday, sTomorrow } } } };
        Clickable(sToday, () => PickDate(DateTime.Today));
        Clickable(sTomorrow, () => PickDate(DateTime.Today.AddDays(1)));
        DockPanel.SetDock(quick, Dock.Right);
        var cal = new TextBlock { Text = "📅", FontSize = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Accent };
        DockPanel.SetDock(cal, Dock.Left);
        dateRow.Children.Add(quick); dateRow.Children.Add(cal); dateRow.Children.Add(sDateText);
        var dateCard = Clickable(Glass.Card(new Border { Padding = new Thickness(22, 10, 10, 10), Child = dateRow, ToolTip = "Open calendar" }, 30),
                                 () => OpenCal(sDate, PickDate));

        // time wheel: 15-min slots; drag with LMB, mouse wheel or click — the centre slot is the pick
        for (int i = 0; i < 96; i++)
            slots.Children.Add(new Border
            {
                Height = SlotH - 4, Margin = new Thickness(70, 2, 70, 2), CornerRadius = new CornerRadius(20), Background = Brushes.Transparent,
                Child = new TextBlock { FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
        slotsScroll.Content = new Border { Padding = new Thickness(0, 2 * SlotH, 0, 2 * SlotH), Child = slots }; // padding lets first/last slot reach the centre
        MakeWheel(slotsScroll, false, SlotH, 96, () => (int)(sStart.TotalMinutes / 15), (i, scroll) => { sStart = TimeSpan.FromMinutes(i * 15); RenderSheet(scroll); });
        var slotPill = Pill(slotPillText); slotPill.Height = SlotH + 6; slotPill.Width = 230; slotPill.CornerRadius = new CornerRadius((SlotH + 6) / 2);
        var slotsCard = Glass.Card(new Border { Padding = new Thickness(0, 8, 0, 8), Child = new Grid { Children = { slotsScroll, slotPill } } }, 30);

        // duration: horizontal wheel, same controls as the time wheel; exact minutes box next to the title
        foreach (var m in DurChoices)
            chips.Children.Add(new Border
            {
                Width = ChipW, Height = 50, CornerRadius = new CornerRadius(25), Background = Brushes.Transparent,
                Child = new TextBlock { Text = m < 60 ? m.ToString() : m % 60 == 0 ? $"{m / 60}h" : $"{m / 60}h{m % 60}", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
        var chipsPad = new Border { Child = chips };
        chipsScroll.Content = chipsPad;
        chipsScroll.SizeChanged += (s, e) => chipsPad.Padding = new Thickness(Math.Max(0, (chipsScroll.ActualWidth - ChipW) / 2), 0, Math.Max(0, (chipsScroll.ActualWidth - ChipW) / 2), 0); // lets first/last chip reach the centre
        MakeWheel(chipsScroll, true, ChipW, DurChoices.Length, NearestDur, (i, scroll) => { sMin = DurChoices[i]; RenderSheet(scroll); });
        var chipPill = Pill(chipPillText); chipPill.Height = 52; chipPill.Width = ChipW + 22; chipPill.CornerRadius = new CornerRadius(26);
        // dim red trail from the left edge up to the pill: "this much time"
        var trail = new Border { Background = Glass.AccentTrail, CornerRadius = new CornerRadius(26, 0, 0, 26), Height = 52, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, Margin = new Thickness(6, 0, 0, 0) };
        var chipsGrid = new Grid { Children = { trail, chipsScroll, chipPill } };
        chipsGrid.SizeChanged += (s, e) => trail.Width = Math.Max(0, chipsGrid.ActualWidth / 2 - 6);
        var chipsCard = Glass.Card(new Border { Padding = new Thickness(0, 6, 0, 6), Child = chipsGrid }, 30);
        sMinBox.TextChanged += (s, e) => { if (!syncing && int.TryParse(sMinBox.Text.Trim(), out var m) && m > 0 && m <= 24 * 60) { sMin = m; RenderSheet(); } };
        // highlights ride the scroll offset itself, so they never lag a drag or fling; jumps animate via SmoothScroll

        var save = Clickable(Glass.Button(new Border { Height = 58, CornerRadius = new CornerRadius(29), Margin = new Thickness(0, 8, 0, 0), Child = sSaveText }), SaveSheet);
        Clickable(sDelete, () => { tasks.Remove(editing); Save(); CloseSheet(); Render(); });

        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        body.Children.Add(dateCard);
        body.Children.Add(suggestions);
        body.Children.Add(Section("Time", null, null));
        body.Children.Add(slotsCard);
        body.Children.Add(Section("Duration", sMinBox, "minutes"));
        body.Children.Add(chipsCard);

        var bottom = new StackPanel { Margin = new Thickness(20, 0, 20, 18) };
        bottom.Children.Add(save); bottom.Children.Add(sDelete);
        var headBorder = Glass.Button(new Border { Child = head, CornerRadius = new CornerRadius(30), Margin = new Thickness(10, 4, 10, 0) });
        var dock = new DockPanel();
        DockPanel.SetDock(headBorder, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(headBorder); dock.Children.Add(bottom);
        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = body }); // hidden = wheel still scrolls, no light Win32 bar
        sheet.Child = dock;
    }

    static UIElement Section(string title, TextBox box, string tip)
    {
        var d = new DockPanel { Margin = new Thickness(12, 20, 4, 10) };
        if (box != null)
        {
            box.ToolTip = "Exact value, " + tip;
            // TextBox can't round its own corners, so the rounded glass frame is a Border around it
            var frame = Glass.Card(new Border { Child = box, VerticalAlignment = VerticalAlignment.Center }, 18);
            DockPanel.SetDock(frame, Dock.Right);
            d.Children.Add(frame);
        }
        d.Children.Add(new TextBlock { Text = title, Foreground = White, FontSize = 24, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        return d;
    }

    void OpenSheet(TaskItem t)
    {
        editing = t;
        if (t != null) { sDate = t.Start.Date; sStart = t.Start.TimeOfDay; sMin = t.Minutes; }
        else
        {
            sDate = selected; sMin = Settings.Cur.DefaultMin;
            var last = tasks.Where(x => x.Start.Date == selected).OrderBy(x => x.End()).LastOrDefault();
            var now = DateTime.Now;
            if (last != null && last.End().Date == selected) sStart = last.End().TimeOfDay; // continue after the day's last task
            else if (selected == DateTime.Today) sStart = TimeSpan.FromMinutes(Math.Min(95, Math.Ceiling(now.TimeOfDay.TotalMinutes / 15)) * 15);
            else sStart = TimeSpan.FromHours(9);
        }
        syncing = true; sTitle.Text = t?.Title ?? ""; syncing = false;
        sIcon = t?.Icon;
        NameWarning(false);
        sLetter.Text = sIcon ?? Glyph(sTitle.Text); // TextChanged doesn't fire when "" → ""
        sSaveText.Text = t == null ? "Add" : "Save";
        sDelete.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        sheetOpen = true;
        sheet.Visibility = Visibility.Visible;
        SlideIn(sheet, 60, 300);
        Anim(behind, System.Windows.Media.Effects.BlurEffect.RadiusProperty, null, 24, 300);
        RenderSheet();
        RenderSuggestions();
        Dispatcher.BeginInvoke(() => { sTitle.Focus(); sTitle.CaretIndex = sTitle.Text.Length; }, DispatcherPriority.Input);
    }

    void CloseSheet()
    {
        sheetOpen = false;
        Anim(sheet, OpacityProperty, null, 0, 200, () => { if (!sheetOpen) sheet.Visibility = Visibility.Collapsed; });
        if (sheet.RenderTransform is TranslateTransform tt) Anim(tt, TranslateTransform.YProperty, null, 60, 200);
        if (!calOpen) Anim(behind, System.Windows.Media.Effects.BlurEffect.RadiusProperty, null, 0, 250);
    }

    void SaveSheet()
    {
        var title = sTitle.Text.Trim();
        if (title.Length == 0) { NameWarning(true); sTitle.Focus(); return; }
        var start = sDate + sStart;
        var t = editing ?? new TaskItem();
        if (editing == null) tasks.Add(t);
        if (t.Start != start || t.Minutes != sMin) t.Notified = false; // re-notify if moved
        t.Title = title; t.Start = start; t.Minutes = sMin; t.Icon = sIcon;
        Save();
        selected = sDate;
        CloseSheet();
        Tick();
    }

    // scrollSlots=false while the user types in a box, so the list doesn't jump under their cursor
    void RenderSheet(bool scrollSlots = true)
    {
        var end = sDate + sStart + TimeSpan.FromMinutes(sMin);
        sRange.Text = $"{sStart:hh\\:mm}–{end:HH:mm} ({Dur(sMin)})";
        sDateText.Text = sDate.ToString("ddd d MMM yyyy");
        StyleQuick(sToday, sDate == DateTime.Today);
        StyleQuick(sTomorrow, sDate == DateTime.Today.AddDays(1));
        MoveQuickHl();

        int sel = (int)(sStart.TotalMinutes / 15);
        for (int i = 0; i < slots.Children.Count; i++)
        {
            var b = (Border)slots.Children[i];
            var tb = (TextBlock)b.Child;
            int dist = Math.Abs(i - sel);
            tb.Text = TimeSpan.FromMinutes(i * 15).ToString("hh\\:mm");
            tb.Foreground = Sub;
            tb.Opacity = Math.Max(0.35, 1 - dist * 0.22); // wheel-like fade
        }
        slotPillText.Text = $"{sStart:hh\\:mm}–{end:HH:mm}";
        if (scrollSlots) SmoothScroll(slotsScroll, sel * SlotH, false);

        int di = NearestDur();
        for (int i = 0; i < chips.Children.Count; i++)
        {
            var b = (Border)chips.Children[i];
            var tb = (TextBlock)b.Child;
            tb.Foreground = Sub;
            tb.Opacity = Math.Max(0.35, 1 - Math.Abs(i - di) * 0.18);
        }
        chipPillText.Text = sMin < 60 ? $"{sMin}m" : sMin % 60 == 0 ? $"{sMin / 60}h" : $"{sMin / 60}h {sMin % 60}m"; // shows typed custom minutes too
        if (scrollSlots) SmoothScroll(chipsScroll, di * ChipW, true);

        syncing = true;
        if (!sMinBox.IsKeyboardFocused) sMinBox.Text = sMin.ToString();
        syncing = false;
    }

    void PickDate(DateTime d) { sDate = d; RenderSheet(); }

    static void StyleQuick(Border b, bool on)
    {
        b.Background = Brushes.Transparent; // the red is quickHl sliding underneath
        ((TextBlock)b.Child).Foreground = on ? White : Sub;
    }

    // one red pill under Today/Tomorrow that slides to the active chip, fades out when another date is picked
    readonly Border quickHl = Glass.Button(new Border { CornerRadius = new CornerRadius(20), HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0, RenderTransform = new TranslateTransform() });
    void MoveQuickHl() => Dispatcher.BeginInvoke(() => // after layout, so chip positions are known
    {
        var on = sDate == DateTime.Today ? sToday : sDate == DateTime.Today.AddDays(1) ? sTomorrow : null;
        Anim(quickHl, OpacityProperty, null, on == null ? 0 : 1, 200);
        if (on == null || quickHl.Parent is not Grid g) return;
        double x = on.TranslatePoint(new Point(0, 0), g).X;
        if (quickHl.Width is double.NaN) { quickHl.Width = on.ActualWidth; ((TranslateTransform)quickHl.RenderTransform).X = x; return; } // first time: no slide from 0
        Anim((TranslateTransform)quickHl.RenderTransform, TranslateTransform.XProperty, null, x, 260);
        Anim(quickHl, WidthProperty, null, on.ActualWidth, 260);
    }, DispatcherPriority.Loaded);

    static Border QuickChip(string text) => new()
    {
        CornerRadius = new CornerRadius(20), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(4, 0, 0, 0),
        Child = new TextBlock { Text = text, FontSize = 15 }
    };

    // ---- full-window calendar ----
    void BuildCalOverlay()
    {
        var close = Clickable(new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Field, BorderBrush = Glass.Rim, BorderThickness = new Thickness(1), ToolTip = "Close (Esc)",
            Child = new TextBlock { Text = "✕", FontSize = 20, Foreground = White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        }, CloseCal);
        var top = new DockPanel { Margin = new Thickness(20, 14, 20, 0) };
        DockPanel.SetDock(close, Dock.Left);
        top.Children.Add(close);
        top.Children.Add(new TextBlock { Text = "Pick a date", Foreground = White, FontSize = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) });
        var today = Clickable(new Border
        {
            Height = 58, CornerRadius = new CornerRadius(29), Background = Glass.Accent, BorderBrush = Glass.AccentRim, BorderThickness = new Thickness(1), Margin = new Thickness(20, 0, 20, 18),
            Child = new TextBlock { Text = "Today", Foreground = White, FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        }, () => PickCal(DateTime.Today));
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(today, Dock.Bottom);
        dock.Children.Add(top); dock.Children.Add(today); dock.Children.Add(calHost);
        calOverlay.Child = dock;
    }

    void OpenCal(DateTime sel, Action<DateTime> pick)
    {
        calPick = pick; calSel = sel; calOpen = true;
        BuildCal(new DateTime(sel.Year, sel.Month, 1), 0);
        calOverlay.Visibility = Visibility.Visible;
        SlideIn(calOverlay, 40, 280);
        Anim(behind, System.Windows.Media.Effects.BlurEffect.RadiusProperty, null, 24, 280);
        BlurSheet();
    }

    void CloseCal()
    {
        calOpen = false;
        Anim(calOverlay, OpacityProperty, null, 0, 200, () => { if (!calOpen) calOverlay.Visibility = Visibility.Collapsed; });
        if (calOverlay.RenderTransform is TranslateTransform tt) Anim(tt, TranslateTransform.YProperty, null, 40, 200);
        if (!sheetOpen) Anim(behind, System.Windows.Media.Effects.BlurEffect.RadiusProperty, null, 0, 250);
        BlurSheet();
    }

    void PickCal(DateTime d) { CloseCal(); calPick(d); }

    // month grid: ‹ November 2026 ›, weekday names, days (Mon-first); dir = slide direction when paging months
    void BuildCal(DateTime month, int dir)
    {
        calHost.Children.Clear();
        var head = new DockPanel { Margin = new Thickness(4, 0, 4, 18) };
        var prev = Clickable(new TextBlock { Text = "‹", FontSize = 34, Foreground = Accent, Padding = new Thickness(14, 0, 14, 4) }, () => BuildCal(month.AddMonths(-1), -1));
        var next = Clickable(new TextBlock { Text = "›", FontSize = 34, Foreground = Accent, Padding = new Thickness(14, 0, 14, 4) }, () => BuildCal(month.AddMonths(1), 1));
        DockPanel.SetDock(prev, Dock.Left); DockPanel.SetDock(next, Dock.Right);
        head.Children.Add(prev); head.Children.Add(next);
        head.Children.Add(new TextBlock { Text = month.ToString("MMMM yyyy"), Foreground = White, FontSize = 24, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        calHost.Children.Add(head);

        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7 };
        var monday = new DateTime(2024, 1, 1); // a Monday, just for day names
        for (int i = 0; i < 7; i++)
            grid.Children.Add(new TextBlock { Text = monday.AddDays(i).ToString("ddd"), Foreground = Sub, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) });
        int offset = ((int)month.DayOfWeek + 6) % 7;
        for (int i = 0; i < offset; i++) grid.Children.Add(new Border());
        for (var d = month; d.Month == month.Month; d = d.AddDays(1))
        {
            var day = d;
            bool sel = day == calSel, today = day == DateTime.Today;
            var cell = new StackPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 3, 0, 3) };
            cell.Children.Add(new Border
            {
                Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = sel ? Accent : Brushes.Transparent,
                Child = new TextBlock { Text = day.Day.ToString(), FontSize = 18, FontWeight = sel || today ? FontWeights.Bold : FontWeights.Normal, Foreground = sel ? White : today ? Accent : White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            cell.Children.Add(new Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 3, 0, 0), Fill = !sel && tasks.Any(t => t.Start.Date == day) ? Accent : Brushes.Transparent });
            grid.Children.Add(Clickable(cell, () => PickCal(day)));
        }
        calHost.Children.Add(grid);
        if (dir != 0) { var tt = new TranslateTransform(); grid.RenderTransform = tt; Anim(tt, TranslateTransform.XProperty, dir * 50, 0, 260); Anim(grid, OpacityProperty, 0, 1, 260); }
    }

    // ---- icon picker: tap the sheet's circle; "Auto" goes back to guessing from the title ----
    string sIcon;               // sheet's chosen glyph, null = auto
    readonly Border iconOverlay = new() { Background = Card, Visibility = Visibility.Collapsed };
    bool iconOpen;
    static readonly string[] IconChoices =
    {
        "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "",
    };

    void OpenIcons()
    {
        iconOpen = true;
        var close = Clickable(new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Field, BorderBrush = Glass.Rim, BorderThickness = new Thickness(1), ToolTip = "Close (Esc)",
            Child = new TextBlock { Text = "✕", FontSize = 20, Foreground = White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        }, CloseIcons);
        var top = new DockPanel { Margin = new Thickness(20, 14, 20, 10) };
        DockPanel.SetDock(close, Dock.Left);
        top.Children.Add(close);
        top.Children.Add(new TextBlock { Text = "Choose icon", Foreground = White, FontSize = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) });

        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 5, Margin = new Thickness(6) };
        Border Cell(string glyph, string label, string value)
        {
            bool on = value == sIcon;
            var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            inner.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 26, Foreground = on ? White : Accent, HorizontalAlignment = HorizontalAlignment.Center });
            if (label != null) inner.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = on ? White : Sub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
            var b = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Margin = new Thickness(0, 6, 0, 6), Child = inner };
            if (on) Glass.Button(b); else Glass.Card(b, 32);
            return Clickable(b, () => { sIcon = value; sLetter.Text = sIcon ?? Glyph(sTitle.Text); CloseIcons(); });
        }
        grid.Children.Add(Cell(Glyph(sTitle.Text), "Auto", null));
        foreach (var g in IconChoices) grid.Children.Add(Cell(g, null, g));

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = grid });
        iconOverlay.Child = dock;
        iconOverlay.Visibility = Visibility.Visible;
        SlideIn(iconOverlay, 40, 280);
        BlurSheet();
    }

    // panels opened over the add/edit sheet frost it, so their text stays readable
    readonly System.Windows.Media.Effects.BlurEffect sheetBlur = new() { Radius = 0 };
    void BlurSheet()
    {
        sheet.Effect = sheetBlur;
        Anim(sheetBlur, System.Windows.Media.Effects.BlurEffect.RadiusProperty, null, calOpen || iconOpen || setOpen || rtOpen ? 24 : 0, 250);
    }

    void CloseIcons()
    {
        iconOpen = false;
        Anim(iconOverlay, OpacityProperty, null, 0, 200, () => { if (!iconOpen) iconOverlay.Visibility = Visibility.Collapsed; });
        if (iconOverlay.RenderTransform is TranslateTransform tt) Anim(tt, TranslateTransform.YProperty, null, 40, 200);
        BlurSheet();
    }

    // ---- routines: saved task sequences; list view (start / edit) and an editor, in one full-window panel ----
    readonly Border rtOverlay = new() { Background = Card, Visibility = Visibility.Collapsed };
    bool rtOpen, rtEditing;

    void OpenRoutines()
    {
        if (calOpen) CloseCal();
        if (setOpen) CloseSettings();
        rtOpen = true;
        ShowRoutineList();
        rtOverlay.Visibility = Visibility.Visible;
        SlideIn(rtOverlay, 40, 280);
        Anim(behind, System.Windows.Media.Effects.BlurEffect.RadiusProperty, null, 24, 280);
        BlurSheet();
    }

    void CloseRoutines()
    {
        rtOpen = false;
        Anim(rtOverlay, OpacityProperty, null, 0, 200, () => { if (!rtOpen) rtOverlay.Visibility = Visibility.Collapsed; });
        if (rtOverlay.RenderTransform is TranslateTransform tt) Anim(tt, TranslateTransform.YProperty, null, 40, 200);
        if (!sheetOpen) Anim(behind, System.Windows.Media.Effects.BlurEffect.RadiusProperty, null, 0, 250);
        BlurSheet();
    }

    // lay the routine onto the selected day and show it
    public void StartRoutine(Routine r)
    {
        var day = selected;
        var last = tasks.Where(x => x.Start.Date == day).Select(x => (DateTime?)x.End()).Max();
        tasks.AddRange(r.Expand(Routine.StartFor(day, DateTime.Now, last)));
        Save();
        if (rtOpen) CloseRoutines();
        lastDay = default; // replay the slide-in so the new tasks are noticed
        Tick();
    }

    UIElement PanelTop(string title, Action close, UIElement right = null)
    {
        var x = Clickable(new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Field, BorderBrush = Glass.Rim, BorderThickness = new Thickness(1), ToolTip = "Close (Esc)",
            Child = new TextBlock { Text = "✕", FontSize = 20, Foreground = White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        }, close);
        var top = new DockPanel { Margin = new Thickness(20, 14, 20, 6) };
        DockPanel.SetDock(x, Dock.Left); top.Children.Add(x);
        if (right != null) { DockPanel.SetDock(right, Dock.Right); top.Children.Add(right); }
        top.Children.Add(new TextBlock { Text = title, Foreground = White, FontSize = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) });
        return top;
    }

    static Border Chip(string text, string glyph = null) => Glass.Card(new Border
    {
        Padding = new Thickness(14, 7, 14, 7), VerticalAlignment = VerticalAlignment.Center,
        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = {
            new TextBlock { Text = glyph ?? "", FontFamily = IconFont, FontSize = 13, Foreground = Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, glyph == null ? 0 : 8, 0) },
            new TextBlock { Text = text, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center } } }
    }, 16);

    void ShowRoutineList()
    {
        rtEditing = false;
        var body = new StackPanel { Margin = new Thickness(16, 10, 16, 16) };
        string dayName = selected == DateTime.Today ? "today" : selected.ToString("ddd d MMM");
        body.Children.Add(new TextBlock { Text = $"Start adds the steps to {dayName}, back to back" + (selected == DateTime.Today ? ", from now." : "."), Foreground = Sub, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 10) });
        if (Routine.All.Count == 0)
            body.Children.Add(new TextBlock { Text = "No routines yet.\nPress New, or save a day you've already planned.", Foreground = Sub, FontSize = 15, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 16) });
        foreach (var r in Routine.All)
        {
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            info.Children.Add(new TextBlock { Text = r.Name, Foreground = White, FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock { Text = $"{r.Steps.Count} {(r.Steps.Count == 1 ? "step" : "steps")} · {Dur(r.TotalMinutes)}", Foreground = Accent, FontSize = 13, Margin = new Thickness(0, 2, 0, 4) });
            info.Children.Add(new TextBlock { Text = string.Join("  →  ", r.Steps.Select(s => $"{s.Title} {s.Minutes}m")), Foreground = Sub, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            var play = Clickable(Glass.Button(new Border
            {
                Width = 50, Height = 50, CornerRadius = new CornerRadius(25), ToolTip = "Start", VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 18, Foreground = White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) }
            }), () => StartRoutine(r));
            var row = new DockPanel();
            DockPanel.SetDock(play, Dock.Right);
            row.Children.Add(play); row.Children.Add(info);
            var card = Clickable(Glass.Card(new Border { Padding = new Thickness(18, 14, 14, 14), Margin = new Thickness(0, 0, 0, 10), Child = row, ToolTip = "Edit" }, 24), () => ShowRoutineEditor(r));
            body.Children.Add(card);
        }
        if (tasks.Any(t => t.Start.Date == selected))
        {
            var fromDay = Clickable(Chip($"Save {dayName}'s tasks as a routine", ""), () => ShowRoutineEditor(null, true));
            fromDay.HorizontalAlignment = HorizontalAlignment.Center; fromDay.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(fromDay);
        }
        var newBtn = Clickable(Chip("New", ""), () => ShowRoutineEditor(null));
        var dock = new DockPanel();
        var top = PanelTop("Routines", CloseRoutines, newBtn);
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = body });
        rtOverlay.Child = dock;
    }

    // editor works on a copy; Save writes it back (new routine is appended)
    void ShowRoutineEditor(Routine existing, bool fromDay = false)
    {
        rtEditing = true;
        var steps = existing?.Steps.Select(s => new RoutineStep { Title = s.Title, Minutes = s.Minutes, Icon = s.Icon }).ToList() ?? new();
        if (fromDay) steps = tasks.Where(t => t.Start.Date == selected).OrderBy(t => t.Start).Select(t => new RoutineStep { Title = t.Title, Minutes = t.Minutes, Icon = t.Icon }).ToList();
        if (steps.Count == 0) steps.Add(new RoutineStep { Minutes = 20 });

        TextBox Box(double size, string text) => new() { Text = text, Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        var name = Box(22, existing?.Name ?? "");
        name.FontWeight = FontWeights.Bold;
        var nameHint = new TextBlock { Text = "Routine name", Foreground = B("#66FFFFFF"), FontSize = 22, FontWeight = FontWeights.Bold, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        name.TextChanged += (s, e) => nameHint.Visibility = name.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        nameHint.Visibility = name.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var nameCard = Glass.Card(new Border { Padding = new Thickness(18, 12, 18, 12), Child = new Grid { Children = { nameHint, name } } }, 22);

        var total = new TextBlock { Foreground = Accent, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        void Total() => total.Text = Dur(steps.Sum(s => s.Minutes));
        var list = new StackPanel();
        void Rows()
        {
            list.Children.Clear();
            foreach (var step in steps)
            {
                var st = step;
                var glyph = new TextBlock { Text = st.Icon ?? Glyph(st.Title), FontFamily = IconFont, FontSize = 18, Foreground = Accent, Width = 28, VerticalAlignment = VerticalAlignment.Center };
                var title = Box(16, st.Title);
                var titleHint = new TextBlock { Text = "Step", Foreground = B("#66FFFFFF"), FontSize = 16, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0), Visibility = st.Title.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
                title.TextChanged += (s, e) => { st.Title = title.Text; titleHint.Visibility = title.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; if (st.Icon == null) glyph.Text = Glyph(st.Title); };
                var min = Box(16, st.Minutes.ToString()); min.Width = 44; min.HorizontalContentAlignment = HorizontalAlignment.Right;
                min.TextChanged += (s, e) => { if (int.TryParse(min.Text.Trim(), out var m) && m > 0 && m <= 24 * 60) { st.Minutes = m; Total(); } };
                var remove = Clickable(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12, Foreground = Sub, Padding = new Thickness(10, 6, 2, 6), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Remove step", Background = Brushes.Transparent },
                                       () => { if (steps.Count > 1) { steps.Remove(st); Rows(); Total(); } });
                var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { min, new TextBlock { Text = "min", Foreground = Sub, FontSize = 14, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, remove } };
                var row = new DockPanel();
                DockPanel.SetDock(glyph, Dock.Left); DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(glyph); row.Children.Add(right); row.Children.Add(new Grid { Children = { titleHint, title } });
                list.Children.Add(Glass.Card(new Border { Padding = new Thickness(16, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = row }, 20));
            }
        }
        Rows(); Total();

        var addStep = Clickable(Chip("Add step", ""), () => { steps.Add(new RoutineStep { Minutes = 20 }); Rows(); Total(); });
        var stepsHead = new DockPanel { Margin = new Thickness(8, 18, 8, 10) };
        DockPanel.SetDock(total, Dock.Right); stepsHead.Children.Add(total);
        stepsHead.Children.Add(new TextBlock { Text = "Steps", Foreground = White, FontSize = 20, FontWeight = FontWeights.Bold });

        var warn = new TextBlock { Text = "⚠  Give it a name and at least one named step", Foreground = White, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
        var save = Clickable(Glass.Button(new Border { Height = 54, CornerRadius = new CornerRadius(27), Child = new TextBlock { Text = existing == null ? "Save routine" : "Save", Foreground = White, FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }), () =>
        {
            var named = steps.Where(s => s.Title.Trim().Length > 0).ToList();
            if (name.Text.Trim().Length == 0 || named.Count == 0) { warn.Visibility = Visibility.Visible; Anim(warn, OpacityProperty, 0, 1, 200); (name.Text.Trim().Length == 0 ? name : null)?.Focus(); return; }
            foreach (var s in named) s.Title = s.Title.Trim();
            var r = existing ?? new Routine();
            r.Name = name.Text.Trim(); r.Steps = named;
            if (existing == null) Routine.All.Add(r);
            Routine.SaveAll();
            ShowRoutineList();
        });
        var bottom = new StackPanel { Margin = new Thickness(20, 4, 20, 16), Children = { warn, save } };
        if (existing != null)
            bottom.Children.Add(Clickable(new Border { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), Child = new TextBlock { Text = "Delete routine", Foreground = B("#FF6B6B"), FontSize = 15 } },
                () => { Routine.All.Remove(existing); Routine.SaveAll(); ShowRoutineList(); }));

        var body = new StackPanel { Margin = new Thickness(16, 10, 16, 16), Children = { nameCard, stepsHead, list } };
        addStep.HorizontalAlignment = HorizontalAlignment.Left; addStep.Margin = new Thickness(4, 2, 0, 0);
        body.Children.Add(addStep);
        var dock = new DockPanel();
        var top = PanelTop(existing == null ? "New routine" : "Edit routine", ShowRoutineList);
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(top); dock.Children.Add(bottom);
        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = body });
        rtOverlay.Child = dock;
        SlideIn(dock, 20, 220);
        if (existing == null && !fromDay) Dispatcher.BeginInvoke(() => name.Focus(), DispatcherPriority.Input);
    }

    // ---- settings: full-window panel like the calendar; rebuilt on every open so it always shows current state ----
    readonly Border setOverlay = new() { Background = Card, Visibility = Visibility.Collapsed };
    bool setOpen;

    void OpenSettings()
    {
        if (calOpen) CloseCal();
        setOpen = true;
        BuildSettings();
        setOverlay.Visibility = Visibility.Visible;
        SlideIn(setOverlay, 40, 280);
        Anim(behind, System.Windows.Media.Effects.BlurEffect.RadiusProperty, null, 24, 280);
        BlurSheet();
    }

    void CloseSettings()
    {
        setOpen = false;
        Anim(setOverlay, OpacityProperty, null, 0, 200, () => { if (!setOpen) setOverlay.Visibility = Visibility.Collapsed; });
        if (setOverlay.RenderTransform is TranslateTransform tt) Anim(tt, TranslateTransform.YProperty, null, 40, 200);
        if (!sheetOpen) Anim(behind, System.Windows.Media.Effects.BlurEffect.RadiusProperty, null, 0, 250);
        BlurSheet();
    }

    // row of colour dots; the picked one gets a white ring, and the whole app recolours live
    static UIElement Swatches()
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 4, 0, 8) };
        var rings = new List<Border>();
        void Mark() { for (int i = 0; i < rings.Count; i++) rings[i].BorderBrush = Glass.Palette[i].hex.Equals(Settings.Cur.Accent, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent; }
        foreach (var (name, hex) in Glass.Palette)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var ring = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(2.5), Padding = new Thickness(3), ToolTip = name,
                Child = new Border { CornerRadius = new CornerRadius(15), Background = new LinearGradientBrush(Color.FromArgb(255, (byte)Math.Min(255, c.R + 40), (byte)Math.Min(255, c.G + 40), (byte)Math.Min(255, c.B + 40)), c, 90) }
            };
            rings.Add(ring);
            grid.Children.Add(Clickable(new Border { Background = Brushes.Transparent, Child = ring }, () => { Settings.Cur.Accent = hex; Settings.Cur.Save(); Glass.SetAccent(hex); Mark(); }));
        }
        Mark();
        return grid;
    }

    void BuildSettings()
    {
        var s = Settings.Cur;
        var close = Clickable(new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Field, BorderBrush = Glass.Rim, BorderThickness = new Thickness(1), ToolTip = "Close (Esc)",
            Child = new TextBlock { Text = "✕", FontSize = 20, Foreground = White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        }, CloseSettings);
        var top = new DockPanel { Margin = new Thickness(20, 14, 20, 6) };
        DockPanel.SetDock(close, Dock.Left);
        top.Children.Add(close);
        top.Children.Add(new TextBlock { Text = "Settings", Foreground = White, FontSize = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) });

        var test = Clickable(Glass.Card(new Border { Padding = new Thickness(12, 5, 12, 5), Child = new TextBlock { Text = "▶  Test", Foreground = White, FontSize = 13 } }, 14), Toast.PlayChime);
        var body = new StackPanel { Margin = new Thickness(16, 6, 16, 16) };
        body.Children.Add(Group("General",
            ToggleRow("Start with Windows", "Opens in the tray when you sign in", s.Autostart == true, v => { s.Autostart = v; s.Save(); Autostart.Set(v); })));
        body.Children.Add(Group("Appearance", Labeled("Accent colour", null, Swatches())));
        body.Children.Add(Group("Notifications",
            ToggleRow("Pop-up cards", "A card in the corner when a task starts", s.Popups, v => { s.Popups = v; s.Save(); }),
            Divider(),
            Labeled("Sound", test, Segmented(new[] { "Off", "Quiet", "Normal", "Loud" }, s.Volume, i => { s.Volume = i; s.Save(); Toast.PlayChime(); }))));
        body.Children.Add(Group("Floating timer",
            ToggleRow("Show timer", "Always on top, current task and time left", overlay != null, SetOverlay),
            Divider(),
            Labeled("Corner", null, Segmented(new[] { "↖", "↗", "↙", "↘" }, s.Corner, i => { s.Corner = i; s.Save(); overlay?.Place(); })),
            Labeled("Monitor", null, Segmented(new[] { "Leftmost", "Main", "Rightmost" }, Math.Clamp(s.Monitor, 0, 2), i => { s.Monitor = i; s.Save(); overlay?.Place(); }))));
        int[] lens = { 15, 30, 60, 90 };
        body.Children.Add(Group("New tasks",
            Labeled("Default length", null, Segmented(new[] { "15m", "30m", "1h", "1h 30m" }, Math.Max(0, Array.IndexOf(lens, s.DefaultMin)), i => { s.DefaultMin = lens[i]; s.Save(); }))));

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = body });
        setOverlay.Child = dock;
    }

    static UIElement Group(string title, params UIElement[] rows)
    {
        var box = new StackPanel();
        foreach (var r in rows) box.Children.Add(r);
        return new StackPanel { Margin = new Thickness(0, 14, 0, 0), Children = {
            new TextBlock { Text = title.ToUpperInvariant(), Foreground = Sub, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 0, 0, 6) },
            Glass.Card(new Border { Padding = new Thickness(16, 6, 16, 6), Child = box }, 24) } };
    }

    static UIElement Divider() => new Border { Height = 1, Background = B("#1FFFFFFF"), Margin = new Thickness(0, 6, 0, 6) };

    static UIElement ToggleRow(string title, string sub, bool on, Action<bool> changed)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        var t = Toggle(on, changed);
        DockPanel.SetDock(t, Dock.Right);
        row.Children.Add(t);
        row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
            new TextBlock { Text = title, Foreground = White, FontSize = 16 },
            new TextBlock { Text = sub, Foreground = Sub, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 10, 0) } } });
        return row;
    }

    static UIElement Labeled(string title, UIElement extra, UIElement control)
    {
        var head = new DockPanel { Margin = new Thickness(0, 6, 0, 8) };
        if (extra != null) { DockPanel.SetDock(extra, Dock.Right); head.Children.Add(extra); }
        head.Children.Add(new TextBlock { Text = title, Foreground = White, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        return new StackPanel { Margin = new Thickness(0, 0, 0, 6), Children = { head, control } };
    }

    // iOS-style switch: knob slides, track fades to red
    static Border Toggle(bool on, Action<bool> changed)
    {
        var tt = new TranslateTransform(on ? 20 : 0, 0);
        var track = new Border
        {
            Width = 52, Height = 30, CornerRadius = new CornerRadius(15), BorderBrush = Glass.Rim, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            Background = on ? Glass.Accent : Glass.Fill,
            Child = new Ellipse { Width = 24, Height = 24, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0), RenderTransform = tt }
        };
        Clickable(track, () =>
        {
            on = !on;
            track.Background = on ? Glass.Accent : Glass.Fill;
            Anim(tt, TranslateTransform.XProperty, null, on ? 20 : 0, 200);
            changed(on);
        });
        return track;
    }

    // segmented control: one red pill slides to the picked option (same feel as Today/Tomorrow)
    static UIElement Segmented(string[] labels, int sel, Action<int> pick)
    {
        var hl = Glass.Button(new Border { CornerRadius = new CornerRadius(16), RenderTransform = new TranslateTransform() });
        var cells = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
        var grid = new Grid { Children = { hl, cells } };
        var texts = new List<TextBlock>();
        void Move(bool animate)
        {
            if (cells.ActualWidth == 0) return;
            double w = cells.ActualWidth / labels.Length;
            hl.HorizontalAlignment = HorizontalAlignment.Left;
            if (animate) { Anim((TranslateTransform)hl.RenderTransform, TranslateTransform.XProperty, null, sel * w, 240); }
            else { hl.RenderTransform.BeginAnimation(TranslateTransform.XProperty, null); ((TranslateTransform)hl.RenderTransform).X = sel * w; }
            hl.Width = w;
            for (int i = 0; i < texts.Count; i++) texts[i].Foreground = i == sel ? White : Sub;
        }
        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            var tb = new TextBlock { Text = labels[i], FontSize = labels[i].Length == 1 ? 20 : 14 /* lone arrows need to be bigger */, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            texts.Add(tb);
            cells.Children.Add(Clickable(new Border { Height = 34, Background = Brushes.Transparent, Child = tb }, () => { if (sel == idx) return; sel = idx; Move(true); pick(idx); }));
        }
        grid.SizeChanged += (s, e) => Move(false);
        return Glass.Card(new Border { Padding = new Thickness(3), Child = grid }, 19);
    }

    // ---- animation helpers ----
    static void Anim(IAnimatable target, DependencyProperty p, double? from, double to, int ms, Action done = null)
    {
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (from != null) a.From = from;
        if (done != null) a.Completed += (s, e) => done();
        target.BeginAnimation(p, a);
    }

    static void SlideIn(UIElement e, double dy, int ms = 260)
    {
        var tt = new TranslateTransform();
        e.RenderTransform = tt;
        Anim(e, OpacityProperty, 0, 1, ms);
        Anim(tt, TranslateTransform.YProperty, dy, 0, ms);
    }

    static void Hover(UIElement e)
    {
        e.MouseEnter += (s, a) => Anim(e, OpacityProperty, null, 0.78, 120);
        e.MouseLeave += (s, a) => Anim(e, OpacityProperty, null, 1, 180);
    }

    // ScrollViewer offsets aren't animatable, so ease them per frame; sv.Tag holds an id that a newer scroll/drag bumps to cancel this one
    static void SmoothScroll(ScrollViewer sv, double to, bool horiz, double ms = 260, Action done = null)
    {
        int id = (int)(sv.Tag ?? 0) + 1; sv.Tag = id;
        double from = horiz ? sv.HorizontalOffset : sv.VerticalOffset;
        var sw = Stopwatch.StartNew();
        EventHandler h = null;
        h = (s, e) =>
        {
            double t = Math.Min(1, sw.ElapsedMilliseconds / ms);
            if ((int)sv.Tag != id) { CompositionTarget.Rendering -= h; return; }
            double v = from + (to - from) * (1 - Math.Pow(1 - t, 3));
            if (horiz) sv.ScrollToHorizontalOffset(v); else sv.ScrollToVerticalOffset(v);
            if (t >= 1) { CompositionTarget.Rendering -= h; done?.Invoke(); }
        };
        CompositionTarget.Rendering += h;
    }

    // picker wheel: drag with LMB, mouse wheel, or click; the item in the centre is the pick.
    // pick(i, scroll): scroll=false while dragging (the user is moving it), true to snap.
    static void MakeWheel(ScrollViewer sv, bool horiz, double size, int count, Func<int> current, Action<int, bool> pick)
    {
        double start = 0, off0 = 0, vel = 0, lastPos = 0; bool down = false, moved = false, flinging = false;
        var clock = Stopwatch.StartNew(); long lastT = 0;
        double Off() => horiz ? sv.HorizontalOffset : sv.VerticalOffset;
        double Pos(MouseEventArgs e) { var p = e.GetPosition(sv); return horiz ? p.X : p.Y; }
        int Clamp(int i) => Math.Clamp(i, 0, count - 1);
        int Centre() => Clamp((int)Math.Round(Off() / size));
        sv.ScrollChanged += (s, e) => { if (flinging && Centre() != current()) pick(Centre(), false); }; // highlight rides along the coast
        sv.PreviewMouseLeftButtonDown += (s, e) =>
        {
            down = true; moved = false; flinging = false; vel = 0; sv.Tag = (int)(sv.Tag ?? 0) + 1; // stop any smooth scroll
            start = lastPos = Pos(e); off0 = Off(); lastT = clock.ElapsedMilliseconds;
            sv.CaptureMouse(); e.Handled = true;
        };
        sv.PreviewMouseMove += (s, e) =>
        {
            if (!down) return;
            double p = Pos(e); long now = clock.ElapsedMilliseconds;
            if (now > lastT) { vel = 0.6 * ((p - lastPos) / (now - lastT)) + 0.4 * vel; lastPos = p; lastT = now; } // px/ms, smoothed
            double d = p - start;
            if (!moved && Math.Abs(d) < 4) return; // still a click
            moved = true;
            if (horiz) sv.ScrollToHorizontalOffset(off0 - d); else sv.ScrollToVerticalOffset(off0 - d);
            if (Centre() != current()) pick(Centre(), false); // highlight follows the centre
        };
        sv.PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (!down) return;
            down = false; sv.ReleaseMouseCapture(); e.Handled = true;
            if (clock.ElapsedMilliseconds - lastT > 80) vel = 0; // paused before release: no fling
            if (moved && Math.Abs(vel) > 0.3)
            {   // fling: coast with ease-out, then settle on the item it lands on
                int target = Clamp((int)Math.Round((Off() - vel * 140) / size));
                double dist = Math.Abs(target * size - Off());
                flinging = true;
                SmoothScroll(sv, target * size, horiz, Math.Clamp(200 + dist * 0.5, 200, 450),() => { flinging = false; pick(target, false); });
                return;
            }
            double pad = ((horiz ? sv.ViewportWidth : sv.ViewportHeight) - size) / 2;
            pick(moved ? Centre() : Clamp((int)Math.Floor((Off() + Pos(e) - pad) / size)), true); // snap
        };
        sv.LostMouseCapture += (s, e) => down = false;
        sv.PreviewMouseWheel += (s, e) => { e.Handled = true; flinging = false; pick(Clamp(current() + (e.Delta > 0 ? -1 : 1)), true); };
    }

    int NearestDur() => Array.IndexOf(DurChoices, DurChoices.OrderBy(m => Math.Abs(m - sMin)).First());

    // past titles matching what's typed; clicking one copies its title, time and duration
    void RenderSuggestions()
    {
        suggestions.Children.Clear();
        var q = sTitle.Text.Trim();
        if (editing != null || q.Length == 0) return; // empty title: keep the time picker in view
        var list = tasks.OrderByDescending(t => t.Start).GroupBy(t => t.Title.Trim().ToLower()).Select(g => g.First())
                        .Where(t => t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) && !t.Title.Equals(q, StringComparison.OrdinalIgnoreCase))
                        .Take(3).ToList();
        if (list.Count == 0) return;
        suggestions.Children.Add(new TextBlock { Text = "Suggestions", Foreground = Sub, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 24, 0, 10) });
        var box = new StackPanel();
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            var row = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            Put(row, new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = Glass.Fill, BorderBrush = Glass.Rim, BorderThickness = new Thickness(1), Child = new TextBlock { Text = IconOf(t), FontFamily = IconFont, Foreground = Accent, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }, 0);
            var info = new StackPanel { Margin = new Thickness(4, 12, 0, 12) };
            info.Children.Add(new TextBlock { Text = $"{t.Start:HH:mm}–{t.End():HH:mm} ({Dur(t.Minutes)})", Foreground = Sub, FontSize = 13 });
            info.Children.Add(new TextBlock { Text = t.Title, Foreground = White, FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            Put(row, info, 1);
            box.Children.Add(Clickable(row, () =>
            {
                sIcon = t.Icon;
                syncing = true; sTitle.Text = t.Title; syncing = false;
                sStart = t.Start.TimeOfDay; sMin = t.Minutes;
                suggestions.Children.Clear();
                RenderSheet();
            }));
            if (i < list.Count - 1) box.Children.Add(new Border { Height = 1, Background = B("#1FFFFFFF"), Margin = new Thickness(56, 0, 12, 0) });
        }
        suggestions.Children.Add(Glass.Card(new Border { Padding = new Thickness(14, 4, 14, 4), Child = box }, 26));
    }

    // ---- storage ----
    void Load()
    {
        if (!File.Exists(dataPath)) return;
        try { tasks = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(dataPath)) ?? new(); }
        catch (Exception ex)
        {
            // keep the broken file instead of overwriting it with an empty list
            var bad = dataPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(dataPath, bad);
            MessageBox.Show($"tasks.json was unreadable and has been kept as {bad}\n{ex.Message}", "Task Scheduler");
        }
    }

    void Save()
    {
        var tmp = dataPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(tasks, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, dataPath, true);
    }

    // ---- helpers ----
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static string Dur(int m) => m < 60 ? $"{m} min" : $"{m / 60} {(m / 60 == 1 ? "hr" : "hrs")}" + (m % 60 == 0 ? "" : $" {m % 60} min");
    // strictly proportional: 30 min = 110 px, 1 h = 220, 5 h = 1100; 15 min (55 px) is the floor so the text still fits
    public static double PillHeight(int m) => Math.Max(55, m * 110 / 30.0);
    public static string LeftText(int m) => m < 60 ? $"{m}m" : $"{m / 60}h {m % 60}m";
    static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    // task icon picked from words in the title (English + Russian); Segoe Fluent Icons ships with Windows 11
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly (string[] words, string glyph)[] Icons =
    {
        (new[] { "break", "rest", "sleep", "nap", "relax", "перерыв", "отдых", "сон", "спать" }, "\uE708"),          // moon
        (new[] { "study", "lecture", "read", "book", "learn", "exam", "homework", "class", "учёб", "учеб", "лекц", "книг", "экзам", "домашк", "пара" }, "\uE8F1"), // books
        (new[] { "job", "work", "career", "apply", "application", "cv", "resume", "interview", "работ", "вакан", "резюме", "собес" }, "\uE821"), // briefcase
        (new[] { "game", "unity", "unreal", "godot", "игр" }, "\uE7FC"),                                             // controller
        (new[] { "code", "coding", "dev", "program", "bug", "git", "вайб", "код", "програм" }, "\uE943"),             // </>
        (new[] { "project", "fyp", "essay", "dissertation", "thesis", "report", "write", "writing", "doc", "проект", "диплом", "курсов", "эссе", "отчёт", "отчет", "писать" }, "\uE70F"), // pen
        (new[] { "meeting", "call", "chat", "discord", "встреч", "созвон", "звонок" }, "\uE8F2"),                    // chat bubbles
        (new[] { "mail", "email", "inbox", "почт", "письм" }, "\uE715"),                                             // envelope
        (new[] { "shop", "buy", "grocer", "магазин", "купить", "покуп" }, "\uE7BF"),                                 // cart
        (new[] { "home", "clean", "chores", "laundry", "дом", "уборк", "убрать", "стирк" }, "\uE80F"),               // house
        (new[] { "music", "guitar", "piano", "музык", "гитар" }, "\uE8D6"),                                          // note
        (new[] { "video", "youtube", "film", "movie", "watch", "видео", "фильм", "кино", "ютуб" }, "\uE714"),        // video
    };
    public static string IconOf(TaskItem t) => string.IsNullOrEmpty(t.Icon) ? Glyph(t.Title) : t.Icon;
    public static string Glyph(string title)
    {
        var s = title.ToLowerInvariant();
        foreach (var (words, glyph) in Icons)
            if (words.Any(w => s.Contains(w))) return glyph;
        return "\uE9D5"; // checklist
    }

    static TextBox SmallBox() => new() { Width = 74, Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(10, 7, 10, 7), FontSize = 16, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    static void Put(Grid g, UIElement e, int col, int row = 0) { Grid.SetColumn(e, col); Grid.SetRow(e, row); g.Children.Add(e); }

    static T Clickable<T>(T e, Action a) where T : FrameworkElement
    {
        e.Cursor = Cursors.Hand;
        e.MouseLeftButtonUp += (s, ev) => { a(); ev.Handled = true; };
        Hover(e);
        return e;
    }
}

// Small always-on-top card: current task, time left, progress bar. Sits top-left of the leftmost monitor.
public class OverlayWindow : Window
{
    static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
    static Brush Accent => Glass.AccentBrush;
    static readonly Brush Sub = B("#8E8E93"), Track = B("#3A3A3C");
    const double BarW = 240;

    readonly Func<List<TaskItem>> tasks;
    readonly TextBlock title = new() { Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 };
    readonly TextBlock clock = new() { Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock range = new() { Foreground = Sub, FontSize = 12 };
    readonly Border fill = new() { Background = Accent, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    readonly Ellipse knob = new() { Width = 12, Height = 12, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };

    public OverlayWindow(Func<List<TaskItem>> tasks, Action open)
    {
        this.tasks = tasks;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; Title = "Task Scheduler timer";

        var top = new DockPanel();
        DockPanel.SetDock(clock, Dock.Right);
        top.Children.Add(clock);
        top.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Children = { title, range } });
        var bar = new Grid { Width = BarW, Height = 12, Margin = new Thickness(0, 10, 0, 0) };
        bar.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Track });
        fill.Height = 6;
        bar.Children.Add(fill); bar.Children.Add(knob);
        var stack = new StackPanel { Children = { top, bar } };
        Content = new Border
        {
            Background = Glass.Solid, CornerRadius = new CornerRadius(20), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(10),
            BorderBrush = Glass.Rim, BorderThickness = new Thickness(1), Child = stack, Cursor = Cursors.Hand, ToolTip = "Open Task Scheduler",
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.5 }
        };
        MouseLeftButtonUp += (s, e) => open();

        SourceInitialized += (s, e) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, -20, Native.GetWindowLong(h, -20) | 0x80 | 0x08000000); // tool window (no Alt+Tab) + never steals focus
            Place();
        };
        SizeChanged += (s, e) => Place();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplay;
        Closed += (s, e) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplay;

        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (s, e) => Update();
        t.Start();
        Closed += (s, e) => t.Stop();
        Update();
    }

    void OnDisplay(object s, EventArgs e) => Dispatcher.BeginInvoke(Place);

    // physical pixels via SetWindowPos, so mixed-DPI monitors don't throw the position off
    public void Place()
    {
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero || !Native.GetWindowRect(h, out var r)) return;
        var s = Settings.Cur;
        var byX = WF.Screen.AllScreens.OrderBy(sc => sc.Bounds.Left);
        var area = (s.Monitor == 1 ? WF.Screen.PrimaryScreen : s.Monitor == 2 ? byX.Last() : byX.First()).WorkingArea;
        int w = r.R - r.L, ht = r.B - r.T;
        int x = s.Corner % 2 == 1 ? area.Right - w - 6 : area.Left + 6;
        int y = s.Corner >= 2 ? area.Bottom - ht - 6 : area.Top + 6;
        Native.SetWindowPos(h, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010); // HWND_TOPMOST, NOSIZE|NOACTIVATE
    }

    void Update()
    {
        var now = DateTime.Now;
        var list = tasks();
        var cur = list.Where(x => x.IsRunning(now)).OrderBy(x => x.Start).FirstOrDefault();
        double f = 0;
        if (cur != null)
        {
            title.Text = cur.Title;
            clock.Text = Clock(cur.End() - now);
            clock.Foreground = Brushes.White;
            range.Text = $"{cur.Start:HH:mm}–{cur.End():HH:mm} · remaining";
            f = (now - cur.Start).TotalSeconds / (cur.Minutes * 60.0);
        }
        else
        {
            var next = list.Where(x => !x.Done && x.Start > now).OrderBy(x => x.Start).FirstOrDefault();
            title.Text = next?.Title ?? "No tasks";
            clock.Text = next != null ? Clock(next.Start - now) : "--:--";
            clock.Foreground = Sub;
            range.Text = next != null ? $"starts at {next.Start:HH:mm}" : "add a task";
        }
        f = Math.Clamp(f, 0, 1);
        fill.Width = BarW * f;
        knob.Margin = new Thickness(Math.Max(0, BarW * f - 6), 0, 0, 0);
        knob.Visibility = cur != null ? Visibility.Visible : Visibility.Collapsed;
        Place(); // other topmost windows can push it down; re-assert every second
    }

    public static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
    public struct Rect { public int L, T, R, B; }
}

// Own notification card: slides in bottom-right of the main monitor, stacks, auto-hides, never steals focus.
public class Toast : Window
{
    static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
    static Brush Accent => Glass.AccentBrush;
    static readonly Brush Sub = B("#8E8E93");
    static readonly List<Toast> shown = new();
    const int LifeMs = 9000;
    const double CardW = 340;

    readonly Border card, life;
    readonly TranslateTransform slide = new(80, 0);
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(LifeMs) };
    bool closing;

    public static void Pop(TaskItem t, Action open) { var w = new Toast(t, open); shown.Add(w); w.Show(); }

    static readonly double[] Volumes = { 0, 0.035, 0.07, 0.14 }; // Off, Quiet, Normal, Loud
    public static void PlayChime()
    {
        int v = Math.Clamp(Settings.Cur.Volume, 0, 3);
        if (v == 0) return;
        try { new System.Media.SoundPlayer(new MemoryStream(Chime(Volumes[v]))).Play(); }
        catch { } // no audio device etc. — the card still shows, sound is a nicety
    }

    Toast(TaskItem t, Action open)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; Title = "Task Scheduler notification";

        var letter = new Border
        {
            Width = 50, Height = 50, CornerRadius = new CornerRadius(25), Background = Glass.Fill, BorderBrush = Accent, BorderThickness = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = MainWindow.IconOf(t), FontFamily = MainWindow.IconFont, Foreground = Accent, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var text = new StackPanel { Margin = new Thickness(14, 0, 8, 0) };
        text.Children.Add(new TextBlock { Text = "STARTED  ·  " + $"{t.Start:HH:mm}–{t.End():HH:mm}", Foreground = Accent, FontSize = 11, FontWeight = FontWeights.Bold });
        text.Children.Add(new TextBlock { Text = t.Title, Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 2) });
        text.Children.Add(new TextBlock { Text = "Duration " + MainWindow.Dur(t.Minutes), Foreground = Sub, FontSize = 13 });
        var x = new TextBlock { Text = "✕", Foreground = Sub, FontSize = 13, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(6, 0, 0, 6), ToolTip = "Dismiss" };
        x.MouseLeftButtonUp += (s, e) => { e.Handled = true; Dismiss(); };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1); Grid.SetColumn(x, 2);
        row.Children.Add(letter); row.Children.Add(text); row.Children.Add(x);

        // thin bar that shrinks until the card hides itself
        life = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Accent, HorizontalAlignment = HorizontalAlignment.Left, Width = CardW - 32, Margin = new Thickness(0, 12, 0, 0) };
        card = new Border
        {
            Width = CardW, Background = Glass.Solid, CornerRadius = new CornerRadius(24), Padding = new Thickness(16, 14, 16, 12), Margin = new Thickness(14),
            BorderBrush = Glass.Rim, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, RenderTransform = slide, Opacity = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = 0.55 },
            Child = new StackPanel { Children = { row, life } }
        };
        card.MouseLeftButtonUp += (s, e) => { open(); Dismiss(); };
        card.MouseEnter += (s, e) => { timer.Stop(); life.BeginAnimation(WidthProperty, null); life.Width = CardW - 32; }; // hovering keeps it
        card.MouseLeave += (s, e) => StartLife();
        Content = card;

        timer.Tick += (s, e) => Dismiss();
        SourceInitialized += (s, e) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, -20, Native.GetWindowLong(h, -20) | 0x80 | 0x08000000); // tool window + no focus steal
        };
        Loaded += (s, e) =>
        {
            Restack();
            Ease(slide, TranslateTransform.XProperty, 80, 0, 380);
            Ease(card, OpacityProperty, 0, 1, 300);
            StartLife();
        };
    }

    void StartLife()
    {
        timer.Stop(); timer.Start();
        life.BeginAnimation(WidthProperty, new DoubleAnimation(CardW - 32, 0, TimeSpan.FromMilliseconds(LifeMs)));
    }

    void Dismiss()
    {
        if (closing) return;
        closing = true; timer.Stop();
        Ease(slide, TranslateTransform.XProperty, null, 90, 220);
        Ease(card, OpacityProperty, null, 0, 220, () => { shown.Remove(this); Close(); Restack(); });
    }

    // newest at the bottom-right corner, older ones above it (physical pixels, so DPI can't skew it)
    static void Restack()
    {
        var area = WF.Screen.PrimaryScreen.WorkingArea;
        int bottom = area.Bottom;
        for (int i = shown.Count - 1; i >= 0; i--)
        {
            var h = new System.Windows.Interop.WindowInteropHelper(shown[i]).Handle;
            if (h == IntPtr.Zero || !Native.GetWindowRect(h, out var r)) continue;
            int w = r.R - r.L, ht = r.B - r.T;
            bottom -= ht;
            Native.SetWindowPos(h, new IntPtr(-1), area.Right - w, bottom, 0, 0, 0x0001 | 0x0010); // TOPMOST, NOSIZE|NOACTIVATE
            bottom += (int)(14 * ht / shown[i].ActualHeight) - 4; // overlap the transparent margins so cards sit ~10px apart
        }
    }

    static void Ease(IAnimatable o, DependencyProperty p, double? from, double to, int ms, Action done = null)
    {
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (from != null) a.From = from;
        if (done != null) a.Completed += (s, e) => done();
        o.BeginAnimation(p, a);
    }

    // soft two-note bell (E6 → A6) with harmonics and exponential decay, built as a 16-bit mono WAV
    public static byte[] Chime(double amp = 0.07)
    {
        const int rate = 44100;
        int n = (int)(rate * 1.1);
        var pcm = new short[n];
        (double f, double at)[] notes = { (1318.5, 0.0), (1760.0, 0.13) };
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate, v = 0;
            foreach (var (f, at) in notes)
            {
                double u = t - at;
                if (u < 0) continue;
                double env = Math.Min(1, u / 0.004) * Math.Exp(-u * 4.2); // 4 ms attack avoids a click
                v += env * (Math.Sin(2 * Math.PI * f * u) + 0.35 * Math.Sin(2 * Math.PI * f * 2 * u) * Math.Exp(-u * 6) + 0.12 * Math.Sin(2 * Math.PI * f * 3 * u) * Math.Exp(-u * 9));
            }
            pcm[i] = (short)(Math.Clamp(v * amp, -1, 1) * short.MaxValue); // 0.07 = "Normal"; 0.22 was too loud
        }
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        foreach (var s in pcm) w.Write(s);
        return ms.ToArray();
    }
}
