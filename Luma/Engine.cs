using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Luma;

public sealed class Engine : IDisposable
{
    public AppSettings Settings { get; }
    private readonly bool _preview;
    public DisplayService Displays { get; } = new();
    public NvidiaService Nvidia { get; } = new();
    public GameDetectionService Games { get; } = new();
    public AdaptiveDecision? AdaptiveDecision { get; private set; }
    public string AdaptiveStatus { get; private set; } = "Oyun ve saat otomasyonu kapalı";
    public event Action? AdaptiveChanged;
    private readonly AdaptivePolicy _adaptivePolicy = new();
    private bool _adaptiveReady;
    private string? _adaptiveAppliedKey;
    private readonly HashSet<string> _adaptiveAppliedDisplays = new();
    private string? _lastAdaptiveError;
    private DateTimeOffset _adaptiveRetryAfter;
    public bool NvidiaReady { get; private set; }
    public bool HardwareReady { get; private set; }
    public event Action? NvidiaLoaded;
    public event Action? SecondarySceneApplied;
    internal void AnnounceSceneApplied() { Changed?.Invoke(); SecondarySceneApplied?.Invoke(); }
    public List<Profile> Profiles => Profile.Defaults().Concat(Settings.CustomProfiles).Concat(Displays.Monitors.SelectMany(d => TuningService.ScenesFor(this, d).Select(x => new Profile(x.Id, x.DisplayName, x.Id.EndsWith("gaming") ? "\uE7FC" : "\uE708", Loc.F($"Yalnızca {d.Name} · donanım + NVIDIA"), x.Color)))).ToList();
    public string? SelectedDisplayId { get; set; }
    public bool Paused { get; private set; }
    public DateTime? PausedUntil { get; private set; }
    public bool FullscreenPaused { get; private set; }
    public bool IsSuspended => Paused || FullscreenPaused;
    public string? ManualUntil => _overrideUntil.HasValue && _overrideUntil > DateTime.Now ? Loc.F($"{_overrideUntil:HH:mm}'e kadar manuel") : null;
    public event Action? Changed;
    public event Action<string, string>? Notify;
    public event Action<ProfileFeedback>? ProfileApplied;
    private bool _lastFeedbackGame;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _cursor = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private CancellationTokenSource? _transition;
    private Task _transitionTask = Task.CompletedTask;
    private Task _hardwareTask = Task.CompletedTask;
    private Task _nvidiaTask = Task.CompletedTask;
    private readonly Dictionary<string, ColorSettings> _rendered = new();
    private DateTime? _overrideUntil;
    private string? _scheduleApplied;
    private DateTime _lastBreak = DateTime.Now;
    private DateTime _lastWarp = DateTime.MinValue;
    private bool _tickBusy, _disposed;
    public bool Refreshing { get; private set; }
    private int _tuningDepth;
    private bool _refreshDeferred;
    internal void BeginTuning() { ObjectDisposedException.ThrowIf(_disposed, this); _tuningDepth++; }
    internal void EndTuning()
    {
        _tuningDepth = Math.Max(0, _tuningDepth - 1);
        if (_tuningDepth == 0 && _refreshDeferred && !_disposed)
        { _refreshDeferred = false; _clock.Dispatcher.BeginInvoke(async () => await RefreshAsync()); }
    }
    public IntPtr WindowHandle { get; set; }

