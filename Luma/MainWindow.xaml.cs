using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Luma;

public partial class MainWindow : Window
{
    private readonly Engine _engine;
    private readonly Func<Task> _exit;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Dictionary<string, ToggleButton> _profileButtons = new();
    private readonly List<DispatcherTimer> _viewTimers = new();
    private readonly List<int> _registeredHotkeys = new();
    private readonly List<string> _hotkeyFailures = new();
    private TextBlock? _profileLabel, _colorStatus, _scheduleNote;
    private Button? _pauseButton;
    private readonly List<(Slider Slider, Func<ColorSettings, double> Read)> _colorSliders = new();
    private bool _syncingSliders;
    private int _page;
    private bool _ready, _allowClose, _dialog;
    private HwndSource? _source;
    private string? _hardwareDisplayId;
    private List<string> _layoutOrder = new();
    private string? _layoutSelected;
    private Dictionary<string, NativeLayout>? _layoutBackup;
    private DateTime _layoutDeadline;
    private readonly DispatcherTimer _layoutCountdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextBlock? _layoutCounter;
    private Border? _layoutConfirm;
    private bool _layoutBusy;

    public MainWindow(Engine engine, Func<Task> exit)
    {
        _engine = engine; _exit = exit;
        InitializeComponent();
        ApplyLanguage();
        Loc.Changed += OnLanguageChanged;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBorder.Visibility = Visibility.Collapsed; };
        _engine.Changed += OnEngineChanged;
        _engine.Displays.HardwareLoaded += () => Dispatcher.BeginInvoke(() => { if (_page == 2 && IsVisible) RenderPage(); });
        _engine.NvidiaLoaded += () => Dispatcher.BeginInvoke(() => { if (_page == 3 && IsVisible) RenderPage(); });
        _engine.SecondarySceneApplied += () => Dispatcher.BeginInvoke(() => { if (_ready) { RenderDisplays(); RenderPage(); } });
        _engine.AdaptiveChanged += () => Dispatcher.BeginInvoke(() => { if (_ready && _page == 1 && IsVisible) RenderPage(); });
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _engine.WindowHandle = handle; _source = HwndSource.FromHwnd(handle); _source?.AddHook(WindowMessage);
            RegisterHotkeys();
        };
        Deactivated += (_, _) =>
        {
            // Popups and file dialogs own their own windows. Give focus time to settle.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!_engine.Settings.Pinned && !_dialog && _layoutBackup == null && !IsActive && !HasOpenDropdown()) Hide();
            });
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !_dialog) { if (_layoutBackup != null) RevertLayout(); else Hide(); e.Handled = true; } };
        Closing += OnClosing;
        _layoutCountdown.Tick += (_, _) =>
        {
            int seconds = Math.Max(0, (int)Math.Ceiling((_layoutDeadline - DateTime.Now).TotalSeconds));
            if (_layoutCounter != null) _layoutCounter.Text = Loc.F($"Bu yerleşim kalsın mı?  {seconds} sn");
            if (seconds == 0) RevertLayout();
        };
        RenderNavigation();
        ViewPanel.Children.Add(T(Loc.T("Ekranların hazırlanıyor…"), 15));
    }
    public void EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();
    public int RegisteredHotkeyCount => _registeredHotkeys.Count;
    public string[] HotkeyConflicts => _hotkeyFailures.ToArray();
    public void RenderPreviews(string directory)
    {
        // Render the actual WPF views offscreen for repeatable layout verification.
        // This does not capture or interact with any desktop window.
        Directory.CreateDirectory(directory);
        for (int page = 0; page < 5; page++)
        {
            _page = page; RenderNavigation(); RenderPage();
            SaveView($"{page + 1:00}-{new[] { "kontrol", "otomasyon", "ekranlar", "nvidia", "ayarlar" }[page]}.png", 808);
            if (page == 2) SaveView("03-ekranlar-tam.png", 1580);
            if (page == 1) SaveView("02-otomasyon-tam.png", 2350);
            if (page == 3) SaveView("04-nvidia-tam.png", 2450);
            if (page == 4)
            {
                SaveView("05-ayarlar-tam.png", 2200);
                var app = (App)Application.Current;
                foreach (var state in new[] { RtssSetupState.Installing, RtssSetupState.Ready, RtssSetupState.NeedsPackageManager, RtssSetupState.Failed })
                {
                    app.PreviewSetupState = state; RenderPage(); SaveView("05-rtss-" + state + ".png", 1700);
                }
                app.PreviewSetupState = RtssSetupState.NeedsConsent;
            }
        }
        var secondary = _engine.Displays.Monitors.FirstOrDefault(TuningService.IsEligible);
        if (secondary != null)
        {
            _engine.SelectedDisplayId = secondary.Id; _page = 3; RenderDisplays(); RenderNavigation(); RenderPage();
            SaveView("04-nvidia-ikinci.png", 808); SaveView("04-nvidia-ikinci-tam.png", 2600);
        }
        void SaveView(string name, int height)
        {
            Shell.Measure(new Size(472, height)); Shell.Arrange(new Rect(0, 0, 472, height)); Shell.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(472, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Shell);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(System.IO.Path.Combine(directory, name)); encoder.Save(stream);
        }
    }
    public void InitializedUi() { _ready = true; RenderDisplays(); RenderPage(); RefreshLive(); }
    private bool HasOpenDropdown() => Descendants<ComboBox>(this).Any(x => x.IsDropDownOpen);
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    public void ToggleVisible() { if (IsVisible) Hide(); else Reveal(); }
    public void Reveal()
    {
        if (_allowClose) return;
        Native.GetCursorPos(out var cursor);
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        bool located = Native.GetMonitorInfo(Native.MonitorFromPoint(cursor, 2), ref info);
        double scale = 1;
        if (located)
        {
            if (GetDpiForMonitor(Native.MonitorFromPoint(cursor, 2), 0, out var dpi, out _) == 0) scale = dpi / 96.0;
            Height = Math.Min(808, info.Work.Height / scale - 16);
        }
        Show();
        if (located)
        {
            int width = (int)Math.Round(Width * scale), height = (int)Math.Round(Height * scale);
            Native.SetWindowPos(_engine.WindowHandle, new IntPtr(-1), info.Work.Right - width - (int)(10 * scale), info.Work.Bottom - height - (int)(8 * scale), width, height, 0x10);
        }
        Activate();
        Shell.RenderTransform = new TranslateTransform();
        Shell.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        RefreshLive();
    }
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    private void HeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) != null) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        while (child != null) { if (child is T value) return value; child = VisualTreeHelper.GetParent(child); } return null;
    }
    private void HideClick(object sender, RoutedEventArgs e) => Hide();
    private void PauseHeaderClick(object sender, RoutedEventArgs e) => _engine.TogglePause();
    private void PinClick(object sender, RoutedEventArgs e) { _engine.Settings.Pinned = !_engine.Settings.Pinned; _engine.SettingsChanged(); RefreshLive(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_allowClose) { e.Cancel = true; Hide(); } }
    public void AllowClose()
    {
        if (_layoutBackup != null) RevertLayout();
        Loc.Changed -= OnLanguageChanged;
        _allowClose = true; _toastTimer.Stop(); _layoutCountdown.Stop(); ClearViewTimers();
        UnregisterHotkeys(); _source?.RemoveHook(WindowMessage); Close();
    }
    private void OnEngineChanged() { if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(RefreshLive); else RefreshLive(); }
    private void RefreshLive()
    {
        StateBadge.Text = _engine.IsSuspended ? Loc.T("Ⅱ DURAKLATILDI") : Loc.T("● AKTİF");
        StateBadge.Foreground = Brush(_engine.IsSuspended ? "#FFD49A" : "#9BD6B4");
        PauseHeaderGlyph.Text = _engine.Paused ? "\uE768" : "\uE769";
        PauseHeader.Foreground = Brush(_engine.IsSuspended ? "#FFC681" : "#A4A8B8");
        PinButton.Foreground = Brush(_engine.Settings.Pinned ? "#FFC681" : "#A4A8B8");
        PinButton.ToolTip = _engine.Settings.Pinned ? Loc.T("Sabitlendi · kaldırmak için tıkla") : Loc.T("Paneli açık tut");
        if (_profileLabel != null) _profileLabel.Text = _engine.IsSuspended ? Loc.T("Renk filtresi duraklatıldı") : Loc.F($"{_engine.CurrentProfileName} modu");
        if (_colorStatus != null)
        {
            var statuses = _engine.Targets.Select(x => x.ColorStatus).Distinct().ToList();
            _colorStatus.Text = _engine.Targets.Any() ? string.Join(" · ", statuses.Select(Loc.T)) : Loc.T("Bağlı ekran bulunamadı. Yenilemeyi dene.");
        }
        if (_scheduleNote != null) _scheduleNote.Text = _engine.Settings.AdaptiveEnabled ? Loc.T(_engine.AdaptiveStatus) : _engine.ManualUntil ?? (_engine.Settings.ScheduleEnabled ? Loc.T("Zaman planı açık") : Loc.T("Kontrol sende · otomasyon kapalı"));
        if (_pauseButton != null) _pauseButton.Content = new TextBlock { Text = _engine.Paused ? Loc.T("▶  Devam et") : Loc.T("Ⅱ  Duraklat"), TextWrapping = TextWrapping.Wrap };
        _syncingSliders = true;
        try { foreach (var item in _colorSliders) item.Slider.Value = item.Read(_engine.Current); }
        finally { _syncingSliders = false; }
        bool specificScene = _engine.SelectedSceneId != null;
        foreach (var item in _profileButtons) item.Value.IsChecked = !_engine.Mixed && (TuningService.FindScene(_engine, item.Key) != null ? specificScene && item.Key == _engine.SelectedSceneId : !specificScene && _engine.Profiles.FirstOrDefault(x => x.Id == item.Key)?.Color.Safe() == _engine.Current);
    }
    public void Toast(string text, bool applied = false)
    {
        text = Loc.Status(text);
        if (applied) ReportSetting(text);
        ToastText.Text = text; ToastBorder.Visibility = Visibility.Visible; _toastTimer.Stop(); _toastTimer.Start();
    }
    private void RenderDisplays()
    {
        DisplayTabs.Children.Clear();
        void Add(string label, string? id)
        {
            var toggle = new ToggleButton { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }, IsChecked = _engine.SelectedDisplayId == id, Margin = new(0, 0, 7, 0), FontSize = 11, Padding = new(12, 8, 12, 8) };
            toggle.Click += (_, _) => { _engine.SelectedDisplayId = id; RenderDisplays(); RenderPage(); };
            DisplayTabs.Children.Add(toggle);
        }
        Add(Loc.T("▣  Tüm ekranlar"), null);
        foreach (var d in _engine.Displays.Monitors) Add($"{d.Number}  {d.Name}", d.Id);
    }
    private void RenderNavigation()
    {
        NavPanel.Children.Clear();
        var items = new[] { ("\uE706", Loc.T("Kontrol")), ("\uE823", Loc.T("Otomasyon")), ("\uE7F4", Loc.T("Ekranlar")), ("\uE950", "NVIDIA"), ("\uE713", Loc.T("Ayarlar")) };
        for (int i = 0; i < items.Length; i++)
        {
            int page = i;
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(Glyph(items[i].Item1, 17, i == _page ? "#FFC681" : "#84899B"));
            content.Children.Add(T(items[i].Item2, 10, i == _page ? "#FFD49A" : "#9296A6", margin: new(0, 5, 0, 0)));
            var button = new Button { Content = content, Background = Brush(i == _page ? "#28241F" : "Transparent"), BorderThickness = new(0), Padding = new(4), Margin = new(3, 0, 3, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(button, items[i].Item2);
            button.Click += (_, _) => { _page = page; RenderNavigation(); RenderPage(); };
            NavPanel.Children.Add(button);
        }
    }
    private void ClearViewTimers() { foreach (var t in _viewTimers) t.Stop(); _viewTimers.Clear(); }
    private void RenderPage()
    {
        ClearViewTimers(); ViewPanel.Children.Clear(); _profileButtons.Clear();
        _profileLabel = null; _colorStatus = null; _scheduleNote = null; _pauseButton = null; _colorSliders.Clear();
        if (!_ready) { ViewPanel.Children.Add(T(Loc.T("Ekranların hazırlanıyor…"), 15)); return; }
        switch (_page) { case 0: RenderControl(); break; case 1: RenderAutomation(); break; case 2: RenderScreens(); break; case 3: RenderNvidia(); break; default: RenderSettings(); break; }
        ContentScroll.ScrollToTop(); RefreshLive();
    }

    private void RenderControl()
    {
        var hero = new Grid { Height = 142, ClipToBounds = true };
        var heroGradient = new LinearGradientBrush(Color.FromRgb(47, 40, 34), Color.FromRgb(28, 30, 40), 20);
        var glow = new Ellipse { Width = 164, Height = 164, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, -12, -20, 0), Fill = new RadialGradientBrush(Color.FromArgb(115, 255, 164, 76), Colors.Transparent) };
        hero.Children.Add(glow);
        var orb = new Ellipse { Width = 85, Height = 85, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 23, 0), Stroke = Brush("#D6A368"), StrokeThickness = .8,
            Fill = new LinearGradientBrush(new GradientStopCollection { new(Colors.Transparent, 0), new(Color.FromRgb(234, 164, 91), .1), new(Color.FromRgb(89, 65, 51), .46), new(Color.FromRgb(30, 31, 41), .82) }, new Point(.15, 0), new Point(.85, 1)) };
        hero.Children.Add(orb);
        hero.Children.Add(new Ellipse { Width = 109, Height = 37, Stroke = Brush("#D4AA78"), StrokeThickness = .65, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 12, 10, 0), RenderTransformOrigin = new(.5, .5), RenderTransform = new RotateTransform(-28) });
        var copy = new StackPanel { Margin = new(18, 17, 115, 12), VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(T(Loc.T("EKRANININ YENİ RİTMİ"), 9, "#C4AD92", FontWeights.SemiBold));
        copy.Children.Add(T(Loc.T("Tam kararında\nbir ışık."), 25, "#FFF2E3", FontWeights.SemiBold, new(0, 5, 0, 9)));
        _profileLabel = T("", 11, "#DCC5AA"); copy.Children.Add(_profileLabel); hero.Children.Add(copy);
        ViewPanel.Children.Add(new Border { Background = heroGradient, CornerRadius = new(13), ClipToBounds = true, Child = hero, BorderBrush = Brush("#453B32"), BorderThickness = new(1), Margin = new(0, 0, 0, 19) });
        var modeHeading = Heading(Loc.T("BİR MOD SEÇ"), Loc.T("Her ana bir ayar")); ViewPanel.Children.Add(modeHeading);
        var profiles = new UniformGrid { Columns = 3, Margin = new(-3, 0, -3, 15) };
        foreach (var profile in _engine.Profiles)
        {
            var inside = new StackPanel();
            inside.Children.Add(Glyph(profile.Icon, 18, "#DBBB91", HorizontalAlignment.Left));
            inside.Children.Add(T(profile.DisplayName, 12, "#F1EDE6", FontWeights.SemiBold, new(0, 8, 0, 0)));
            var button = new ToggleButton { Content = inside, Margin = new(3), Padding = new(12, 12, 10, 12), ToolTip = Loc.T(profile.Description), HorizontalContentAlignment = HorizontalAlignment.Stretch, IsChecked = profile.Color.Safe() == _engine.Current };
            AutomationProperties.SetName(button, Loc.F($"{profile.DisplayName} modu"));
            button.Click += (_, _) => { _engine.SelectProfile(profile.Id); RenderPage(); };
            _profileButtons[profile.Id] = button; profiles.Children.Add(button);
        }
        ViewPanel.Children.Add(profiles);
        ViewPanel.Children.Add(Heading(Loc.T("İNCE AYAR"), _engine.SelectedDisplayId == null ? Loc.T("Tüm ekranlara uygulanır") : Loc.T("Yalnızca seçili ekran")));
        var sliders = new StackPanel();
        if (_engine.Mixed) sliders.Children.Add(T(Loc.T("Ekranların farklı ayarlarda. Değişiklik tümüne uygulanır."), 10, "#FFC681", margin: new(0, 0, 0, 12)));
        bool supported = _engine.Targets.Any(x => x.GammaAvailable && x.ActiveColorMode == 0 && x.ColorInfoKnown);
        Slider color = AddSlider(sliders, Loc.T("Renk sıcaklığı"), "\uE753", _engine.Current.Temperature, 2500, 7500, v => $"{v:0} K", v => { if (!_syncingSliders) _engine.SetColor(_engine.Current with { Temperature = Math.Round(v / 50) * 50 }); }, 50, Loc.T("Sıcak"), Loc.T("Soğuk")); color.IsEnabled = supported;
        _colorSliders.Add((color, s => s.Temperature));
        color.Foreground = new LinearGradientBrush(Color.FromRgb(255, 170, 92), Color.FromRgb(159, 193, 255), 0);
        var brightness = AddSlider(sliders, Loc.T("Yazılımsal parlaklık"), "\uE706", _engine.Current.Brightness, 50, 100, v => Loc.Percent(v), v => { if (!_syncingSliders) _engine.SetColor(_engine.Current with { Brightness = Math.Round(v) }); }, 1); brightness.IsEnabled = supported;
        _colorSliders.Add((brightness, s => s.Brightness));
        _colorStatus = T("", 10, "#9097A7", margin: new(0, 6, 0, 0)); sliders.Children.Add(_colorStatus);
        ViewPanel.Children.Add(Card(sliders));
        var advanced = new StackPanel();
        var contrast = AddSlider(advanced, Loc.T("Kontrast"), "\uE793", _engine.Current.Contrast, 75, 125, v => Loc.Percent(v), v => { if (!_syncingSliders) _engine.SetColor(_engine.Current with { Contrast = Math.Round(v) }); }, 1); contrast.IsEnabled = supported;
        var gamma = AddSlider(advanced, Loc.T("Gama"), "\uE790", _engine.Current.Gamma, .7, 1.5, v => $"{v:0.00}", v => { if (!_syncingSliders) _engine.SetColor(_engine.Current with { Gamma = Math.Round(v, 2) }); }, .01); gamma.IsEnabled = supported;
        _colorSliders.Add((contrast, s => s.Contrast)); _colorSliders.Add((gamma, s => s.Gamma));
        var profileName = new TextBox { Text = Loc.T("Özel profilim"), MaxLength = 28, Margin = new(0, 5, 8, 0), MinWidth = 160 };
        AutomationProperties.SetName(profileName, Loc.T("Özel profil adı"));
        var saveProfile = Btn(Loc.T("Profili kaydet"), () =>
        {
            string name = profileName.Text.Trim();
            if (name.Length == 0) { Toast(Loc.T("Profiline bir ad ver.")); return; }
            if (_engine.Settings.CustomProfiles.Count >= 12) { Toast(Loc.T("En fazla 12 özel profil ekleyebilirsin. Ayarlar'dan eskileri kaldır.")); return; }
            _engine.Settings.CustomProfiles.Add(new(Guid.NewGuid().ToString("N"), name, "\uE734", Loc.T("Kendi ayarın"), _engine.Current)); _engine.SaveSoon(); RenderPage(); Toast(Loc.T("Profilin kaydedildi."));
        }); saveProfile.Margin = new(0, 5, 0, 0);
        var saveRow = new DockPanel(); DockPanel.SetDock(saveProfile, Dock.Right); saveRow.Children.Add(saveProfile); saveRow.Children.Add(profileName); advanced.Children.Add(saveRow);
        ViewPanel.Children.Add(Expandable(Loc.T("Gelişmiş renk ayarları"), advanced));
        var footer = new Grid { Margin = new(0, 12, 0, 4) };
        footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var note = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; _scheduleNote = T("", 10, "#9297A8"); note.Children.Add(_scheduleNote);
        note.Children.Add(T(Loc.T("Ctrl + Alt + L  ·  her yerden aç"), 9, "#777E90", margin: new(0, 3, 0, 0))); footer.Children.Add(note);
        var pause = Btn(_engine.Paused ? Loc.T("▶  Devam et") : Loc.T("Ⅱ  Duraklat"), () => { _engine.TogglePause(); RenderPage(); }); _pauseButton = pause; Grid.SetColumn(pause, 1); footer.Children.Add(pause); ViewPanel.Children.Add(footer);
    }

    private void RenderAutomation()
    {
        PageTitle(Loc.T("Günün akışına bırak."), Loc.T("Işık değişsin, sen işine devam et."));
        RenderAdaptiveAutomation();
        var settings = new StackPanel();
        settings.Children.Add(SwitchRow(Loc.T("Klasik zaman planı"), Loc.T("Oyun + saat otomasyonu kapalıyken bu çizelge kullanılır."), _engine.Settings.ScheduleEnabled, value => { _engine.Settings.ScheduleEnabled = value; if (value) { _engine.Settings.AdaptiveEnabled = false; _engine.AdaptiveSettingsChanged(); } _engine.ScheduleChanged(); RenderPage(); }));
        settings.Children.Add(Divider());
        settings.Children.Add(SwitchRow(Loc.T("Yumuşak geçişler"), Loc.T("Modlar arasında yaklaşık bir saniyede geç."), _engine.Settings.SmoothTransitions, value => { _engine.Settings.SmoothTransitions = value; _engine.SettingsChanged(); }));
        ViewPanel.Children.Add(Card(settings));
        ViewPanel.Children.Add(Heading(Loc.T("ZAMAN ÇİZELGEN"), Loc.T("Her gün · bu bilgisayarın saati"), new(0, 22, 0, 10)));
        ViewPanel.Children.Add(T(Loc.T("Manuel ayarın bir sonraki zaman dilimine kadar korunur. Standart modlar tüm ekranlara; monitöre özel profiller yalnızca kendi ekranına uygulanır."), 11, "#969CAE", margin: new(0, 0, 0, 13)));
        foreach (var entry in _engine.Settings.Schedule.OrderBy(x => x.Time).ToList())
        {
            var row = new Grid { Margin = new(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new() { Width = new(40) }); row.ColumnDefinitions.Add(new() { Width = new(91) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new(34) });
            var enabled = new ToggleButton { Style = (Style)FindResource("Switch"), IsChecked = entry.Enabled, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(enabled, Loc.F($"{entry.Time} zaman dilimi açık"));
            enabled.Click += (_, _) =>
            {
                if (enabled.IsChecked == true && _engine.Settings.Schedule.Any(x => x != entry && x.Enabled && x.Time == entry.Time)) { enabled.IsChecked = false; Toast(Loc.T("Aynı saatte iki etkin zaman dilimi olamaz.")); return; }
                entry.Enabled = enabled.IsChecked == true; _engine.ScheduleChanged();
            }; row.Children.Add(enabled);
            var time = new TextBox { Text = entry.Time, MaxLength = 5, Margin = new(5, 0, 8, 0), Padding = new(3, 5, 3, 5), VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(time, Loc.T("Başlangıç saati"));
            void SaveTime()
            {
                if (!TimeOnly.TryParseExact(time.Text, "HH:mm", out _)) { time.Text = entry.Time; Toast(Loc.T("Saati 24 saat biçiminde yaz: 22:00")); return; }
                if (_engine.Settings.Schedule.Any(x => x != entry && x.Enabled && x.Time == time.Text)) { time.Text = entry.Time; Toast(Loc.T("Bu saat için zaten bir zaman dilimi var.")); return; }
                entry.Time = time.Text; _engine.ScheduleChanged();
            }
            time.LostKeyboardFocus += (_, _) => SaveTime(); time.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveTime(); Keyboard.ClearFocus(); } };
            Grid.SetColumn(time, 1); row.Children.Add(time);
            var profile = ProfileCombo(entry.ProfileId); profile.Margin = new(0, 0, 7, 0);
            profile.SelectionChanged += (_, _) => { if (profile.SelectedValue is string id) { entry.ProfileId = id; _engine.ScheduleChanged(); } };
            Grid.SetColumn(profile, 2); row.Children.Add(profile);
            var remove = Btn("×", () => { _engine.Settings.Schedule.Remove(entry); _engine.ScheduleChanged(); RenderPage(); }); remove.Padding = new(0); remove.ToolTip = Loc.T("Zaman dilimini kaldır"); Grid.SetColumn(remove, 3); row.Children.Add(remove);
            ViewPanel.Children.Add(row);
        }
        ViewPanel.Children.Add(Btn(Loc.T("＋  Zaman dilimi ekle"), () =>
        {
            if (_engine.Settings.Schedule.Count >= 12) { Toast(Loc.T("En fazla 12 zaman dilimi ekleyebilirsin.")); return; }
            var used = _engine.Settings.Schedule.Select(x => x.Time).ToHashSet();
            string free = Enumerable.Range(0, 24).Select(x => $"{x:00}:00").First(x => !used.Contains(x));
            _engine.Settings.Schedule.Add(new() { Time = free, ProfileId = "focus" }); _engine.ScheduleChanged(); RenderPage();
        }));
        var options = new StackPanel();
        options.Children.Add(SwitchRow(Loc.T("Tam ekranda duraklat"), Loc.T("Oyun veya video tam ekran olduğunda renk filtresini kaldır."), _engine.Settings.PauseFullscreen, value => { _engine.Settings.PauseFullscreen = value; _engine.SettingsChanged(); }));
        options.Children.Add(Divider());
        options.Children.Add(SwitchRow(Loc.T("Mola hatırlatıcısı"), Loc.T("Bildirimi tam ekran uygulamaları bölmeden göster."), _engine.Settings.BreakReminder, value => { _engine.Settings.BreakReminder = value; _engine.SettingsChanged(); }));
        var interval = new ComboBox { ItemsSource = new[] { 15, 20, 30, 45, 60, 90, 120 }.Append(_engine.Settings.BreakMinutes).Distinct().OrderBy(x => x).ToList(), SelectedItem = _engine.Settings.BreakMinutes, Margin = new(0, 10, 0, 0) };
        AutomationProperties.SetName(interval, Loc.T("Mola aralığı dakika"));
        interval.SelectionChanged += (_, _) => { if (interval.SelectedItem is int minutes) { _engine.Settings.BreakMinutes = minutes; _engine.SaveSoon(); } };
        var intervalRow = new DockPanel(); var label = T(Loc.T("Mola aralığı (dakika)"), 11, "#A5AABB", margin: new(0, 17, 10, 0)); DockPanel.SetDock(label, Dock.Left); intervalRow.Children.Add(label); intervalRow.Children.Add(interval); options.Children.Add(intervalRow);
        ViewPanel.Children.Add(Heading(Loc.T("AKILLI DOKUNUŞLAR"), "", new(0, 22, 0, 10))); ViewPanel.Children.Add(Card(options));
        var quick = new WrapPanel { Margin = new(0, 14, 0, 0) };
        foreach (int m in new[] { 15, 30, 60 }) { var b = Btn(Loc.F($"{m} dk duraklat"), () => { _engine.Pause(m); Toast(Loc.F($"Renk filtresi {m} dakika duraklatıldı.")); }); b.Margin = new(0, 0, 7, 7); quick.Children.Add(b); }
        ViewPanel.Children.Add(quick);
    }

    private void RenderScreens()
    {
        PageTitle(Loc.T("Ekranların bir arada."), Loc.T("Donanım, yerleşim ve geçişler. Tek bir yerden."));
        var display = _engine.Displays.Monitors.FirstOrDefault(x => x.Id == _engine.SelectedDisplayId)
            ?? _engine.Displays.Monitors.FirstOrDefault(x => x.Id == _hardwareDisplayId) ?? _engine.Displays.Monitors.FirstOrDefault();
        if (display == null)
        {
            ViewPanel.Children.Add(T(Loc.T("Bağlı ekran bulunamadı."), 13)); ViewPanel.Children.Add(Btn(Loc.T("Yeniden tara"), RefreshClicked)); return;
        }
        _hardwareDisplayId = display.Id;
        var pick = new ComboBox { ItemsSource = _engine.Displays.Monitors, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = display.Id, Margin = new(0, 0, 0, 12) };
        AutomationProperties.SetName(pick, Loc.T("Donanım ayarları yapılacak monitör"));
        pick.SelectionChanged += (_, _) => { if (pick.SelectedValue is string id) { _hardwareDisplayId = id; _engine.SelectedDisplayId = id; RenderDisplays(); RenderPage(); } };
        ViewPanel.Children.Add(pick);
        var hardware = new StackPanel();
        var infoRow = new DockPanel();
        var badge = new Border { Background = Brush(display.Features.Count > 0 ? "#23352D" : "#302D28"), CornerRadius = new(5), Padding = new(7, 4, 7, 4), VerticalAlignment = VerticalAlignment.Top, Child = T(display.Features.Count > 0 ? "DDC/CI" : Loc.T("BİLGİ"), 9, display.Features.Count > 0 ? "#9DD6B5" : "#D9BD95", FontWeights.SemiBold) };
        DockPanel.SetDock(badge, Dock.Right); infoRow.Children.Add(badge);
        var info = new StackPanel(); info.Children.Add(T(display.Name, 15, "#F4EEE5", FontWeights.SemiBold)); info.Children.Add(T(display.Detail + (display.Primary ? " · " + Loc.T("Ana ekran") : ""), 10, "#979FB0", margin: new(0, 5, 0, 0))); infoRow.Children.Add(info); hardware.Children.Add(infoRow);
        hardware.Children.Add(T(Loc.T(display.HardwareStatus), 10, "#A1ABBC", margin: new(0, 12, 0, 9)));
        hardware.Children.Add(Divider());
        foreach (byte code in new byte[] { 0x10, 0x12, 0x62 }) AddHardwareSlider(hardware, display, code);
        if (display.Features.Count == 0)
        {
            hardware.Children.Add(T(Loc.T("Monitörünün fiziksel menüsünde DDC/CI seçeneğini aç. HDMI veya DisplayPort bağlantısı gerekir; bazı dizüstü panelleri ve adaptörler bu özelliği sunmaz."), 11, "#A4AABB", margin: new(0, 7, 0, 8)));
            hardware.Children.Add(Btn(Loc.T("Windows parlaklık ayarları ↗"), () => OpenSettings("ms-settings:display")));
        }
        var rgb = new StackPanel();
        foreach (byte code in new byte[] { 0x16, 0x18, 0x1A }) AddHardwareSlider(rgb, display, code);
        if (rgb.Children.Count > 0) hardware.Children.Add(Expandable(Loc.T("Monitörün RGB kazançları"), rgb));
        if (display.Features.TryGetValue(0x60, out var input) && display.Inputs.Count > 0)
        {
            hardware.Children.Add(T(Loc.T("Giriş kaynağı"), 11, "#ABB0BF", margin: new(0, 12, 0, 8)));
            var sources = new ComboBox { ItemsSource = display.Inputs, DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = input.Current };
            AutomationProperties.SetName(sources, Loc.T("Monitör giriş kaynağı")); hardware.Children.Add(sources);
            hardware.Children.Add(T(Loc.T("Değiştirdiğinde mevcut görüntü kesilebilir. Seçimden sonra uygula."), 10, "#A7A7AB", margin: new(0, 7, 0, 8)));
            hardware.Children.Add(Btn(Loc.T("Giriş kaynağını uygula"), async () =>
            {
                if (sources.SelectedValue is not uint selected || selected == input.Current) return;
                _dialog = true;
                try
                {
                    if (MessageBox.Show(this, Loc.F($"{display.Name}, {DisplayService.InputName(selected)} girişine geçirilecek. Bu bilgisayar o girişe bağlı değilse görüntü kesilir. Devam edilsin mi?"), Loc.T("Giriş kaynağı"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    bool ok = await _engine.Displays.SetHardwareAsync(display.Id, 0x60, selected); Toast(ok ? Loc.T("Giriş değişikliği monitöre gönderildi.") : Loc.T("Monitör giriş değişikliğini kabul etmedi."));
                }
                finally { _dialog = false; }
            }));
        }
        hardware.Children.Add(T(Loc.T("Donanım ayarları doğrudan monitöre uygulanır; renk profillerinden bağımsızdır."), 10, "#7F899E", margin: new(0, 12, 0, 0)));
        ViewPanel.Children.Add(Card(hardware));
        if (display.WindowsBrightnessInstance != null && !display.Features.ContainsKey(0x10))
        {
            var windows = new StackPanel(); windows.Children.Add(T(Loc.T("Windows panel parlaklığı"), 12));
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = display.WindowsBrightnessPercent ?? 50 };
            AutomationProperties.SetName(slider, Loc.T("Windows gerçek ekran parlaklığı"));
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) }; _viewTimers.Add(timer);
            slider.ValueChanged += (_, _) => { timer.Stop(); timer.Start(); };
            timer.Tick += async (_, _) => { timer.Stop(); bool ok = await _engine.Displays.SetBrightnessAsync(display.Id, slider.Value); if (!ok) Toast(Loc.T("Windows parlaklık ayarı doğrulanamadı.")); else ReportSetting(Loc.T("Windows panel parlaklığı"), "windows-brightness"); };
            windows.Children.Add(slider); ViewPanel.Children.Add(Card(windows));
        }
        RenderTuning(display);
        ViewPanel.Children.Add(Heading(Loc.T("SAĞDAN SOLA, KESİNTİSİZ"), Loc.T("Ekran yerleşimi"), new(0, 22, 0, 10)));
        RenderLayout();
        var transitions = new StackPanel();
        transitions.Children.Add(SwitchRow(Loc.T("Dış kenarlarda imleci sar"), Loc.T("En sağ kenardan en sola, en soldan en sağa geç."), _engine.Settings.WrapCursor, value => { _engine.Settings.WrapCursor = value; _engine.SettingsChanged(); }));
        transitions.Children.Add(T(Loc.T("Normal ekran geçişleri Windows yerleşimini izler. Farklı çözünürlüklerde geçiş, ekranların ortak kenarında yapılır."), 10, "#8B95A8", margin: new(0, 11, 0, 0)));
        ViewPanel.Children.Add(Card(transitions, new(0, 12, 0, 0)));
        RenderGpu();
        var actions = new WrapPanel { Margin = new(0, 16, 0, 0) };
        var identify = Btn(Loc.T("Ekranları tanımla"), IdentifyMonitors); identify.Margin = new(0, 0, 8, 6); actions.Children.Add(identify);
        var refresh = Btn(Loc.T("↻  Yeniden tara"), RefreshClicked); refresh.Margin = new(0, 0, 8, 6); actions.Children.Add(refresh); ViewPanel.Children.Add(actions);
    }

    private void AddHardwareSlider(StackPanel panel, Display display, byte code)
    {
        if (!display.Features.TryGetValue(code, out var feature) || feature.Maximum == 0) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) }; _viewTimers.Add(timer);
        var status = T("", 10, "#A8BAAA");
        // Hardware sliders use the monitor's actual range; never assume it is 0–100.
        double minimum = code == 0x10 ? Math.Min(feature.Current, Math.Max(1, Math.Ceiling(feature.Maximum * .02))) : 0;
        var slider = AddSlider(panel, feature.Name, code == 0x62 ? "\uE767" : "\uE7F4", feature.Current, minimum, feature.Maximum,
            v => Loc.Percent(v / feature.Maximum * 100), _ => { timer.Stop(); timer.Start(); }, 1);
        AutomationProperties.SetName(slider, Loc.F($"{display.Name} donanım {Loc.T(feature.Name)}"));
        panel.Children.Add(status);
        bool applying = false; uint? pending = null;
        async Task ApplyPending()
        {
            if (applying) return; applying = true;
            try
            {
                while (pending.HasValue)
                {
                    var value = pending.Value; pending = null;
                    status.Text = Loc.T("Monitöre gönderiliyor…");
                    bool ok = await _engine.Displays.SetHardwareAsync(display.Id, code, value);
                    status.Text = ok ? Loc.T("Monitörden doğrulandı") : Loc.T("Monitör doğrulamadı; ayarını monitör menüsünden kontrol et.");
                    if (ok) ReportSetting(Loc.F($"{display.Name} donanım {Loc.T(feature.Name)}"), "hardware");
                    status.Foreground = Brush(ok ? "#8ABB9E" : "#E4B77E");
                }
            }
            catch (Exception e) { Log.Write(e); status.Text = Loc.T("Monitöre erişilemedi. Yeniden taramayı dene."); }
            finally { applying = false; }
        }
        timer.Tick += async (_, _) => { timer.Stop(); pending = (uint)Math.Round(slider.Value); await ApplyPending(); };
    }

    private void RenderLayout()
    {
        var monitors = _engine.Displays.Monitors;
        if (_layoutOrder.Count != monitors.Count || !_layoutOrder.All(id => monitors.Any(x => x.Id == id))) _layoutOrder = monitors.OrderBy(x => x.Bounds.Left).ThenBy(x => x.Bounds.Top).Select(x => x.Id).ToList();
        if (_layoutSelected == null || !_layoutOrder.Contains(_layoutSelected)) _layoutSelected = _layoutOrder.FirstOrDefault();
        var panel = new StackPanel();
        panel.Children.Add(T(Loc.T("Bir ekranı seç, oklarla soldaki veya sağdaki yerine taşı. Uygula dediğinde Windows yerleşimi de değişir."), 11, "#A3AABD", margin: new(0, 0, 0, 14)));
        var strip = new UniformGrid { Columns = Math.Max(1, monitors.Count), Height = 80 };
        foreach (var id in _layoutOrder)
        {
            var d = monitors.First(x => x.Id == id); var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(T(d.Number.ToString(), 24, id == _layoutSelected ? "#FFD297" : "#A4ADBD", FontWeights.SemiBold));
            text.Children.Add(T(d.Primary ? Loc.T("ANA EKRAN") : $"{d.Bounds.Width} × {d.Bounds.Height}", 8, "#9A9EAB", margin: new(0, 3, 0, 0)));
            var b = new ToggleButton { Content = text, Margin = new(3), IsChecked = id == _layoutSelected, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.ToolTip = d.Name; AutomationProperties.SetName(b, Loc.F($"Yerleşimde ekran {d.Number}"));
            b.Click += (_, _) => { _layoutSelected = id; RenderPage(); };
            strip.Children.Add(b);
        }
        panel.Children.Add(strip);
        var buttons = new Grid { Margin = new(0, 13, 0, 0) }; buttons.ColumnDefinitions.Add(new()); buttons.ColumnDefinitions.Add(new()); buttons.ColumnDefinitions.Add(new() { Width = new(130) });
        var left = Btn(Loc.T("←  Sola"), () => MoveLayout(-1)); var right = Btn(Loc.T("Sağa  →"), () => MoveLayout(1));
        left.Margin = new(0, 0, 6, 0); right.Margin = new(0, 0, 6, 0); left.IsEnabled = monitors.Count > 1 && !_layoutBusy && _layoutBackup == null; right.IsEnabled = left.IsEnabled;
        var apply = Btn(Loc.T("Yerleşimi uygula"), ApplyLayout); apply.IsEnabled = left.IsEnabled; apply.Background = Brush("#FFC681"); apply.Foreground = Brush("#2A2117"); apply.BorderBrush = Brush("#FFC681");
        Grid.SetColumn(right, 1); Grid.SetColumn(apply, 2); buttons.Children.Add(left); buttons.Children.Add(right); buttons.Children.Add(apply); panel.Children.Add(buttons);
        if (monitors.Count < 2) panel.Children.Add(T(Loc.T("Bu özellik için ikinci bir ekran bağla."), 10, "#A09B92", margin: new(0, 10, 0, 0)));
        var confirmPanel = new StackPanel(); _layoutCounter = T("", 12, "#FFD49A", FontWeights.SemiBold); confirmPanel.Children.Add(_layoutCounter);
        var confirmation = new WrapPanel { Margin = new(0, 9, 0, 0) };
        var keep = Btn(Loc.T("Bu yerleşimi koru"), () => { _layoutCountdown.Stop(); _layoutBackup = null; if (_layoutConfirm != null) _layoutConfirm.Visibility = Visibility.Collapsed; RenderPage(); Toast(Loc.T("Yeni ekran yerleşimi kaydedildi."), true); }); keep.Margin = new(0, 0, 8, 0); confirmation.Children.Add(keep); confirmation.Children.Add(Btn(Loc.T("Geri al"), RevertLayout)); confirmPanel.Children.Add(confirmation);
        _layoutConfirm = new Border { Background = Brush("#373024"), Padding = new(12), CornerRadius = new(9), Margin = new(0, 12, 0, 0), Child = confirmPanel, Visibility = _layoutBackup == null ? Visibility.Collapsed : Visibility.Visible }; panel.Children.Add(_layoutConfirm);
        panel.Children.Add(Btn(Loc.T("Windows ekran ayarları ↗"), () => OpenSettings("ms-settings:display"), new(0, 12, 0, 0)));
        ViewPanel.Children.Add(Card(panel));
    }
    private void MoveLayout(int direction)
    {
        int i = _layoutOrder.IndexOf(_layoutSelected ?? ""); int next = i + direction;
        if (i < 0 || next < 0 || next >= _layoutOrder.Count) return;
        (_layoutOrder[i], _layoutOrder[next]) = (_layoutOrder[next], _layoutOrder[i]); RenderPage();
    }
    private async void ApplyLayout()
    {
        if (_layoutBusy || _layoutBackup != null) return; _layoutBusy = true;
        try
        {
            _layoutBackup = await _engine.Displays.ArrangeAsync(_layoutOrder.ToList());
            _layoutDeadline = DateTime.Now.AddSeconds(15); _layoutCountdown.Start();
            RenderPage(); Toast(Loc.T("15 saniye içinde onaylamazsan eski yerleşim geri gelir."));
        }
        catch (Exception e) { Log.Write(e); Toast(e.Message); }
        finally { _layoutBusy = false; }
    }
    private void RevertLayout()
    {
        _layoutCountdown.Stop(); var backup = _layoutBackup; _layoutBackup = null;
        if (backup == null) return;
        DisplayService.RestoreLayout(backup); _layoutOrder.Clear();
        if (_layoutConfirm != null) _layoutConfirm.Visibility = Visibility.Collapsed;
        Toast(Loc.T("Önceki ekran yerleşimi geri yüklendi."), true); RenderPage();
    }
    private void RenderGpu()
    {
        ViewPanel.Children.Add(Heading(Loc.T("GRAFİK KARTIN"), Loc.T("Doğrudan sürücü kontrolü"), new(0, 22, 0, 10)));
        var gpu = new StackPanel();
        bool nvidia = _engine.Displays.Gpus.Any(x => x.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        gpu.Children.Add(T(string.Join("\n", _engine.Displays.Gpus.DefaultIfEmpty(Loc.T("Grafik kartı bilgisi alınamadı"))), 13, "#DDE5DD", FontWeights.SemiBold));
        gpu.Children.Add(T(Loc.T("Dijital canlılık, renk tonu, RGB çıkışı, bit derinliği ve 3D tercihlerini NVIDIA sekmesinden yönet."), 11, "#959FAF", margin: new(0, 9, 0, 12)));
        if (nvidia)
        {
            var open = Btn(Loc.T("NVIDIA ayarlarını Luma'da aç"), () => { _page = 3; RenderNavigation(); RenderPage(); }); open.BorderBrush = Brush("#4C6442"); open.Foreground = Brush("#B4D79B"); gpu.Children.Add(open);
        }
        else gpu.Children.Add(T(Loc.T("Bu bilgisayarda etkin bir NVIDIA ekran kartı algılanmadı."), 10, "#9AA2B0"));
        gpu.Children.Add(Btn(Loc.T("Windows gelişmiş ekran ayarları ↗"), () => OpenSettings("ms-settings:display-advanced"), new(0, 8, 0, 0)));
        gpu.Children.Add(Btn(Loc.T("Uygulama başına GPU tercihi ↗"), () => OpenSettings("ms-settings:display-advancedgraphics"), new(0, 8, 0, 0)));
        ViewPanel.Children.Add(Card(gpu));
    }

    private void RenderSettings()
    {
        PageTitle(Loc.T("İnce düşünülmüş."), Loc.T("Luma senin alışkanlıklarına uysun."));
        RenderLanguageSettings();
        var system = new StackPanel();
        system.Children.Add(SwitchRow(Loc.T("Windows ile başlat"), Loc.T("Oturum açınca sessizce sistem tepsisinde başla."), SystemIntegration.StartupEnabled, value =>
        {
            try { SystemIntegration.SetStartup(value); Toast(value ? Loc.T("Windows açılışında sistem tepsisinde başlayacak.") : Loc.T("Otomatik başlatma kapatıldı.")); }
            catch (Exception e) { Log.Write(e); Toast(Loc.T("Başlangıç tercihi kaydedilemedi.")); RenderPage(); }
        }));
        system.Children.Add(Divider());
        system.Children.Add(SwitchRow(Loc.T("Paneli açık tut"), Loc.T("Başka yere tıkladığında da Luma görünür kalsın."), _engine.Settings.Pinned, value => { _engine.Settings.Pinned = value; _engine.SettingsChanged(); }));
        system.Children.Add(Divider());
        system.Children.Add(SwitchRow(Loc.T("Genel klavye kısayolları"), Loc.T("Luma arka plandayken de kontrol sende."), _engine.Settings.Hotkeys, value => { _engine.Settings.Hotkeys = value; _engine.SettingsChanged(); RegisterHotkeys(); RenderPage(); }));
        ViewPanel.Children.Add(Card(system));
        ViewPanel.Children.Add(Heading(Loc.T("ELİNİN ALTINDA"), Loc.T("Klavye kısayolları"), new(0, 22, 0, 10)));
        var keys = new StackPanel();
        foreach (var (key, action) in new[] { ("Ctrl + Alt + L", Loc.T("Paneli aç / kapat")), ("Ctrl + Alt + P", Loc.T("Duraklat / devam et")), ("Ctrl + Alt + 0", Loc.T("Yazılımsal renkleri sıfırla")), ("Ctrl + Alt + 1…6", Loc.T("Hazır modları seç")), ("Esc", Loc.T("Paneli tepsiye küçült")) })
        {
            var row = new DockPanel { Margin = new(0, 4, 0, 8) }; var hotkey = T(key, 11, "#D4BEA1", FontWeights.SemiBold); DockPanel.SetDock(hotkey, Dock.Right); row.Children.Add(hotkey); row.Children.Add(T(action, 11, "#ADB2C0")); keys.Children.Add(row);
        }
        if (_hotkeyFailures.Count > 0) keys.Children.Add(T(Loc.F($"Başka uygulama kullanıyor: {string.Join(", ", _hotkeyFailures)}"), 10, "#EAC18D", margin: new(0, 5, 0, 0)));
        ViewPanel.Children.Add(Card(keys));
        ViewPanel.Children.Add(Heading(Loc.T("PROFİLLERİN VE YEDEKLERİN"), Loc.T("Yalnızca bu bilgisayarda"), new(0, 22, 0, 10)));
        var backup = new StackPanel();
        var rowBackup = new UniformGrid { Columns = 2 };
        rowBackup.Children.Add(Btn(Loc.T("Ayarları dışa aktar"), ExportSettings, new(0, 0, 5, 0)));
        rowBackup.Children.Add(Btn(Loc.T("Yedekten içe aktar"), ImportSettings, new(5, 0, 0, 0))); backup.Children.Add(rowBackup);
        foreach (var profile in _engine.Settings.CustomProfiles.ToList())
        {
            var row = new DockPanel { Margin = new(0, 12, 0, 0) }; var remove = Btn(Loc.T("Kaldır"), () =>
            {
                if (_engine.Settings.Schedule.Any(x => x.ProfileId == profile.Id)) { Toast(Loc.T("Bu profil zaman planında kullanılıyor. Önce o zaman dilimini değiştir.")); return; }
                _engine.Settings.CustomProfiles.Remove(profile); _engine.SaveSoon(); RenderPage();
            }); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove); row.Children.Add(T(profile.DisplayName, 12, "#D0D1D9", margin: new(0, 8, 8, 0))); backup.Children.Add(row);
        }
        ViewPanel.Children.Add(Card(backup));
        var info = new StackPanel();
        info.Children.Add(T("luma", 24, "#F3D3AC", FontWeights.SemiBold)); info.Children.Add(T(Loc.F($"{typeof(App).Assembly.GetName().Version!.ToString(3)}  ·  Windows 10 / 11  ·  Çevrimdışı"), 10, "#A0A8BA", margin: new(0, 5, 0, 12)));
        info.Children.Add(T(Loc.T("Renk filtresi SDR ekranlarda sürücü desteğiyle çalışır. HDR / gelişmiş renk açıkken güvenli şekilde atlanır. Windows Gece Işığı veya başka bir renk uygulaması aynı ayarları değiştirebilir."), 11, "#929CAE"));
        info.Children.Add(T(Loc.T("Luma'dan çıkınca başlangıçtaki renk eğrisi geri yüklenir. Monitörün donanım ayarları monitörde kalır. Luma bilgi toplamaz. İsteğe bağlı RTSS kurulumu internet gerektirir."), 11, "#929CAE", margin: new(0, 9, 0, 12)));
        info.Children.Add(Btn(Loc.T("Ayar klasörünü aç ↗"), () => { Directory.CreateDirectory(AppSettings.Folder); if (!SystemIntegration.Open(AppSettings.Folder)) Toast(Loc.T("Ayar klasörü açılamadı.")); }));
        ViewPanel.Children.Add(Card(info, new(0, 16, 0, 0)));
        var exit = new UniformGrid { Columns = 2, Margin = new(0, 14, 0, 0) };
        exit.Children.Add(Btn(Loc.T("Filtreyi sıfırla"), () => { _engine.Reset(); RenderPage(); Toast(Loc.T("Yazılımsal renkler sıfırlandı. Otomasyon ve imleç sarma kapatıldı.")); }, new(0, 0, 5, 0)));
        exit.Children.Add(Btn(Loc.T("Luma'dan çık"), async () => await _exit(), new(5, 0, 0, 0))); ViewPanel.Children.Add(exit);
    }

    private void ExportSettings()
    {
        _dialog = true;
        try
        {
            var dialog = new SaveFileDialog { Filter = Loc.T("Luma ayarları (*.json)|*.json"), FileName = $"luma-yedek-{DateTime.Now:yyyy-MM-dd}.json", Title = Loc.T("Luma ayarlarını yedekle") };
            if (dialog.ShowDialog(this) == true) { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_engine.Settings, AppSettings.JsonOptions)); Toast(Loc.T("Ayarların yedeklendi.")); }
        }
        catch (Exception e) { Log.Write(e); Toast(Loc.T("Yedek kaydedilemedi. Dosya konumunu kontrol et.")); }
        finally { _dialog = false; }
    }
    private void ImportSettings()
    {
        _dialog = true;
        try
        {
            var dialog = new OpenFileDialog { Filter = Loc.T("Luma ayarları (*.json)|*.json"), Title = Loc.T("Luma yedeğini seç") };
            if (dialog.ShowDialog(this) != true) return;
            if (new FileInfo(dialog.FileName).Length > 1_000_000) throw new InvalidDataException();
            var imported = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(dialog.FileName));
            if (imported == null || imported.Displays == null || imported.CustomProfiles == null || imported.Schedule == null || imported.CustomProfiles.Count > 12 || imported.Schedule.Count > 12) throw new InvalidDataException();
            var profiles = Profile.Defaults().Select(x => x.Id).Concat(TuningService.AllScenes(_engine).Select(x => x.Id)).Concat(imported.CustomProfiles.Select(x => x.Id)).ToHashSet();
            if (imported.CustomProfiles.Any(x => x.Color == null || string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 28) || imported.CustomProfiles.Select(x => x.Id).Distinct().Count() != imported.CustomProfiles.Count || imported.CustomProfiles.Any(x => Profile.Defaults().Any(p => p.Id == x.Id) || TuningService.FindScene(_engine, x.Id) != null)) throw new InvalidDataException();
            if (imported.Schedule.Any(x => !TimeOnly.TryParseExact(x.Time, "HH:mm", out _) || !profiles.Contains(x.ProfileId))) throw new InvalidDataException();
            if (imported.NvidiaDisplays != null && imported.NvidiaDisplays.Values.Any(x => x == null || x.Vibrance is double v && (!double.IsFinite(v) || v < 0 || v > 100) || x.Hue is int h && (h < 0 || h > 359))) throw new InvalidDataException();
            if (!TimeOnly.TryParseExact(imported.DayStarts, "HH:mm", out _) || !TimeOnly.TryParseExact(imported.EveningStarts, "HH:mm", out _) || imported.DayStarts == imported.EveningStarts || imported.GameExecutables == null || imported.IgnoredGameExecutables == null || imported.GameExecutables.Count > 100 || imported.IgnoredGameExecutables.Count > 100 || imported.GameExecutables.Concat(imported.IgnoredGameExecutables).Any(x => string.IsNullOrWhiteSpace(x))) throw new InvalidDataException();
            _engine.Settings.CustomProfiles = imported.CustomProfiles.Select(x => x with { Color = x.Color.Safe() }).ToList();
            _engine.Settings.Schedule = imported.Schedule;
            _engine.Settings.NvidiaDisplays = imported.NvidiaDisplays ?? new();
            _engine.Settings.SecondarySceneId = null;
            _engine.Settings.PrimarySceneId = null;
            _engine.Settings.MonitorProfiles = (imported.MonitorProfiles ?? new()).Where(x => ProfilePlanner.Valid(x.Value, x.Key)).ToDictionary(x => x.Key, x => x.Value);
            _engine.Settings.ActiveMonitorScenes.Clear();
            _engine.Settings.ScheduleEnabled = imported.ScheduleEnabled;
            _engine.Settings.AdaptiveEnabled = imported.AdaptiveEnabled;
            _engine.Settings.DayStarts = imported.DayStarts; _engine.Settings.EveningStarts = imported.EveningStarts;
            _engine.Settings.GameExecutables = imported.GameExecutables.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _engine.Settings.IgnoredGameExecutables = imported.IgnoredGameExecutables.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _engine.Settings.SmoothTransitions = imported.SmoothTransitions; _engine.Settings.PauseFullscreen = imported.PauseFullscreen;
            _engine.Settings.BreakReminder = imported.BreakReminder; _engine.Settings.BreakMinutes = Math.Clamp(imported.BreakMinutes, 15, 120);
            foreach (var item in imported.Displays) if (item.Value != null) _engine.Settings.Displays[item.Key] = item.Value.Safe();
            _engine.AdaptiveSettingsChanged(); _engine.ScheduleChanged(); _ = _engine.RefreshAsync(); RenderPage(); Toast(Loc.T("Yedek içe aktarıldı. Başlangıç ve kısayol tercihlerin korunur."));
        }
        catch (Exception e) { Log.Write(e); Toast(Loc.T("Geçerli bir Luma yedeği seç. Dosya okunamadı veya biçimi uygun değil.")); }
        finally { _dialog = false; }
    }
    private async void RefreshClicked()
    {
        if (_engine.Refreshing) return;
        Toast(Loc.T("Ekranlar ve donanım özellikleri taranıyor…"));
        try { await _engine.RefreshAsync(); RenderDisplays(); RenderPage(); Toast(Loc.F($"{_engine.Displays.Monitors.Count} ekran algılandı.")); }
        catch (Exception e) { Log.Write(e); Toast(Loc.T("Ekranlar taranamadı. Bağlantıları kontrol et.")); }
    }
    private void IdentifyMonitors()
    {
        foreach (var d in _engine.Displays.Monitors)
        {
            double scale = GetDpiForMonitor(d.MonitorHandle, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
            var label = new StackPanel { Margin = new(28, 18, 28, 18) };
            label.Children.Add(T(d.Number.ToString(), 64, "#FFC681", FontWeights.SemiBold)); label.Children.Add(T(d.Name, 14, "#E9E3DB"));
            var overlay = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Topmost = true, SizeToContent = SizeToContent.WidthAndHeight, Content = new Border { Background = Brush("#E8171A22"), BorderBrush = Brush("#806A4D"), BorderThickness = new(1), CornerRadius = new(18), Child = label }, Left = d.Bounds.Left / scale + 28, Top = d.Bounds.Top / scale + 28 };
            overlay.Show(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; timer.Tick += (_, _) => { timer.Stop(); overlay.Close(); }; timer.Start();
        }
    }
    private void OpenSettings(string uri) { if (SystemIntegration.Open(uri)) Hide(); else Toast(Loc.T("Windows ayarları açılamadı.")); }
    private void RegisterHotkeys()
    {
        UnregisterHotkeys(); _hotkeyFailures.Clear(); if (!_engine.Settings.Hotkeys) return;
        var handle = _engine.WindowHandle; if (handle == IntPtr.Zero) return;
        var definitions = new List<(int Id, uint Key, string Name)> { (1, 0x4C, "Ctrl+Alt+L"), (2, 0x50, "Ctrl+Alt+P"), (3, 0x30, "Ctrl+Alt+0") };
        for (int i = 0; i < 6; i++) definitions.Add((10 + i, (uint)(0x31 + i), $"Ctrl+Alt+{i + 1}"));
        foreach (var d in definitions) { if (Native.RegisterHotKey(handle, d.Id, 0x4003, d.Key)) _registeredHotkeys.Add(d.Id); else _hotkeyFailures.Add(d.Name); }
    }
    private void UnregisterHotkeys() { foreach (int id in _registeredHotkeys) Native.UnregisterHotKey(_engine.WindowHandle, id); _registeredHotkeys.Clear(); }
    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x312) return IntPtr.Zero; handled = true;
        int id = wParam.ToInt32();
        if (id == 1) ToggleVisible(); else if (id == 2) _engine.TogglePause(); else if (id == 3) { _engine.Reset(); if (IsVisible) RenderPage(); }
        else if (id >= 10 && id < 16) { var profile = Profile.Defaults()[id - 10]; _engine.SelectedDisplayId = null; _engine.SelectProfile(profile.Id); if (IsVisible) { RenderDisplays(); RenderPage(); } }
        return IntPtr.Zero;
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static TextBlock T(string text, double size = 12, string color = "#F3EFE9", FontWeight? weight = null, Thickness? margin = null) => new()
    {
        Text = text, FontSize = size, Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new(0), VerticalAlignment = VerticalAlignment.Center
    };
    private static TextBlock Glyph(string glyph, double size, string color, HorizontalAlignment align = HorizontalAlignment.Center) => new() { Text = glyph, FontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, Foreground = Brush(color), HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center };
    private static Border Card(UIElement child, Thickness? margin = null) => new() { Background = Brush("#1B1E26"), BorderBrush = Brush("#30333D"), BorderThickness = new(1), CornerRadius = new(12), Padding = new(15), Child = child, Margin = margin ?? new(0) };
    private static Border Divider() => new() { Height = 1, Background = Brush("#30333D"), Margin = new(0, 12, 0, 12) };
    private static Button Btn(string label, Action action, Thickness? margin = null)
    {
        var b = new Button { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = margin ?? new(0) };
        b.Click += (_, _) => action(); return b;
    }
    private static Grid Heading(string title, string detail, Thickness? margin = null)
    {
        var grid = new Grid { Margin = margin ?? new(0, 0, 0, 10) }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(T(title, 9, "#B0B1BB", FontWeights.SemiBold)); var right = T(detail, 9, "#7F889C"); right.MaxWidth = 170; right.TextAlignment = TextAlignment.Right; right.Margin = new(8, 0, 0, 0); Grid.SetColumn(right, 1); grid.Children.Add(right); return grid;
    }
    private void PageTitle(string title, string subtitle)
    {
        ViewPanel.Children.Add(T(title, 25, "#F3EEE7", FontWeights.SemiBold, new(0, 4, 0, 6))); ViewPanel.Children.Add(T(subtitle, 12, "#969EB0", margin: new(0, 0, 0, 21)));
    }
    private static Border Expandable(string title, UIElement body)
    {
        var container = new StackPanel(); var header = new DockPanel();
        var arrow = T("＋", 15, "#AAA28F"); DockPanel.SetDock(arrow, Dock.Right); header.Children.Add(arrow); header.Children.Add(T(title, 11, "#AFB2BD"));
        var button = new Button { Content = header, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0, 12, 0, 12) };
        var wrapper = new Border { Child = body, Visibility = Visibility.Collapsed, Padding = new(0, 5, 0, 9) };
        button.Click += (_, _) => { bool show = wrapper.Visibility != Visibility.Visible; wrapper.Visibility = show ? Visibility.Visible : Visibility.Collapsed; arrow.Text = show ? "−" : "＋"; };
        container.Children.Add(button); container.Children.Add(wrapper);
        return new Border { Child = container, BorderBrush = Brush("#30333D"), BorderThickness = new(0, 0, 0, 1), Margin = new(0, 2, 0, 0) };
    }
    private ToggleButton NewSwitch(bool value, string label, Action<bool> changed)
    {
        var toggle = new ToggleButton { Style = (Style)FindResource("Switch"), IsChecked = value, Margin = new(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, Loc.T(label)); toggle.Click += (_, _) => changed(toggle.IsChecked == true); return toggle;
    }
    private DockPanel SwitchRow(string title, string description, bool value, Action<bool> changed)
    {
        var row = new DockPanel(); var toggle = NewSwitch(value, title, changed); DockPanel.SetDock(toggle, Dock.Right); row.Children.Add(toggle);
        var text = new StackPanel(); text.Children.Add(T(title, 12, "#E9E7E3", FontWeights.SemiBold)); text.Children.Add(T(description, 10, "#909BAD", margin: new(0, 5, 0, 0))); row.Children.Add(text); return row;
    }
    private static Slider AddSlider(StackPanel panel, string name, string icon, double value, double min, double max, Func<double, string> format, Action<double> onChange, double tick, string? left = null, string? right = null)
    {
        var group = new StackPanel { Margin = new(0, 2, 0, 8) }; var header = new DockPanel(); var readout = T(format(value), 12, "#F2D2A9", FontWeights.SemiBold); DockPanel.SetDock(readout, Dock.Right); header.Children.Add(readout);
        var caption = new StackPanel { Orientation = Orientation.Horizontal }; var glyph = Glyph(icon, 13, "#AAAFAF"); glyph.Margin = new(0, 0, 8, 0); caption.Children.Add(glyph); caption.Children.Add(T(Loc.T(name), 11, "#B5BBC8")); header.Children.Add(caption); group.Children.Add(header);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), TickFrequency = tick, SmallChange = tick, LargeChange = tick * 10, Margin = new(0, 6, 0, 0) };
        AutomationProperties.SetName(slider, Loc.T(name));
        slider.ValueChanged += (_, _) => { readout.Text = format(slider.Value); onChange(slider.Value); }; group.Children.Add(slider);
        if (left != null)
        {
            var labels = new DockPanel(); var rightText = T(right ?? "", 9, "#7E879B"); DockPanel.SetDock(rightText, Dock.Right); labels.Children.Add(rightText); labels.Children.Add(T(left, 9, "#7E879B")); group.Children.Add(labels);
        }
        panel.Children.Add(group); return slider;
    }
    private ComboBox ProfileCombo(string selected) => new() { ItemsSource = _engine.Profiles, DisplayMemberPath = "DisplayName", SelectedValuePath = "Id", SelectedValue = selected };
}
