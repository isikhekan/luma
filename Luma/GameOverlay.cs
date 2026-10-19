using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Luma;

/// <summary>Event-driven RTSS notification publisher. No permanent rendering or polling loop.</summary>
public sealed class GameOverlay : IDisposable
{
    private readonly Engine _engine;
    private readonly Dispatcher _dispatcher;
    private readonly RtssClient _client = new();
    private readonly FeedbackGate _gate = new();
    private readonly ForegroundChanged _foregroundChanged;
    private CancellationTokenSource? _operation;
    private IntPtr _eventHook;
    private uint _target;
    private int _generation;
    private bool _startedServer, _disposed;
    public string Status { get; private set; } = "Tam ekran bildirimi hazırlanıyor…";
    public event Action? Changed;
    public double LastPublishMilliseconds { get; private set; }
    public double? LastTargetFps { get; private set; }
    public bool Active { get; private set; }
    public GameOverlay(Engine engine, Dispatcher dispatcher)
    {
        _engine = engine; _dispatcher = dispatcher;
        _foregroundChanged = (_, _, _, _, _, _, _) => _dispatcher.BeginInvoke(() =>
        { if (_target != 0 && ForegroundProcess() != _target) Cancel(); });
    }
    private void SetStatus(string text) { if (Status == text) return; Status = text; Changed?.Invoke(); }
    public void Prepare(bool retry = false)
    {
        if (_disposed) return;
        if (retry) _startedServer = false;
        if (!_engine.Settings.ProfileNotifications || !_engine.Settings.GameOverlay) { Cancel(); SetStatus("Tam ekran bildirimi kapalı"); return; }
        if (_client.Available) { SetStatus("RTSS hazır · tam ekran oyun bildirimi etkin"); return; }
        if (RtssClient.FindInstalledServer() == null) { SetStatus("Tam ekran bildirimi için RTSS kurulumu gerekli"); return; }
        if (!_startedServer) { _startedServer = true; if (!RtssClient.StartInstalledServer()) { SetStatus("RTSS bildirimi tamamlanamadı · masaüstü odağı korundu"); return; } }
        SetStatus("RTSS bağlantısı bekleniyor");
    }
    public bool ShouldRoute(ProfileFeedback feedback)
    {
        if (!_engine.Settings.GameOverlay) return false;
        uint foreground = ForegroundProcess();
        return _engine.Games.Running.Any(g => (uint)g.ProcessId == foreground)
            || feedback.Title == "Oyun algılandı" && _engine.Games.Running.Count > 0;
    }
    public void Present(ProfileFeedback feedback)
    {
        if (_disposed || !_gate.Accept(feedback.Key, DateTimeOffset.Now)) return;
        Cancel(); Prepare();
        if (RtssClient.FindInstalledServer() == null && !_client.Available) return;
        var operation = new CancellationTokenSource(); _operation = operation;
        _ = RunAsync(feedback, operation);
    }
    private async Task RunAsync(ProfileFeedback feedback, CancellationTokenSource owner)
    {
        string? image = null; var token = owner.Token;
        try
        {
            // Game launchers often hand over to the renderer. Wait briefly for a real RTSS target.
            // In particular, don't let the four-second notification expire during RTSS hook delay.
            var wait = Stopwatch.StartNew(); RtssTarget target = default;
            while (wait.Elapsed < TimeSpan.FromSeconds(30))
            {
                token.ThrowIfCancellationRequested();
                _client.Clear();
                uint foreground = ForegroundProcess();
                if (_engine.Games.Running.Any(g => (uint)g.ProcessId == foreground) && _client.TryGetTarget(foreground, out target)) break;
                if (_engine.Games.Running.Count == 0) return;
                await Task.Delay(250, token);
            }
            if (target.ProcessId == 0) { SetStatus("RTSS bu oyuna bağlanamadı · oyun içi bildirim gösterilemedi"); return; }
            _target = target.ProcessId; LastTargetFps = target.FramesPerSecond;
            _eventHook = SetWinEventHook(3, 3, IntPtr.Zero, _foregroundChanged, 0, 0, 0);
            string folder = Path.Combine(AppSettings.Folder, "overlay-cache"); Directory.CreateDirectory(folder);
            image = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
            var size = ProfileIndicator.RenderOverlayImage(feedback, image);
            string? protocolPath = RtssClient.ImagePath(image);
            if (protocolPath == null) { SetStatus("RTSS bildirim resmi okunamadı"); return; }
            Active = true; SetStatus("Oyun içi bildirim gösteriliyor · RTSS");
            async Task Animate(bool entering)
            {
                var animation = Stopwatch.StartNew();
                do
                {
                    token.ThrowIfCancellationRequested();
                    if (ForegroundProcess() != _target) throw new OperationCanceledException();
                    double progress = Math.Min(1, animation.Elapsed.TotalMilliseconds / OverlayMotion.Animation.TotalMilliseconds);
                    double visible = entering ? OverlayMotion.Ease(progress) : 1 - OverlayMotion.Ease(progress);
                    if (Native.GetClientRect(Native.GetForegroundWindow(), out var rect))
                    {
                        var timer = Stopwatch.StartNew();
                        bool wrote = _client.TryWrite(OverlayMotion.Markup(protocolPath, size.Width, size.Height, rect.Width, rect.Height, visible, target.PixelZoom));
                        LastPublishMilliseconds = timer.Elapsed.TotalMilliseconds;
                        if (!wrote && progress >= 1) throw new IOException("RTSS OSD is busy or unavailable.");
                    }
                    if (progress >= 1) break;
                    await Task.Delay(33, token);
                } while (true);
            }
            await Animate(true);
            await Task.Delay(TimeSpan.FromSeconds(4), token); // no updates while the card is stationary
            await Animate(false);
            SetStatus("RTSS hazır · tam ekran oyun bildirimi etkin");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Write(e); SetStatus("RTSS bildirimi tamamlanamadı · masaüstü odağı korundu"); }
        finally
        {
            // Only the current generation may clear the shared slot or remove its foreground hook.
            if (ReferenceEquals(_operation, owner))
            {
                for (int i = 0; i < 20; i++)
                {
                    if (!ReferenceEquals(_operation, owner) || _client.Clear()) break;
                    await Task.Delay(10);
                }
                if (ReferenceEquals(_operation, owner))
                {
                    _client.Dispose(); Active = false; _target = 0;
                    if (_eventHook != IntPtr.Zero) { UnhookWinEvent(_eventHook); _eventHook = IntPtr.Zero; }
                    _operation = null; Changed?.Invoke();
                }
            }
            owner.Dispose();
            if (image != null) { try { File.Delete(image); } catch (IOException) { } }
        }
    }
    public void Cancel()
    {
        int generation = ++_generation;
        var old = _operation; _operation = null; old?.Cancel();
        Active = false; _target = 0;
        if (_eventHook != IntPtr.Zero) { UnhookWinEvent(_eventHook); _eventHook = IntPtr.Zero; }
        bool cleared = _client.Clear(); _client.Dispose();
        if (!cleared) _ = RetryClearAsync(generation);
    }
    private async Task RetryClearAsync(int generation)
    {
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(10);
            if (generation != _generation || _operation != null) return;
            if (_client.Clear()) { _client.Dispose(); return; }
        }
    }
    public void Dispose() { if (_disposed) return; Cancel(); _disposed = true; }
    public async Task ShutdownAsync()
    {
        Cancel();
        for (int i = 0; i < 20 && !_client.Clear(); i++) await Task.Delay(10);
        _client.Dispose(); _disposed = true;
    }
    private static uint ForegroundProcess()
    { Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint process); return process; }
    private delegate void ForegroundChanged(IntPtr hook, uint evt, IntPtr window, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, ForegroundChanged callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