    public Engine(AppSettings settings, bool preview = false)
    {
        Settings = settings; _preview = preview;
        _save.Tick += (_, _) => { _save.Stop(); try { Settings.Save(); } catch (Exception e) { Log.Write(e); Notify?.Invoke("Ayarlar kaydedilemedi", "Disk erişimini kontrol et. Ayrıntı luma.log dosyasında."); } };
        _clock.Tick += async (_, _) => await TickAsync();
        _cursor.Tick += (_, _) => WrapCursor();
        Games.CatalogChanged += () => _clock.Dispatcher.BeginInvoke(() => { if (!_disposed) { Changed?.Invoke(); AdaptiveChanged?.Invoke(); } });
    }
    public async Task InitializeAsync()
    {
        await RefreshAsync(); await Games.RefreshCatalogAsync(false); _clock.Start(); UpdateCursorTimer(); await TickAsync();
    }
    public async Task RefreshAsync()
    {
        if (_tuningDepth > 0) { _refreshDeferred = true; return; }
        if (Refreshing || _disposed) return;
        Refreshing = true;
        try
        {
            await StopTransitionAsync();
            await Displays.RefreshAsync();
            _adaptiveAppliedKey = null;
            _adaptiveAppliedDisplays.Clear();
            foreach (var d in Displays.Monitors)
            {
                if (!Settings.Displays.ContainsKey(d.Id)) Settings.Displays[d.Id] = new();
                _rendered[d.Id] = new();
            }
            if (SelectedDisplayId != null && !Displays.Monitors.Any(x => x.Id == SelectedDisplayId)) SelectedDisplayId = null;
            Apply(false); SaveSoon();
            HardwareReady = false; NvidiaReady = false;
            _hardwareTask = LoadHardwareAsync();
            NvidiaReady = false; _nvidiaTask = LoadNvidiaAsync();
        }
        finally { Refreshing = false; Changed?.Invoke(); }
    }
    private async Task LoadHardwareAsync()
    {
        try { await Displays.LoadHardwareAsync(); }
        catch (Exception e) { Log.Write(e); }
        finally { HardwareReady = true; TuningService.EnsureProfiles(this); Changed?.Invoke(); }
    }
    private async Task LoadNvidiaAsync()
    {
        try
        {
            await Nvidia.RefreshAsync(Displays.Monitors);
            if (_disposed) return;
            foreach (var entry in Settings.NvidiaDisplays)
            {
                if (!Nvidia.Displays.TryGetValue(entry.Key, out var state)) continue;
                if (Displays.Monitors.FirstOrDefault(x => x.Id == entry.Key)?.Hdr == true) continue;
                if (entry.Value.Vibrance is double v && state.Vibrance != null && Math.Abs(v - state.Vibrance.Percent) > .6) await Nvidia.SetVibranceAsync(entry.Key, v);
                if (entry.Value.Hue is int h && state.Hue.HasValue && state.Hue != h) await Nvidia.SetHueAsync(entry.Key, h);
            }
        }
        catch (Exception e) { Log.Write(e); }
        finally { NvidiaReady = true; TuningService.EnsureProfiles(this); NvidiaLoaded?.Invoke(); Changed?.Invoke(); }
    }
    public async Task EnsureNvidiaAsync() => await _nvidiaTask;
    public async Task RefreshNvidiaAsync() { _nvidiaTask = LoadNvidiaAsync(); await _nvidiaTask; }
    public async Task EnsureHardwareAsync() => await _hardwareTask;
    internal void PreparePreviewProfiles() { HardwareReady = true; NvidiaReady = true; TuningService.EnsureProfiles(this); _save.Stop(); }
    public async Task FlushColorsAsync() => await _transitionTask;
    public void SetDisplayColor(string id, ColorSettings color)
    {
        if (!Displays.Monitors.Any(x => x.Id == id)) throw new InvalidOperationException(Loc.T("Ekran bağlı değil."));
        Settings.Displays[id] = color.Safe(); Settings.ActiveMonitorScenes.Remove(id); Settings.ActiveProfileId = "custom"; ManualOverride(); Apply(Settings.SmoothTransitions); SaveSoon(); Changed?.Invoke();
    }
    public ColorSettings Current => SelectedDisplayId != null && Settings.Displays.TryGetValue(SelectedDisplayId, out var s)
        ? s : Displays.Monitors.FirstOrDefault() is { } first && Settings.Displays.TryGetValue(first.Id, out var value) ? value : new();
    public bool Mixed => SelectedDisplayId == null && Displays.Monitors.Select(x => Settings.Displays.GetValueOrDefault(x.Id, new())).Distinct().Count() > 1;
    public IEnumerable<Display> Targets => Displays.Monitors.Where(x => SelectedDisplayId == null || x.Id == SelectedDisplayId);
    public string? SelectedSceneId => Displays.Monitors.FirstOrDefault(x => x.Id == SelectedDisplayId) is { } display ? TuningService.ActiveScene(this, display) : null;
    public string CurrentProfileName => Settings.AdaptiveEnabled && SelectedDisplayId == null && AdaptiveDecision != null ? (AdaptiveDecision.Scene == "gaming" ? Loc.T("Gaming · otomatik") : Loc.T("Akşam · otomatik")) : SelectedSceneId is string sceneId && TuningService.FindScene(this, sceneId) is { } scene ? scene.DisplayName : Mixed ? Loc.T("Farklı ekran ayarları") : Profiles.FirstOrDefault(x => x.Color.Safe() == Current)?.DisplayName ?? Loc.T("Özel ayar");

