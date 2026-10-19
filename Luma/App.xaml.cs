using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Luma;

public partial class App : Application
{
    private Mutex? _mutex;
    private Forms.NotifyIcon? _tray;
    private CancellationTokenSource? _pipeCancellation;
    private MainWindow? _panel;
    private Engine? _engine;
    private ProfileIndicator? _indicator;
    private GameOverlay? _gameOverlay;
    internal RtssSetupState? PreviewSetupState { get; set; }
    internal string GameOverlayStatus => PreviewSetupState is { } preview ? RtssSetup.StatusFor(preview) : _gameOverlay?.Status ?? "Tam ekran bildirimi hazırlanıyor…";
    internal RtssSetupState GameOverlaySetupState => PreviewSetupState ?? _gameOverlay?.SetupState ?? RtssSetupState.NotChecked;
    internal bool GameOverlaySetupBusy => GameOverlaySetupState is RtssSetupState.Deferred or RtssSetupState.Installing or RtssSetupState.Connecting;
    internal bool GameOverlayInstallMissing => PreviewSetupState is { } preview ? preview != RtssSetupState.Ready : _gameOverlay?.InstallMissing == true;
    internal void ApproveGameOverlaySetup() { if (_overlayInitialized) _gameOverlay?.ApproveSetup(); }
    internal void PrepareGameOverlay() => _gameOverlay?.Prepare(retry: true);
    private bool _overlayInitialized, _setupOfferShown;
    private void OfferGameOverlaySetup()
    {
        if (!_overlayInitialized || _setupOfferShown || _exiting || _panel?.IsVisible != true || _engine == null
            || _gameOverlay?.SetupState != RtssSetupState.NeedsConsent || _engine.Games.Running.Count > 0 || _engine.IsFullscreen()) return;
        _setupOfferShown = true; _panel.ShowGameOverlaySetup();
    }
    private bool _exiting;
    private readonly string _pipeName = "Luma-" + Environment.UserName;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Loc.Set("auto");
        if (e.Args.Contains("--render-test"))
        {
            try
            {
                string directory = e.Args.SkipWhile(x => x != "--render-test").Skip(1).FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, "artifacts");
                string language = e.Args.SkipWhile(x => x != "--language").Skip(1).FirstOrDefault() ?? "tr";
                Loc.Set(language);
                PreviewSetupState = RtssSetupState.NeedsConsent;
                var previewSettings = new AppSettings { Language = language };
                using var previewEngine = new Engine(previewSettings, preview: true);
                await previewEngine.Displays.RefreshAsync(true);
                foreach (var d in previewEngine.Displays.Monitors) previewSettings.Displays[d.Id] = new();
                await previewEngine.RefreshNvidiaAsync();
                previewEngine.PreparePreviewProfiles();
                var previewWindow = new MainWindow(previewEngine, () => Task.CompletedTask);
                previewWindow.InitializedUi(); previewWindow.RenderPreviews(Path.GetFullPath(directory));
                previewWindow.CheckLanguageSwitches();
                var indicator = new ProfileIndicator();
                indicator.RenderPreview(Path.Combine(directory, "06-indicator.png")); indicator.Close();
                previewWindow.AllowClose();
                File.WriteAllText(Path.Combine(directory, "render-test.txt"), "PASS: Kontrol, otomasyon, donanım, NVIDIA ve ayarlar WPF görsel ağaçlarından oluşturuldu. Masaüstü girdisi gönderilmedi.");
            }
            catch (Exception error) { Log.Write(error); Environment.ExitCode = 1; }
            Shutdown(Environment.ExitCode); return;
        }
        _mutex = new Mutex(true, @"Local\Luma-" + Environment.UserName, out bool first);
        if (!first)
        {
            await SendCommandAsync(e.Args.Contains("--quit") ? "quit" : e.Args.Contains("--reset") ? "reset" : e.Args.Contains("--enable-adaptive") ? "adaptive:on" : e.Args.Contains("--disable-adaptive") ? "adaptive:off" : e.Args.Contains("--restore-primary") ? "restore-primary" : e.Args.Contains("--restore-secondary") ? "restore-secondary" : e.Args.Contains("--dell-evening") ? "scene:dell-evening" : e.Args.Contains("--dell-gaming") ? "scene:dell-gaming" : e.Args.Contains("--scene-evening") ? "scene:lg-evening" : e.Args.Contains("--scene-gaming") ? "scene:lg-gaming" : "show");
            Shutdown(); return;
        }
        if (e.Args.Contains("--quit")) { Shutdown(); return; }
        DispatcherUnhandledException += (_, args) => { Log.Write(args.Exception); args.Handled = true; _tray?.ShowBalloonTip(4000, "Luma", Loc.T("Bir işlem tamamlanamadı. Ayrıntı luma.log dosyasına yazıldı."), Forms.ToolTipIcon.Warning); };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => { try { _engine?.Dispose(); } catch { } };
        try
        {
            _engine = new Engine(AppSettings.Load());
            Loc.Set(_engine.Settings.Language);
            if (e.Args.Contains("--enable-adaptive")) { _engine.Settings.AdaptiveEnabled = true; _engine.Settings.ScheduleEnabled = false; }
            if (e.Args.Contains("--disable-adaptive")) _engine.Settings.AdaptiveEnabled = false;
            _panel = new MainWindow(_engine, ExitAsync);
            _panel.IsVisibleChanged += (_, _) => { WriteRuntimeStatus(); OfferGameOverlaySetup(); };
            MainWindow = _panel;
            CreateTray();
            _gameOverlay = new GameOverlay(_engine, Dispatcher);
            _gameOverlay.Changed += () => { _panel.RefreshGameOverlayStatus(); WriteRuntimeStatus(); OfferGameOverlaySetup(); };
            _engine.Notify += (title, text) => _tray?.ShowBalloonTip(5000, Loc.T(title), Loc.Status(text), Forms.ToolTipIcon.Info);
            _engine.ProfileApplied += ShowIndicator;
            _panel.SettingApplied += ShowIndicator;
            _engine.Changed += () => { if (!_engine.Settings.ProfileNotifications) _indicator?.Dismiss(); if (!_engine.Settings.ProfileNotifications || !_engine.Settings.GameOverlay) _gameOverlay?.Prepare(); };
            Loc.Changed += UpdateLanguage;
            _engine.Changed += UpdateTray;
            _engine.Displays.HardwareLoaded += () => Dispatcher.BeginInvoke(WriteRuntimeStatus);
            _engine.Nvidia.Changed += () => Dispatcher.BeginInvoke(WriteRuntimeStatus);
            _pipeCancellation = new(); _ = ListenAsync(_pipeCancellation.Token);
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.PowerModeChanged += OnPowerChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SessionEnding += (_, _) => { _engine?.Dispose(); };
            // Create the message source even when startup is hidden, so global hotkeys work.
            _panel.EnsureHandle();
            if (!e.Args.Contains("--tray")) _panel.Reveal();
            await _engine.InitializeAsync();
            _panel.InitializedUi();
            WriteRuntimeStatus();
            if (e.Args.Contains("--enable-startup")) SystemIntegration.SetStartup(true);
            if (e.Args.Contains("--reset")) _engine.Reset();
            await _engine.EnsureNvidiaAsync();
            await _engine.EnsureHardwareAsync(); TuningService.EnsureProfiles(_engine);
            if (File.Exists(NvidiaOutputRecovery.FilePath))
            {
                try
                {
                var pending = JsonSerializer.Deserialize<NvidiaOutputRecovery>(File.ReadAllText(NvidiaOutputRecovery.FilePath));
                if (pending != null && _engine.Nvidia.Displays.ContainsKey(pending.DisplayId))
                {
                    await _engine.Nvidia.SetOutputAsync(pending.DisplayId, pending.Output);
                    if (pending.PreviousColorMode.HasValue) await _engine.Displays.SetWideColorAsync(pending.DisplayId, pending.PreviousColorMode == 1);
                    File.Delete(NvidiaOutputRecovery.FilePath);
                }
                }
                catch (Exception recoveryError) { Log.Write(recoveryError); _tray?.ShowBalloonTip(5000, Loc.T("NVIDIA geri alma tamamlanamadı"), Loc.T("Önceki renk çıkışı yedeği korundu. Ekran bağlantısını ve HDR durumunu kontrol et."), Forms.ToolTipIcon.Warning); }
            }
            foreach (var target in _engine.Displays.Monitors)
            {
                try
                {
                    bool primary = target.Primary;
                    if ((primary && e.Args.Contains("--restore-primary")) || (target.Number == 2 && e.Args.Contains("--restore-secondary"))) { await TuningService.RestoreAsync(_engine, target.Id); continue; }
                    if (_engine.Settings.AdaptiveEnabled) continue;
                    string? mode = _engine.Settings.ActiveMonitorScenes.GetValueOrDefault(target.Id);
                    if (primary && e.Args.Contains("--dell-gaming") || target.Number == 2 && e.Args.Contains("--scene-gaming")) mode = "gaming";
                    if (primary && e.Args.Contains("--dell-evening") || target.Number == 2 && e.Args.Contains("--scene-evening")) mode = "evening";
                    if (mode != null && TuningService.SceneFor(_engine, target, mode) is { } scene) await TuningService.ApplyAsync(_engine, target.Id, scene.Id);
                }
                catch (Exception error) { Log.Write(error); _tray?.ShowBalloonTip(5000, Loc.T("Ekran profili uygulanamadı"), Loc.Status(error.Message), Forms.ToolTipIcon.Warning); }
            }
            await _engine.StartAdaptiveAsync(); _overlayInitialized = true; _gameOverlay?.Prepare(); _panel.InitializedUi(); OfferGameOverlaySetup(); WriteRuntimeStatus();
        }
        catch (Exception error)
        {
            Log.Write(error);
            MessageBox.Show(Loc.T("Luma başlatılamadı. Ayrıntılar: ") + Path.Combine(AppSettings.Folder, "luma.log"), "Luma", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }
    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon { Visible = true, Text = Loc.T("Luma · Ekranın, tam kararında") };
        try { _tray.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { _tray.Icon = SystemIcons.Application; }
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new TrayColors()),
            BackColor = Color.FromArgb(27, 30, 38), ForeColor = Color.FromArgb(241, 235, 224),
            Font = new Font("Segoe UI", 9), ShowImageMargin = false
        };
        menu.Items.Add(Loc.T("Luma'yı aç"), null, (_, _) => _panel?.Reveal());
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var profile in Profile.Defaults())
        {
            var id = profile.Id;
            menu.Items.Add(profile.DisplayName, null, (_, _) => { if (_engine != null) { _engine.SelectedDisplayId = null; _engine.SelectProfile(id); } });
        }
        var monitorsMenu = new Forms.ToolStripMenuItem(Loc.T("Monitör profilleri"));
        menu.Items.Add(monitorsMenu);
        menu.Opening += (_, _) =>
        {
            monitorsMenu.DropDownItems.Clear();
            if (_engine == null) return;
            foreach (var display in _engine.Displays.Monitors)
            {
                var item = new Forms.ToolStripMenuItem(display.Number + " · " + display.Name);
                foreach (var scene in TuningService.ScenesFor(_engine, display))
                {
                    string id = scene.Id;
                    item.DropDownItems.Add(scene.Mode == "gaming" ? Loc.T("Gaming") : Loc.T("Akşam"), null, async (_, _) => await _engine.ApplyMonitorSceneAsync(id));
                }
                monitorsMenu.DropDownItems.Add(item);
            }
        };
        menu.Items.Add(Loc.T("Gaming · tüm ekranlar"), null, async (_, _) => { if (_engine != null) await _engine.ApplySceneModeAsync("gaming"); });
        menu.Items.Add(Loc.T("Akşam · tüm ekranlar"), null, async (_, _) => { if (_engine != null) await _engine.ApplySceneModeAsync("evening"); });
        menu.Items.Add(Loc.T("Oyun + saat otomasyonu"), null, (_, _) => { if (_engine != null) { _engine.Settings.AdaptiveEnabled = !_engine.Settings.AdaptiveEnabled; _engine.AdaptiveSettingsChanged(); } });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Duraklat / devam et · Ctrl+Alt+P"), null, (_, _) => _engine?.TogglePause());
        menu.Items.Add(Loc.T("15 dakika duraklat"), null, (_, _) => _engine?.Pause(15));
        menu.Items.Add(Loc.T("1 saat duraklat"), null, (_, _) => _engine?.Pause(60));
        menu.Items.Add(Loc.T("Yazılımsal renkleri sıfırla · Ctrl+Alt+0"), null, (_, _) => _engine?.Reset());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Çıkış"), null, async (_, _) => await ExitAsync());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) _panel?.ToggleVisible(); };
    }
    private void UpdateTray()
    {
        if (_tray == null || _engine == null) return;
        var text = "Luma · " + (_engine.IsSuspended ? Loc.T("Duraklatıldı") : _engine.CurrentProfileName);
        _tray.Text = text.Length > 63 ? text[..63] : text;
        WriteRuntimeStatus();
    }
    private void ShowIndicator(ProfileFeedback feedback)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowIndicator(feedback)); return; }
        if (_exiting || _engine?.Settings.ProfileNotifications != true) return;
        try
        {
            if (_gameOverlay?.ShouldRoute(feedback) == true) { _indicator?.Dismiss(); _gameOverlay.Present(feedback); return; }
            _gameOverlay?.Cancel(); (_indicator ??= new ProfileIndicator()).Present(feedback);
        }
        catch (Exception error) { Log.Write(error); }
    }
    private void UpdateLanguage()
    {
        _indicator?.Dismiss();
        _gameOverlay?.Cancel();
        var previous = _tray; var previousMenu = previous?.ContextMenuStrip;
        if (previous != null) { previous.Visible = false; previous.Dispose(); previousMenu?.Dispose(); }
        CreateTray(); UpdateTray();
    }
    private void WriteRuntimeStatus()
    {
        if (_engine == null || _panel == null) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(Path.Combine(AppSettings.Folder, "status.json"), JsonSerializer.Serialize(new
            {
                Version = "1.7.0", Language = Loc.Language, LanguagePreference = _engine.Settings.Language, ProfileNotifications = _engine.Settings.ProfileNotifications,
                GameOverlay = new { Enabled = _engine.Settings.GameOverlay, Backend = "RTSS", Status = _gameOverlay?.Status, Active = _gameOverlay?.Active, PublishMilliseconds = _gameOverlay?.LastPublishMilliseconds, TargetFps = _gameOverlay?.LastTargetFps, SetupState = _gameOverlay?.SetupState.ToString(), SetupBusy = _gameOverlay?.SetupBusy, SetupExitCode = _gameOverlay?.SetupExitCode },
                ProcessId = Environment.ProcessId, Running = !_exiting, Updated = DateTimeOffset.Now,
                TrayVisible = _tray?.Visible == true, PanelVisible = _panel.IsVisible,
                RegisteredHotkeys = _panel.RegisteredHotkeyCount, HotkeyConflicts = _panel.HotkeyConflicts,
                StartupEnabled = SystemIntegration.StartupEnabled, Gpus = _engine.Displays.Gpus,
                ActiveSecondaryScene = _engine.Displays.Monitors.FirstOrDefault(x => x.Number == 2) is { } second ? TuningService.ActiveScene(_engine, second) : null,
                ActivePrimaryScene = _engine.Displays.Monitors.FirstOrDefault(x => x.Primary) is { } primary ? TuningService.ActiveScene(_engine, primary) : null,
                ActiveMonitorScenes = _engine.Settings.ActiveMonitorScenes,
                Automation = new { Enabled = _engine.Settings.AdaptiveEnabled, _engine.Settings.DayStarts, _engine.Settings.EveningStarts, _engine.AdaptiveStatus, _engine.AdaptiveDecision, _engine.Games.InstalledCount, _engine.Games.Running, _engine.Games.CatalogScannedAt, _engine.Games.CatalogWarnings, Catalog = _engine.Games.Catalog.Select(x => new { x.Name, x.Source, x.Directory, ExecutableCount = x.Executables?.Length ?? 0 }) },
                Nvidia = new { _engine.Nvidia.Available, _engine.Nvidia.Status, _engine.Nvidia.DriverVersion, _engine.Nvidia.Displays, _engine.Nvidia.DriverSettings },
                Monitors = _engine.Displays.Monitors.Select(x => new { x.Number, x.Name, x.Detail, x.AdapterName, x.GpuVendor, x.BrightnessBackend, x.Hdr, x.ActiveColorMode, x.AdvancedColorFlags, x.ColorInfoKnown, x.GammaAvailable, x.ColorStatus, x.HardwareStatus, HardwareControls = x.Features.Values.Select(f => f.Name).ToArray() })
            }, AppSettings.JsonOptions));
        }
        catch (Exception e) { Log.Write(e); }
    }
    private async Task SendCommandAsync(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000);
            using var writer = new StreamWriter(pipe) { AutoFlush = true }; await writer.WriteLineAsync(command);
        }
        catch (Exception error) { Log.Write(error); }
    }
    private async Task ListenAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellation);
                using var reader = new StreamReader(server);
                string? command = await reader.ReadLineAsync(cancellation);
                if (command == null) continue;
                await Dispatcher.InvokeAsync(async () =>
                {
                    if (command == "quit") await ExitAsync();
                    else if (command == "reset") _engine?.Reset();
                    else if (command.StartsWith("adaptive:", StringComparison.Ordinal) && _engine != null)
                    { _engine.Settings.AdaptiveEnabled = command == "adaptive:on"; _engine.AdaptiveSettingsChanged(); }
                    else if ((command == "restore-secondary" || command == "restore-primary") && _engine != null)
                    {
                        var target = _engine.Displays.Monitors.SingleOrDefault(command == "restore-primary" ? TuningService.IsPrimaryEligible : TuningService.IsEligible);
                        if (target != null) { await TuningService.RestoreAsync(_engine, target.Id); _panel?.InitializedUi(); WriteRuntimeStatus(); }
                    }
                    else if (command.StartsWith("scene:", StringComparison.Ordinal) && _engine != null) { await _engine.ApplySecondarySceneAsync(command[6..]); _panel?.InitializedUi(); WriteRuntimeStatus(); }
                    else _panel?.Reveal();
                });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception error) { Log.Write(error); if (!cancellation.IsCancellationRequested) await Task.Delay(500, cancellation); }
        }
    }
    private void OnDisplayChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(async () => { if (_engine != null && !_exiting) { await Task.Delay(1200); await _engine.RefreshAsync(); _panel?.InitializedUi(); } });
    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) OnDisplayChanged(sender, EventArgs.Empty); }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) { if (e.Reason == SessionSwitchReason.SessionUnlock) OnDisplayChanged(sender, EventArgs.Empty); }
    public async Task ExitAsync()
    {
        if (_exiting) return; _exiting = true;
        Loc.Changed -= UpdateLanguage; if (_gameOverlay != null) await _gameOverlay.ShutdownAsync(); _indicator?.Close(); _indicator = null;
        _pipeCancellation?.Cancel();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; SystemEvents.PowerModeChanged -= OnPowerChanged; SystemEvents.SessionSwitch -= OnSessionSwitch;
        try { if (_engine != null) await _engine.ShutdownAsync(); } catch (Exception e) { Log.Write(e); }
        if (_tray != null) { _tray.Visible = false; WriteRuntimeStatus(); _tray.Dispose(); }
        _panel?.AllowClose(); Shutdown();
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}
