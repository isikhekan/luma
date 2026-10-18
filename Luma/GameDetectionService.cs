using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Luma;

public sealed record RunningGame(int ProcessId, string Name, string Executable, DateTime Started);

public sealed class GameDetectionService
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private static string? ExecutablePath(int id)
    {
        var handle = OpenProcess(0x1000, false, id); // Query limited information; no game memory access.
        if (handle == IntPtr.Zero) return null;
        try { uint size = 32768; var path = new StringBuilder((int)size); return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null; }
        finally { CloseHandle(handle); }
    }
    private readonly Dictionary<int, RunningGame> _tracked = new();
    private readonly Func<GameCatalog> _scan;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _work = new(1, 1);
    private GameCatalog _catalog = new(default, Array.Empty<GameInstall>(), Array.Empty<string>(), 0);
    private HashSet<string> _executableNames = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _nextCatalogRefresh;
    public IReadOnlyList<RunningGame> Running { get; private set; } = Array.Empty<RunningGame>();
    public event Action? CatalogChanged;
    public IReadOnlyList<GameInstall> Catalog => _catalog.Games;
    public int InstalledCount => _catalog.Games.Length;
    public DateTimeOffset CatalogScannedAt => _catalog.ScannedAt;
    public IReadOnlyList<string> CatalogWarnings => _catalog.Warnings;
    public long CatalogDurationMs => _catalog.DurationMs;
    public bool Scanning { get; private set; }
    public static string CatalogPath => Path.Combine(AppSettings.Folder, "games-catalog.json");
    public string Status { get; private set; } = "Oyun listesi hazırlanıyor";
    private static readonly HashSet<string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2", "dota2", "RocketLeague", "VALORANT-Win64-Shipping", "FortniteClient-Win64-Shipping",
        "r5apex", "RainbowSix", "RainbowSix_DX11", "GTA5", "GTA5_Enhanced", "RDR2", "eldenring",
        "Overwatch", "League of Legends", "Hades", "Hades2", "acs", "ac2-win64-shipping", "ItTakesTwo",
        "Overcooked", "Overcooked2", "ChainedTogether-Win64-Shipping", "Minecraft.Windows", "bf6", "cod"
    };
    public GameDetectionService() : this(() => new GameCatalogScanner().Scan(), CatalogPath) { }
    internal GameDetectionService(Func<GameCatalog> scan, string cachePath)
    {
        _scan = scan; _cachePath = cachePath;
        try
        {
            if (File.Exists(_cachePath) && new FileInfo(_cachePath).Length < 5_000_000)
            {
                var cached = JsonSerializer.Deserialize<GameCatalog>(File.ReadAllText(_cachePath));
                if (cached?.Games != null) SetCatalog(cached with { Games = cached.Games.Where(x => x != null && Directory.Exists(x.Directory)).ToArray(), Warnings = cached.Warnings ?? Array.Empty<string>() });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { Log.Write(e); }
    }
    public static bool IsHelper(string executable)
    {
        string name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        return new[] { "launcher", "crashhandler", "crashreport", "crashpad", "anticheat", "easyanticheat", "battleye", "reporter", "updater", "setup", "unins", "webhelper", "cef", "overlay", "redist", "wallpaper", "3dmark", "benchmark", "bootstrapper", "prereq", "patcher", " yama" }.Any(name.Contains)
            || name.StartsWith("leagueclient") || name.StartsWith("riotclient") || name.EndsWith("server")
            || name is "steam" or "ubisoftconnect" or "eadesktop" or "luma" or "content manager" or "vconsole2" or "steamstatisticsreader";
    }
    public static bool IsInside(string executable, string directory)
    {
        try { return Path.GetFullPath(executable).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    public async Task PollAsync(IReadOnlyCollection<string> custom, IReadOnlyCollection<string> ignored)
    {
        var customCopy = custom.ToArray(); var ignoredCopy = ignored.ToArray();
        await _work.WaitAsync();
        try { await Task.Run(() => { RefreshCatalog(false); Poll(customCopy, ignoredCopy); }); }
        finally { _work.Release(); }
    }
    private void Poll(string[] custom, string[] ignored)
    {
        var allowed = custom.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = ignored.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var alive = new Dictionary<int, RunningGame>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            try
            {
                int id = process.Id;
                if (id == Environment.ProcessId) continue;
                if (_tracked.TryGetValue(id, out var tracked))
                {
                    if (!process.HasExited && !excluded.Contains(tracked.Executable) && process.StartTime.ToUniversalTime() == tracked.Started)
                    { alive[id] = tracked; continue; }
                }
                string name = process.ProcessName;
                bool known = KnownGames.Contains(name);
                bool customName = custom.Any(x => string.Equals(Path.GetFileNameWithoutExtension(x), name, StringComparison.OrdinalIgnoreCase));
                if (!known && !customName && !_executableNames.Contains(name) && process.MainWindowHandle == IntPtr.Zero) continue;
                string? path = ExecutablePath(id);
                if (path == null || excluded.Contains(path)) continue;
                // An explicitly selected EXE is allowed even if its name resembles a launcher.
                bool explicitGame = allowed.Contains(path);
                if (!explicitGame && IsHelper(path)) continue;
                var install = _catalog.Games.FirstOrDefault(x => IsInside(path, x.Directory));
                if (!explicitGame && install != null && GameCatalogScanner.IsAuxiliaryPath(path, install.Directory)) continue;
                if (explicitGame || known || install != null)
                    alive[id] = new(id, install?.Name ?? name, path, process.StartTime.ToUniversalTime());
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        _tracked.Clear(); foreach (var entry in alive) _tracked[entry.Key] = entry.Value;
        Running = alive.Values.OrderBy(x => x.Name).ToArray();
        Status = Running.Count > 0 ? "Oyun açık: " + string.Join(", ", Running.Select(x => x.Name).Distinct()) : $"Oyun bekleniyor · {InstalledCount} oyun otomatik eklendi";
    }
    private void SetCatalog(GameCatalog catalog)
    {
        _catalog = catalog;
        _executableNames = catalog.Games.SelectMany(x => x.Executables ?? Array.Empty<string>()).Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Status = $"Oyun bekleniyor · {InstalledCount} oyun otomatik eklendi";
    }
    private void RefreshCatalog(bool force)
    {
        if (!force && DateTime.UtcNow < _nextCatalogRefresh) return;
        Scanning = true;
        try
        {
            var catalog = _scan();
            SetCatalog(catalog); _nextCatalogRefresh = DateTime.UtcNow.AddMinutes(1);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                string temporary = _cachePath + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(catalog, AppSettings.JsonOptions));
                File.Move(temporary, _cachePath, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write(e); }
            CatalogChanged?.Invoke();
        }
        finally { Scanning = false; }
    }
    public async Task RefreshCatalogAsync(bool force = true)
    {
        await _work.WaitAsync();
        try { await Task.Run(() => RefreshCatalog(force)); }
        finally { _work.Release(); }
    }
}