    public void SelectProfile(string id, bool automatic = false)
    {
        if (TuningService.FindScene(this, id) != null) { _ = ApplyMonitorSceneAsync(id); return; }
        var profile = Profiles.FirstOrDefault(x => x.Id == id); if (profile == null) return;
        if ((automatic ? Displays.Monitors : Targets).Any(TuningService.IsEligible)) Settings.SecondarySceneId = null;
        if ((automatic ? Displays.Monitors : Targets).Any(TuningService.IsPrimaryEligible)) Settings.PrimarySceneId = null;
        foreach (var d in automatic ? Displays.Monitors : Targets) Settings.ActiveMonitorScenes.Remove(d.Id);
        foreach (var d in automatic ? Displays.Monitors : Targets) Settings.Displays[d.Id] = profile.Color.Safe();
        Settings.ActiveProfileId = id;
        if (!automatic) ManualOverride();
        Apply(Settings.SmoothTransitions); SaveSoon(); Changed?.Invoke();
        _ = ReportColorsAsync(_transitionTask, automatic ? "Saat profili uygulandı" : "Profil uygulandı", profile.DisplayName);
    }
    public void SetColor(ColorSettings color)
    {
        if (Targets.Any(TuningService.IsEligible)) Settings.SecondarySceneId = null;
        if (Targets.Any(TuningService.IsPrimaryEligible)) Settings.PrimarySceneId = null;
        foreach (var d in Targets) Settings.ActiveMonitorScenes.Remove(d.Id);
        foreach (var d in Targets) Settings.Displays[d.Id] = color.Safe();
        Settings.ActiveProfileId = "custom"; ManualOverride(); Apply(false, true); SaveSoon();
        _ = ReportColorsAsync(_transitionTask, "Ayar uygulandı", Loc.T("Renk ayarları güncellendi"));
    }
    private async Task ReportColorsAsync(Task transition, string title, string message)
    {
        await transition;
        if (!_disposed && ReferenceEquals(_transitionTask, transition) && _tuningDepth == 0 && Targets.Any(d => d.ColorStatus == "Renk ayarı uygulandı"))
            ProfileApplied?.Invoke(new(title, message, "color:" + message));
    }
    public void ManualOverride()
    {
        _overrideUntil = Settings.ScheduleEnabled ? Scheduler.NextBoundary(Settings.Schedule, DateTime.Now) : null;
        _scheduleApplied = null;
    }
    public async Task ApplySecondarySceneAsync(string sceneId)
        => await ApplyMonitorSceneAsync(sceneId);
    public async Task ApplyMonitorSceneAsync(string sceneId)
    {
        try
        {
            await EnsureHardwareAsync(); await EnsureNvidiaAsync(); TuningService.EnsureProfiles(this);
            if (sceneId.StartsWith("dell-") || sceneId.StartsWith("lg-"))
            {
                var legacyTarget = Displays.Monitors.FirstOrDefault(sceneId.StartsWith("dell-") ? TuningService.IsPrimaryEligible : TuningService.IsEligible);
                if (legacyTarget != null) sceneId = TuningService.SceneFor(this, legacyTarget, sceneId.EndsWith("evening") ? "evening" : "gaming")?.Id ?? sceneId;
            }
            var target = Displays.Monitors.SingleOrDefault(d => TuningService.ScenesFor(this, d).Any(s => s.Id == sceneId)) ?? throw new InvalidOperationException(Loc.T("Bu profile ait monitör bağlı değil."));
            await TuningService.ApplyAsync(this, target.Id, sceneId); Changed?.Invoke();
            ProfileApplied?.Invoke(new("Profil uygulandı", TuningService.FindScene(this, sceneId)?.DisplayName ?? sceneId, "scene:" + sceneId));
        }
        catch (Exception e) { Log.Write(e); Notify?.Invoke("Ekran profili uygulanamadı", e.Message); }
    }
    public async Task ApplySceneModeAsync(string mode)
    {
        await EnsureHardwareAsync(); await EnsureNvidiaAsync(); TuningService.EnsureProfiles(this);
        BeginTuning();
        try
        {
            int applied = 0;
            foreach (var display in Displays.Monitors) if (TuningService.SceneFor(this, display, mode) is { } scene) { await TuningService.ApplyAsync(this, display.Id, scene.Id); applied++; }
            if (applied > 0) ProfileApplied?.Invoke(new("Profil uygulandı", Loc.F($"{Loc.T(mode == "gaming" ? "Gaming" : "Akşam")} · {applied} ekran"), "mode:" + mode));
        }
        catch (Exception e) { Log.Write(e); Notify?.Invoke("Profil tamamlanamadı", e.Message); }
        finally { EndTuning(); }
    }
    public async Task StartAdaptiveAsync()
    { _adaptiveReady = true; await TickAsync(); }
    public void AdaptiveSettingsChanged()
    {
        _adaptiveAppliedKey = null; _adaptiveRetryAfter = default;
        if (Settings.AdaptiveEnabled) Settings.ScheduleEnabled = false;
        else { AdaptiveDecision = null; AdaptiveStatus = "Oyun ve saat otomasyonu kapalı"; }
        SettingsChanged(); AdaptiveChanged?.Invoke(); _ = TickAsync();
    }
    private async Task TickAdaptiveAsync()
    {
        if (!_adaptiveReady || !Settings.AdaptiveEnabled || IsSuspended || _disposed || _tuningDepth > 0) return;
        var decision = _adaptivePolicy.Observe(DateTimeOffset.Now, Games.Running.Count > 0,
            TimeOnly.ParseExact(Settings.DayStarts, "HH:mm"), TimeOnly.ParseExact(Settings.EveningStarts, "HH:mm"));
        string description = decision.Reason switch
        {
            "game" => "Gaming · " + string.Join(", ", Games.Running.Select(x => x.Name).Distinct()),
            "game-exit-delay" => "Gaming · oyun kapanışı bekleniyor",
            "day" => $"Gaming · gündüz ({Settings.DayStarts}–{Settings.EveningStarts})",
            _ => $"Akşam · saat kuralı ({Settings.EveningStarts}–{Settings.DayStarts})"
        };
        bool changed = AdaptiveStatus != description;
        AdaptiveDecision = decision; AdaptiveStatus = description;
        if (changed) { Changed?.Invoke(); AdaptiveChanged?.Invoke(); }
        var targets = Displays.Monitors.Where(TuningService.IsTunable).ToArray();
        string key = decision.Context + ":" + string.Join("|", targets.Select(x => x.Id).OrderBy(x => x));
        if (targets.Length == 0 || key == _adaptiveAppliedKey || DateTimeOffset.Now < _adaptiveRetryAfter) return;
        BeginTuning();
        try
        {
            await EnsureHardwareAsync(); await EnsureNvidiaAsync();
            TuningService.EnsureProfiles(this);
            var errors = new List<string>(); string? selected = SelectedDisplayId;
            foreach (var display in targets)
            {
                if (_disposed) return;
                string? id = TuningService.SceneFor(this, display, decision.Scene)?.Id;
                if (id == null) continue;
                if (_adaptiveAppliedDisplays.Contains(display.Id) && TuningService.FindScene(this, id) is { } scene && TuningService.Matches(this, display, scene)) continue;
                try { await TuningService.ApplyAsync(this, display.Id, id); _adaptiveAppliedDisplays.Add(display.Id); }
                catch (Exception e) { Log.Write(e); errors.Add(display.Name + ": " + e.Message); }
            }
            SelectedDisplayId = selected;
            if (errors.Count == 0)
            {
                _adaptiveAppliedKey = key; _adaptiveRetryAfter = default; _lastAdaptiveError = null;
                ProfileApplied?.Invoke(new(FeedbackGate.AutomaticTitle(decision.GameActive, _lastFeedbackGame),
                    Loc.F($"{Loc.T(decision.Scene == "gaming" ? "Gaming" : "Akşam")} · {targets.Length} ekran"), "adaptive:" + key));
                _lastFeedbackGame = decision.GameActive;
            }
            else
            {
                _adaptiveRetryAfter = DateTimeOffset.Now.AddSeconds(30);
                AdaptiveStatus = "Bazı ekranlara uygulanamadı · 30 sn sonra yeniden denenecek";
                string error = string.Join("\n", errors);
                if (_lastAdaptiveError != error) { _lastAdaptiveError = error; Notify?.Invoke("Otomatik profil tamamlanamadı", error); }
            }
            Changed?.Invoke(); AdaptiveChanged?.Invoke();
        }
        finally { EndTuning(); }
    }
    public void SaveSoon() { if (_disposed || _preview) return; _save.Stop(); _save.Start(); }
    public void SettingsChanged() { UpdateCursorTimer(); SaveSoon(); Changed?.Invoke(); }
    public void ScheduleChanged() { _overrideUntil = null; _scheduleApplied = null; SettingsChanged(); _ = TickAsync(); }
    public void Pause(int? minutes = null)
    {
        Paused = true; PausedUntil = minutes.HasValue ? DateTime.Now.AddMinutes(minutes.Value) : null;
        Apply(Settings.SmoothTransitions); Changed?.Invoke();
        _ = ReportColorsAsync(_transitionTask, "Ayar uygulandı", Loc.T("Renk filtresi duraklatıldı"));
    }
    public void Resume()
    {
        Paused = false; PausedUntil = null; Apply(Settings.SmoothTransitions); Changed?.Invoke();
        _ = ReportColorsAsync(_transitionTask, "Ayar uygulandı", Loc.T("Renk filtresi sürdürüldü"));
    }
    public void TogglePause() { if (Paused) Resume(); else Pause(); }
    public void Reset()
    {
        Paused = false; PausedUntil = null; _overrideUntil = null; _scheduleApplied = null;
        // Reset stays neutral until the user explicitly re-enables their schedule.
        Settings.ScheduleEnabled = false;
        Settings.AdaptiveEnabled = false; AdaptiveDecision = null; AdaptiveStatus = "Oyun ve saat otomasyonu kapalı";
        Settings.SecondarySceneId = null;
        Settings.PrimarySceneId = null;
        Settings.ActiveMonitorScenes.Clear();
        foreach (var d in Displays.Monitors) Settings.Displays[d.Id] = new();
        Settings.ActiveProfileId = "day"; Settings.WrapCursor = false;
        Apply(false); SettingsChanged();
        _ = ReportColorsAsync(_transitionTask, "Ayar uygulandı", Loc.T("Yazılımsal renkler sıfırlandı. Otomasyon ve imleç sarma kapatıldı."));
    }
    private void Apply(bool smooth, bool debounce = false)
    {
        _transition?.Cancel();
        var cancellation = new CancellationTokenSource(); _transition = cancellation;
        var target = Displays.Monitors.ToDictionary(x => x.Id, x => IsSuspended ? new ColorSettings() : Settings.Displays.GetValueOrDefault(x.Id, new()));
        var from = target.ToDictionary(x => x.Key, x => _rendered.GetValueOrDefault(x.Key, new()));
        _transitionTask = TransitionAsync(from, target, smooth, debounce, cancellation);
    }
    private async Task TransitionAsync(Dictionary<string, ColorSettings> from, Dictionary<string, ColorSettings> to, bool smooth, bool debounce, CancellationTokenSource owner)
    {
        try
        {
            var token = owner.Token;
            if (debounce) await Task.Delay(110, token);
            int steps = smooth ? 12 : 1;
            for (int i = 1; i <= steps; i++)
            {
                token.ThrowIfCancellationRequested();
                double t = i / (double)steps; t = t * t * (3 - 2 * t);
                var frame = to.ToDictionary(x => x.Key, x => ColorSettings.Lerp(from[x.Key], x.Value, t));
                await Displays.ApplyColorsAsync(frame, i == steps, token);
                foreach (var item in frame) _rendered[item.Key] = item.Value;
                if (i < steps) await Task.Delay(65, token);
            }
            if (!token.IsCancellationRequested) Changed?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Write(e); Notify?.Invoke("Ekran ayarı uygulanamadı", "Bağlantıyı kontrol et ve ekranları yenile."); }
        finally { owner.Dispose(); if (ReferenceEquals(_transition, owner)) _transition = null; }
    }
    private async Task StopTransitionAsync()
    {
        _transition?.Cancel(); await _transitionTask;
    }
    private async Task TickAsync()
    {
        if (_tickBusy || _disposed || Refreshing) return;
        _tickBusy = true;
        try
        {
            bool changed = false;
            if (PausedUntil.HasValue && DateTime.Now >= PausedUntil) { Paused = false; PausedUntil = null; changed = true; }
            if (_adaptiveReady && (Settings.AdaptiveEnabled || Settings.ProfileNotifications && Settings.GameOverlay)) await Games.PollAsync(Settings.GameExecutables, Settings.IgnoredGameExecutables);
            else if (_adaptiveReady) await Games.RefreshCatalogAsync(false);
            if (_disposed) return;
            bool fullscreen = Settings.PauseFullscreen && !(Settings.AdaptiveEnabled && Games.Running.Count > 0) && IsFullscreen();
            if (FullscreenPaused != fullscreen) { FullscreenPaused = fullscreen; changed = true; }
            if (changed) { Apply(Settings.SmoothTransitions); Changed?.Invoke(); }
            await TickAdaptiveAsync();
            if (Settings.ScheduleEnabled && !Settings.AdaptiveEnabled && (!_overrideUntil.HasValue || DateTime.Now >= _overrideUntil))
            {
                _overrideUntil = null;
                string? id = Scheduler.Resolve(Settings.Schedule, TimeOnly.FromDateTime(DateTime.Now));
                if (id != null && id != _scheduleApplied) { _scheduleApplied = id; SelectProfile(id, true); }
            }
            if (Settings.BreakReminder && DateTime.Now - _lastBreak >= TimeSpan.FromMinutes(Settings.BreakMinutes))
            {
                _lastBreak = DateTime.Now;
                if (!IsFullscreen()) Notify?.Invoke("Küçük bir mola", "20 saniye boyunca uzağa bak. Ekranların seni burada bekliyor.");
            }
            if (!Settings.BreakReminder) _lastBreak = DateTime.Now;
            await Task.CompletedTask;
        }
        catch (Exception e) { Log.Write(e); }
        finally { _tickBusy = false; }
    }
    private bool IsFullscreen()
    {
        var window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero || window == WindowHandle) return false;
        Native.GetWindowThreadProcessId(window, out var pid);
        try { using var process = Process.GetProcessById((int)pid); if (process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return false; } catch { return false; }
        if (Native.DwmGetWindowAttribute(window, 9, out var rect, 16) != 0 && !Native.GetWindowRect(window, out rect)) return false;
        var monitor = Displays.Monitors.FirstOrDefault(x => x.MonitorHandle == Native.MonitorFromWindow(window, 2));
        if (monitor == null) return false;
        var b = monitor.Bounds;
        return Math.Abs(rect.Left - b.Left) <= 2 && Math.Abs(rect.Top - b.Top) <= 2 && Math.Abs(rect.Right - b.Right) <= 2 && Math.Abs(rect.Bottom - b.Bottom) <= 2;
    }
    private void UpdateCursorTimer() { if (Settings.WrapCursor) _cursor.Start(); else _cursor.Stop(); }
    private void WrapCursor()
    {
        if (_disposed || Refreshing || IsSuspended || Displays.Monitors.Count < 2 || DateTime.Now - _lastWarp < TimeSpan.FromMilliseconds(600)) return;
        if (!Native.GetCursorPos(out var pos)) return;
        var ordered = Displays.Monitors.OrderBy(x => x.Bounds.Left).ToList();
        var left = ordered[0]; var right = ordered.OrderByDescending(x => x.Bounds.Right).First();
        Display? destination = pos.X <= left.Bounds.Left && pos.Y >= left.Bounds.Top && pos.Y < left.Bounds.Bottom ? right :
            pos.X >= right.Bounds.Right - 1 && pos.Y >= right.Bounds.Top && pos.Y < right.Bounds.Bottom ? left : null;
        if (destination == null) return;
        int y = Math.Clamp(pos.Y, destination.Bounds.Top + 2, destination.Bounds.Bottom - 3);
        Native.SetCursorPos(destination == left ? left.Bounds.Left + 3 : right.Bounds.Right - 4, y); _lastWarp = DateTime.Now;
    }
    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        _disposed = true; _clock.Stop(); _cursor.Stop(); _save.Stop();
        await TuningService.WaitIdleAsync();
        await StopTransitionAsync();
        try { Settings.Save(); } catch (Exception e) { Log.Write(e); }
        try { await Displays.RestoreAsync(); } finally { Displays.Dispose(); Nvidia.Dispose(); }
    }
    public void Dispose() { _disposed = true; _clock.Stop(); _cursor.Stop(); _save.Stop(); _transition?.Cancel(); Displays.Dispose(); Nvidia.Dispose(); }
}
